using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Summit.GpuDeadlineScheduler;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[DefaultExecutionOrder(10000)]
public sealed class GpuDeadlineSchedulerBenchmarkController : MonoBehaviour
{
    private const string EnableArgument =
        "-gpu-deadline-scheduler-benchmark";
    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private readonly List<RawSample> rawSamples =
        new List<RawSample>(20000);
    private readonly List<BlockSummary> blockSummaries =
        new List<BlockSummary>(24);
    private readonly List<ValidationRow> validations =
        new List<ValidationRow>(24);

    private GpuDeadlineSchedulerBenchmarkAdapter adapter;
    private GpuDeadlineSchedulerNativeTimestampBackend timestampBackend;
    private GpuTimestampSupport timestampSupport;
    private bool timestampWarmupPassed;
    private string timestampWarmupStatus = "not-run";
    private bool finished;
    private int processId;

    private string reportDirectory;
    private string scenarioId = "custom";
    private string buildCommit = "unknown";
    private int superRounds = 4;
    private int warmupSamples = 20;
    private int sampleCount = 240;
    private int cooldownFrames = 5;
    private int workItems = 16384;
    private int criticalIterations = 48;
    private int normalIterations = 96;
    private int backgroundIterations = 192;
    private int pressureItems = 65536;
    private int pressureIterations = 128;
    private int criticalDeadlineUs = 2500;
    private int normalDeadlineUs = 6000;
    private int backgroundDeadlineUs = 18000;
    private int calibrationSamples = 12;
    private float timeoutSeconds = 60.0f;
    private bool selectedAsyncBackend;
    private double calibrationMainGpuAverageMs;
    private double calibrationMainGpuP99Ms;
    private double calibrationAsyncGpuAverageMs;
    private double calibrationAsyncGpuP99Ms;

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
        var host = new GameObject(
            "GPU Deadline Scheduler Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuDeadlineSchedulerBenchmarkController controller =
            host.AddComponent<GpuDeadlineSchedulerBenchmarkController>();
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
        reportDirectory = ReadString(
            args,
            "-gpu-deadline-report-dir",
            string.Empty);
        scenarioId = ReadString(
            args,
            "-gpu-deadline-scenario-id",
            scenarioId);
        buildCommit = ReadString(
            args,
            "-gpu-deadline-build-commit",
            buildCommit);
        superRounds = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-super-rounds",
            superRounds), 1, 4);
        warmupSamples = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-warmup-samples",
            warmupSamples), 1, 300);
        sampleCount = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-sample-count",
            sampleCount), 64, 1800);
        cooldownFrames = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-cooldown-frames",
            cooldownFrames), 0, 120);
        workItems = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-work-items",
            workItems), 256, 1048576);
        criticalIterations = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-critical-iterations",
            criticalIterations), 1, 4096);
        normalIterations = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-normal-iterations",
            normalIterations), 1, 4096);
        backgroundIterations = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-background-iterations",
            backgroundIterations), 1, 4096);
        pressureItems = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-pressure-items",
            pressureItems), 256, 1048576);
        pressureIterations = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-pressure-iterations",
            pressureIterations), 1, 4096);
        criticalDeadlineUs = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-critical-deadline-us",
            criticalDeadlineUs), 100, 1000000);
        normalDeadlineUs = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-normal-deadline-us",
            normalDeadlineUs), 100, 1000000);
        backgroundDeadlineUs = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-background-deadline-us",
            backgroundDeadlineUs), 100, 1000000);
        calibrationSamples = Mathf.Clamp(ReadInt(
            args,
            "-gpu-deadline-calibration-samples",
            calibrationSamples), 4, 64);
        timeoutSeconds = Mathf.Clamp(ReadFloat(
            args,
            "-gpu-deadline-timeout-seconds",
            timeoutSeconds), 5.0f, 600.0f);
    }

    private IEnumerator RunGuarded()
    {
        Stack<IEnumerator> stack = new Stack<IEnumerator>();
        stack.Push(Run());
        while (stack.Count != 0)
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
                while (stack.Count != 0)
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                }
                HandleFailure(failure);
                yield break;
            }
            if (!moved)
            {
                (stack.Pop() as IDisposable)?.Dispose();
                continue;
            }
            if (yielded is IEnumerator nested)
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
        if (!SystemInfo.supportsAsyncCompute)
        {
            throw new InvalidOperationException(
                "The active device does not support async compute; " +
                "the optimized variant would silently collapse to baseline.");
        }

        adapter = new GpuDeadlineSchedulerBenchmarkAdapter(
            workItems,
            criticalIterations,
            normalIterations,
            backgroundIterations,
            pressureItems,
            pressureIterations,
            criticalDeadlineUs,
            normalDeadlineUs,
            backgroundDeadlineUs,
            seed: 20260803);
        if (!GpuDeadlineSchedulerNativeTimestampBackend.TryCreate(
                out timestampBackend,
                out timestampSupport))
        {
            throw new InvalidOperationException(
                "Native DX12 timestamp backend unavailable: " +
                timestampSupport.Message);
        }
        timestampBackend.ExecuteFrequencyInitialization();
        WriteConfiguration();
        WriteDeviceMetadata();
        yield return WarmupTimestampBackend();
        if (!timestampWarmupPassed)
        {
            throw new InvalidOperationException(
                "Native timestamp warmup failed: " + timestampWarmupStatus);
        }

        yield return CalibrateOptimizedBackend();
        WriteConfiguration();
        WriteDeviceMetadata();

        bool correct = true;
        yield return ValidateBoth("before", passed => correct &= passed);
        if (!correct)
        {
            WriteOutputs(false, "before-validation-failed");
            Finish(false, "before-validation-failed");
            yield break;
        }

        IReadOnlyList<GpuDeadlineSchedulerScheduleEntry> schedule =
            GpuDeadlineSchedulerBenchmarkSchedule.Build(superRounds);
        foreach (GpuDeadlineSchedulerScheduleEntry entry in schedule)
        {
            for (int frame = 0; frame < cooldownFrames; frame++)
            {
                yield return null;
            }
            for (int warmup = 0; warmup < warmupSamples; warmup++)
            {
                SampleResult ignored = null;
                yield return IssueSample(
                    entry.Variant,
                    checked((uint)GpuDeadlineSchedulerBenchmarkSchedule.
                        LogicalState(warmup)),
                    measured: false,
                    result => ignored = result);
            }

            int sampleStart = rawSamples.Count;
            for (int sample = 0; sample < sampleCount; sample++)
            {
                SampleResult result = null;
                yield return IssueSample(
                    entry.Variant,
                    checked((uint)GpuDeadlineSchedulerBenchmarkSchedule.
                        LogicalState(sample)),
                    measured: true,
                    value => result = value);
                rawSamples.Add(CreateRawSample(entry, sample + 1, result));
            }
            blockSummaries.Add(SummarizeBlock(
                entry,
                sampleStart,
                sampleCount));

            if (entry.WithinPairPosition == 2)
            {
                bool pairCorrect = true;
                yield return ValidateBoth(
                    "post-pair-" + entry.PairIndex,
                    passed => pairCorrect &= passed);
                correct &= pairCorrect;
                if (!correct)
                {
                    break;
                }
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
        GpuTimestampStatus status = timestampBackend.Acquire(
            ulong.MaxValue,
            GpuTimestampSampleFlags.EmptyScope,
            Time.frameCount,
            out GpuTimestampToken token);
        if (status != GpuTimestampStatus.Ready)
        {
            timestampWarmupStatus = "acquire-" + status;
            yield break;
        }
        status = timestampBackend.MarkSubmitted(token);
        if (status != GpuTimestampStatus.Ready)
        {
            timestampBackend.Cancel(token);
            timestampWarmupStatus = "submit-" + status;
            yield break;
        }
        using (var commands = new CommandBuffer
               {
                   name = "GPU.DeadlineScheduler/TimestampWarmup"
               })
        {
            timestampBackend.RecordBegin(token.ScopeIndex, commands);
            timestampBackend.RecordEnd(token.ScopeIndex, commands);
            Graphics.ExecuteCommandBuffer(commands);
        }
        double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            status = timestampBackend.TryConsume(
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
        GpuDeadlineSchedulerBenchmarkVariant variant,
        uint logicalState,
        bool measured,
        Action<SampleResult> completion,
        bool? optimizedAsyncOverride = null)
    {
        GpuTimestampToken token = default;
        bool timestampSubmitted = false;
        if (measured)
        {
            GpuTimestampStatus status = timestampBackend.Acquire(
                checked((ulong)rawSamples.Count + 1UL),
                GpuTimestampSampleFlags.None,
                Time.frameCount,
                out token);
            if (status != GpuTimestampStatus.Ready)
            {
                throw new InvalidOperationException(
                    "Timestamp acquire failed: " + status);
            }
            status = timestampBackend.MarkSubmitted(token);
            if (status != GpuTimestampStatus.Ready)
            {
                timestampBackend.Cancel(token);
                throw new InvalidOperationException(
                    "Timestamp submission failed: " + status);
            }
            timestampSubmitted = true;
        }

        long releaseTicks = Stopwatch.GetTimestamp();
        bool optimizedUsesAsync = variant !=
            GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics &&
            (optimizedAsyncOverride ?? selectedAsyncBackend);
        GpuDeadlineBenchmarkSubmission submission = adapter.Submit(
            variant,
            logicalState,
            optimizedUsesAsync,
            timestampSubmitted
                ? commands => timestampBackend.RecordBegin(
                    token.ScopeIndex,
                    commands)
                : (Action<CommandBuffer>)null,
            timestampSubmitted
                ? commands => timestampBackend.RecordEnd(
                    token.ScopeIndex,
                    commands)
                : (Action<CommandBuffer>)null);

        var observed = new bool[submission.JobFences.Length];
        var latencies = new double[submission.JobFences.Length];
        long timeoutTicks = checked(
            releaseTicks +
            (long)(timeoutSeconds * Stopwatch.Frequency));
        while (!submission.JoinFence.passed)
        {
            PollJobFences(
                submission.JobFences,
                observed,
                latencies,
                releaseTicks);
            if (Stopwatch.GetTimestamp() >= timeoutTicks)
            {
                throw new TimeoutException(
                    "GPU deadline sample did not complete.");
            }
            Thread.SpinWait(32);
        }
        PollJobFences(
            submission.JobFences,
            observed,
            latencies,
            releaseTicks);
        double observedMakespanMs = ElapsedMilliseconds(releaseTicks);

        double gpuMakespanMs = 0.0;
        if (timestampSubmitted)
        {
            double deadline =
                Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                GpuTimestampStatus status = timestampBackend.TryConsume(
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
                gpuMakespanMs = result.ElapsedMilliseconds;
                break;
            }
            if (gpuMakespanMs <= 0.0)
            {
                throw new TimeoutException(
                    "Native GPU timestamp result timed out.");
            }
        }

        completion(BuildSampleResult(
            submission,
            latencies,
            observedMakespanMs,
            gpuMakespanMs));
    }

    private IEnumerator ValidateBoth(
        string phase,
        Action<bool> completion)
    {
        bool passed = true;
        foreach (GpuDeadlineSchedulerBenchmarkVariant variant in new[]
        {
            GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics,
            GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive
        })
        {
            const uint state = 37u;
            SampleResult ignored = null;
            yield return IssueSample(
                variant,
                state,
                measured: false,
                result => ignored = result);
            GpuDeadlineValidationResult validation =
                adapter.ValidateDigests(state);
            passed &= validation.Passed;
            validations.Add(new ValidationRow
            {
                Phase = phase,
                Variant = adapter.VariantName(
                    variant,
                    selectedAsyncBackend),
                Passed = validation.Passed,
                Message = validation.Message,
                ResultHash = validation.ResultHash,
                ReadbackBytes = validation.ReadbackBytes
            });
        }
        completion(passed);
    }

    private IEnumerator CalibrateOptimizedBackend()
    {
        var mainSamples = new List<double>(calibrationSamples);
        var asyncSamples = new List<double>(calibrationSamples);
        for (int warmup = 0; warmup < 4; warmup++)
        {
            SampleResult ignored = null;
            yield return IssueSample(
                GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive,
                checked((uint)warmup),
                measured: false,
                result => ignored = result,
                optimizedAsyncOverride: false);
            yield return IssueSample(
                GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive,
                checked((uint)warmup),
                measured: false,
                result => ignored = result,
                optimizedAsyncOverride: true);
        }
        for (int sample = 0; sample < calibrationSamples; sample++)
        {
            bool asyncFirst = (sample & 1) != 0;
            foreach (bool useAsync in asyncFirst
                ? new[] { true, false }
                : new[] { false, true })
            {
                SampleResult result = null;
                yield return IssueSample(
                    GpuDeadlineSchedulerBenchmarkVariant.
                        DeadlineAwareAdaptive,
                    checked((uint)(sample %
                        GpuDeadlineSchedulerBenchmarkSchedule.StateCount)),
                    measured: true,
                    value => result = value,
                    optimizedAsyncOverride: useAsync);
                (useAsync ? asyncSamples : mainSamples).Add(
                    result.GpuMakespanMs);
            }
        }

        calibrationMainGpuAverageMs = mainSamples.Average();
        calibrationMainGpuP99Ms = Percentile99(mainSamples);
        calibrationAsyncGpuAverageMs = asyncSamples.Average();
        calibrationAsyncGpuP99Ms = Percentile99(asyncSamples);
        selectedAsyncBackend =
            calibrationAsyncGpuAverageMs <
                calibrationMainGpuAverageMs * 0.98 &&
            calibrationAsyncGpuP99Ms <= calibrationMainGpuP99Ms;
    }

    private static void PollJobFences(
        GpuDeadlineJobFence[] fences,
        bool[] observed,
        double[] latencies,
        long releaseTicks)
    {
        for (int index = 0; index < fences.Length; index++)
        {
            if (!observed[index] && fences[index].Fence.passed)
            {
                observed[index] = true;
                latencies[index] = ElapsedMilliseconds(releaseTicks);
            }
        }
    }

    private static SampleResult BuildSampleResult(
        GpuDeadlineBenchmarkSubmission submission,
        double[] latencies,
        double observedMakespanMs,
        double gpuMakespanMs)
    {
        var result = new SampleResult
        {
            UsedAsyncCompute = submission.UsedAsyncCompute,
            ObservedMakespanMs = observedMakespanMs,
            GpuMakespanMs = gpuMakespanMs
        };
        for (int index = 0;
            index < submission.JobFences.Length;
            index++)
        {
            GpuDeadlineJob job = submission.JobFences[index].Job;
            double latency = latencies[index];
            bool missed = latency * 1000.0 >
                job.RelativeDeadlineMicroseconds;
            if (missed)
            {
                result.TotalMisses++;
            }
            switch (job.DeadlineClass)
            {
                case GpuDeadlineClass.Critical:
                    result.CriticalLatencyMs = latency;
                    result.CriticalMisses += missed ? 1 : 0;
                    break;
                case GpuDeadlineClass.Normal:
                    result.NormalLatencyMs = latency;
                    result.NormalMisses += missed ? 1 : 0;
                    break;
                case GpuDeadlineClass.Background:
                    result.BackgroundMaxLatencyMs = Math.Max(
                        result.BackgroundMaxLatencyMs,
                        latency);
                    result.BackgroundMisses += missed ? 1 : 0;
                    break;
            }
        }
        return result;
    }

    private RawSample CreateRawSample(
        GpuDeadlineSchedulerScheduleEntry entry,
        int sampleIndex,
        SampleResult result)
    {
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
            CaseId = adapter.CaseId(
                entry.Variant,
                selectedAsyncBackend),
            Variant = adapter.VariantName(
                entry.Variant,
                selectedAsyncBackend),
            SampleIndex = sampleIndex,
            LogicalState =
                GpuDeadlineSchedulerBenchmarkSchedule.LogicalState(
                    sampleIndex - 1),
            GpuMakespanMs = result.GpuMakespanMs,
            ObservedMakespanMs = result.ObservedMakespanMs,
            CriticalLatencyMs = result.CriticalLatencyMs,
            NormalLatencyMs = result.NormalLatencyMs,
            BackgroundMaxLatencyMs = result.BackgroundMaxLatencyMs,
            CriticalMisses = result.CriticalMisses,
            NormalMisses = result.NormalMisses,
            BackgroundMisses = result.BackgroundMisses,
            TotalMisses = result.TotalMisses,
            UsedAsyncCompute = result.UsedAsyncCompute ? 1 : 0,
            MeasurementReadbackBytes = 0
        };
    }

    private BlockSummary SummarizeBlock(
        GpuDeadlineSchedulerScheduleEntry entry,
        int sampleStart,
        int count)
    {
        RawSample[] rows = rawSamples.Skip(sampleStart).Take(count).ToArray();
        return new BlockSummary
        {
            ScenarioId = scenarioId,
            BlockIndex = entry.BlockIndex,
            SuperRound = entry.SuperRound,
            SequencePosition = entry.SequencePosition,
            PairIndex = entry.PairIndex,
            PairOrder = entry.PairOrder,
            WithinPairPosition = entry.WithinPairPosition,
            Variant = adapter.VariantName(
                entry.Variant,
                selectedAsyncBackend),
            SampleCount = rows.Length,
            GpuAverageMs = rows.Average(row => row.GpuMakespanMs),
            GpuP99Ms = Percentile99(rows.Select(row => row.GpuMakespanMs)),
            ObservedAverageMs = rows.Average(
                row => row.ObservedMakespanMs),
            ObservedP99Ms = Percentile99(
                rows.Select(row => row.ObservedMakespanMs)),
            CriticalAverageLatencyMs = rows.Average(
                row => row.CriticalLatencyMs),
            CriticalP99LatencyMs = Percentile99(
                rows.Select(row => row.CriticalLatencyMs)),
            CriticalDeadlineMissRate = rows.Average(
                row => (double)row.CriticalMisses),
            TotalDeadlineMissRate = rows.Average(
                row => row.TotalMisses / 4.0)
        };
    }

    private void WriteOutputs(bool passed, string status)
    {
        WriteRawSamples();
        WriteBlockSummaries();
        WriteValidations();
        WriteRunSummary(passed, status);
    }

    private void WriteRawSamples()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "processId,scenarioId,blockIndex,superRound,sequencePosition," +
            "pairIndex,pairOrder,withinPairPosition,caseId,variant," +
            "sampleIndex,logicalState,gpuMakespanMs,observedMakespanMs," +
            "criticalLatencyMs,normalLatencyMs,backgroundMaxLatencyMs," +
            "criticalMisses,normalMisses,backgroundMisses,totalMisses," +
            "usedAsyncCompute,measurementReadbackBytes");
        foreach (RawSample row in rawSamples)
        {
            builder.Append(row.ProcessId).Append(',')
                .Append(Csv(row.ScenarioId)).Append(',')
                .Append(row.BlockIndex).Append(',')
                .Append(row.SuperRound).Append(',')
                .Append(row.SequencePosition).Append(',')
                .Append(row.PairIndex).Append(',')
                .Append(row.PairOrder).Append(',')
                .Append(row.WithinPairPosition).Append(',')
                .Append(Csv(row.CaseId)).Append(',')
                .Append(Csv(row.Variant)).Append(',')
                .Append(row.SampleIndex).Append(',')
                .Append(row.LogicalState).Append(',')
                .Append(Number(row.GpuMakespanMs)).Append(',')
                .Append(Number(row.ObservedMakespanMs)).Append(',')
                .Append(Number(row.CriticalLatencyMs)).Append(',')
                .Append(Number(row.NormalLatencyMs)).Append(',')
                .Append(Number(row.BackgroundMaxLatencyMs)).Append(',')
                .Append(row.CriticalMisses).Append(',')
                .Append(row.NormalMisses).Append(',')
                .Append(row.BackgroundMisses).Append(',')
                .Append(row.TotalMisses).Append(',')
                .Append(row.UsedAsyncCompute).Append(',')
                .Append(row.MeasurementReadbackBytes).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(reportDirectory, "raw-samples.csv"),
            builder.ToString());
    }

    private void WriteBlockSummaries()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "scenarioId,blockIndex,superRound,sequencePosition,pairIndex," +
            "pairOrder,withinPairPosition,variant,sampleCount,gpuAverageMs," +
            "gpuP99Ms,observedAverageMs,observedP99Ms," +
            "criticalAverageLatencyMs,criticalP99LatencyMs," +
            "criticalDeadlineMissRate,totalDeadlineMissRate");
        foreach (BlockSummary row in blockSummaries)
        {
            builder.Append(Csv(row.ScenarioId)).Append(',')
                .Append(row.BlockIndex).Append(',')
                .Append(row.SuperRound).Append(',')
                .Append(row.SequencePosition).Append(',')
                .Append(row.PairIndex).Append(',')
                .Append(row.PairOrder).Append(',')
                .Append(row.WithinPairPosition).Append(',')
                .Append(Csv(row.Variant)).Append(',')
                .Append(row.SampleCount).Append(',')
                .Append(Number(row.GpuAverageMs)).Append(',')
                .Append(Number(row.GpuP99Ms)).Append(',')
                .Append(Number(row.ObservedAverageMs)).Append(',')
                .Append(Number(row.ObservedP99Ms)).Append(',')
                .Append(Number(row.CriticalAverageLatencyMs)).Append(',')
                .Append(Number(row.CriticalP99LatencyMs)).Append(',')
                .Append(Number(row.CriticalDeadlineMissRate)).Append(',')
                .Append(Number(row.TotalDeadlineMissRate)).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(reportDirectory, "block-summary.csv"),
            builder.ToString());
    }

    private void WriteValidations()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "phase,variant,passed,message,resultHash,readbackBytes");
        foreach (ValidationRow row in validations)
        {
            builder.Append(Csv(row.Phase)).Append(',')
                .Append(Csv(row.Variant)).Append(',')
                .Append(row.Passed ? 1 : 0).Append(',')
                .Append(Csv(row.Message)).Append(',')
                .Append(row.ResultHash).Append(',')
                .Append(row.ReadbackBytes).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(reportDirectory, "validation.csv"),
            builder.ToString());
    }

    private void WriteConfiguration()
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, "config.json"),
            JsonUtility.ToJson(new ConfigEvidence
            {
                schemaVersion = 1,
                suite = "summit.gpu-deadline-scheduler",
                scenarioId = scenarioId,
                buildCommit = buildCommit,
                superRounds = superRounds,
                warmupSamples = warmupSamples,
                sampleCount = sampleCount,
                cooldownFrames = cooldownFrames,
                workItems = workItems,
                criticalIterations = criticalIterations,
                normalIterations = normalIterations,
                backgroundIterations = backgroundIterations,
                pressureItems = pressureItems,
                pressureIterations = pressureIterations,
                criticalDeadlineUs = criticalDeadlineUs,
                normalDeadlineUs = normalDeadlineUs,
                backgroundDeadlineUs = backgroundDeadlineUs,
                calibrationSamples = calibrationSamples,
                calibrationMainGpuAverageMs =
                    calibrationMainGpuAverageMs,
                calibrationMainGpuP99Ms = calibrationMainGpuP99Ms,
                calibrationAsyncGpuAverageMs =
                    calibrationAsyncGpuAverageMs,
                calibrationAsyncGpuP99Ms = calibrationAsyncGpuP99Ms,
                selectedBackend = selectedAsyncBackend
                    ? "least-slack split async"
                    : "least-slack main queue",
                optimizedUsesAsyncCompute = selectedAsyncBackend,
                jobCount = adapter.JobCount,
                residentBytes = adapter.ResidentBytes,
                baselinePolicy = "FIFO on main graphics queue",
                optimizedPolicy =
                    "least slack first; critical/normal urgent async lane, background main bulk lane",
                copyStageQueue = "main graphics fallback",
                dedicatedCopyQueueClaim = false,
                workloadReadbackBytesPerMeasuredSample = 0
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
                graphicsDeviceVendorId = SystemInfo.graphicsDeviceVendorID,
                graphicsDeviceId = SystemInfo.graphicsDeviceID,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMemoryMiB = SystemInfo.graphicsMemorySize,
                supportsComputeShaders = SystemInfo.supportsComputeShaders,
                supportsAsyncCompute = SystemInfo.supportsAsyncCompute,
                nativeTimestampAvailability =
                    timestampSupport.Availability.ToString(),
                nativeTimestampAbiVersion = timestampSupport.AbiVersion,
                nativeTimestampCapabilityFlags =
                    timestampSupport.CapabilityFlags,
                timestampScope =
                    "main-queue begin/end spanning async fence join",
                copyQueueClaim = false,
                asyncComputePathExercised = true,
                optimizedAsyncComputeSelected = selectedAsyncBackend
            }, true));
    }

    private void WriteRunSummary(bool passed, string status)
    {
        long validationReadbackBytes = validations.Sum(
            row => (long)row.ReadbackBytes);
        string[] lines =
        {
            "GPU deadline scheduler benchmark",
            "schemaVersion=1",
            "suite=summit.gpu-deadline-scheduler",
            "passed=" + (passed ? 1 : 0),
            "status=" + status,
            "scenarioId=" + scenarioId,
            "rawSampleCount=" + rawSamples.Count,
            "blockCount=" + blockSummaries.Count,
            "validationRows=" + validations.Count,
            "validationFailures=" + validations.Count(row => !row.Passed),
            "validationReadbackBytes=" + validationReadbackBytes,
            "measurementReadbackBytes=0",
            "nativeTimestampWarmupPassed=" +
                (timestampWarmupPassed ? 1 : 0),
            "nativeTimestampWarmupStatus=" + timestampWarmupStatus,
            "asyncComputePathExercised=1",
            "optimizedAsyncComputeSelected=" +
                (selectedAsyncBackend ? 1 : 0),
            "copyQueueClaim=0",
            "mainGraphicsQueueTimestamp=1",
            "crossQueueFenceJoin=1"
        };
        File.WriteAllLines(
            Path.Combine(reportDirectory, "run-summary.txt"),
            lines);
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
        Debug.Log(
            "GPU deadline scheduler benchmark " + status +
            "; report=" + reportDirectory);
        Application.Quit(passed ? 0 : 2);
    }

    private void DisposeResources()
    {
        timestampBackend?.Dispose();
        timestampBackend = null;
        adapter?.Dispose();
        adapter = null;
    }

    private static double Percentile99(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return 0.0;
        }
        int index = Math.Max(
            0,
            (int)Math.Ceiling(sorted.Length * 0.99) - 1);
        return sorted[index];
    }

    private static double ElapsedMilliseconds(long startTicks)
    {
        return (Stopwatch.GetTimestamp() - startTicks) *
            (1000.0 / Stopwatch.Frequency);
    }

    private static string Number(double value)
    {
        return value.ToString("R", Invariant);
    }

    private static string Csv(string value)
    {
        string text = value ?? string.Empty;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static bool HasArgument(string[] args, string name)
    {
        return args.Any(arg => string.Equals(
            arg,
            name,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadString(
        string[] args,
        string name,
        string fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(
                    args[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return fallback;
    }

    private static int ReadInt(
        string[] args,
        string name,
        int fallback)
    {
        string value = ReadString(args, name, null);
        return int.TryParse(
            value,
            NumberStyles.Integer,
            Invariant,
            out int parsed)
            ? parsed
            : fallback;
    }

    private static float ReadFloat(
        string[] args,
        string name,
        float fallback)
    {
        string value = ReadString(args, name, null);
        return float.TryParse(
            value,
            NumberStyles.Float,
            Invariant,
            out float parsed)
            ? parsed
            : fallback;
    }

    private sealed class SampleResult
    {
        public bool UsedAsyncCompute;
        public double GpuMakespanMs;
        public double ObservedMakespanMs;
        public double CriticalLatencyMs;
        public double NormalLatencyMs;
        public double BackgroundMaxLatencyMs;
        public int CriticalMisses;
        public int NormalMisses;
        public int BackgroundMisses;
        public int TotalMisses;
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
        public int LogicalState;
        public double GpuMakespanMs;
        public double ObservedMakespanMs;
        public double CriticalLatencyMs;
        public double NormalLatencyMs;
        public double BackgroundMaxLatencyMs;
        public int CriticalMisses;
        public int NormalMisses;
        public int BackgroundMisses;
        public int TotalMisses;
        public int UsedAsyncCompute;
        public int MeasurementReadbackBytes;
    }

    private sealed class BlockSummary
    {
        public string ScenarioId;
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public string Variant;
        public int SampleCount;
        public double GpuAverageMs;
        public double GpuP99Ms;
        public double ObservedAverageMs;
        public double ObservedP99Ms;
        public double CriticalAverageLatencyMs;
        public double CriticalP99LatencyMs;
        public double CriticalDeadlineMissRate;
        public double TotalDeadlineMissRate;
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
        public int warmupSamples;
        public int sampleCount;
        public int cooldownFrames;
        public int workItems;
        public int criticalIterations;
        public int normalIterations;
        public int backgroundIterations;
        public int pressureItems;
        public int pressureIterations;
        public int criticalDeadlineUs;
        public int normalDeadlineUs;
        public int backgroundDeadlineUs;
        public int calibrationSamples;
        public double calibrationMainGpuAverageMs;
        public double calibrationMainGpuP99Ms;
        public double calibrationAsyncGpuAverageMs;
        public double calibrationAsyncGpuP99Ms;
        public string selectedBackend;
        public bool optimizedUsesAsyncCompute;
        public int jobCount;
        public long residentBytes;
        public string baselinePolicy;
        public string optimizedPolicy;
        public string copyStageQueue;
        public bool dedicatedCopyQueueClaim;
        public int workloadReadbackBytesPerMeasuredSample;
    }

    [Serializable]
    private sealed class DeviceEvidence
    {
        public string operatingSystem;
        public string processorType;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public int graphicsDeviceVendorId;
        public int graphicsDeviceId;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public int graphicsMemoryMiB;
        public bool supportsComputeShaders;
        public bool supportsAsyncCompute;
        public string nativeTimestampAvailability;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public string timestampScope;
        public bool copyQueueClaim;
        public bool asyncComputePathExercised;
        public bool optimizedAsyncComputeSelected;
    }
}
