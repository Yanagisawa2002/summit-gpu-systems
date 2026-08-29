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
public sealed class GpuDrivenInstanceMacrobenchmarkController : MonoBehaviour
{
    private const string EnableArgument =
        "-gpu-driven-instance-macrobenchmark";
    private const int MaxPreparedTimestampScopes = 64;
    private const int FrameTimingResultLatencyFrames = 4;
    private const int MaxValidationAttempts = 3;
    private static readonly WaitForEndOfFrame EndOfFrame =
        new WaitForEndOfFrame();

    private GpuDrivenInstanceMacrobenchmarkAdapter adapter;
    private GpuDrivenInstanceNativeTimestampBackend timestampBackend;
    private GpuTimestampSupport timestampSupport;
    private CommandBuffer[] timestampBeginCommands;
    private CommandBuffer[] timestampEndCommands;
    private CommandBuffer gpuWorkCommands;
    private int preparedTimestampScopes;
    private GpuDrivenInstanceFrameTimingCollector frameTimingCollector;
    private readonly List<PendingTimestamp> pendingTimestamps =
        new List<PendingTimestamp>(256);
    private RawSample[] rawSamples;
    private int rawSampleCount;
    private BlockSummary[] blockSummaries;
    private int blockSummaryCount;
    private readonly List<GpuDrivenInstanceMacrobenchmarkValidationResult>
        validationResults =
            new List<GpuDrivenInstanceMacrobenchmarkValidationResult>(4);

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
    private int validationReadbackRetries;
    private long validationAttemptReadbackBytes;
    private bool finished;
    private int processId;
    private double benchmarkStart;
    private string benchmarkStartedUtc;

