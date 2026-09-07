using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[DefaultExecutionOrder(10000)]
public sealed class GpuResidencyBenchmarkController : MonoBehaviour
{
    private const string EnableArgument = "-gpu-residency-benchmark";
    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private readonly List<RawSample> rawSamples =
        new List<RawSample>(20000);
    private readonly List<BlockSummary> blocks =
        new List<BlockSummary>(20);
    private readonly List<ValidationRow> validations =
        new List<ValidationRow>(24);

    private GpuResidencyBenchmarkAdapter adapter;
    private GpuResidencyNativeTimestampBackend timestamps;
    private GpuTimestampSupport timestampSupport;
    private bool timestampWarmupPassed;
    private string timestampWarmupStatus = "not-run";
    private bool finished;
    private int processId;

    private string reportDirectory;
    private string scenarioId = "custom";
    private string buildCommit = "unknown";
    private int superRounds = 4;
    private int warmupFrames = 30;
    private int sampleFrames = 240;
    private int cooldownFrames = 5;
    private int virtualPages = 4096;
    private int physicalSlots = 384;
    private int pointsPerPage = 1024;
    private bool compareLruPolicies;
    private int seed = 20260804;
    private float timeoutSeconds = 60.0f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigureLogging()
    {
        if (HasArgument(Environment.GetCommandLineArgs(), EnableArgument))
        {
            Debug.unityLogger.filterLogType = LogType.Warning;
            Application.SetStackTraceLogType(
                LogType.Log,
                StackTraceLogType.None);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArgument(args, EnableArgument))
        {
            return;
        }
        var host = new GameObject("GPU Residency Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuResidencyBenchmarkController controller =
            host.AddComponent<GpuResidencyBenchmarkController>();
        controller.Configure(args);
    }

    private void Start()
    {
        StartCoroutine(RunGuarded());
    }

    private void OnDisable()
    {
        DisposeResources();
    }

    private void Configure(string[] args)
    {
        compareLruPolicies = HasArgument(args, "-gpu-residency-compare-lru");
        reportDirectory = ReadString(
            args, "-gpu-residency-report-dir", string.Empty);
        scenarioId = ReadString(
            args, "-gpu-residency-scenario-id", scenarioId);
        buildCommit = ReadString(
            args, "-gpu-residency-build-commit", buildCommit);
        superRounds = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-super-rounds", superRounds), 1, 4);
        warmupFrames = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-warmup-frames", warmupFrames), 1, 300);
        sampleFrames = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-sample-frames", sampleFrames), 64, 1800);
        cooldownFrames = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-cooldown-frames", cooldownFrames), 0, 120);
        virtualPages = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-virtual-pages", virtualPages), 4096, 65536);
        physicalSlots = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-physical-slots", physicalSlots), 256, 32768);
        pointsPerPage = Mathf.Clamp(ReadInt(
            args, "-gpu-residency-points-per-page", pointsPerPage), 64, 16384);
        seed = ReadInt(args, "-gpu-residency-seed", seed);
        timeoutSeconds = Mathf.Clamp(ReadFloat(
            args, "-gpu-residency-timeout-seconds", timeoutSeconds),
            5.0f, 600.0f);
    }

    private IEnumerator RunGuarded()
    {
        Stack<IEnumerator> stack = new Stack<IEnumerator>();
        stack.Push(Run());
        while (stack.Count > 0)
        {
            IEnumerator current = stack.Peek();
            bool moved = false;
            object yielded = null;
            Exception failure = null;
            try
            {
                moved = current.MoveNext();
                if (moved)
                {
                    yielded = current.Current;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (failure != null)
            {
                while (stack.Count > 0)
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                }
                HandleFailure(failure);
                yield break;
            }
            if (!moved)
            {
                (stack.Pop() as IDisposable)?.Dispose();
            }
            else if (yielded is IEnumerator nested)
            {
                stack.Push(nested);
            }
            else
            {
                yield return yielded;
            }
        }
    }

    private IEnumerator Run()
    {
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        processId = Process.GetCurrentProcess().Id;
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            throw new InvalidOperationException(
                "A benchmark report directory is required.");
        }
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);

        adapter = new GpuResidencyBenchmarkAdapter(
            virtualPages,
            physicalSlots,
            pointsPerPage,
            seed, compareLruPolicies);
        if (!GpuResidencyNativeTimestampBackend.TryCreate(
                out timestamps,
                out timestampSupport))
        {
            throw new InvalidOperationException(
                "Native DX12 timestamp backend unavailable: " +
                timestampSupport.Message);
        }
        timestamps.InitializeFrequency();
        WriteConfiguration();
        WriteDeviceMetadata();
        yield return WarmupTimestampBackend();
        if (!timestampWarmupPassed)
        {
            throw new InvalidOperationException(
                "Native timestamp warmup failed: " + timestampWarmupStatus);
        }

        bool correct = true;
        yield return ValidateBoth("before", passed => correct &= passed);
        IReadOnlyList<GpuResidencyScheduleEntry> schedule =
            GpuResidencyBenchmarkSchedule.Build(superRounds);
        foreach (GpuResidencyScheduleEntry entry in schedule)
        {
            adapter.Reset(entry.Variant);
            for (int cooldown = 0; cooldown < cooldownFrames; cooldown++)
            {
                yield return null;
            }
            for (int warmup = 0; warmup < warmupFrames; warmup++)
            {
                SampleResult ignored = null;
                yield return IssueSample(
                    entry.Variant,
                    warmup,
                    measured: false,
                    result => ignored = result);
            }

            int start = rawSamples.Count;
            for (int sample = 0; sample < sampleFrames; sample++)
            {
                SampleResult result = null;
                int pathFrame = checked(warmupFrames + sample);
                yield return IssueSample(
                    entry.Variant,
                    pathFrame,
                    measured: true,
                    value => result = value);
                rawSamples.Add(CreateRaw(entry, sample + 1, pathFrame, result));
            }
            blocks.Add(SummarizeBlock(entry, start, sampleFrames));
            GpuResidencyValidationResult validation =
                adapter.ValidateLastFrame();
            validations.Add(ToValidation(
                "block-" + entry.BlockIndex,
                entry.Variant,
                validation));
            correct &= validation.Passed;
            if (!correct)
            {
                break;
            }
        }

        if (correct)
        {
            yield return ValidateBoth("after", passed => correct &= passed);
        }
        string status = correct ? "completed" : "correctness-failed";
        WriteOutputs(correct, status);
        Finish(correct, status);
    }

