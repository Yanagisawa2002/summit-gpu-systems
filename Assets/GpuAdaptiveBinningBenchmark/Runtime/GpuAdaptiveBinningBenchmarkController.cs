using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[DefaultExecutionOrder(10000)]
public sealed class GpuAdaptiveBinningBenchmarkController : MonoBehaviour
{
    private const string EnableArgument =
        "-gpu-adaptive-binning-benchmark";
    private const int MaxPreparedTimestampScopes = 64;
    private static readonly WaitForEndOfFrame EndOfFrame =
        new WaitForEndOfFrame();

    private readonly List<RawSample> rawSamples = new List<RawSample>(12000);
    private readonly List<BlockSummary> blockSummaries =
        new List<BlockSummary>(16);
    private readonly List<GpuAdaptiveBinningValidationResult> validationResults =
        new List<GpuAdaptiveBinningValidationResult>(4);
    private readonly List<PendingTimestamp> pendingTimestamps =
        new List<PendingTimestamp>(256);

    private GpuAdaptiveBinningBenchmarkAdapter adapter;
    private GpuAdaptiveBinningNativeTimestampBackend timestampBackend;
    private GpuTimestampSupport timestampSupport;
    private CommandBuffer[] measurementCommands;
    private CommandBuffer[,] timestampCommands;
    private int preparedTimestampScopes;
    private bool timestampOperational;
    private bool timestampWarmupPassed;
    private string timestampWarmupStatus = "not-run";
    private double timestampWarmupElapsedMs;
    private ulong timestampWarmupFrequency;
    private ulong timestampWarmupFence;
    private uint timestampWarmupGeneration;
    private int timestampAcquireFailures;
    private int timestampResultFailures;
    private int timestampTimeouts;
    private bool finished;
    private int processId;
    private double benchmarkStart;