    private string reportDirectory;
    private string scenarioId = "custom";
    private string visibility = "visible25";
    private int superRounds = 2;
    private int warmupFrames = 60;
    private int sampleFrames = 240;
    private int cooldownFrames = 15;
    private int instanceCount = 100000;
    private int viewCount = 1;
    private int drawGroupCount = 1;
    private int seed = 20260730;
    private float validationTimeoutSeconds = 60f;
    private bool requireCompleteGpuTimings = true;
    private bool requireCompleteFrameTimings = true;
    private string buildCommit = "unknown";
    private string runtimeShaderSha256 = "unknown";
    private string macroShaderSha256 = "unknown";
    private string runtimeApiSha256 = "unknown";
    private string[] originalArguments;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigureBenchmarkProcessLogging()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArgument(args, EnableArgument))
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
        if (!HasArgument(args, EnableArgument))
        {
            return;
        }
        GameObject host = new GameObject(
            "GPU Driven Instance Macrobenchmark Controller");
        DontDestroyOnLoad(host);
        GpuDrivenInstanceMacrobenchmarkController controller =
            host.AddComponent<GpuDrivenInstanceMacrobenchmarkController>();
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
            "-gpu-driven-instance-macro-report-dir",
            string.Empty);
        scenarioId = ReadString(
            args,
            "-gpu-driven-instance-macro-scenario-id",
            scenarioId);
        visibility = ReadString(
            args,
            "-gpu-driven-instance-macro-visibility",
            visibility);
        superRounds = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-super-rounds",
                superRounds),
            1,
            4);
        warmupFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-warmup-frames",
                warmupFrames),
            5,
            1800);
        sampleFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-sample-frames",
                sampleFrames),
            30,
            7200);
        cooldownFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-cooldown-frames",
                cooldownFrames),
            0,
            600);
        instanceCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-instance-count",
                instanceCount),
            1,
            16776960);
        viewCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-view-count",
                viewCount),
            1,
            Summit.GpuDrivenInstances.GpuDrivenInstancePipeline.MaximumViewCount);
        drawGroupCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-macro-draw-group-count",
                drawGroupCount),
            1,
            GpuDrivenInstanceMacrobenchmarkAdapter.FixedDrawGroupCount);
        seed = ReadInt(
            args,
            "-gpu-driven-instance-macro-seed",
            seed);
        validationTimeoutSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-driven-instance-macro-validation-timeout-seconds",
                validationTimeoutSeconds),
            5f,
            600f);
        requireCompleteGpuTimings =
            ReadInt(
                args,
                "-gpu-driven-instance-macro-require-complete-gpu-timings",
                requireCompleteGpuTimings ? 1 : 0) != 0;
        requireCompleteFrameTimings =
            ReadInt(
                args,
                "-gpu-driven-instance-macro-require-complete-frame-timings",
                requireCompleteFrameTimings ? 1 : 0) != 0;
        buildCommit = ReadString(
            args,
            "-gpu-driven-instance-macro-build-commit",
            buildCommit);
        runtimeShaderSha256 = ReadString(
            args,
            "-gpu-driven-instance-macro-runtime-shader-sha256",
            runtimeShaderSha256);
        macroShaderSha256 = ReadString(
            args,
            "-gpu-driven-instance-macro-render-shader-sha256",
            macroShaderSha256);
        runtimeApiSha256 = ReadString(
            args,
            "-gpu-driven-instance-macro-runtime-api-sha256",
            runtimeApiSha256);
    }

    private IEnumerator RunGuarded()
    {
        Stack<IEnumerator> routines = new Stack<IEnumerator>();
        routines.Push(Run());
        while (routines.Count != 0)
        {
            IEnumerator current = routines.Peek();
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
                HandleUnhandledException(failure);
                yield break;
            }
            if (!moved)
            {
                routines.Pop();
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
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        processId = Process.GetCurrentProcess().Id;
        benchmarkStart = Time.realtimeSinceStartupAsDouble;
        benchmarkStartedUtc = DateTime.UtcNow.ToString("O");
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            Finish(false, "missing-report-directory");
            yield break;
        }
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);

        IReadOnlyList<GpuDrivenInstanceScheduleEntry> schedule;
        try
        {
            adapter = new GpuDrivenInstanceMacrobenchmarkAdapter(
                instanceCount,
                viewCount,
                visibility,
                seed,
                drawGroupCount);
            gpuWorkCommands = new CommandBuffer
            {
                name =
                    GpuDrivenInstanceMacrobenchmarkAdapter.GpuMarker +
                    "/DynamicWork"
            };
            frameTimingCollector =
                new GpuDrivenInstanceFrameTimingCollector();
            InitializeTimestampBackend();
            BuildTimestampCommands();
            schedule = GpuDrivenInstanceBenchmarkSchedule.Build(superRounds);
            rawSamples = new RawSample[
                checked(schedule.Count * sampleFrames)];
            blockSummaries = new BlockSummary[schedule.Count];
            WriteConfiguration(schedule);
            WriteDeviceMetadata();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            WriteRunSummary(false, "initialization-failed");
            Finish(false, "initialization-failed");
            yield break;
        }

        // The custom guarded coroutine resumes immediately after an
        // EndOfFrame child completes. Explicit null yields keep every draw
        // submission in the normal Update phase; RenderMeshInstanced issued
        // from the end-of-frame phase is too late for the current frame.
        yield return null;
        SubmitUnmeasured(GpuDrivenInstanceBenchmarkVariant.Reference);
        yield return EndOfFrame;
        yield return null;
        SubmitUnmeasured(GpuDrivenInstanceBenchmarkVariant.Direct);
        yield return EndOfFrame;

        yield return null;
        yield return WarmupTimestampBackend();
        WriteConfiguration(schedule);
        if (requireCompleteGpuTimings && !timestampWarmupPassed)
        {
            WriteAllOutputs(false, "native-timestamp-warmup-failed", schedule);
            Finish(false, "native-timestamp-warmup-failed");
            yield break;
        }

        GpuDrivenInstanceMacrobenchmarkValidationResult warmupCpu = null;
        GpuDrivenInstanceMacrobenchmarkValidationResult warmupGpu = null;
        yield return null;
        yield return Validate(
            GpuDrivenInstanceBenchmarkVariant.Reference,
            "warmup",
            result => warmupCpu = result);
        yield return null;
        yield return Validate(
            GpuDrivenInstanceBenchmarkVariant.Direct,
            "warmup",
            result => warmupGpu = result);
        bool warmupValidationPassed =
            ValidationPairPassed(warmupCpu, warmupGpu);
        if (!warmupValidationPassed)
        {
            WriteAllOutputs(false, "warmup-validation-failed", schedule);
            Finish(false, "warmup-validation-failed");
            yield break;
        }

        for (int scheduleIndex = 0;
             scheduleIndex < schedule.Count;
             scheduleIndex++)
        {
            GpuDrivenInstanceScheduleEntry entry = schedule[scheduleIndex];
            for (int frame = 0; frame < cooldownFrames; frame++)
            {
                yield return null;
                SubmitUnmeasured(GpuDrivenInstanceBenchmarkVariant.Control);
                frameTimingCollector.CaptureFrameTimings();
                yield return EndOfFrame;
                frameTimingCollector.CollectLatest();
            }
            for (int frame = 0; frame < warmupFrames; frame++)
            {
                yield return null;
                SubmitUnmeasured(entry.Variant);
                frameTimingCollector.CaptureFrameTimings();
                yield return EndOfFrame;
                frameTimingCollector.CollectLatest();
            }

            frameTimingCollector.Reset();
            frameTimingCollector.CollectLatest();
            int sampleStart = rawSampleCount;
            for (int sampleIndex = 1;
                 sampleIndex <= sampleFrames;
                 sampleIndex++)
            {
                yield return null;
                int rowIndex = rawSampleCount;
                int sourceFrame = Time.frameCount;
                ulong userTag = checked((ulong)rowIndex + 1UL);
                GpuTimestampSampleFlags flags =
                    GpuTimestampSampleFlags.None;
                GpuTimestampStatus timestampStatus =
                    GpuTimestampStatus.Unsupported;
                GpuTimestampToken token = default;
                bool timestampSubmitted = TryAcquireTimestamp(
                    userTag,
                    flags,
                    sourceFrame,
                    out token,
                    out timestampStatus);

                SubmissionReceipt receipt = SubmitMeasured(
                    entry.Variant,
                    timestampSubmitted ? token.ScopeIndex : -1,
                    timestampSubmitted);
                if (timestampSubmitted)
                {
                    GpuTimestampStatus markStatus =
                        timestampBackend.MarkSubmitted(token);
                    if (markStatus != GpuTimestampStatus.Ready)
                    {
                        timestampBackend.Cancel(token);
                        timestampSubmitted = false;
                        timestampStatus = markStatus;
                        timestampResultFailures++;
                    }
                    else
                    {
                        ExecuteTimestampedWork(ref receipt, token.ScopeIndex);
                    }
                }
                if (!timestampSubmitted)
                {
                    ExecuteUntimestampedWork(ref receipt);
                }

                RawSample row = new RawSample
                {
                    SourceRowIndex = rowIndex,
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
                    SampleIndex = sampleIndex,
                    SourceUnityFrame = sourceFrame,
                    ElapsedSeconds =
                        Time.realtimeSinceStartupAsDouble - benchmarkStart,
                    CpuCullPackMs = receipt.CpuCullPackMs,
                    CommandRecordCpuMs = receipt.CommandRecordCpuMs,
                    CommandEnqueueCpuMs = receipt.CommandEnqueueCpuMs,
                    RenderApiSubmitMs = receipt.RenderApiSubmitMs,
                    TotalCpuSubmissionMs = receipt.TotalCpuSubmissionMs,
                    MainThreadAllocatedBytes =
                        receipt.MainThreadAllocatedBytes,
                    RenderApiCalls = receipt.RenderApiCalls,
                    LogicalDrawCommands = receipt.LogicalDrawCommands,
                    PipelineRecordCalls = receipt.PipelineRecordCalls,
                    ExplicitBufferUploadBytes = 0,
                    EngineInstancePayloadBytes =
                        entry.Variant ==
                            GpuDrivenInstanceBenchmarkVariant.Reference
                            ? adapter.CpuEngineInstancePayloadBytes
                            : 0,
                    NativeTimestampToken = token.Value,
                    NativeTimestampUserTag = userTag,
                    NativeTimestampFlags = (uint)flags,
                    NativeTimestampStatus = timestampSubmitted
                        ? "pending"
                        : StatusName(timestampStatus),
                    FrameTimingStatus =
                        GpuDrivenInstanceFrameTimingStatus.NoTimingAvailable,
                    CpuRenderThreadFrameValid = false,
                    GpuFrameValid = false,
                    EngineSubmissionWindowStatus =
                        GpuDrivenInstanceSubmissionWindowStatus
                            .FrameTimingUnavailable,
                    CpuFrameMs = double.NaN,
                    CpuMainThreadFrameMs = double.NaN,
                    CpuRenderThreadFrameMs = double.NaN,
                    GpuFrameMs = double.NaN,
                    RenderSubmissionWindowMs = double.NaN,
                    MeasurementReadbackBytes = 0
                };
                rawSamples[rowIndex] = row;
                rawSampleCount++;
                if (timestampSubmitted)
                {
                    pendingTimestamps.Add(new PendingTimestamp
                    {
                        Token = token,
                        RowIndex = rowIndex,
                        ExpectedFlags = flags
                    });
                }

                frameTimingCollector.CaptureFrameTimings();
                yield return EndOfFrame;
                GpuDrivenInstanceFrameTimingSample timing =
                    frameTimingCollector.CollectLatest();
                int readyOffset =
                    sampleIndex - 1 - FrameTimingResultLatencyFrames;
                if (readyOffset >= 0)
                {
                    ApplyFrameTiming(
                        sampleStart + readyOffset,
                        timing,
                        Time.frameCount);
                }
                PollTimestampResults();
            }

            for (int drain = 0;
                 drain < FrameTimingResultLatencyFrames;
                 drain++)
            {
                yield return null;
                SubmitUnmeasured(entry.Variant);
                frameTimingCollector.CaptureFrameTimings();
                yield return EndOfFrame;
                GpuDrivenInstanceFrameTimingSample timing =
                    frameTimingCollector.CollectLatest();
                ApplyFrameTiming(
                    sampleStart + sampleFrames -
                        FrameTimingResultLatencyFrames + drain,
                    timing,
                    Time.frameCount);
                PollTimestampResults();
            }

            yield return DrainTimestamps(sampleStart, sampleFrames);
            bool fencePassed = false;
            yield return CompleteWithGraphicsFence(
                passed => fencePassed = passed);
            blockSummaries[blockSummaryCount++] = SummarizeBlock(
                entry,
                sampleStart,
                sampleFrames,
                fencePassed);
        }
        yield return DrainAllTimestamps();

        GpuDrivenInstanceMacrobenchmarkValidationResult finalCpu = null;
        GpuDrivenInstanceMacrobenchmarkValidationResult finalGpu = null;
        yield return null;
        yield return Validate(
            GpuDrivenInstanceBenchmarkVariant.Reference,
            "final",
            result => finalCpu = result);
        yield return null;
        yield return Validate(
            GpuDrivenInstanceBenchmarkVariant.Direct,
            "final",
            result => finalGpu = result);
        bool finalValidationPassed = ValidationPairPassed(finalCpu, finalGpu);
        bool gpuTimingComplete = IsTimestampEvidenceComplete();
        bool frameTimingComplete = IsFrameTimingEvidenceComplete();
        bool fencesComplete = AreAllBlockFencesComplete();
        bool allocationFree = HasNoTimedAllocations();
        bool passedBenchmark =
            warmupValidationPassed &&
            finalValidationPassed &&
            fencesComplete &&
            allocationFree &&
            (!requireCompleteGpuTimings || gpuTimingComplete) &&
            (!requireCompleteFrameTimings || frameTimingComplete);
        string status = passedBenchmark
            ? "completed"
            : !finalValidationPassed
                ? "final-validation-failed"
                : !gpuTimingComplete
                    ? "gpu-timing-incomplete"
                    : !frameTimingComplete
                        ? "frame-timing-incomplete"
                        : !fencesComplete
                            ? "completion-fence-failed"
                            : "timed-allocation-detected";
        WriteAllOutputs(passedBenchmark, status, schedule);
        Finish(passedBenchmark, status);
    }

    private SubmissionReceipt SubmitMeasured(
        GpuDrivenInstanceBenchmarkVariant variant,
        int timestampScopeIndex,
        bool timestampSubmitted)
    {
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long cullStart = Stopwatch.GetTimestamp();
        if (variant == GpuDrivenInstanceBenchmarkVariant.Reference)
        {
            adapter.CullAndPackCpu();
        }
        long cullEnd = Stopwatch.GetTimestamp();

        CommandBuffer work = gpuWorkCommands;
        int renderApiCalls;
        int logicalDrawCommands;
        int pipelineRecordCalls;
        long recordStart = Stopwatch.GetTimestamp();
        gpuWorkCommands.Clear();
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                renderApiCalls = adapter.RecordCpuDraws(work);
                logicalDrawCommands = renderApiCalls;
                pipelineRecordCalls = 0;
                break;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                renderApiCalls = adapter.RecordGpuPipelineAndDraws(work);
                logicalDrawCommands =
                    adapter.GpuLogicalDrawCommandCount;
                pipelineRecordCalls = 1;
                break;
            case GpuDrivenInstanceBenchmarkVariant.Control:
                adapter.RecordControlFrame(work);
                renderApiCalls = 0;
                logicalDrawCommands = 0;
                pipelineRecordCalls = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
        long recordEnd = Stopwatch.GetTimestamp();
        return new SubmissionReceipt
        {
            Variant = variant,
            WorkCommands = work,
            TimestampScopeIndex = timestampScopeIndex,
            TimestampSubmitted = timestampSubmitted,
            AllocationStartBytes = allocationStart,
            CpuCullPackMs = TicksToMilliseconds(cullEnd - cullStart),
            CommandRecordCpuMs =
                TicksToMilliseconds(recordEnd - recordStart),
            RenderApiCalls = renderApiCalls,
            LogicalDrawCommands = logicalDrawCommands,
            PipelineRecordCalls = pipelineRecordCalls
        };
    }

    private void ExecuteTimestampedWork(
        ref SubmissionReceipt receipt,
        int scopeIndex)
    {
        Graphics.ExecuteCommandBuffer(timestampBeginCommands[scopeIndex]);
        ExecuteWorkBody(ref receipt);
        Graphics.ExecuteCommandBuffer(timestampEndCommands[scopeIndex]);
    }

    private void ExecuteUntimestampedWork(ref SubmissionReceipt receipt)
    {
        ExecuteWorkBody(ref receipt);
    }

    private void ExecuteWorkBody(ref SubmissionReceipt receipt)
    {
        long enqueueStart = Stopwatch.GetTimestamp();
        if (receipt.WorkCommands != null)
        {
            Graphics.ExecuteCommandBuffer(receipt.WorkCommands);
        }
        long enqueueEnd = Stopwatch.GetTimestamp();

        receipt.CommandEnqueueCpuMs =
            TicksToMilliseconds(enqueueEnd - enqueueStart);
        // ExecuteCommandBuffer is already timed end-to-end in
        // CommandEnqueueCpuMs. There is no independent public Unity timing
        // for the internal render-thread/driver submission portion, so keep
        // that separate field explicitly unavailable instead of reporting a
        // misleading measured zero.
        receipt.RenderApiSubmitMs = double.NaN;
        receipt.TotalCpuSubmissionMs =
            receipt.CpuCullPackMs +
            receipt.CommandRecordCpuMs +
            receipt.CommandEnqueueCpuMs;
        receipt.MainThreadAllocatedBytes = Math.Max(
            0L,
            GC.GetAllocatedBytesForCurrentThread() -
            receipt.AllocationStartBytes);
    }

    private void SubmitUnmeasured(
        GpuDrivenInstanceBenchmarkVariant variant)
    {
        SubmissionReceipt receipt = SubmitMeasured(variant, -1, false);
        ExecuteUntimestampedWork(ref receipt);
    }

    private bool TryAcquireTimestamp(
        ulong userTag,
        GpuTimestampSampleFlags flags,
        int sourceFrame,
        out GpuTimestampToken token,
        out GpuTimestampStatus status)
    {
        token = default;
        status = GpuTimestampStatus.Unsupported;
        if (!timestampOperational || timestampBackend == null)
        {
            return false;
        }
        status = timestampBackend.Acquire(
            userTag,
            flags,
            sourceFrame,
            out token);
        if (status == GpuTimestampStatus.Ready &&
            token.ScopeIndex >= 0 &&
            token.ScopeIndex < preparedTimestampScopes)
        {
            return true;
        }
        if (status == GpuTimestampStatus.Ready)
        {
            timestampBackend.Cancel(token);
            status = GpuTimestampStatus.RingFull;
        }
        timestampAcquireFailures++;
        return false;
    }

    private void InitializeTimestampBackend()
    {
        timestampOperational =
            GpuDrivenInstanceNativeTimestampBackend.TryCreate(
                out timestampBackend,
                out timestampSupport);
        if (timestampOperational)
        {
            timestampBackend.ExecuteFrequencyInitialization();
        }
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
        timestampBeginCommands =
            new CommandBuffer[preparedTimestampScopes];
        timestampEndCommands =
            new CommandBuffer[preparedTimestampScopes];
        for (int scope = 0; scope < preparedTimestampScopes; scope++)
        {
            CommandBuffer begin = new CommandBuffer
            {
                name = "GPU.DrivenInstanceMacro/Timestamp/Begin/" + scope
            };
            timestampBackend.RecordBegin(scope, begin);
            timestampBeginCommands[scope] = begin;
            CommandBuffer end = new CommandBuffer
            {
                name = "GPU.DrivenInstanceMacro/Timestamp/End/" + scope
            };
            timestampBackend.RecordEnd(scope, end);
            timestampEndCommands[scope] = end;
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
            timestampWarmupStatus =
                "mark-submitted-" + StatusName(status);
            timestampOperational = false;
            yield break;
        }
        Graphics.ExecuteCommandBuffer(
            timestampBeginCommands[token.ScopeIndex]);
        Graphics.ExecuteCommandBuffer(
            timestampEndCommands[token.ScopeIndex]);
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
                    result.DeviceGeneration ==
                        timestampSupport.DeviceGeneration &&
                    result.EndTicks >= result.BeginTicks &&
                    result.FenceValue > 0;
                timestampWarmupStatus = timestampWarmupPassed
                    ? "ready"
                    : "validation-failed";
            }
            else
            {
                timestampWarmupStatus =
                    "result-" + StatusName(status);
            }
            timestampOperational = timestampWarmupPassed;
            yield break;
        }
        timestampWarmupStatus = "timeout";
        timestampOperational = false;
        timestampTimeouts++;
    }

    private IEnumerator Validate(
        GpuDrivenInstanceBenchmarkVariant variant,
        string phase,
        Action<GpuDrivenInstanceMacrobenchmarkValidationResult> completion)
    {
        string retryHistory = string.Empty;
        for (int attempt = 1; attempt <= MaxValidationAttempts; attempt++)
        {
            adapter.BeginValidation(variant, phase);
            yield return EndOfFrame;
            adapter.RequestValidationReadback();
            double deadline =
                Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
            bool retry = false;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (adapter.TryCompleteValidation(out var result))
                {
                    validationAttemptReadbackBytes += result.ReadbackBytes;
                    result.AttemptCount = attempt;
                    result.RetryHistory = retryHistory;
                    if (result.RetryableReadbackFailure &&
                        attempt < MaxValidationAttempts)
                    {
                        retryHistory = AppendValidationRetryHistory(
                            retryHistory,
                            attempt,
                            result.FailedReadbackRequests);
                        validationReadbackRetries++;
                        retry = true;
                        break;
                    }
                    validationResults.Add(result);
                    completion(result);
                    yield break;
                }
                yield return null;
            }
            if (retry)
            {
                yield return null;
                continue;
            }
            var timeout = new GpuDrivenInstanceMacrobenchmarkValidationResult
            {
                Phase = phase,
                CaseId = adapter.CaseId(variant),
                Variant = adapter.VariantName(variant),
                Passed = false,
                Message = "Validation timed out.",
                ResultHash = "unavailable",
                ImageHash = "unavailable",
                AttemptCount = attempt,
                RetryHistory = retryHistory
            };
            validationResults.Add(timeout);
            completion(timeout);
            yield break;
        }
    }

    private static string AppendValidationRetryHistory(
        string current,
        int attempt,
        string failedRequests)
    {
        string entry =
            "attempt-" + attempt.ToString(CultureInfo.InvariantCulture) +
            ":" + (failedRequests ?? "unknown");
        return string.IsNullOrEmpty(current)
            ? entry
            : current + "|" + entry;
    }

    private static bool ValidationPairPassed(
        GpuDrivenInstanceMacrobenchmarkValidationResult cpu,
        GpuDrivenInstanceMacrobenchmarkValidationResult gpu)
    {
        return cpu != null &&
            gpu != null &&
            cpu.Passed &&
            gpu.Passed &&
            !string.IsNullOrEmpty(cpu.ImageHash) &&
            string.Equals(
                cpu.ImageHash,
                gpu.ImageHash,
                StringComparison.Ordinal);
    }

    private void PollTimestampResults()
    {
        if (timestampBackend == null)
        {
            return;
        }
        for (int index = pendingTimestamps.Count - 1;
             index >= 0;
             index--)
        {
            PendingTimestamp pending = pendingTimestamps[index];
            GpuTimestampStatus status = timestampBackend.TryConsume(
                pending.Token,
                Time.frameCount,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }
            RawSample row = rawSamples[pending.RowIndex];
            row.NativeTimestampStatus = StatusName(status);
            if (status == GpuTimestampStatus.Ready)
            {
                bool valid =
                    result.Token.Value == pending.Token.Value &&
                    result.Token.UserTag == pending.Token.UserTag &&
                    result.SourceFrame == pending.Token.SourceFrame &&
                    result.NativeFlags == (uint)pending.ExpectedFlags &&
                    result.TimestampFrequency > 0 &&
                    result.EndTicks >= result.BeginTicks &&
                    result.DeviceGeneration ==
                        timestampSupport.DeviceGeneration &&
                    result.FenceValue > 0;
                if (!valid)
                {
                    row.NativeTimestampStatus = "malformed-result";
                    timestampResultFailures++;
                    timestampOperational = false;
                }
                else
                {
                    row.ResultUnityFrame = result.ResultFrame;
                    row.NativeTimestampBeginTicks = result.BeginTicks;
                    row.NativeTimestampEndTicks = result.EndTicks;
                    row.NativeTimestampElapsedTicks = result.ElapsedTicks;
                    row.NativeTimestampFrequency = result.TimestampFrequency;
                    row.NativeTimestampElapsedNanoseconds =
                        result.ElapsedNanoseconds;
                    row.NativeTimestampFenceValue = result.FenceValue;
                    row.NativeTimestampDeviceGeneration =
                        result.DeviceGeneration;
                    row.NativeGpuRegionMs = result.ElapsedMilliseconds;
                    row.TimestampInstrumentationReadbackBytes = 16;
                }
            }
            else
            {
                timestampResultFailures++;
                timestampOperational = false;
            }
            rawSamples[pending.RowIndex] = row;
            pendingTimestamps.RemoveAt(index);
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
        if (HasPendingRows(sampleStart, count))
        {
            MarkTimeouts(sampleStart, count);
            timestampOperational = false;
        }
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
            MarkTimeouts(0, rawSampleCount);
        }
    }

    private bool HasPendingRows(int sampleStart, int count)
    {
        int end = sampleStart + count;
        for (int index = 0; index < pendingTimestamps.Count; index++)
        {
            int row = pendingTimestamps[index].RowIndex;
            if (row >= sampleStart && row < end)
            {
                return true;
            }
        }
        return false;
    }

    private void MarkTimeouts(int sampleStart, int count)
    {
        int end = sampleStart + count;
        for (int index = pendingTimestamps.Count - 1;
             index >= 0;
             index--)
        {
            PendingTimestamp pending = pendingTimestamps[index];
            if (pending.RowIndex < sampleStart || pending.RowIndex >= end)
            {
                continue;
            }
            RawSample row = rawSamples[pending.RowIndex];
            row.NativeTimestampStatus = "timeout";
            rawSamples[pending.RowIndex] = row;
            timestampTimeouts++;
            pendingTimestamps.RemoveAt(index);
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
            name = "GPU.DrivenInstanceMacro/CompletionFence"
        };
        GraphicsFence fence = commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.AllGPUOperations);
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

    private void ApplyFrameTiming(
        int rowIndex,
        GpuDrivenInstanceFrameTimingSample timing,
        int resultUnityFrame)
    {
        RawSample row = rawSamples[rowIndex];
        row.FrameTimingValid = timing.Valid;
        row.FrameTimingStatus = timing.Status;
        row.CpuRenderThreadFrameValid =
            timing.CpuRenderThreadValid;
        row.GpuFrameValid = timing.GpuFrameValid;
        row.EngineSubmissionWindowValid =
            timing.SubmissionWindowValid;
        row.EngineSubmissionWindowStatus =
            timing.SubmissionWindowStatus;
        row.FrameTimingResultUnityFrame = resultUnityFrame;
        row.FrameTimingCaptureLatencyFrames =
            FrameTimingResultLatencyFrames;
        row.FrameStartTimestamp = timing.FrameStartTimestamp;
        row.FirstSubmitTimestamp = timing.FirstSubmitTimestamp;
        row.CpuTimePresentCalled = timing.CpuTimePresentCalled;
        row.CpuTimeFrameComplete = timing.CpuTimeFrameComplete;
        row.CpuFrameMs = timing.CpuFrameMs;
        row.CpuMainThreadFrameMs = timing.CpuMainThreadMs;
        row.CpuRenderThreadFrameMs = timing.CpuRenderThreadMs;
        row.GpuFrameMs = timing.GpuFrameMs;
        row.RenderSubmissionWindowMs = timing.CpuSubmissionMs;
        rawSamples[rowIndex] = row;
    }

    private BlockSummary SummarizeBlock(
        GpuDrivenInstanceScheduleEntry entry,
        int sampleStart,
        int count,
        bool fencePassed)
    {
        double[] cull = new double[count];
        double[] record = new double[count];
        double[] enqueue = new double[count];
        double[] renderSubmit = new double[count];
        double[] totalSubmit = new double[count];
        double[] gpuRegion = new double[count];
        double[] cpuFrame = new double[count];
        double[] mainFrame = new double[count];
        double[] renderFrame = new double[count];
        double[] gpuFrame = new double[count];
        double[] engineWindow = new double[count];
        int gpuRegionCount = 0;
        int frameTimingCount = 0;
        int renderThreadFrameCount = 0;
        int gpuFrameCount = 0;
        int engineWindowCount = 0;
        int allocationRows = 0;
        long allocationBytes = 0;
        long instrumentationReadback = 0;
        for (int index = 0; index < count; index++)
        {
            RawSample row = rawSamples[sampleStart + index];
            cull[index] = row.CpuCullPackMs;
            record[index] = row.CommandRecordCpuMs;
            enqueue[index] = row.CommandEnqueueCpuMs;
            renderSubmit[index] = row.RenderApiSubmitMs;
            totalSubmit[index] = row.TotalCpuSubmissionMs;
            if (row.NativeTimestampStatus == "ready")
            {
                gpuRegion[gpuRegionCount++] = row.NativeGpuRegionMs;
            }
            if (row.FrameTimingValid)
            {
                cpuFrame[frameTimingCount] = row.CpuFrameMs;
                mainFrame[frameTimingCount] = row.CpuMainThreadFrameMs;
                frameTimingCount++;
            }
            if (row.CpuRenderThreadFrameValid)
            {
                renderFrame[renderThreadFrameCount++] =
                    row.CpuRenderThreadFrameMs;
            }
            if (row.GpuFrameValid)
            {
                gpuFrame[gpuFrameCount++] = row.GpuFrameMs;
            }
            if (row.EngineSubmissionWindowValid)
            {
                engineWindow[engineWindowCount++] =
                    row.RenderSubmissionWindowMs;
            }
            if (row.MainThreadAllocatedBytes != 0)
            {
                allocationRows++;
                allocationBytes += row.MainThreadAllocatedBytes;
            }
            instrumentationReadback +=
                row.TimestampInstrumentationReadbackBytes;
        }
        GpuDrivenInstanceBenchmarkVariant variant = entry.Variant;
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
            NativeGpuValidSamples = gpuRegionCount,
            FrameTimingValidSamples = frameTimingCount,
            RenderThreadFrameValidSamples = renderThreadFrameCount,
            GpuFrameValidSamples = gpuFrameCount,
            EngineSubmissionWindowValidSamples = engineWindowCount,
            RenderApiCalls = rawSamples[sampleStart].RenderApiCalls,
            LogicalDrawCommands = rawSamples[sampleStart].LogicalDrawCommands,
            PipelineRecordCalls =
                rawSamples[sampleStart].PipelineRecordCalls,
            CpuCullPack = MetricStats.Compute(cull, count),
            CommandRecord = MetricStats.Compute(record, count),
            CommandEnqueue = MetricStats.Compute(enqueue, count),
            RenderApiSubmit = MetricStats.Compute(renderSubmit, count),
            TotalCpuSubmission = MetricStats.Compute(totalSubmit, count),
            NativeGpuRegion = MetricStats.Compute(gpuRegion, gpuRegionCount),
            CpuFrame = MetricStats.Compute(cpuFrame, frameTimingCount),
            MainThreadFrame = MetricStats.Compute(mainFrame, frameTimingCount),
            RenderThreadFrame =
                MetricStats.Compute(renderFrame, renderThreadFrameCount),
            GpuFrame = MetricStats.Compute(gpuFrame, gpuFrameCount),
            EngineSubmissionWindow =
                MetricStats.Compute(engineWindow, engineWindowCount),
            MainThreadAllocationRows = allocationRows,
            MainThreadAllocatedBytes = allocationBytes,
            FenceSupported = SystemInfo.supportsGraphicsFence,
            FencePassed = fencePassed,
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
        for (int index = 0; index < rawSampleCount; index++)
        {
            if (rawSamples[index].NativeTimestampStatus != "ready")
            {
                return false;
            }
        }
        return true;
    }

    private bool IsFrameTimingEvidenceComplete()
    {
        for (int index = 0; index < rawSampleCount; index++)
        {
            if (!rawSamples[index].FrameTimingValid)
            {
                return false;
            }
        }
        return rawSampleCount > 0;
    }

    private bool AreAllBlockFencesComplete()
    {
        if (blockSummaryCount == 0)
        {
            return false;
        }
        for (int index = 0; index < blockSummaryCount; index++)
        {
            if (!blockSummaries[index].FenceSupported ||
                !blockSummaries[index].FencePassed)
            {
                return false;
            }
        }
        return true;
    }

    private bool HasNoTimedAllocations()
    {
        if (rawSampleCount == 0)
        {
            return false;
        }
        for (int index = 0; index < rawSampleCount; index++)
        {
            if (rawSamples[index].MainThreadAllocatedBytes != 0)
            {
                return false;
            }
        }
        return true;
    }

    private void WriteAllOutputs(
        bool passed,
        string status,
        IReadOnlyList<GpuDrivenInstanceScheduleEntry> schedule)
    {
        WriteConfiguration(schedule);
        WriteRawSamples();
        WriteBlockSummaries();
        WriteValidationResults();
        WriteRunSummary(passed, status);
    }

    private void WriteConfiguration(
        IReadOnlyList<GpuDrivenInstanceScheduleEntry> schedule)
    {
        if (string.IsNullOrWhiteSpace(reportDirectory) || adapter == null)
        {
            return;
        }
        string[] order = new string[schedule.Count];
        for (int index = 0; index < schedule.Count; index++)
        {
            order[index] =
                schedule[index].BlockType + ":" +
                adapter.VariantName(schedule[index].Variant);
        }
        var config = new BenchmarkConfiguration
        {
            schemaVersion = 1,
            suite = "summit.gpu-driven-instance-macro",
            processId = processId,
            unityVersion = Application.unityVersion,
            startedUtc = benchmarkStartedUtc,
            scenarioId = scenarioId,
            visibility = visibility,
            visibilityLayout =
                GpuDrivenInstanceInputGenerator.VisibilityLayoutId,
            superRounds = superRounds,
            warmupFrames = warmupFrames,
            sampleFrames = sampleFrames,
            cooldownFrames = cooldownFrames,
            instanceCount = instanceCount,
            viewCount = viewCount,
            drawGroupCount = drawGroupCount,
            seed = seed,
            baselineId = "cpu-burst-engine-native-v1",
            directId =
                "gpu-visible-only-engine-indirect-v1",
            schedule = order,
            scheduleContract = "control-pre;ABBA;BAAB;control-post",
            caseLocalWarmup = true,
            sameProcessPaired = true,
            vSyncCount = QualitySettings.vSyncCount,
            targetFrameRate = Application.targetFrameRate,
            cpuFrameMetric = "FrameTiming.cpuFrameTime",
            engineSubmissionWindowMetric =
                "FrameTiming.firstSubmitTimestamp-to-cpuTimePresentCalled",
            frameTimingAlignment =
                "capture-fifo-fixed-four-frame-latency-v1",
            frameTimingResultLatencyFrames =
                FrameTimingResultLatencyFrames,
            cpuSubmissionMetric =
                "Stopwatch(cull+pack+engine-command-record+enqueue)",
            sharedInputBytes = adapter.SharedInputBytes,
            sharedOutputBytes = adapter.SharedOutputBytes,
            cpuResidentBytes = adapter.CpuResidentBytes,
            gpuResidentBytes = adapter.GpuResidentBytes,
            gpuScratchBytes = adapter.GpuScratchBytes,
            expectedResultHash = adapter.ExpectedResultHash,
            visibleInstanceCount = adapter.VisibleInstanceCount,
            visiblePairCount = adapter.VisiblePairCount,
            measurementReadbackBytesPerFrame = 0,
            requireCompleteGpuTimings = requireCompleteGpuTimings,
            requireCompleteFrameTimings = requireCompleteFrameTimings,
            frameTimingFeatureEnabled =
                FrameTimingManager.IsFeatureEnabled(),
            cpuTimerFrequency =
                FrameTimingManager.GetCpuTimerFrequency(),
            gpuTimerFrequency =
                FrameTimingManager.GetGpuTimerFrequency(),
            nativeTimestampBackend =
                "native-d3d12-begin-work-end/private-completion-fence",
            nativeTimestampAbiVersion = timestampSupport.AbiVersion,
            nativeTimestampCapabilityFlags =
                timestampSupport.CapabilityFlags,
            nativeTimestampRingCapacity = timestampSupport.RingCapacity,
            nativeTimestampWarmupPassed = timestampWarmupPassed,
            nativeTimestampWarmupStatus = timestampWarmupStatus,
            nativeTimestampWarmupElapsedMs = timestampWarmupElapsedMs,
            nativeTimestampWarmupFrequency = timestampWarmupFrequency,
            nativeTimestampWarmupFenceValue = timestampWarmupFence,
            nativeTimestampWarmupDeviceGeneration =
                timestampWarmupGeneration,
            buildCommit = buildCommit,
            runtimeShaderSha256 = runtimeShaderSha256,
            macroShaderSha256 = macroShaderSha256,
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
        var device = new DeviceMetadata
        {
            processId = processId,
            operatingSystem = SystemInfo.operatingSystem,
            processorType = SystemInfo.processorType,
            processorCount = SystemInfo.processorCount,
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
            supportsInstancing = SystemInfo.supportsInstancing,
            supportsIndirectArgumentsBuffer =
                SystemInfo.supportsIndirectArgumentsBuffer,
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
                "sourceUnityFrame,resultUnityFrame,elapsedSeconds,cpuCullPackMs," +
                "commandRecordCpuMs,commandEnqueueCpuMs,renderApiSubmitMs," +
                "totalCpuSubmissionMs,mainThreadAllocatedBytes,renderApiCalls," +
                "logicalDrawCommands,pipelineRecordCalls," +
                "explicitBufferUploadBytes,engineInstancePayloadBytes," +
                "frameTimingValid," +
                "frameTimingStatus,frameTimingResultUnityFrame," +
                "frameTimingCaptureLatencyFrames," +
                "cpuRenderThreadFrameValid,gpuFrameValid," +
                "engineSubmissionWindowValid," +
                "engineSubmissionWindowStatus,frameStartTimestamp,firstSubmitTimestamp," +
                "cpuTimePresentCalled,cpuTimeFrameComplete,cpuFrameMs,cpuMainThreadFrameMs," +
                "cpuRenderThreadFrameMs,gpuFrameMs,renderSubmissionWindowMs," +
                "nativeTimestampToken,nativeTimestampUserTag,nativeTimestampFlags," +
                "nativeTimestampStatus,nativeTimestampBeginTicks,nativeTimestampEndTicks," +
                "nativeTimestampElapsedTicks,nativeTimestampFrequency," +
                "nativeTimestampElapsedNanoseconds,nativeTimestampFenceValue," +
                "nativeTimestampDeviceGeneration,nativeGpuRegionMs," +
                "measurementReadbackBytes,timestampInstrumentationReadbackBytes");
            for (int index = 0; index < rawSampleCount; index++)
            {
                RawSample row = rawSamples[index];
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
                    Number(row.CpuCullPackMs),
                    Number(row.CommandRecordCpuMs),
                    Number(row.CommandEnqueueCpuMs),
                    Number(row.RenderApiSubmitMs),
                    Number(row.TotalCpuSubmissionMs),
                    row.MainThreadAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                    row.RenderApiCalls.ToString(CultureInfo.InvariantCulture),
                    row.LogicalDrawCommands.ToString(CultureInfo.InvariantCulture),
                    row.PipelineRecordCalls.ToString(
                        CultureInfo.InvariantCulture),
                    row.ExplicitBufferUploadBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.EngineInstancePayloadBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.FrameTimingValid ? "1" : "0",
                    Csv(FrameTimingStatusName(row.FrameTimingStatus)),
                    row.FrameTimingResultUnityFrame.ToString(
                        CultureInfo.InvariantCulture),
                    row.FrameTimingCaptureLatencyFrames.ToString(
                        CultureInfo.InvariantCulture),
                    row.CpuRenderThreadFrameValid ? "1" : "0",
                    row.GpuFrameValid ? "1" : "0",
                    row.EngineSubmissionWindowValid ? "1" : "0",
                    Csv(SubmissionWindowStatusName(
                        row.EngineSubmissionWindowStatus)),
                    row.FrameStartTimestamp.ToString(CultureInfo.InvariantCulture),
                    row.FirstSubmitTimestamp.ToString(CultureInfo.InvariantCulture),
                    row.CpuTimePresentCalled.ToString(CultureInfo.InvariantCulture),
                    row.CpuTimeFrameComplete.ToString(CultureInfo.InvariantCulture),
                    Number(row.CpuFrameMs),
                    Number(row.CpuMainThreadFrameMs),
                    Number(row.CpuRenderThreadFrameMs),
                    Number(row.GpuFrameMs),
                    Number(row.RenderSubmissionWindowMs),
                    row.NativeTimestampToken.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampUserTag.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFlags.ToString(CultureInfo.InvariantCulture),
                    Csv(row.NativeTimestampStatus),
                    row.NativeTimestampBeginTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampEndTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFrequency.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedNanoseconds.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFenceValue.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampDeviceGeneration.ToString(CultureInfo.InvariantCulture),
                    Number(row.NativeGpuRegionMs),
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
                "nativeGpuValidSamples,frameTimingValidSamples," +
                "renderThreadFrameValidSamples,gpuFrameValidSamples," +
                "engineSubmissionWindowValidSamples,renderApiCalls," +
                "logicalDrawCommands,pipelineRecordCalls," +
                MetricStats.Header("cpuCullPack") + "," +
                MetricStats.Header("commandRecordCpu") + "," +
                MetricStats.Header("commandEnqueueCpu") + "," +
                MetricStats.Header("renderApiSubmit") + "," +
                MetricStats.Header("totalCpuSubmission") + "," +
                MetricStats.Header("nativeGpuRegion") + "," +
                MetricStats.Header("cpuFrame") + "," +
                MetricStats.Header("cpuMainThreadFrame") + "," +
                MetricStats.Header("cpuRenderThreadFrame") + "," +
                MetricStats.Header("gpuFrame") + "," +
                MetricStats.Header("renderSubmissionWindow") + "," +
                "mainThreadAllocationRows,mainThreadAllocatedBytes," +
                "fenceSupported,fencePassed," +
                "timestampInstrumentationReadbackBytes");
            for (int index = 0; index < blockSummaryCount; index++)
            {
                BlockSummary row = blockSummaries[index];
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
                    row.NativeGpuValidSamples.ToString(CultureInfo.InvariantCulture),
                    row.FrameTimingValidSamples.ToString(CultureInfo.InvariantCulture),
                    row.RenderThreadFrameValidSamples.ToString(
                        CultureInfo.InvariantCulture),
                    row.GpuFrameValidSamples.ToString(
                        CultureInfo.InvariantCulture),
                    row.EngineSubmissionWindowValidSamples.ToString(
                        CultureInfo.InvariantCulture),
                    row.RenderApiCalls.ToString(CultureInfo.InvariantCulture),
                    row.LogicalDrawCommands.ToString(CultureInfo.InvariantCulture),
                    row.PipelineRecordCalls.ToString(
                        CultureInfo.InvariantCulture),
                    row.CpuCullPack.CsvValues(),
                    row.CommandRecord.CsvValues(),
                    row.CommandEnqueue.CsvValues(),
                    row.RenderApiSubmit.CsvValues(),
                    row.TotalCpuSubmission.CsvValues(),
                    row.NativeGpuRegion.CsvValues(),
                    row.CpuFrame.CsvValues(),
                    row.MainThreadFrame.CsvValues(),
                    row.RenderThreadFrame.CsvValues(),
                    row.GpuFrame.CsvValues(),
                    row.EngineSubmissionWindow.CsvValues(),
                    row.MainThreadAllocationRows.ToString(CultureInfo.InvariantCulture),
                    row.MainThreadAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                    row.FenceSupported ? "1" : "0",
                    row.FencePassed ? "1" : "0",
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
                "phase,caseId,variant,passed,message,attemptCount,retryHistory," +
                "readbackBytes,resultHash,imageHash,validCount,invalidKeyCount," +
                "diagnosticFlags");
            foreach (var row in validationResults)
            {
                writer.WriteLine(string.Join(",",
                    Csv(row.Phase),
                    Csv(row.CaseId),
                    Csv(row.Variant),
                    row.Passed ? "1" : "0",
                    Csv(row.Message),
                    row.AttemptCount.ToString(CultureInfo.InvariantCulture),
                    Csv(row.RetryHistory),
                    row.ReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ResultHash),
                    Csv(row.ImageHash),
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
        int validationFailures = 0;
        long validationReadback = 0;
        int imageParityFailures = 0;
        var imageHashes = new Dictionary<string, string>();
        foreach (var result in validationResults)
        {
            validationReadback += result.ReadbackBytes;
            if (!result.Passed)
            {
                validationFailures++;
            }
            string key = result.Phase;
            if (result.Variant ==
                GpuDrivenInstanceMacrobenchmarkAdapter.CpuVariantName)
            {
                imageHashes[key] = result.ImageHash;
            }
            else if (imageHashes.TryGetValue(key, out string cpuHash) &&
                !string.Equals(cpuHash, result.ImageHash,
                    StringComparison.Ordinal))
            {
                imageParityFailures++;
            }
        }
        int timestampReady = 0;
        int frameTimingReady = 0;
        int renderThreadFrameReady = 0;
        int gpuFrameReady = 0;
        int engineSubmissionWindowReady = 0;
        int allocationRows = 0;
        long allocationBytes = 0;
        long instrumentationReadback = 0;
        for (int index = 0; index < rawSampleCount; index++)
        {
            RawSample row = rawSamples[index];
            if (row.NativeTimestampStatus == "ready")
            {
                timestampReady++;
            }
            if (row.FrameTimingValid)
            {
                frameTimingReady++;
            }
            if (row.CpuRenderThreadFrameValid)
            {
                renderThreadFrameReady++;
            }
            if (row.GpuFrameValid)
            {
                gpuFrameReady++;
            }
            if (row.EngineSubmissionWindowValid)
            {
                engineSubmissionWindowReady++;
            }
            if (row.MainThreadAllocatedBytes != 0)
            {
                allocationRows++;
                allocationBytes += row.MainThreadAllocatedBytes;
            }
            instrumentationReadback +=
                row.TimestampInstrumentationReadbackBytes;
        }
        string[] lines =
        {
            "GPU driven instance macrobenchmark",
            "schemaVersion=1",
            "suite=summit.gpu-driven-instance-macro",
            "passed=" + (passed ? "1" : "0"),
            "status=" + status,
            "processId=" + processId.ToString(CultureInfo.InvariantCulture),
            "scenarioId=" + scenarioId,
            "rawSampleCount=" + rawSampleCount.ToString(CultureInfo.InvariantCulture),
            "blockCount=" + blockSummaryCount.ToString(CultureInfo.InvariantCulture),
            "validationRows=" + validationResults.Count.ToString(CultureInfo.InvariantCulture),
            "validationFailures=" + validationFailures.ToString(CultureInfo.InvariantCulture),
            "validationReadbackRetries=" +
                validationReadbackRetries.ToString(CultureInfo.InvariantCulture),
            "validationMaxAttempts=" +
                MaxValidationAttempts.ToString(CultureInfo.InvariantCulture),
            "imageParityFailures=" + imageParityFailures.ToString(CultureInfo.InvariantCulture),
            "validationReadbackBytes=" +
                validationAttemptReadbackBytes.ToString(
                    CultureInfo.InvariantCulture),
            "validationFinalReadbackBytes=" +
                validationReadback.ToString(CultureInfo.InvariantCulture),
            "measurementReadbackBytes=0",
            "timestampInstrumentationReadbackBytes=" +
                instrumentationReadback.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampReadyRows=" + timestampReady.ToString(CultureInfo.InvariantCulture),
            "frameTimingReadyRows=" + frameTimingReady.ToString(CultureInfo.InvariantCulture),
            "renderThreadFrameReadyRows=" +
                renderThreadFrameReady.ToString(CultureInfo.InvariantCulture),
            "gpuFrameReadyRows=" +
                gpuFrameReady.ToString(CultureInfo.InvariantCulture),
            "engineSubmissionWindowReadyRows=" +
                engineSubmissionWindowReady.ToString(
                    CultureInfo.InvariantCulture),
            "mainThreadAllocationRows=" + allocationRows.ToString(CultureInfo.InvariantCulture),
            "mainThreadAllocatedBytes=" + allocationBytes.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampWarmupPassed=" + (timestampWarmupPassed ? "1" : "0"),
            "nativeTimestampWarmupStatus=" + timestampWarmupStatus,
            "nativeTimestampAcquireFailures=" + timestampAcquireFailures.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampResultFailures=" + timestampResultFailures.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampTimeouts=" + timestampTimeouts.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampPendingRows=" + pendingTimestamps.Count.ToString(CultureInfo.InvariantCulture),
            "gpuRegionTimingComplete=" + (IsTimestampEvidenceComplete() ? "1" : "0"),
            "frameTimingComplete=" + (IsFrameTimingEvidenceComplete() ? "1" : "0"),
            "completionFencesComplete=" +
                (AreAllBlockFencesComplete() ? "1" : "0"),
            "timedAllocationFree=" +
                (HasNoTimedAllocations() ? "1" : "0")
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
            "GPU driven-instance macrobenchmark " + status +
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
        if (timestampBeginCommands != null)
        {
            foreach (CommandBuffer commands in timestampBeginCommands)
            {
                commands?.Dispose();
            }
            timestampBeginCommands = null;
        }
        if (timestampEndCommands != null)
        {
            foreach (CommandBuffer commands in timestampEndCommands)
            {
                commands?.Dispose();
            }
            timestampEndCommands = null;
        }
        timestampBackend?.Dispose();
        timestampBackend = null;
        gpuWorkCommands?.Dispose();
        gpuWorkCommands = null;
        adapter?.Dispose();
        adapter = null;
    }

    private static string StatusName(GpuTimestampStatus status)
    {
        switch (status)
        {
            case GpuTimestampStatus.Ready:
                return "ready";
            case GpuTimestampStatus.Pending:
                return "pending";
            case GpuTimestampStatus.Unsupported:
                return "unsupported";
            case GpuTimestampStatus.RingFull:
                return "ring-full";
            case GpuTimestampStatus.Error:
                return "error";
            case GpuTimestampStatus.InvalidArgument:
                return "invalid-argument";
            case GpuTimestampStatus.InvalidToken:
                return "invalid-token";
            case GpuTimestampStatus.DeviceLost:
                return "device-lost";
            case GpuTimestampStatus.NotInitialized:
                return "not-initialized";
            case GpuTimestampStatus.CallbackError:
                return "callback-error";
            case GpuTimestampStatus.FrequencyUnavailable:
                return "frequency-unavailable";
            case GpuTimestampStatus.StaleManagedToken:
                return "stale-managed-token";
            case GpuTimestampStatus.MalformedNativeResult:
                return "malformed-native-result";
            default:
                return "unknown";
        }
    }

    private static string FrameTimingStatusName(
        GpuDrivenInstanceFrameTimingStatus status)
    {
        switch (status)
        {
            case GpuDrivenInstanceFrameTimingStatus.Valid:
                return "valid";
            case GpuDrivenInstanceFrameTimingStatus.FeatureUnavailable:
                return "feature-unavailable";
            case GpuDrivenInstanceFrameTimingStatus.NoTimingAvailable:
                return "no-timing-available";
            case GpuDrivenInstanceFrameTimingStatus
                .DuplicateFrameStartTimestamp:
                return "duplicate-frame-start-timestamp";
            case GpuDrivenInstanceFrameTimingStatus
                .NonMonotonicFrameStartTimestamp:
                return "non-monotonic-frame-start-timestamp";
            case GpuDrivenInstanceFrameTimingStatus
                .InvalidCpuTimerFrequency:
                return "invalid-cpu-timer-frequency";
            case GpuDrivenInstanceFrameTimingStatus
                .InvalidGpuTimerFrequency:
                return "invalid-gpu-timer-frequency";
            case GpuDrivenInstanceFrameTimingStatus.InvalidTimestamp:
                return "invalid-timestamp";
            case GpuDrivenInstanceFrameTimingStatus.InvalidMetric:
                return "invalid-metric";
            default:
                return "unknown";
        }
    }

    private static string SubmissionWindowStatusName(
        GpuDrivenInstanceSubmissionWindowStatus status)
    {
        switch (status)
        {
            case GpuDrivenInstanceSubmissionWindowStatus.Valid:
                return "valid";
            case GpuDrivenInstanceSubmissionWindowStatus
                .FrameTimingUnavailable:
                return "frame-timing-unavailable";
            case GpuDrivenInstanceSubmissionWindowStatus
                .InvalidCpuTimerFrequency:
                return "invalid-cpu-timer-frequency";
            case GpuDrivenInstanceSubmissionWindowStatus.InvalidTimestamp:
                return "invalid-timestamp";
            case GpuDrivenInstanceSubmissionWindowStatus.InvalidMetric:
                return "invalid-metric";
            default:
                return "unknown";
        }
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string Number(double value)
    {
        return double.IsNaN(value)
            ? "unavailable"
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Csv(string value)
    {
        string safe = value ?? string.Empty;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private static bool HasArgument(string[] args, string name)
    {
        if (args == null)
        {
            return false;
        }
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name,
                StringComparison.OrdinalIgnoreCase))
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
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name,
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
        return arguments == null ? string.Empty : string.Join(" ", arguments);
    }

    private struct SubmissionReceipt
    {
        public GpuDrivenInstanceBenchmarkVariant Variant;
        public CommandBuffer WorkCommands;
        public int TimestampScopeIndex;
        public bool TimestampSubmitted;
        public long AllocationStartBytes;
        public double CpuCullPackMs;
        public double CommandRecordCpuMs;
        public double CommandEnqueueCpuMs;
        public double RenderApiSubmitMs;
        public double TotalCpuSubmissionMs;
        public long MainThreadAllocatedBytes;
        public int RenderApiCalls;
        public int LogicalDrawCommands;
        public int PipelineRecordCalls;
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
        public double CpuCullPackMs;
        public double CommandRecordCpuMs;
        public double CommandEnqueueCpuMs;
        public double RenderApiSubmitMs;
        public double TotalCpuSubmissionMs;
        public long MainThreadAllocatedBytes;
        public int RenderApiCalls;
        public int LogicalDrawCommands;
        public int PipelineRecordCalls;
        public long ExplicitBufferUploadBytes;
        public long EngineInstancePayloadBytes;
        public bool FrameTimingValid;
        public GpuDrivenInstanceFrameTimingStatus FrameTimingStatus;
        public int FrameTimingResultUnityFrame;
        public int FrameTimingCaptureLatencyFrames;
        public bool CpuRenderThreadFrameValid;
        public bool GpuFrameValid;
        public bool EngineSubmissionWindowValid;
        public GpuDrivenInstanceSubmissionWindowStatus
            EngineSubmissionWindowStatus;
        public ulong FrameStartTimestamp;
        public ulong FirstSubmitTimestamp;
        public ulong CpuTimePresentCalled;
        public ulong CpuTimeFrameComplete;
        public double CpuFrameMs;
        public double CpuMainThreadFrameMs;
        public double CpuRenderThreadFrameMs;
        public double GpuFrameMs;
        public double RenderSubmissionWindowMs;
        public ulong NativeTimestampToken;
        public ulong NativeTimestampUserTag;
        public uint NativeTimestampFlags;
        public string NativeTimestampStatus;
        public ulong NativeTimestampBeginTicks;
        public ulong NativeTimestampEndTicks;
        public ulong NativeTimestampElapsedTicks;
        public ulong NativeTimestampFrequency;
        public long NativeTimestampElapsedNanoseconds;
        public ulong NativeTimestampFenceValue;
        public uint NativeTimestampDeviceGeneration;
        public double NativeGpuRegionMs;
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
                    Average = double.NaN,
                    P50 = double.NaN,
                    P95 = double.NaN,
                    P99 = double.NaN
                };
            }
            double[] sorted = new double[count];
            Array.Copy(values, sorted, count);
            Array.Sort(sorted);
            double sum = 0.0;
            for (int index = 0; index < count; index++)
            {
                sum += sorted[index];
            }
            return new MetricStats
            {
                Average = sum / count,
                P50 = Percentile(sorted, 0.50),
                P95 = Percentile(sorted, 0.95),
                P99 = Percentile(sorted, 0.99)
            };
        }

        public static string Header(string prefix)
        {
            return prefix + "AverageMs," +
                prefix + "P50Ms," +
                prefix + "P95Ms," +
                prefix + "P99Ms";
        }

        public string CsvValues()
        {
            return string.Join(",",
                Number(Average),
                Number(P50),
                Number(P95),
                Number(P99));
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
        public int NativeGpuValidSamples;
        public int FrameTimingValidSamples;
        public int RenderThreadFrameValidSamples;
        public int GpuFrameValidSamples;
        public int EngineSubmissionWindowValidSamples;
        public int RenderApiCalls;
        public int LogicalDrawCommands;
        public int PipelineRecordCalls;
        public MetricStats CpuCullPack;
        public MetricStats CommandRecord;
        public MetricStats CommandEnqueue;
        public MetricStats RenderApiSubmit;
        public MetricStats TotalCpuSubmission;
        public MetricStats NativeGpuRegion;
        public MetricStats CpuFrame;
        public MetricStats MainThreadFrame;
        public MetricStats RenderThreadFrame;
        public MetricStats GpuFrame;
        public MetricStats EngineSubmissionWindow;
        public int MainThreadAllocationRows;
        public long MainThreadAllocatedBytes;
        public bool FenceSupported;
        public bool FencePassed;
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
        public string visibility;
        public string visibilityLayout;
        public int superRounds;
        public int warmupFrames;
        public int sampleFrames;
        public int cooldownFrames;
        public int instanceCount;
        public int viewCount;
        public int drawGroupCount;
        public int seed;
        public string baselineId;
        public string directId;
        public string[] schedule;
        public string scheduleContract;
        public bool caseLocalWarmup;
        public bool sameProcessPaired;
        public int vSyncCount;
        public int targetFrameRate;
        public string cpuFrameMetric;
        public string engineSubmissionWindowMetric;
        public string frameTimingAlignment;
        public int frameTimingResultLatencyFrames;
        public string cpuSubmissionMetric;
        public long sharedInputBytes;
        public long sharedOutputBytes;
        public long cpuResidentBytes;
        public long gpuResidentBytes;
        public long gpuScratchBytes;
        public string expectedResultHash;
        public int visibleInstanceCount;
        public int visiblePairCount;
        public int measurementReadbackBytesPerFrame;
        public bool requireCompleteGpuTimings;
        public bool requireCompleteFrameTimings;
        public bool frameTimingFeatureEnabled;
        public ulong cpuTimerFrequency;
        public ulong gpuTimerFrequency;
        public string nativeTimestampBackend;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public int nativeTimestampRingCapacity;
        public bool nativeTimestampWarmupPassed;
        public string nativeTimestampWarmupStatus;
        public double nativeTimestampWarmupElapsedMs;
        public ulong nativeTimestampWarmupFrequency;
        public ulong nativeTimestampWarmupFenceValue;
        public uint nativeTimestampWarmupDeviceGeneration;
        public string buildCommit;
        public string runtimeShaderSha256;
        public string macroShaderSha256;
        public string runtimeApiSha256;
        public string commandLine;
    }

    [Serializable]
    private sealed class DeviceMetadata
    {
        public int processId;
        public string operatingSystem;
        public string processorType;
        public int processorCount;
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
        public bool supportsInstancing;
        public bool supportsIndirectArgumentsBuffer;
        public string nativeTimestampAvailability;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
    }
}