    private IEnumerator WarmupTimestampBackend()
    {
        GpuTimestampStatus status = timestamps.Acquire(
            ulong.MaxValue,
            GpuTimestampSampleFlags.EmptyScope,
            Time.frameCount,
            out GpuTimestampToken token);
        if (status != GpuTimestampStatus.Ready)
        {
            timestampWarmupStatus = "acquire-" + status;
            yield break;
        }
        status = timestamps.MarkSubmitted(token);
        if (status != GpuTimestampStatus.Ready)
        {
            timestamps.Cancel(token);
            timestampWarmupStatus = "submit-" + status;
            yield break;
        }
        using (var commands = new CommandBuffer
               {
                   name = "GPU.Residency/TimestampWarmup"
               })
        {
            timestamps.RecordBegin(token.ScopeIndex, commands);
            timestamps.RecordEnd(token.ScopeIndex, commands);
            Graphics.ExecuteCommandBuffer(commands);
        }
        double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            status = timestamps.TryConsume(
                token,
                Time.frameCount,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                yield return null;
                continue;
            }
            timestampWarmupPassed =
                status == GpuTimestampStatus.Ready &&
                result.TimestampFrequency > 0 &&
                result.EndTicks >= result.BeginTicks;
            timestampWarmupStatus = timestampWarmupPassed
                ? "ready"
                : "result-" + status;
            yield break;
        }
        timestampWarmupStatus = "timeout";
    }

    private IEnumerator IssueSample(
        GpuResidencyBenchmarkVariant variant,
        int pathFrame,
        bool measured,
        Action<SampleResult> completion)
    {
        GpuResidencyFramePreparation preparation =
            adapter.PrepareFrame(variant, pathFrame);
        CommandBuffer commands = adapter.FrameCommands;
        commands.Clear();
        commands.name = "GPU.Residency/" + adapter.VariantName(variant);

        GpuTimestampToken token = default;
        if (measured)
        {
            GpuTimestampStatus acquire = timestamps.Acquire(
                checked((ulong)rawSamples.Count + 1UL),
                GpuTimestampSampleFlags.None,
                Time.frameCount,
                out token);
            if (acquire != GpuTimestampStatus.Ready)
            {
                throw new InvalidOperationException(
                    "Timestamp acquire failed: " + acquire);
            }
            GpuTimestampStatus submitted = timestamps.MarkSubmitted(token);
            if (submitted != GpuTimestampStatus.Ready)
            {
                timestamps.Cancel(token);
                throw new InvalidOperationException(
                    "Timestamp submission failed: " + submitted);
            }
            timestamps.RecordBegin(token.ScopeIndex, commands);
        }
        double recordMs = adapter.RecordFrame(preparation);
        if (measured)
        {
            timestamps.RecordEnd(token.ScopeIndex, commands);
        }
        GraphicsFence fence = commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
        long submitStart = Stopwatch.GetTimestamp();
        Graphics.ExecuteCommandBuffer(commands);
        double submitMs = ElapsedMilliseconds(submitStart);

        long waitStart = Stopwatch.GetTimestamp();
        long timeoutTicks = checked(
            waitStart + (long)(timeoutSeconds * Stopwatch.Frequency));
        while (!fence.passed)
        {
            if (Stopwatch.GetTimestamp() >= timeoutTicks)
            {
                throw new TimeoutException("GPU residency frame timed out.");
            }
            Thread.SpinWait(32);
        }
        double observedMs = ElapsedMilliseconds(submitStart);

        double gpuMs = 0.0;
        if (measured)
        {
            double deadline =
                Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                GpuTimestampStatus status = timestamps.TryConsume(
                    token,
                    Time.frameCount,
                    out GpuTimestampResult result);
                if (status == GpuTimestampStatus.Pending)
                {
                    yield return null;
                    continue;
                }
                if (status != GpuTimestampStatus.Ready)
                {
                    throw new InvalidOperationException(
                        "Timestamp result failed: " + status);
                }
                gpuMs = result.ElapsedMilliseconds;
                break;
            }
            if (gpuMs <= 0.0)
            {
                throw new TimeoutException("GPU timestamp result timed out.");
            }
        }
        completion(new SampleResult
        {
            Preparation = preparation,
            RecordMs = recordMs,
            SubmitMs = submitMs,
            ObservedMs = observedMs,
            GpuMs = gpuMs
        });
    }

    private IEnumerator ValidateBoth(string phase, Action<bool> completion)
    {
        bool passed = true;
        foreach (GpuResidencyBenchmarkVariant variant in new[]
        {
            GpuResidencyBenchmarkVariant.RebuildVisibleSet,
            GpuResidencyBenchmarkVariant.PersistentLru
        })
        {
            adapter.Reset(variant);
            SampleResult ignored = null;
            yield return IssueSample(
                variant,
                0,
                measured: false,
                value => ignored = value);
            GpuResidencyValidationResult result =
                adapter.ValidateLastFrame();
            validations.Add(ToValidation(phase, variant, result));
            passed &= result.Passed;
        }
        completion(passed);
    }

    private RawSample CreateRaw(
        GpuResidencyScheduleEntry entry,
        int sampleIndex,
        int pathFrame,
        SampleResult result)
    {
        GpuResidencyFramePreparation p = result.Preparation;
        return new RawSample
        {
            ProcessId = processId,
            ScenarioId = scenarioId,
            BlockIndex = entry.BlockIndex,
            SuperRound = entry.SuperRound,
            SequencePosition = entry.SequencePosition,
            PairIndex = entry.PairIndex,
            PairOrder = entry.PairOrder,
            WithinPairPosition = entry.WithinPairPosition,
            CaseId = adapter.CaseId(entry.Variant),
            Variant = adapter.VariantName(entry.Variant),
            SampleIndex = sampleIndex,
            PathFrame = pathFrame,
            GpuMs = result.GpuMs,
            ObservedMs = result.ObservedMs,
            PlanningMs = p.PlanningMs,
            PlanningAllocatedBytes = p.PlanningAllocatedBytes,
            StagingMs = p.StagingMs,
            RecordMs = result.RecordMs,
            SubmitMs = result.SubmitMs,
            RequestedPages = p.Plan.RequestedCount,
            HitPages = p.Plan.HitCount,
            MissPages = p.Plan.MissCount,
            Evictions = p.Plan.EvictionCount,
            PointPayloadBytes = p.PointPayloadBytes,
            DescriptorBytes = p.DescriptorBytes,
            DeltaBytes = p.DeltaBytes,
            RequestBytes = p.RequestBytes,
            TotalUploadBytes = p.TotalUploadBytes,
            MeasurementReadbackBytes = 0
        };
    }

    private BlockSummary SummarizeBlock(
        GpuResidencyScheduleEntry entry,
        int start,
        int count)
    {
        RawSample[] rows = rawSamples.Skip(start).Take(count).ToArray();
        return new BlockSummary
        {
            ScenarioId = scenarioId,
            BlockIndex = entry.BlockIndex,
            PairIndex = entry.PairIndex,
            PairOrder = entry.PairOrder,
            Variant = adapter.VariantName(entry.Variant),
            SampleCount = count,
            GpuAverageMs = rows.Average(row => row.GpuMs),
            GpuP99Ms = P99(rows.Select(row => row.GpuMs)),
            CpuPreparationAverageMs = rows.Average(
                row => row.PlanningMs + row.StagingMs),
            CpuPreparationP99Ms = P99(rows.Select(
                row => row.PlanningMs + row.StagingMs)),
            UploadAverageBytes = rows.Average(
                row => (double)row.TotalUploadBytes),
            UploadP99Bytes = P99(rows.Select(
                row => (double)row.TotalUploadBytes)),
            MissRate = rows.Average(
                row => row.MissPages / (double)row.RequestedPages),
            AverageEvictions = rows.Average(
                row => (double)row.Evictions)
        };
    }

    private ValidationRow ToValidation(
        string phase,
        GpuResidencyBenchmarkVariant variant,
        GpuResidencyValidationResult result)
    {
        return new ValidationRow
        {
            Phase = phase,
            Variant = adapter.VariantName(variant),
            Passed = result.Passed,
            Message = result.Message,
            ResultHash = result.ResultHash,
            ReadbackBytes = result.ReadbackBytes
        };
    }

    private void WriteOutputs(bool passed, string status)
    {
        WriteRaw();
        WriteBlocks();
        WriteValidations();
        WriteRunSummary(passed, status);
    }

    private void WriteRaw()
    {
        var b = new StringBuilder();
        b.AppendLine(
            "processId,scenarioId,blockIndex,superRound,sequencePosition," +
            "pairIndex,pairOrder,withinPairPosition,caseId,variant," +
            "sampleIndex,pathFrame,gpuMs,observedMs,planningMs,stagingMs," +
            "recordMs,submitMs,requestedPages,hitPages,missPages,evictions," +
            "pointPayloadBytes,descriptorBytes,deltaBytes,requestBytes," +
            "totalUploadBytes,measurementReadbackBytes,planningAllocatedBytes");
        foreach (RawSample r in rawSamples)
        {
            b.Append(r.ProcessId).Append(',').Append(Csv(r.ScenarioId))
                .Append(',').Append(r.BlockIndex).Append(',')
                .Append(r.SuperRound).Append(',').Append(r.SequencePosition)
                .Append(',').Append(r.PairIndex).Append(',')
                .Append(r.PairOrder).Append(',').Append(r.WithinPairPosition)
                .Append(',').Append(Csv(r.CaseId)).Append(',')
                .Append(Csv(r.Variant)).Append(',').Append(r.SampleIndex)
                .Append(',').Append(r.PathFrame).Append(',')
                .Append(Number(r.GpuMs)).Append(',')
                .Append(Number(r.ObservedMs)).Append(',')
                .Append(Number(r.PlanningMs)).Append(',')
                .Append(Number(r.StagingMs)).Append(',')
                .Append(Number(r.RecordMs)).Append(',')
                .Append(Number(r.SubmitMs)).Append(',')
                .Append(r.RequestedPages).Append(',').Append(r.HitPages)
                .Append(',').Append(r.MissPages).Append(',')
                .Append(r.Evictions).Append(',').Append(r.PointPayloadBytes)
                .Append(',').Append(r.DescriptorBytes).Append(',')
                .Append(r.DeltaBytes).Append(',').Append(r.RequestBytes)
                .Append(',').Append(r.TotalUploadBytes).Append(',')
                .Append(r.MeasurementReadbackBytes).Append(',').Append(r.PlanningAllocatedBytes).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(reportDirectory, "raw-samples.csv"),
            b.ToString());
    }

    private void WriteBlocks()
    {
        var b = new StringBuilder();
        b.AppendLine(
            "scenarioId,blockIndex,pairIndex,pairOrder,variant,sampleCount," +
            "gpuAverageMs,gpuP99Ms,cpuPreparationAverageMs," +
            "cpuPreparationP99Ms,uploadAverageBytes,uploadP99Bytes," +
            "missRate,averageEvictions");
        foreach (BlockSummary r in blocks)
        {
            b.Append(Csv(r.ScenarioId)).Append(',').Append(r.BlockIndex)
                .Append(',').Append(r.PairIndex).Append(',')
                .Append(r.PairOrder).Append(',').Append(Csv(r.Variant))
                .Append(',').Append(r.SampleCount).Append(',')
                .Append(Number(r.GpuAverageMs)).Append(',')
                .Append(Number(r.GpuP99Ms)).Append(',')
                .Append(Number(r.CpuPreparationAverageMs)).Append(',')
                .Append(Number(r.CpuPreparationP99Ms)).Append(',')
                .Append(Number(r.UploadAverageBytes)).Append(',')
                .Append(Number(r.UploadP99Bytes)).Append(',')
                .Append(Number(r.MissRate)).Append(',')
                .Append(Number(r.AverageEvictions)).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(reportDirectory, "block-summary.csv"),
            b.ToString());
    }

    private void WriteValidations()
    {
        var b = new StringBuilder();
        b.AppendLine("phase,variant,passed,message,resultHash,readbackBytes");
        foreach (ValidationRow r in validations)
        {
            b.Append(Csv(r.Phase)).Append(',').Append(Csv(r.Variant))
                .Append(',').Append(r.Passed ? 1 : 0).Append(',')
                .Append(Csv(r.Message)).Append(',').Append(r.ResultHash)
                .Append(',').Append(r.ReadbackBytes).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(reportDirectory, "validation.csv"),
            b.ToString());
    }

    private void WriteConfiguration()
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, "config.json"),
            JsonUtility.ToJson(new ConfigEvidence
            {
                schemaVersion = 1,
                suite = "summit.gpu-residency-manager",
                scenarioId = scenarioId,
                buildCommit = buildCommit,
                superRounds = superRounds,
                warmupFrames = warmupFrames,
                sampleFrames = sampleFrames,
                cooldownFrames = cooldownFrames,
                virtualPages = virtualPages,
                physicalSlots = physicalSlots,
                requestedPages = GpuResidencyBenchmarkAdapter.RequestedPageCount,
                pointsPerPage = pointsPerPage,
                gpuResidentBytes = adapter.GpuResidentBytes,
                cpuBackingStoreBytes = adapter.CpuBackingStoreBytes,
                cpuStagingBytes = adapter.CpuStagingBytes,
                baselinePolicy = compareLruPolicies ? "persistent full-scan LRU v2" : "rebuild and upload all visible pages",
                optimizedPolicy = compareLruPolicies ? "persistent indexed-heap LRU v2 (unpromoted candidate)" : "persistent LRU physical slots with delta uploads",
                measurementReadbackBytesPerFrame = 0,
                sparseResourceClaim = false
            }, true));
    }

    private void WriteDeviceMetadata()
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, "device.json"),
            JsonUtility.ToJson(new DeviceEvidence
            {
                operatingSystem = SystemInfo.operatingSystem,
                processorType = SystemInfo.processorType,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMemoryMiB = SystemInfo.graphicsMemorySize,
                nativeTimestampAvailability =
                    timestampSupport.Availability.ToString(),
                nativeTimestampAbiVersion = timestampSupport.AbiVersion,
                nativeTimestampCapabilityFlags =
                    timestampSupport.CapabilityFlags,
                mainGraphicsQueueTimestamp = true,
                sparseResourceClaim = false
            }, true));
    }

    private void WriteRunSummary(bool passed, string status)
    {
        File.WriteAllLines(
            Path.Combine(reportDirectory, "run-summary.txt"),
            new[]
            {
                "GPU residency manager benchmark",
                "schemaVersion=1",
                "suite=summit.gpu-residency-manager",
                "passed=" + (passed ? 1 : 0),
                "status=" + status,
                "scenarioId=" + scenarioId,
                "rawSampleCount=" + rawSamples.Count,
                "blockCount=" + blocks.Count,
                "validationRows=" + validations.Count,
                "validationFailures=" + validations.Count(v => !v.Passed),
                "validationReadbackBytes=" +
                    validations.Sum(v => (long)v.ReadbackBytes),
                "measurementReadbackBytes=0",
                "nativeTimestampWarmupPassed=" +
                    (timestampWarmupPassed ? 1 : 0),
                "nativeTimestampWarmupStatus=" + timestampWarmupStatus,
                "sparseResourceClaim=0",
                "mainGraphicsQueueTimestamp=1"
            });
    }

    private void HandleFailure(Exception exception)
    {
        Debug.LogException(exception);
        try
        {
            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                reportDirectory = Path.GetFullPath(reportDirectory);
                Directory.CreateDirectory(reportDirectory);
                WriteOutputs(false, "unhandled-exception");
            }
        }
        catch (Exception evidenceFailure)
        {
            Debug.LogException(evidenceFailure);
        }
        Finish(false, "unhandled-exception");
    }

    private void Finish(bool passed, string status)
    {
        if (finished)
        {
            return;
        }
        finished = true;
        DisposeResources();
        Debug.unityLogger.filterLogType = LogType.Log;
        Debug.Log("GPU residency benchmark " + status);
        Application.Quit(passed ? 0 : 2);
    }

    private void DisposeResources()
    {
        timestamps?.Dispose();
        timestamps = null;
        adapter?.Dispose();
        adapter = null;
    }

    private static double P99(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        int index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.99) - 1);
        return sorted.Length == 0 ? 0.0 : sorted[index];
    }

    private static double ElapsedMilliseconds(long start)
    {
        return (Stopwatch.GetTimestamp() - start) *
            (1000.0 / Stopwatch.Frequency);
    }

    private static string Number(double value) =>
        value.ToString("R", Invariant);

    private static string Csv(string value)
    {
        string text = value ?? string.Empty;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static bool HasArgument(string[] args, string name) =>
        args.Any(arg => string.Equals(
            arg, name, StringComparison.OrdinalIgnoreCase));

    private static string ReadString(
        string[] args, string name, string fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(
                    args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return fallback;
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string value = ReadString(args, name, null);
        return int.TryParse(value, NumberStyles.Integer, Invariant, out int parsed)
            ? parsed : fallback;
    }

    private static float ReadFloat(
        string[] args, string name, float fallback)
    {
        string value = ReadString(args, name, null);
        return float.TryParse(value, NumberStyles.Float, Invariant, out float parsed)
            ? parsed : fallback;
    }

    private sealed class SampleResult
    {
        public GpuResidencyFramePreparation Preparation;
        public double RecordMs;
        public double SubmitMs;
        public double ObservedMs;
        public double GpuMs;
    }

    private sealed class RawSample
    {
        public int ProcessId;
        public string ScenarioId;
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public string CaseId;
        public string Variant;
        public int SampleIndex;
        public int PathFrame;
        public double GpuMs;
        public double ObservedMs;
        public long PlanningAllocatedBytes;
        public double PlanningMs;
        public double StagingMs;
        public double RecordMs;
        public double SubmitMs;
        public int RequestedPages;
        public int HitPages;
        public int MissPages;
        public int Evictions;
        public long PointPayloadBytes;
        public long DescriptorBytes;
        public long DeltaBytes;
        public long RequestBytes;
        public long TotalUploadBytes;
        public int MeasurementReadbackBytes;
    }

    private sealed class BlockSummary
    {
        public string ScenarioId;
        public int BlockIndex;
        public int PairIndex;
        public string PairOrder;
        public string Variant;
        public int SampleCount;
        public double GpuAverageMs;
        public double GpuP99Ms;
        public double CpuPreparationAverageMs;
        public double CpuPreparationP99Ms;
        public double UploadAverageBytes;
        public double UploadP99Bytes;
        public double MissRate;
        public double AverageEvictions;
    }

    private sealed class ValidationRow
    {
        public string Phase;
        public string Variant;
        public bool Passed;
        public string Message;
        public string ResultHash;
        public int ReadbackBytes;
    }

    [Serializable]
    private sealed class ConfigEvidence
    {
        public int schemaVersion;
        public string suite;
        public string scenarioId;
        public string buildCommit;
        public int superRounds;
        public int warmupFrames;
        public int sampleFrames;
        public int cooldownFrames;
        public int virtualPages;
        public int physicalSlots;
        public int requestedPages;
        public int pointsPerPage;
        public long gpuResidentBytes;
        public long cpuBackingStoreBytes;
        public long cpuStagingBytes;
        public string baselinePolicy;
        public string optimizedPolicy;
        public int measurementReadbackBytesPerFrame;
        public bool sparseResourceClaim;
    }

    [Serializable]
    private sealed class DeviceEvidence
    {
        public string operatingSystem;
        public string processorType;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public int graphicsMemoryMiB;
        public string nativeTimestampAvailability;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public bool mainGraphicsQueueTimestamp;
        public bool sparseResourceClaim;
    }
}