    private string reportDirectory;
    private string scenarioId = "custom";
    private string distribution = "uniform";
    private int superRounds = 2;
    private int warmupFrames = 60;
    private int sampleFrames = 240;
    private int cooldownFrames = 15;
    private int elementCount = 1 << 20;
    private int binCount = 4096;
    private int seed = 20260730;
    private int dispatchesPerFrame = 1;
    private float validationTimeoutSeconds = 60.0f;
    private bool requireCompleteGpuTimings = true;
    private string buildCommit = "unknown";
    private string directShaderSha256 = "unknown";
    private string radixShaderSha256 = "unknown";
    private string runtimeApiSha256 = "unknown";
    private string[] originalArguments;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigureBenchmarkProcessLogging()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!ShouldSuppressInformationalLogs(args))
        {
            return;
        }
        Debug.unityLogger.filterLogType = LogType.Warning;
        Application.SetStackTraceLogType(
            LogType.Log,
            StackTraceLogType.None);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!ShouldSuppressInformationalLogs(args))
        {
            return;
        }
        GameObject host = new GameObject(
            "GPU Adaptive Binning Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuAdaptiveBinningBenchmarkController controller =
            host.AddComponent<GpuAdaptiveBinningBenchmarkController>();
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
        originalArguments = args;
        reportDirectory = ReadString(
            args,
            "-gpu-adaptive-binning-report-dir",
            string.Empty);
        scenarioId = ReadString(
            args,
            "-gpu-adaptive-binning-scenario-id",
            scenarioId);
        distribution = ReadString(
            args,
            "-gpu-adaptive-binning-distribution",
            distribution);
        superRounds = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-super-rounds",
                superRounds),
            1,
            4);
        warmupFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-warmup-frames",
                warmupFrames),
            5,
            1800);
        sampleFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-sample-frames",
                sampleFrames),
            60,
            7200);
        cooldownFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-cooldown-frames",
                cooldownFrames),
            0,
            600);
        elementCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-element-count",
                elementCount),
            1024,
            16776960);
        binCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-bin-count",
                binCount),
            1,
            16776960);
        seed = ReadInt(args, "-gpu-adaptive-binning-seed", seed);
        dispatchesPerFrame = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-adaptive-binning-dispatches-per-frame",
                dispatchesPerFrame),
            1,
            128);
        validationTimeoutSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-adaptive-binning-validation-timeout-seconds",
                validationTimeoutSeconds),
            5.0f,
            600.0f);
        requireCompleteGpuTimings =
            ReadInt(
                args,
                "-gpu-adaptive-binning-require-complete-gpu-timings",
                requireCompleteGpuTimings ? 1 : 0) != 0;
        buildCommit = ReadString(
            args,
            "-gpu-adaptive-binning-build-commit",
            buildCommit);
        directShaderSha256 = ReadString(
            args,
            "-gpu-adaptive-binning-direct-shader-sha256",
            directShaderSha256);
        radixShaderSha256 = ReadString(
            args,
            "-gpu-adaptive-binning-radix-shader-sha256",
            radixShaderSha256);
        runtimeApiSha256 = ReadString(
            args,
            "-gpu-adaptive-binning-runtime-api-sha256",
            runtimeApiSha256);

        int scheduledBlockCount = checked(superRounds * 4 + 2);
        int scheduledSampleCount = checked(
            scheduledBlockCount * sampleFrames);
        rawSamples.Capacity = Math.Max(
            rawSamples.Capacity,
            scheduledSampleCount);
        blockSummaries.Capacity = Math.Max(
            blockSummaries.Capacity,
            scheduledBlockCount);
    }

    private IEnumerator RunGuarded()
    {
        Stack<IEnumerator> routines = new Stack<IEnumerator>();
        routines.Push(Run());
        while (routines.Count != 0)
        {
            IEnumerator currentRoutine = routines.Peek();
            bool moved = false;
            object yielded = null;
            Exception failure = null;
            try
            {
                moved = currentRoutine.MoveNext();
                if (moved)
                {
                    yielded = currentRoutine.Current;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure != null)
            {
                while (routines.Count != 0)
                {
                    (routines.Pop() as IDisposable)?.Dispose();
                }
                HandleUnhandledException(failure);
                yield break;
            }
            if (!moved)
            {
                (routines.Pop() as IDisposable)?.Dispose();
                continue;
            }
            if (yielded is IEnumerator nested)
            {
                routines.Push(nested);
                continue;
            }
            yield return yielded;
        }
    }

    private void HandleUnhandledException(Exception exception)
    {
        Debug.LogException(exception);
        try
        {
            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                reportDirectory = Path.GetFullPath(reportDirectory);
                Directory.CreateDirectory(reportDirectory);
                WriteRunSummary(false, "unhandled-exception");
            }
        }
        catch (Exception evidenceException)
        {
            Debug.LogException(evidenceException);
        }
        Finish(false, "unhandled-exception");
    }

    private IEnumerator Run()
    {
        Application.runInBackground = true;
        processId = Process.GetCurrentProcess().Id;
        benchmarkStart = Time.realtimeSinceStartupAsDouble;
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            Finish(false, "missing-report-directory");
            yield break;
        }
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);

        try
        {
            adapter = new GpuAdaptiveBinningBenchmarkAdapter(
                elementCount,
                binCount,
                distribution,
                seed,
                dispatchesPerFrame);
            InitializeTimestampBackend();
            BuildMeasurementCommands();
            BuildTimestampCommands();
            WriteConfiguration();
            WriteDeviceMetadata();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            WriteRunSummary(false, "initialization-failed");
            Finish(false, "initialization-failed");
            yield break;
        }

        for (int caseIndex = 1; caseIndex < measurementCommands.Length; caseIndex++)
        {
            Graphics.ExecuteCommandBuffer(measurementCommands[caseIndex]);
            yield return EndOfFrame;
        }

        yield return WarmupTimestampBackend();
        WriteConfiguration();
        if (requireCompleteGpuTimings && !timestampWarmupPassed)
        {
            WriteAllOutputs(false, "native-timestamp-warmup-failed");
            Finish(false, "native-timestamp-warmup-failed");
            yield break;
        }

        bool warmupValidationPassed = true;
        yield return Validate(
            GpuAdaptiveBinningBenchmarkVariant.Radix,
            "warmup",
            passed => warmupValidationPassed &= passed);
        yield return Validate(
            GpuAdaptiveBinningBenchmarkVariant.Direct,
            "warmup",
            passed => warmupValidationPassed &= passed);
        if (!warmupValidationPassed)
        {
            WriteAllOutputs(false, "warmup-validation-failed");
            Finish(false, "warmup-validation-failed");
            yield break;
        }

        IReadOnlyList<GpuAdaptiveBinningScheduleEntry> schedule =
            GpuAdaptiveBinningBenchmarkSchedule.Build(superRounds);
        for (int scheduleIndex = 0; scheduleIndex < schedule.Count; scheduleIndex++)
        {
            GpuAdaptiveBinningScheduleEntry entry = schedule[scheduleIndex];
            int caseIndex = CaseIndex(entry.Variant);
            CommandBuffer plainCommands = measurementCommands[caseIndex];

            for (int frame = 0; frame < cooldownFrames; frame++)
            {
                yield return EndOfFrame;
            }
            for (int frame = 0; frame < warmupFrames; frame++)
            {
                Graphics.ExecuteCommandBuffer(plainCommands);
                yield return EndOfFrame;
            }

            int sampleStart = rawSamples.Count;
            for (int sample = 1; sample <= sampleFrames; sample++)
            {
                int sourceFrame = Time.frameCount;
                ulong userTag = checked((ulong)rawSamples.Count + 1UL);
                GpuTimestampSampleFlags flags =
                    entry.Variant == GpuAdaptiveBinningBenchmarkVariant.Control
                        ? GpuTimestampSampleFlags.EmptyScope
                        : GpuTimestampSampleFlags.None;
                GpuTimestampStatus status = GpuTimestampStatus.Unsupported;
                GpuTimestampToken token = default;
                CommandBuffer submittedCommands = plainCommands;
                bool submitted = false;

                if (timestampOperational && timestampBackend != null)
                {
                    status = timestampBackend.Acquire(
                        userTag,
                        flags,
                        sourceFrame,
                        out token);
                    if (status == GpuTimestampStatus.Ready &&
                        token.ScopeIndex >= 0 &&
                        token.ScopeIndex < preparedTimestampScopes)
                    {
                        status = timestampBackend.MarkSubmitted(token);
                        if (status == GpuTimestampStatus.Ready)
                        {
                            submittedCommands =
                                timestampCommands[token.ScopeIndex, caseIndex];
                            submitted = true;
                        }
                        else
                        {
                            timestampResultFailures++;
                            timestampBackend.Cancel(token);
                        }
                    }
                    else
                    {
                        if (status == GpuTimestampStatus.Ready)
                        {
                            timestampBackend.Cancel(token);
                            status = GpuTimestampStatus.RingFull;
                        }
                        timestampAcquireFailures++;
                    }
                }

                long enqueueStart = Stopwatch.GetTimestamp();
                Graphics.ExecuteCommandBuffer(submittedCommands);
                long enqueueEnd = Stopwatch.GetTimestamp();
                RawSample row = new RawSample
                {
                    SourceRowIndex = rawSamples.Count,
                    ProcessId = processId,
                    ScenarioId = scenarioId,
                    SuperRound = entry.SuperRound,
                    SequencePosition = entry.SequencePosition,
                    PairIndex = entry.PairIndex,
                    PairOrder = entry.PairOrder,
                    WithinPairPosition = entry.WithinPairPosition,
                    BlockIndex = entry.BlockIndex,
                    BlockType = entry.BlockType,
                    CaseId = adapter.CaseId(entry.Variant),
                    Variant = adapter.VariantName(entry.Variant),
                    Marker = adapter.Marker(entry.Variant),
                    SampleIndex = sample,
                    SourceUnityFrame = sourceFrame,
                    ElapsedSeconds =
                        Time.realtimeSinceStartupAsDouble - benchmarkStart,
                    EnqueueCpuMs = TicksToMilliseconds(
                        enqueueEnd - enqueueStart),
                    NativeTimestampToken = token.Value,
                    NativeTimestampUserTag = userTag,
                    NativeTimestampFlags = (uint)flags,
                    NativeTimestampStatus = submitted
                        ? GpuTimestampStatus.Pending
                        : status,
                    MeasurementReadbackBytes = 0
                };
                int rowIndex = rawSamples.Count;
                rawSamples.Add(row);
                if (submitted)
                {
                    pendingTimestamps.Add(new PendingTimestamp
                    {
                        Token = token,
                        RowIndex = rowIndex,
                        ExpectedFlags = flags
                    });
                }

                yield return EndOfFrame;
                row = rawSamples[rowIndex];
                row.FrameMs = Time.unscaledDeltaTime * 1000.0;
                rawSamples[rowIndex] = row;
                PollTimestampResults();
            }

            yield return DrainTimestamps(sampleStart, sampleFrames);
            bool fencePassed = false;
            yield return CompleteWithGraphicsFence(
                passed => fencePassed = passed);
            blockSummaries.Add(
                SummarizeBlock(
                    entry,
                    sampleStart,
                    sampleFrames,
                    fencePassed));
        }
        yield return DrainAllTimestamps();

        bool finalValidationPassed = true;
        yield return Validate(
            GpuAdaptiveBinningBenchmarkVariant.Radix,
            "final",
            passed => finalValidationPassed &= passed);
        yield return Validate(
            GpuAdaptiveBinningBenchmarkVariant.Direct,
            "final",
            passed => finalValidationPassed &= passed);

        bool timingComplete = IsTimestampEvidenceComplete();
        bool passedBenchmark =
            warmupValidationPassed &&
            finalValidationPassed &&
            (!requireCompleteGpuTimings || timingComplete);
        string statusText = passedBenchmark
            ? timingComplete
                ? "completed"
                : "completed-correctness-only"
            : finalValidationPassed
                ? "gpu-timing-incomplete"
                : "final-validation-failed";
        WriteAllOutputs(passedBenchmark, statusText);
        Finish(passedBenchmark, statusText);
    }

    private void InitializeTimestampBackend()
    {
        timestampOperational =
            GpuAdaptiveBinningNativeTimestampBackend.TryCreate(
                out timestampBackend,
                out timestampSupport);
        if (timestampOperational)
        {
            timestampBackend.ExecuteFrequencyInitialization();
        }
    }

    private void BuildMeasurementCommands()
    {
        measurementCommands = new CommandBuffer[3];
        measurementCommands[0] = adapter.CreateMeasurementCommandBuffer(
            GpuAdaptiveBinningBenchmarkVariant.Control);
        measurementCommands[1] = adapter.CreateMeasurementCommandBuffer(
            GpuAdaptiveBinningBenchmarkVariant.Radix);
        measurementCommands[2] = adapter.CreateMeasurementCommandBuffer(
            GpuAdaptiveBinningBenchmarkVariant.Direct);
    }

    private void BuildTimestampCommands()
    {
        if (timestampBackend == null)
        {
            return;
        }
        preparedTimestampScopes = Math.Min(
            MaxPreparedTimestampScopes,
            timestampBackend.Capacity);
        timestampCommands = new CommandBuffer[preparedTimestampScopes, 3];
        for (int scope = 0; scope < preparedTimestampScopes; scope++)
        {
            for (int caseIndex = 0; caseIndex < 3; caseIndex++)
            {
                GpuAdaptiveBinningBenchmarkVariant variant =
                    VariantFromCaseIndex(caseIndex);
                CommandBuffer commands = new CommandBuffer
                {
                    name =
                        adapter.Marker(variant) +
                        "/NativeTimestamp/" +
                        scope.ToString(CultureInfo.InvariantCulture)
                };
                timestampBackend.RecordBegin(scope, commands);
                adapter.RecordMeasurement(commands, variant);
                timestampBackend.RecordEnd(scope, commands);
                timestampCommands[scope, caseIndex] = commands;
            }
        }
    }

    private IEnumerator WarmupTimestampBackend()
    {
        timestampWarmupPassed = false;
        if (!timestampOperational || timestampBackend == null)
        {
            timestampWarmupStatus = "backend-unavailable";
            yield break;
        }

        GpuTimestampStatus status = timestampBackend.Acquire(
            ulong.MaxValue,
            GpuTimestampSampleFlags.EmptyScope,
            Time.frameCount,
            out GpuTimestampToken token);
        if (status != GpuTimestampStatus.Ready ||
            token.ScopeIndex < 0 ||
            token.ScopeIndex >= preparedTimestampScopes)
        {
            if (status == GpuTimestampStatus.Ready)
            {
                timestampBackend.Cancel(token);
            }
            timestampWarmupStatus = "acquire-" + StatusName(status);
            timestampOperational = false;
            yield break;
        }

        status = timestampBackend.MarkSubmitted(token);
        if (status != GpuTimestampStatus.Ready)
        {
            timestampBackend.Cancel(token);
            timestampWarmupStatus = "mark-submitted-" + StatusName(status);
            timestampOperational = false;
            yield break;
        }
        Graphics.ExecuteCommandBuffer(timestampCommands[token.ScopeIndex, 0]);
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return EndOfFrame;
            status = timestampBackend.TryConsume(
                token,
                Time.frameCount,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }
            if (status == GpuTimestampStatus.Ready)
            {
                timestampWarmupElapsedMs = result.ElapsedMilliseconds;
                timestampWarmupFrequency = result.TimestampFrequency;
                timestampWarmupFence = result.FenceValue;
                timestampWarmupGeneration = result.DeviceGeneration;
                timestampWarmupPassed =
                    result.Token.UserTag == ulong.MaxValue &&
                    result.TimestampFrequency > 0 &&
                    result.DeviceGeneration == timestampSupport.DeviceGeneration &&
                    result.EndTicks >= result.BeginTicks &&
                    result.FenceValue > 0;
                timestampWarmupStatus =
                    timestampWarmupPassed ? "ready" : "validation-failed";
            }
            else
            {
                timestampWarmupStatus = "result-" + StatusName(status);
            }
            timestampOperational = timestampWarmupPassed;
            yield break;
        }
        timestampWarmupStatus = "timeout";
        timestampOperational = false;
        timestampTimeouts++;
    }

    private IEnumerator Validate(
        GpuAdaptiveBinningBenchmarkVariant variant,
        string phase,
        Action<bool> completion)
    {
        adapter.BeginValidation(variant, phase);
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (adapter.TryCompleteValidation(
                    out GpuAdaptiveBinningValidationResult result))
            {
                validationResults.Add(result);
                completion(result.Passed);
                yield break;
            }
            yield return null;
        }

        GpuAdaptiveBinningValidationResult timeout =
            new GpuAdaptiveBinningValidationResult
            {
                Phase = phase,
                CaseId = adapter.CaseId(variant),
                Variant = adapter.VariantName(variant),
                Passed = false,
                Message = "Validation timed out.",
                ResultHash = "unavailable",
                ReadbackBytes = adapter.AbandonPendingValidation()
            };
        validationResults.Add(timeout);
        completion(false);
    }

    private void PollTimestampResults()
    {
        if (timestampBackend == null)
        {
            return;
        }
        for (int i = pendingTimestamps.Count - 1; i >= 0; i--)
        {
            PendingTimestamp pending = pendingTimestamps[i];
            GpuTimestampStatus status = timestampBackend.TryConsume(
                pending.Token,
                Time.frameCount,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }

            RawSample row = rawSamples[pending.RowIndex];
            row.NativeTimestampStatus = status;
            if (status == GpuTimestampStatus.Ready)
            {
                bool valid =
                    result.Token.Value == pending.Token.Value &&
                    result.Token.UserTag == pending.Token.UserTag &&
                    result.SourceFrame == pending.Token.SourceFrame &&
                    result.NativeFlags == (uint)pending.ExpectedFlags &&
                    result.TimestampFrequency > 0 &&
                    result.EndTicks >= result.BeginTicks &&
                    result.DeviceGeneration == timestampSupport.DeviceGeneration &&
                    result.FenceValue > 0;
                if (!valid)
                {
                    row.NativeTimestampStatus =
                        GpuTimestampStatus.MalformedNativeResult;
                    timestampResultFailures++;
                    timestampOperational = false;
                }
                else
                {
                    row.ResultUnityFrame = result.ResultFrame;
                    row.NativeTimestampToken = result.Token.Value;
                    row.NativeTimestampUserTag = result.Token.UserTag;
                    row.NativeTimestampFlags = result.NativeFlags;
                    row.NativeTimestampBeginTicks = result.BeginTicks;
                    row.NativeTimestampEndTicks = result.EndTicks;
                    row.NativeTimestampElapsedTicks = result.ElapsedTicks;
                    row.NativeTimestampFrequency = result.TimestampFrequency;
                    row.NativeTimestampElapsedNanoseconds =
                        result.ElapsedNanoseconds;
                    row.NativeTimestampFenceValue = result.FenceValue;
                    row.NativeTimestampDeviceGeneration =
                        result.DeviceGeneration;
                    row.GpuRegionElapsedMs = result.ElapsedMilliseconds;
                    row.TimestampInstrumentationReadbackBytes = 16;
                }
            }
            else
            {
                timestampResultFailures++;
                timestampOperational = false;
            }
            rawSamples[pending.RowIndex] = row;
            pendingTimestamps.RemoveAt(i);
        }
    }

    private IEnumerator DrainTimestamps(int sampleStart, int count)
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (HasPendingRows(sampleStart, count) &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return EndOfFrame;
            PollTimestampResults();
        }
        if (!HasPendingRows(sampleStart, count))
        {
            yield break;
        }
        MarkTimeouts(sampleStart, count);
        timestampOperational = false;
    }

    private IEnumerator DrainAllTimestamps()
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (pendingTimestamps.Count != 0 &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return EndOfFrame;
            PollTimestampResults();
        }
        if (pendingTimestamps.Count != 0)
        {
            MarkTimeouts(0, rawSamples.Count);
        }
    }

    private bool HasPendingRows(int sampleStart, int count)
    {
        int end = sampleStart + count;
        for (int i = 0; i < pendingTimestamps.Count; i++)
        {
            int rowIndex = pendingTimestamps[i].RowIndex;
            if (rowIndex >= sampleStart && rowIndex < end)
            {
                return true;
            }
        }
        return false;
    }

    private void MarkTimeouts(int sampleStart, int count)
    {
        int end = sampleStart + count;
        for (int i = pendingTimestamps.Count - 1; i >= 0; i--)
        {
            PendingTimestamp pending = pendingTimestamps[i];
            int rowIndex = pending.RowIndex;
            if (rowIndex < sampleStart || rowIndex >= end)
            {
                continue;
            }
            RawSample row = rawSamples[rowIndex];
            row.NativeTimestampTimedOut = true;
            rawSamples[rowIndex] = row;
            timestampTimeouts++;
            pendingTimestamps.RemoveAt(i);
        }
    }

    private IEnumerator CompleteWithGraphicsFence(Action<bool> completion)
    {
        if (!SystemInfo.supportsGraphicsFence)
        {
            completion(false);
            yield break;
        }
        CommandBuffer commands = new CommandBuffer
        {
            name = "GPU.AdaptiveBinning/CompletionFence"
        };
        GraphicsFence fence = commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
        Graphics.ExecuteCommandBuffer(commands);
        commands.Dispose();
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (!fence.passed &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
        }
        completion(fence.passed);
    }

    private BlockSummary SummarizeBlock(
        GpuAdaptiveBinningScheduleEntry entry,
        int sampleStart,
        int count,
        bool fencePassed)
    {
        double[] gpu = new double[count];
        double[] frame = new double[count];
        double[] enqueue = new double[count];
        int gpuCount = 0;
        long measurementReadback = 0;
        long instrumentationReadback = 0;
        for (int i = 0; i < count; i++)
        {
            RawSample row = rawSamples[sampleStart + i];
            frame[i] = row.FrameMs;
            enqueue[i] = row.EnqueueCpuMs;
            if (IsTimestampReady(row))
            {
                gpu[gpuCount++] = row.GpuRegionElapsedMs;
            }
            measurementReadback += row.MeasurementReadbackBytes;
            instrumentationReadback +=
                row.TimestampInstrumentationReadbackBytes;
        }
        GpuAdaptiveBinningBenchmarkVariant variant = entry.Variant;
        return new BlockSummary
        {
            ProcessId = processId,
            ScenarioId = scenarioId,
            SuperRound = entry.SuperRound,
            SequencePosition = entry.SequencePosition,
            PairIndex = entry.PairIndex,
            PairOrder = entry.PairOrder,
            WithinPairPosition = entry.WithinPairPosition,
            BlockIndex = entry.BlockIndex,
            BlockType = entry.BlockType,
            CaseId = adapter.CaseId(variant),
            Variant = adapter.VariantName(variant),
            Marker = adapter.Marker(variant),
            Samples = count,
            GpuValidSamples = gpuCount,
            DispatchesPerFrame =
                variant == GpuAdaptiveBinningBenchmarkVariant.Control
                    ? 0
                    : dispatchesPerFrame,
            LogicalProblemBytesPerDispatch =
                variant == GpuAdaptiveBinningBenchmarkVariant.Control
                    ? 0
                    : adapter.LogicalProblemBytesPerDispatch,
            SharedInputBytes =
                variant == GpuAdaptiveBinningBenchmarkVariant.Control
                    ? 0
                    : adapter.SharedInputBytes,
            SharedOutputBytes =
                variant == GpuAdaptiveBinningBenchmarkVariant.Control
                    ? 0
                    : adapter.SharedOutputBytes,
            PrimitiveScratchBytes =
                adapter.CasePrimitiveScratchBytes(variant),
            CaseScratchBytes = adapter.CaseScratchBytes(variant),
            CaseResidentBytes = adapter.CaseResidentBytes(variant),
            ActualBenchmarkBufferResidentBytes =
                adapter.ActualBenchmarkBufferResidentBytes,
            Gpu = MetricStats.Compute(gpu, gpuCount),
            Frame = MetricStats.Compute(frame, frame.Length),
            Enqueue = MetricStats.Compute(enqueue, enqueue.Length),
            FenceSupported = SystemInfo.supportsGraphicsFence,
            FencePassed = fencePassed,
            MeasurementReadbackBytes = measurementReadback,
            TimestampInstrumentationReadbackBytes =
                instrumentationReadback
        };
    }

    private bool IsTimestampEvidenceComplete()
    {
        if (timestampBackend == null ||
            !timestampWarmupPassed ||
            !timestampSupport.IsAvailable ||
            timestampSupport.AbiVersion != 2 ||
            (timestampSupport.CapabilityFlags & 0x1Fu) != 0x1Fu ||
            timestampBackend.IsTerminal ||
            timestampBackend.ActiveSampleCount != 0 ||
            timestampBackend.ReservedSampleCount != 0 ||
            timestampBackend.SubmittedSampleCount != 0 ||
            pendingTimestamps.Count != 0 ||
            timestampAcquireFailures != 0 ||
            timestampResultFailures != 0 ||
            timestampTimeouts != 0)
        {
            return false;
        }
        for (int i = 0; i < rawSamples.Count; i++)
        {
            if (!IsTimestampReady(rawSamples[i]))
            {
                return false;
            }
        }
        return true;
    }

    private void WriteAllOutputs(bool passed, string status)
    {
        WriteConfiguration();
        WriteRawSamples();
        WriteBlockSummaries();
        WriteValidationResults();
        WriteRunSummary(passed, status);
    }

    private void WriteConfiguration()
    {
        if (string.IsNullOrWhiteSpace(reportDirectory) || adapter == null)
        {
            return;
        }
        IReadOnlyList<GpuAdaptiveBinningScheduleEntry> schedule =
            GpuAdaptiveBinningBenchmarkSchedule.Build(superRounds);
        string[] order = new string[schedule.Count];
        for (int i = 0; i < schedule.Count; i++)
        {
            order[i] =
                schedule[i].BlockType + ":" +
                adapter.VariantName(schedule[i].Variant);
        }
        BenchmarkConfiguration config = new BenchmarkConfiguration
        {
            schemaVersion = 2,
            suite = "summit.gpu-adaptive-binning",
            processId = processId,
            unityVersion = Application.unityVersion,
            startedUtc = DateTime.UtcNow.ToString("O"),
            scenarioId = scenarioId,
            distribution = distribution,
            superRounds = superRounds,
            warmupFrames = warmupFrames,
            sampleFrames = sampleFrames,
            cooldownFrames = cooldownFrames,
            elementCount = elementCount,
            binCount = binCount,
            seed = seed,
            exactSingleBinKey = string.Equals(
                distribution,
                GpuAdaptiveBinningInputGenerator.SingleBinDistribution,
                StringComparison.OrdinalIgnoreCase)
                    ? checked((int)(unchecked((uint)seed) % checked((uint)binCount)))
                    : -1,
            dispatchesPerFrame = dispatchesPerFrame,
            primitiveBackend = GpuAdaptiveBinningBenchmarkAdapter.PrimitiveBackendName,
            keyDomain = GpuAdaptiveBinningBenchmarkAdapter.KeyDomainName,
            orderingContract = GpuAdaptiveBinningBenchmarkAdapter.OrderingContractName,
            directValidationContract =
                GpuAdaptiveBinningBenchmarkAdapter.DirectValidationContract,
            inputGeneratorContract =
                GpuAdaptiveBinningInputGenerator.GeneratorContract,
            directStageContract =
                GpuAdaptiveBinningBenchmarkAdapter.DirectStageContract,
            radixStageContract =
                GpuAdaptiveBinningBenchmarkAdapter.RadixStageContract,
            innerProfilerMarkersEnabled =
                GpuAdaptiveBinningBenchmarkAdapter.InnerProfilerMarkersEnabled,
            radixKeyBitCount = adapter.RequiredKeyBitCount,
            radixPassCount = adapter.RadixPassCount,
            directId = adapter.CaseId(
                GpuAdaptiveBinningBenchmarkVariant.Direct),
            radixId = adapter.CaseId(
                GpuAdaptiveBinningBenchmarkVariant.Radix),
            schedule = order,
            scheduleContract =
                GpuAdaptiveBinningBenchmarkSchedule.Contract(superRounds),
            caseLocalWarmup = true,
            sameProcessPaired = true,
            informationalPlayerLogsSuppressed = true,
            logicalProblemBytesPerDispatch =
                adapter.LogicalProblemBytesPerDispatch,
            sharedInputBytes = adapter.SharedInputBytes,
            sharedOutputBytes = adapter.SharedOutputBytes,
            sharedContractResidentBytes =
                adapter.SharedInputBytes + adapter.SharedOutputBytes,
            unionPrimitiveScratchBytes =
                adapter.PrimitiveScratchBytes,
            directPrimitiveScratchBytes =
                adapter.DirectPrimitiveScratchBytes,
            radixPrimitiveScratchBytes =
                adapter.RadixPrimitiveScratchBytes,
            directInternalScratchBytes =
                adapter.DirectInternalScratchBytes,
            radixInternalScratchBytes =
                adapter.RadixInternalScratchBytes,
            directCaseScratchBytes =
                adapter.DirectCaseScratchBytes,
            radixCaseScratchBytes =
                adapter.RadixCaseScratchBytes,
            directCaseResidentBytes = adapter.DirectCaseResidentBytes,
            radixCaseResidentBytes =
                adapter.RadixCaseResidentBytes,
            actualBenchmarkBufferResidentBytes =
                adapter.ActualBenchmarkBufferResidentBytes,
            expectedResultHash = adapter.ExpectedResultHash,
            resultHashAlgorithm = GpuAdaptiveBinningCpuOracle.CanonicalHashSchema,
            measurementReadbackBytesPerFrame = 0,
            timestampInstrumentationBytesPerCompletedSample = 16,
            requireCompleteGpuTimings = requireCompleteGpuTimings,
            nativeTimestampBackend =
                "native-d3d12-timestamp-query/private-completion-fence",
            nativeTimestampAbiVersion = timestampSupport.AbiVersion,
            nativeTimestampCapabilityFlags =
                timestampSupport.CapabilityFlags,
            nativeTimestampRingCapacity = timestampSupport.RingCapacity,
            nativeTimestampDeviceGeneration =
                timestampSupport.DeviceGeneration,
            nativeTimestampWarmupPassed = timestampWarmupPassed,
            nativeTimestampWarmupStatus = timestampWarmupStatus,
            nativeTimestampWarmupElapsedMs =
                timestampWarmupElapsedMs,
            nativeTimestampWarmupFrequency =
                timestampWarmupFrequency,
            nativeTimestampWarmupFenceValue =
                timestampWarmupFence,
            nativeTimestampWarmupDeviceGeneration =
                timestampWarmupGeneration,
            buildCommit = buildCommit,
            directShaderSha256 = directShaderSha256,
            radixShaderSha256 = radixShaderSha256,
            runtimeApiSha256 = runtimeApiSha256,
            commandLine = SanitizeCommandLine(originalArguments)
        };
        File.WriteAllText(
            Path.Combine(reportDirectory, "config.json"),
            JsonUtility.ToJson(config, true),
            new UTF8Encoding(false));
    }

    private void WriteDeviceMetadata()
    {
        DeviceMetadata device = new DeviceMetadata
        {
            processId = processId,
            operatingSystem = SystemInfo.operatingSystem,
            processorType = SystemInfo.processorType,
            systemMemoryMiB = SystemInfo.systemMemorySize,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
            graphicsDeviceVendorId = SystemInfo.graphicsDeviceVendorID,
            graphicsDeviceId = SystemInfo.graphicsDeviceID,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            graphicsMemoryMiB = SystemInfo.graphicsMemorySize,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            supportsGraphicsFence = SystemInfo.supportsGraphicsFence,
            supportsAsyncGpuReadback = SystemInfo.supportsAsyncGPUReadback,
            nativeTimestampAvailability =
                timestampSupport.Availability.ToString(),
            nativeTimestampAbiVersion = timestampSupport.AbiVersion,
            nativeTimestampCapabilityFlags =
                timestampSupport.CapabilityFlags
        };
        File.WriteAllText(
            Path.Combine(reportDirectory, "device.json"),
            JsonUtility.ToJson(device, true),
            new UTF8Encoding(false));
    }

    private void WriteRawSamples()
    {
        using (StreamWriter writer = CreateWriter("raw-frames.csv"))
        {
            writer.WriteLine(
                "processId,scenarioId,superRound,sequencePosition,pairIndex,pairOrder," +
                "withinPairPosition,blockIndex,blockType,caseId,variant,marker,sampleIndex," +
                "sourceUnityFrame,resultUnityFrame,elapsedSeconds,enqueueCpuMs," +
                "nativeTimestampToken,nativeTimestampUserTag,nativeTimestampFlags," +
                "nativeTimestampStatus,nativeTimestampBeginTicks,nativeTimestampEndTicks," +
                "nativeTimestampElapsedTicks,nativeTimestampFrequency," +
                "nativeTimestampElapsedNanoseconds,nativeTimestampFenceValue," +
                "nativeTimestampDeviceGeneration,gpuRegionElapsedMs,frameMs," +
                "measurementReadbackBytes,timestampInstrumentationReadbackBytes");
            foreach (RawSample row in rawSamples)
            {
                writer.WriteLine(string.Join(",",
                    row.ProcessId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ScenarioId),
                    row.SuperRound.ToString(CultureInfo.InvariantCulture),
                    row.SequencePosition.ToString(CultureInfo.InvariantCulture),
                    row.PairIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.PairOrder),
                    row.WithinPairPosition.ToString(CultureInfo.InvariantCulture),
                    row.BlockIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.BlockType),
                    Csv(row.CaseId),
                    Csv(row.Variant),
                    Csv(row.Marker),
                    row.SampleIndex.ToString(CultureInfo.InvariantCulture),
                    row.SourceUnityFrame.ToString(CultureInfo.InvariantCulture),
                    row.ResultUnityFrame.ToString(CultureInfo.InvariantCulture),
                    Number(row.ElapsedSeconds),
                    Number(row.EnqueueCpuMs),
                    row.NativeTimestampToken.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampUserTag.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFlags.ToString(CultureInfo.InvariantCulture),
                    Csv(TimestampStatusName(row)),
                    row.NativeTimestampBeginTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampEndTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFrequency.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedNanoseconds.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFenceValue.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampDeviceGeneration.ToString(CultureInfo.InvariantCulture),
                    Number(row.GpuRegionElapsedMs),
                    Number(row.FrameMs),
                    row.MeasurementReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    row.TimestampInstrumentationReadbackBytes.ToString(
                        CultureInfo.InvariantCulture)));
            }
        }
    }

    private void WriteBlockSummaries()
    {
        using (StreamWriter writer = CreateWriter("block-summary.csv"))
        {
            writer.WriteLine(
                "processId,scenarioId,superRound,sequencePosition,pairIndex,pairOrder," +
                "withinPairPosition,blockIndex,blockType,caseId,variant,marker,samples," +
                "gpuRegionValidSamples,dispatchesPerFrame,logicalProblemBytesPerDispatch," +
                "sharedInputBytes,sharedOutputBytes,primitiveScratchBytes,caseScratchBytes," +
                "caseResidentBytes,actualBenchmarkBufferResidentBytes,gpuRegionAverageMs," +
                "gpuRegionP50Ms,gpuRegionP95Ms,gpuRegionP99Ms,frameAverageMs,frameP99Ms," +
                "enqueueAverageMs,enqueueP99Ms,fenceSupported,fencePassed," +
                "measurementReadbackBytes,timestampInstrumentationReadbackBytes");
            foreach (BlockSummary row in blockSummaries)
            {
                writer.WriteLine(string.Join(",",
                    row.ProcessId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ScenarioId),
                    row.SuperRound.ToString(CultureInfo.InvariantCulture),
                    row.SequencePosition.ToString(CultureInfo.InvariantCulture),
                    row.PairIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.PairOrder),
                    row.WithinPairPosition.ToString(CultureInfo.InvariantCulture),
                    row.BlockIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.BlockType),
                    Csv(row.CaseId),
                    Csv(row.Variant),
                    Csv(row.Marker),
                    row.Samples.ToString(CultureInfo.InvariantCulture),
                    row.GpuValidSamples.ToString(CultureInfo.InvariantCulture),
                    row.DispatchesPerFrame.ToString(CultureInfo.InvariantCulture),
                    row.LogicalProblemBytesPerDispatch.ToString(CultureInfo.InvariantCulture),
                    row.SharedInputBytes.ToString(CultureInfo.InvariantCulture),
                    row.SharedOutputBytes.ToString(CultureInfo.InvariantCulture),
                    row.PrimitiveScratchBytes.ToString(CultureInfo.InvariantCulture),
                    row.CaseScratchBytes.ToString(CultureInfo.InvariantCulture),
                    row.CaseResidentBytes.ToString(CultureInfo.InvariantCulture),
                    row.ActualBenchmarkBufferResidentBytes.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.Gpu.Average),
                    Number(row.Gpu.P50),
                    Number(row.Gpu.P95),
                    Number(row.Gpu.P99),
                    Number(row.Frame.Average),
                    Number(row.Frame.P99),
                    Number(row.Enqueue.Average),
                    Number(row.Enqueue.P99),
                    row.FenceSupported ? "1" : "0",
                    row.FencePassed ? "1" : "0",
                    row.MeasurementReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    row.TimestampInstrumentationReadbackBytes.ToString(
                        CultureInfo.InvariantCulture)));
            }
        }
    }

    private void WriteValidationResults()
    {
        using (StreamWriter writer = CreateWriter("validation.csv"))
        {
            writer.WriteLine(
                "phase,caseId,variant,passed,message,readbackBytes,resultHash," +
                "validCount,invalidKeyCount,diagnosticFlags");
            foreach (GpuAdaptiveBinningValidationResult row in validationResults)
            {
                writer.WriteLine(string.Join(",",
                    Csv(row.Phase),
                    Csv(row.CaseId),
                    Csv(row.Variant),
                    row.Passed ? "1" : "0",
                    Csv(row.Message),
                    row.ReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ResultHash),
                    row.ValidCount.ToString(CultureInfo.InvariantCulture),
                    row.InvalidKeyCount.ToString(CultureInfo.InvariantCulture),
                    row.DiagnosticFlags.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private void WriteRunSummary(bool passed, string status)
    {
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            return;
        }
        long measurementReadback = 0;
        long instrumentationReadback = 0;
        int readyRows = 0;
        foreach (RawSample row in rawSamples)
        {
            measurementReadback += row.MeasurementReadbackBytes;
            instrumentationReadback +=
                row.TimestampInstrumentationReadbackBytes;
            if (IsTimestampReady(row))
            {
                readyRows++;
            }
        }
        int validationFailures = 0;
        long validationReadback = 0;
        foreach (GpuAdaptiveBinningValidationResult result in validationResults)
        {
            validationReadback += result.ReadbackBytes;
            if (!result.Passed)
            {
                validationFailures++;
            }
        }
        string[] lines =
        {
            "GPU adaptive spatial binning benchmark",
            "schemaVersion=2",
            "suite=summit.gpu-adaptive-binning",
            "passed=" + (passed ? "1" : "0"),
            "status=" + status,
            "processId=" + processId.ToString(CultureInfo.InvariantCulture),
            "scenarioId=" + scenarioId,
            "rawSampleCount=" + rawSamples.Count.ToString(CultureInfo.InvariantCulture),
            "blockCount=" + blockSummaries.Count.ToString(CultureInfo.InvariantCulture),
            "validationRows=" + validationResults.Count.ToString(CultureInfo.InvariantCulture),
            "validationFailures=" + validationFailures.ToString(CultureInfo.InvariantCulture),
            "validationReadbackBytes=" + validationReadback.ToString(CultureInfo.InvariantCulture),
            "measurementReadbackBytes=" + measurementReadback.ToString(CultureInfo.InvariantCulture),
            "timestampInstrumentationReadbackBytes=" +
                instrumentationReadback.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampReadyRows=" + readyRows.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampWarmupPassed=" + (timestampWarmupPassed ? "1" : "0"),
            "nativeTimestampWarmupStatus=" + timestampWarmupStatus,
            "nativeTimestampAbiVersion=" +
                timestampSupport.AbiVersion.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampCapabilityFlags=" +
                timestampSupport.CapabilityFlags.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampAcquireFailures=" +
                timestampAcquireFailures.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampResultFailures=" +
                timestampResultFailures.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampTimeouts=" +
                timestampTimeouts.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampPendingRows=" +
                pendingTimestamps.Count.ToString(CultureInfo.InvariantCulture),
            "gpuRegionTimingComplete=" +
                (IsTimestampEvidenceComplete() ? "1" : "0")
        };
        File.WriteAllLines(
            Path.Combine(reportDirectory, "run-summary.txt"),
            lines,
            new UTF8Encoding(false));
    }

    private StreamWriter CreateWriter(string fileName)
    {
        return new StreamWriter(
            Path.Combine(reportDirectory, fileName),
            false,
            new UTF8Encoding(false));
    }

    private void Finish(bool passed, string status)
    {
        if (finished)
        {
            return;
        }
        finished = true;
        Debug.Log(
            "GPU adaptive-binning benchmark " + status +
            ". Report: " + reportDirectory);
        try
        {
            DisposeResources();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            Application.Quit(passed ? 0 : 2);
        }
    }

    private void DisposeResources()
    {
        if (timestampCommands != null)
        {
            foreach (CommandBuffer commands in timestampCommands)
            {
                commands?.Dispose();
            }
            timestampCommands = null;
        }
        if (measurementCommands != null)
        {
            foreach (CommandBuffer commands in measurementCommands)
            {
                commands?.Dispose();
            }
            measurementCommands = null;
        }
        timestampBackend?.Dispose();
        timestampBackend = null;
        adapter?.Dispose();
        adapter = null;
    }

    private static int CaseIndex(GpuAdaptiveBinningBenchmarkVariant variant)
    {
        return variant == GpuAdaptiveBinningBenchmarkVariant.Control
            ? 0
            : variant == GpuAdaptiveBinningBenchmarkVariant.Radix
                ? 1
                : 2;
    }

    private static GpuAdaptiveBinningBenchmarkVariant VariantFromCaseIndex(
        int caseIndex)
    {
        return caseIndex == 0
            ? GpuAdaptiveBinningBenchmarkVariant.Control
            : caseIndex == 1
                ? GpuAdaptiveBinningBenchmarkVariant.Radix
                : GpuAdaptiveBinningBenchmarkVariant.Direct;
    }

    private static string StatusName(GpuTimestampStatus status)
    {
        switch (status)
        {
            case GpuTimestampStatus.Ready:
                return "ready";
            case GpuTimestampStatus.Pending:
                return "pending";
            case GpuTimestampStatus.Error:
                return "error";
            case GpuTimestampStatus.InvalidArgument:
                return "invalid-argument";
            case GpuTimestampStatus.InvalidToken:
                return "invalid-token";
            case GpuTimestampStatus.RingFull:
                return "ring-full";
            case GpuTimestampStatus.NotInitialized:
                return "not-initialized";
            case GpuTimestampStatus.Unsupported:
                return "unsupported";
            case GpuTimestampStatus.DeviceLost:
                return "device-lost";
            case GpuTimestampStatus.CallbackError:
                return "callback-error";
            case GpuTimestampStatus.FrequencyUnavailable:
                return "frequency-unavailable";
            case GpuTimestampStatus.StaleManagedToken:
                return "stale-managed-token";
            case GpuTimestampStatus.MalformedNativeResult:
                return "malformed-result";
            default:
                return "unknown";
        }
    }

    private static bool IsTimestampReady(RawSample row)
    {
        return !row.NativeTimestampTimedOut &&
            row.NativeTimestampStatus == GpuTimestampStatus.Ready;
    }

    private static string TimestampStatusName(RawSample row)
    {
        return row.NativeTimestampTimedOut
            ? "timeout"
            : StatusName(row.NativeTimestampStatus);
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string Number(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Csv(string value)
    {
        string safe = value ?? string.Empty;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    internal static bool ShouldSuppressInformationalLogs(string[] args)
    {
        return args != null &&
            HasArgument(args, EnableArgument);
    }

    private static bool HasArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ReadString(
        string[] args,
        string name,
        string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return fallback;
    }

    private static int ReadInt(
        string[] args,
        string name,
        int fallback)
    {
        return int.TryParse(
            ReadString(args, name, string.Empty),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : fallback;
    }

    private static float ReadFloat(
        string[] args,
        string name,
        float fallback)
    {
        return float.TryParse(
            ReadString(args, name, string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float value)
            ? value
            : fallback;
    }

    private static string SanitizeCommandLine(string[] arguments)
    {
        if (arguments == null)
        {
            return string.Empty;
        }
        return string.Join(" ", arguments);
    }

    private struct PendingTimestamp
    {
        public GpuTimestampToken Token;
        public int RowIndex;
        public GpuTimestampSampleFlags ExpectedFlags;
    }

    private struct RawSample
    {
        public int SourceRowIndex;
        public int ProcessId;
        public string ScenarioId;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public int BlockIndex;
        public string BlockType;
        public string CaseId;
        public string Variant;
        public string Marker;
        public int SampleIndex;
        public int SourceUnityFrame;
        public int ResultUnityFrame;
        public double ElapsedSeconds;
        public double EnqueueCpuMs;
        public ulong NativeTimestampToken;
        public ulong NativeTimestampUserTag;
        public uint NativeTimestampFlags;
        public GpuTimestampStatus NativeTimestampStatus;
        public bool NativeTimestampTimedOut;
        public ulong NativeTimestampBeginTicks;
        public ulong NativeTimestampEndTicks;
        public ulong NativeTimestampElapsedTicks;
        public ulong NativeTimestampFrequency;
        public long NativeTimestampElapsedNanoseconds;
        public ulong NativeTimestampFenceValue;
        public uint NativeTimestampDeviceGeneration;
        public double GpuRegionElapsedMs;
        public double FrameMs;
        public long MeasurementReadbackBytes;
        public long TimestampInstrumentationReadbackBytes;
    }

    private struct MetricStats
    {
        public double Average;
        public double P50;
        public double P95;
        public double P99;

        public static MetricStats Compute(double[] values, int count)
        {
            if (count <= 0)
            {
                return new MetricStats
                {
                    Average = -1.0,
                    P50 = -1.0,
                    P95 = -1.0,
                    P99 = -1.0
                };
            }
            double[] sorted = new double[count];
            Array.Copy(values, sorted, count);
            Array.Sort(sorted);
            double sum = 0.0;
            for (int i = 0; i < count; i++)
            {
                sum += sorted[i];
            }
            return new MetricStats
            {
                Average = sum / count,
                P50 = Percentile(sorted, 0.50),
                P95 = Percentile(sorted, 0.95),
                P99 = Percentile(sorted, 0.99)
            };
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            int index = Math.Max(
                0,
                Math.Min(
                    sorted.Length - 1,
                    (int)Math.Ceiling(sorted.Length * percentile) - 1));
            return sorted[index];
        }
    }

    private sealed class BlockSummary
    {
        public int ProcessId;
        public string ScenarioId;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public int BlockIndex;
        public string BlockType;
        public string CaseId;
        public string Variant;
        public string Marker;
        public int Samples;
        public int GpuValidSamples;
        public int DispatchesPerFrame;
        public long LogicalProblemBytesPerDispatch;
        public long SharedInputBytes;
        public long SharedOutputBytes;
        public long PrimitiveScratchBytes;
        public long CaseScratchBytes;
        public long CaseResidentBytes;
        public long ActualBenchmarkBufferResidentBytes;
        public MetricStats Gpu;
        public MetricStats Frame;
        public MetricStats Enqueue;
        public bool FenceSupported;
        public bool FencePassed;
        public long MeasurementReadbackBytes;
        public long TimestampInstrumentationReadbackBytes;
    }

    [Serializable]
    private sealed class BenchmarkConfiguration
    {
        public int schemaVersion;
        public string suite;
        public int processId;
        public string unityVersion;
        public string startedUtc;
        public string scenarioId;
        public string distribution;
        public int superRounds;
        public int warmupFrames;
        public int sampleFrames;
        public int cooldownFrames;
        public int elementCount;
        public int binCount;
        public int seed;
        public int exactSingleBinKey;
        public int dispatchesPerFrame;
        public string primitiveBackend;
        public string keyDomain;
        public string orderingContract;
        public string directValidationContract;
        public string inputGeneratorContract;
        public string directStageContract;
        public string radixStageContract;
        public bool innerProfilerMarkersEnabled;
        public int radixKeyBitCount;
        public int radixPassCount;
        public string directId;
        public string radixId;
        public string[] schedule;
        public string scheduleContract;
        public bool caseLocalWarmup;
        public bool sameProcessPaired;
        public bool informationalPlayerLogsSuppressed;
        public long logicalProblemBytesPerDispatch;
        public long sharedInputBytes;
        public long sharedOutputBytes;
        public long sharedContractResidentBytes;
        public long unionPrimitiveScratchBytes;
        public long directPrimitiveScratchBytes;
        public long radixPrimitiveScratchBytes;
        public long directInternalScratchBytes;
        public long radixInternalScratchBytes;
        public long directCaseScratchBytes;
        public long radixCaseScratchBytes;
        public long directCaseResidentBytes;
        public long radixCaseResidentBytes;
        public long actualBenchmarkBufferResidentBytes;
        public string expectedResultHash;
        public string resultHashAlgorithm;
        public int measurementReadbackBytesPerFrame;
        public int timestampInstrumentationBytesPerCompletedSample;
        public bool requireCompleteGpuTimings;
        public string nativeTimestampBackend;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public int nativeTimestampRingCapacity;
        public uint nativeTimestampDeviceGeneration;
        public bool nativeTimestampWarmupPassed;
        public string nativeTimestampWarmupStatus;
        public double nativeTimestampWarmupElapsedMs;
        public ulong nativeTimestampWarmupFrequency;
        public ulong nativeTimestampWarmupFenceValue;
        public uint nativeTimestampWarmupDeviceGeneration;
        public string buildCommit;
        public string directShaderSha256;
        public string radixShaderSha256;
        public string runtimeApiSha256;
        public string commandLine;
    }

    [Serializable]
    private sealed class DeviceMetadata
    {
        public int processId;
        public string operatingSystem;
        public string processorType;
        public int systemMemoryMiB;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public int graphicsDeviceVendorId;
        public int graphicsDeviceId;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public int graphicsMemoryMiB;
        public bool supportsComputeShaders;
        public bool supportsGraphicsFence;
        public bool supportsAsyncGpuReadback;
        public string nativeTimestampAvailability;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
    }
}
