using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Summit.GpuAdaptiveBinning;
using Summit.GpuAutotuning;
using Summit.GpuPrimitives;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Actual RecordAdaptive replay with explicit CPU features, uploads, native GPU timings,
/// persistent scratch and dynamic transitions. Discovery never changes a runtime matrix.</summary>
public sealed class GpuAdaptiveRuntimeBenchmark : MonoBehaviour
{
    [Serializable] public sealed class Sample
    {
        public int sequence, segment, observation, repeat;
        public string variant, selectedBackend, reason;
        public bool switched;
        public GpuAdaptiveBinningFeatures features;
        public double featureCpuMs, uploadCpuMs, recordCpuMs, selectorCpuMs, switchStateCpuMs,
            submitCpuMs, pipelineCpuMs, gpuMs;
        public long managedAllocatedBytes;
        public ulong timestampToken, timestampFrequency, timestampFence, timestampBeginTicks, timestampEndTicks, timestampElapsedTicks;
        public int sourceFrame, resultFrame;
        public uint timestampFlags, deviceGeneration;
    }
    [Serializable] public sealed class Report
    {
        public int schemaVersion = 1;
        public string protocol = "r9700-adaptive-runtime-v1", phase, runId, matrixSha256, primitiveCandidateId;
        public int seed, framesPerSegment, repeats;
        public GpuDeviceFingerprint device;
        public GpuCalibrationEnvironment environment;
        public bool passed;
        public string failure;
        public long persistentGpuScratchBytes, cpuFeatureScratchBytes, sharedGpuBytes;
        public long correctnessReadbackBytes, measurementReadbackBytes, timestampInstrumentationReadbackBytes, fixtureCpuBytes;
        public int validationCount;
        public Sample[] samples;
        public Sample[] controlSamples;
    }
    private sealed class Fixture
    {
        public uint[] keys, values;
        public GpuAdaptiveBinningFeatures features;
        public GpuAdaptiveBinningCpuOracle oracle;
    }
    private readonly List<Sample> samples = new List<Sample>();
    private readonly List<Sample> controls = new List<Sample>();
    private Report report;
    private string output;
    private GpuAdaptiveBinningNativeTimestampBackend timestamps;
    private GpuTimestampSupport support;
    private GpuAdaptiveSpatialBinner binner;
    private GraphicsBuffer keys, values, counts, offsets, result, diagnostics;
    private CommandBuffer commands;
    private int[] histogram;
    private GpuAdaptiveBinningStableSelector selector;
    private GpuPrimitiveBackend primitive;
    private int n, c;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gpu-adaptive-runtime") < 0) return;
        var host = new GameObject("Adaptive runtime benchmark"); DontDestroyOnLoad(host);
        host.AddComponent<GpuAdaptiveRuntimeBenchmark>();
    }
    private IEnumerator Start()
    {
        // Flatten nested iterators so all failures produce an explicit failed report/exit code.
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count > 0)
        {
            object next = null; bool moved = false; Exception failure = null;
            try { moved = stack.Peek().MoveNext(); if (moved) next = stack.Peek().Current; }
            catch (Exception e) { failure = e; }
            if (failure != null)
            {
                if (report != null) { report.failure = failure.ToString(); report.passed = false; }
                UnityEngine.Debug.LogException(failure); break;
            }
            if (!moved) { stack.Pop(); continue; }
            if (next is IEnumerator nested) { stack.Push(nested); continue; }
            yield return next;
        }
        if (report != null && !string.IsNullOrEmpty(output))
        {
            report.samples = samples.ToArray(); report.controlSamples = controls.ToArray();
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "runtime-report.json"), JsonUtility.ToJson(report, true));
        }
        DisposeResources();
        Application.Quit(report != null && report.passed ? 0 : 1);
    }
    private IEnumerator Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        output = Arg(args, "-runtime-output", null) ?? throw new ArgumentException("Missing output directory");
        string phase = Arg(args, "-runtime-phase", "smoke");
        if (phase != "smoke" && phase != "discovery" && phase != "evaluation") throw new ArgumentException("Invalid phase");
        n = int.Parse(Arg(args, "-runtime-n", "4096")); c = int.Parse(Arg(args, "-runtime-c", "16"));
        int frames = int.Parse(Arg(args, "-runtime-frames", "6"));
        int repeats = int.Parse(Arg(args, "-runtime-repeats", "1"));
        int seed = int.Parse(Arg(args, "-runtime-seed", "9701"));
        if (n < c || c < 16 || n > Summit.GpuPrimitives.GpuPrimitives.MaxElementCount ||
            frames < 3 || repeats < 1 || frames > 10000 || repeats > 100) throw new ArgumentException("Invalid bounded fixture dimensions");
        primitive = phase == "smoke" && !Summit.GpuPrimitives.GpuPrimitives.SupportsWaveOperations
            ? GpuPrimitiveBackend.Portable : GpuPrimitiveBackend.WaveOps;
        var environment = GpuCalibrationEnvironment.Capture(
            Arg(args, "-runtime-compiler", null), Arg(args, "-runtime-shader", null), Arg(args, "-runtime-build", null));
        if (!environment.IsValid) throw new ArgumentException("Independent build identity is required");
        report = new Report { phase = phase, runId = Guid.NewGuid().ToString("N"), seed = seed,
            framesPerSegment = frames, repeats = repeats, primitiveCandidateId = primitive.ToString(), device = GpuDeviceFingerprint.Capture(Arg(args, "-runtime-driver", null)), environment = environment };
        if (report.device.vendorId != 0x1002 || report.device.deviceId != 0x7551 || report.device.graphicsApi != "Direct3D12")
            throw new PlatformNotSupportedException("This protocol is R9700 / DX12 only");
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
        histogram = new int[c];
        var fixtures = new Fixture[4];
        for (int i = 0; i < fixtures.Length; i++) fixtures[i] = CreateFixture(i, seed);
        var document = new GpuAdaptiveBinningMatrixDocument { matrixId = "smoke-only-not-performance-evidence",
            revision = 1, phase = "frozen", calibrationRunId = "synthetic-correctness-only",
            device = report.device, environment = environment, rows = new GpuAdaptiveBinningMatrixRow[4] };
        for (int i = 0; i < 4; i++) document.rows[i] = new GpuAdaptiveBinningMatrixRow
        { features = fixtures[i].features, backend = i == 1 ? GpuAdaptiveBinningBackend.Radix : GpuAdaptiveBinningBackend.Direct,
          primitiveCandidateId = primitive.ToString(), validationPassed = true,
          evidenceId = "synthetic-smoke-test", calibrationSamples = 3 };
        if (phase == "evaluation")
        {
            string matrixText = File.ReadAllText(Arg(args, "-runtime-matrix", null));
            document = JsonUtility.FromJson<GpuAdaptiveBinningMatrixDocument>(matrixText);
            report.matrixSha256 = Sha256(System.Text.Encoding.UTF8.GetBytes(matrixText));
            if (document.calibrationSeeds == null || document.calibrationSeeds.Length == 0 ||
                Array.IndexOf(document.calibrationSeeds, seed) >= 0) throw new InvalidOperationException("Evaluation requires an unseen seed");
            if (document.calibrationRunId == "synthetic-correctness-only") throw new InvalidOperationException("Smoke profiles cannot be evaluated");
        }
        var matrix = phase == "discovery" ? null : new GpuAdaptiveBinningMatrix(document);
        if (phase == "evaluation" && (!matrix.IsValid || !document.device.Equals(report.device) ||
            !document.environment.Matches(environment))) throw new InvalidOperationException("Frozen matrix incompatible with running artifact");
        selector = new GpuAdaptiveBinningStableSelector(matrix, report.device, environment);
        binner = new GpuAdaptiveSpatialBinner(n, c, false);
        keys = Buffer(n); values = Buffer(n); counts = Buffer(c); offsets = Buffer(c + 1);
        result = Buffer(n); diagnostics = Buffer(2); commands = new CommandBuffer();
        report.persistentGpuScratchBytes = binner.UnionScratchBytes;
        report.cpuFeatureScratchBytes = (long)c * sizeof(int);
        report.fixtureCpuBytes = 4L * n * 2 * sizeof(uint);
        report.sharedGpuBytes = ((long)n * 3 + (long)c * 2 + 3) * sizeof(uint);
        if (!GpuAdaptiveBinningNativeTimestampBackend.TryCreate(out timestamps, out support))
            throw new InvalidOperationException("Native GPU timestamps required: " + support.Message);
        timestamps.ExecuteFrequencyInitialization(); yield return null;
        for (int i = 0; i < 3; i++) yield return Measure(fixtures[0], "Control", -1, i, -1, true);
        // Warm all kernels and native timestamp callbacks without using warmup data for calibration.
        foreach (var fixture in fixtures)
            foreach (string variant in new[] { "Direct", "Radix", "Adaptive" })
            {
                selector.Reset();
                for (int warmup = 0; warmup < 3; warmup++) yield return Measure(fixture, variant, -1, warmup, -1, false);
                Validate(fixture);
            }
        samples.Clear(); selector.Reset();
        string[] variants = phase == "discovery" ? new[] { "Direct", "Radix", "Radix", "Direct" }
            : new[] { "Direct", "Radix", "Adaptive", "Adaptive", "Radix", "Direct" };
        int[] trace = { 0, 1, 0, 2, 1, 3 }; // uniform -> single -> uniform -> hot4 -> single -> hot16
        for (int repeat = 0; repeat < repeats; repeat++)
            foreach (string variant in variants)
            {
                selector.Reset();
                for (int segment = 0; segment < trace.Length; segment++)
                {
                    Fixture fixture = fixtures[trace[segment]];
                    for (int observation = 0; observation < frames; observation++)
                        yield return Measure(fixture, variant, segment, observation, repeat, true);
                    Validate(fixture); // Outside timed region; output validation never feeds selection.
                }
            }
        for (int i = 0; i < 3; i++) yield return Measure(fixtures[0], "Control", -2, i, -1, true);
        report.passed = true;
    }
    private Fixture CreateFixture(int distribution, int seed)
    {
        var fixture = new Fixture { keys = new uint[n], values = new uint[n] };
        // Exact histogram + a seeded permutation gives unseen ordering while preserving exact cells.
        for (int i = 0; i < n; i++)
        {
            fixture.keys[i] = distribution == 1 ? 7u : distribution == 2 && i % 8 != 0 ? (uint)(i % 4)
                : distribution == 3 && i % 8 != 0 ? (uint)(i % 16) : (uint)(i % c);
            fixture.values[i] = (uint)i;
        }
        var random = new System.Random(seed + distribution);
        for (int i = n - 1; i > 0; i--) { int j = random.Next(i + 1); uint temp = fixture.keys[i]; fixture.keys[i] = fixture.keys[j]; fixture.keys[j] = temp; }
        string id = "r9700-exact-histogram-v1/" + new[] { "uniform", "singlebin", "hotset4", "hotset16" }[distribution];
        var concentration = distribution == 1 ? GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed
            : distribution == 0 ? GpuAdaptiveBinningWorkloadConcentration.General : GpuAdaptiveBinningWorkloadConcentration.Hotset;
        if (!GpuAdaptiveBinningFeatures.TryGather(fixture.keys, n, c, id, concentration, histogram, out fixture.features))
            throw new InvalidOperationException("Invalid fixture");
        fixture.oracle = new GpuAdaptiveBinningCpuOracle(fixture.keys, fixture.values, c);
        return fixture;
    }
    private IEnumerator Measure(Fixture fixture, string variant, int segment, int observation, int repeat, bool retain)
    {
        var status = timestamps.Acquire((ulong)(samples.Count + controls.Count + 1),
            variant == "Control" ? GpuTimestampSampleFlags.EmptyScope : GpuTimestampSampleFlags.None,
            Time.frameCount, out GpuTimestampToken token);
        if (status != GpuTimestampStatus.Ready) throw new InvalidOperationException("Timestamp acquire: " + status);
        var sample = new Sample { sequence = samples.Count, segment = segment, observation = observation,
            repeat = repeat, variant = variant, features = fixture.features };
        commands.Clear(); timestamps.RecordBegin(token.ScopeIndex, commands);
        long allocations = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
        var features = fixture.features;
        if (variant != "Control" && !GpuAdaptiveBinningFeatures.TryGather(fixture.keys, n, c, fixture.features.workloadId,
            fixture.features.concentration, histogram, out features)) throw new InvalidOperationException("Feature gathering failed");
        long featureEnd = Stopwatch.GetTimestamp();
        if (variant != "Control") { keys.SetData(fixture.keys); values.SetData(fixture.values); }
        long uploadEnd = Stopwatch.GetTimestamp();
        GpuAdaptiveBinningDecision decision = default;
        if (variant == "Adaptive")
        {
            decision = binner.RecordAdaptive(commands, keys, values, counts, offsets, result, diagnostics,
                n, c, GpuAdaptiveBinningKeyDomain.GuaranteedInRange, selector, in features, primitive);
        }
        else if (variant != "Control")
        {
            var backend = variant == "Direct" ? GpuAdaptiveBinningBackend.Direct : GpuAdaptiveBinningBackend.Radix;
            binner.Record(commands, keys, values, counts, offsets, result, diagnostics,
                n, c, backend, GpuAdaptiveBinningKeyDomain.GuaranteedInRange, primitive);
            sample.selectedBackend = variant; sample.reason = "forced-reference";
        }
        long recordEnd = Stopwatch.GetTimestamp();
        timestamps.RecordEnd(token.ScopeIndex, commands);
        status = timestamps.MarkSubmitted(token);
        if (status != GpuTimestampStatus.Ready) throw new InvalidOperationException("Timestamp submit failed: " + status);
        long submitStart = Stopwatch.GetTimestamp(); Graphics.ExecuteCommandBuffer(commands);
        long end = Stopwatch.GetTimestamp();
        sample.managedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
        if (variant == "Adaptive")
        {
            sample.selectedBackend = decision.Backend.ToString(); sample.reason = decision.Reason.ToString();
            sample.switched = decision.Switched; sample.selectorCpuMs = Ms(decision.SelectorCpuTicks);
            sample.switchStateCpuMs = Ms(decision.SwitchStateCpuTicks);
        }
        sample.featureCpuMs = Ms(featureEnd - start); sample.uploadCpuMs = Ms(uploadEnd - featureEnd);
        sample.recordCpuMs = Ms(recordEnd - uploadEnd); sample.submitCpuMs = Ms(end - submitStart);
        sample.pipelineCpuMs = Ms(end - start);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        GpuTimestampResult timing;
        do
        {
            yield return null;
            status = timestamps.TryConsume(token, Time.frameCount, out timing);
        } while (status == GpuTimestampStatus.Pending && Time.realtimeSinceStartupAsDouble < deadline);
        if (status != GpuTimestampStatus.Ready || timing.Token.Value != token.Value ||
            timing.Token.UserTag != token.UserTag || timing.SourceFrame != token.SourceFrame ||
            timing.TimestampFrequency == 0 || timing.FenceValue == 0 || timing.EndTicks < timing.BeginTicks ||
            timing.DeviceGeneration != support.DeviceGeneration || timing.NativeFlags != (uint)token.Flags)
            throw new InvalidOperationException("Invalid/incomplete timestamp: " + status);
        sample.gpuMs = timing.ElapsedMilliseconds; sample.timestampToken = timing.Token.Value;
        sample.timestampFence = timing.FenceValue; sample.timestampFrequency = timing.TimestampFrequency;
        sample.timestampBeginTicks = timing.BeginTicks; sample.timestampEndTicks = timing.EndTicks;
        sample.timestampElapsedTicks = timing.ElapsedTicks; sample.timestampFlags = timing.NativeFlags;
        sample.sourceFrame = timing.SourceFrame; sample.resultFrame = timing.ResultFrame; sample.deviceGeneration = timing.DeviceGeneration;
        report.timestampInstrumentationReadbackBytes += 16;
        if (retain) { if (variant == "Control") controls.Add(sample); else samples.Add(sample); }
    }
    private void Validate(Fixture fixture)
    {
        uint[] actualCounts = new uint[c], actualOffsets = new uint[c + 1], actualValues = new uint[n], actualDiagnostics = new uint[2];
        counts.GetData(actualCounts); offsets.GetData(actualOffsets); result.GetData(actualValues); diagnostics.GetData(actualDiagnostics);
        report.correctnessReadbackBytes += ((long)c * 2 + n + 3) * sizeof(uint);
        report.validationCount++;
        if (!fixture.oracle.Validate(actualCounts, actualOffsets, actualValues, actualDiagnostics, out string message, out _))
            throw new InvalidOperationException("Independent CSR oracle failed: " + message);
    }
    private static GraphicsBuffer Buffer(int count) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static string Arg(string[] args, string key, string fallback)
    { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Sha256(byte[] bytes)
    { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private void OnDisable() => DisposeResources();
    private void DisposeResources()
    {
        timestamps?.Dispose(); timestamps = null; commands?.Dispose(); commands = null;
        binner?.Dispose(); binner = null; keys?.Dispose(); keys = null; values?.Dispose(); values = null;
        counts?.Dispose(); counts = null; offsets?.Dispose(); offsets = null;
        result?.Dispose(); result = null; diagnostics?.Dispose(); diagnostics = null;
    }
}
