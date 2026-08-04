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
public sealed class GpuSensorDataPackingBenchmarkController : MonoBehaviour
{
    private const string EnableArgument =
        "-gpu-sensor-data-packing-benchmark";
    private static readonly WaitForEndOfFrame EndOfFrame =
        new WaitForEndOfFrame();

    private readonly List<RawSample> rawSamples =
        new List<RawSample>(20000);
    private readonly List<BlockSummary> blockSummaries =
        new List<BlockSummary>(24);
    private readonly List<GpuSensorDataPackingValidationResult>
        validationResults =
            new List<GpuSensorDataPackingValidationResult>(12);
    private readonly List<PendingTimestamp> pendingTimestamps =
        new List<PendingTimestamp>(256);

    private GpuSensorDataPackingBenchmarkAdapter adapter;
    private GpuSensorPipelineNativeTimestampBackend timestampBackend;
    private GpuTimestampSupport timestampSupport;
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
    private int superRounds =
        GpuSensorDataPackingBenchmarkSchedule.FormalSuperRoundCount;
    private int warmupFrames = 60;
    private int sampleFrames = 240;
    private int cooldownFrames = 15;
    private int elementCount = 1 << 18;
    private int binCount = 1 << 18;
    private int queryCount = 64;
    private int seed = 20261003;
    private int commandSlotCount =
        GpuSensorDataPackingBenchmarkAdapter.DefaultCommandSlotCount;
    private float validationTimeoutSeconds = 60.0f;
    private bool requireCompleteGpuTimings = true;
    private string buildCommit = "unknown";
    private string runtimeShaderSha256 = "unknown";
    private string runtimeApiSha256 = "unknown";
    private string nativeTimestampDllSha256 = "unknown";
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
            "GPU Sensor Data Packing Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuSensorDataPackingBenchmarkController controller =
            host.AddComponent<GpuSensorDataPackingBenchmarkController>();
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
            "-gpu-sensor-data-packing-report-dir",
            string.Empty);
        scenarioId = ReadString(
            args,
            "-gpu-sensor-data-packing-scenario-id",
            scenarioId);
        superRounds = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-super-rounds",
                superRounds),
            1,
            4);
        warmupFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-warmup-frames",
                warmupFrames),
            5,
            1800);
        sampleFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-sample-frames",
                sampleFrames),
            GpuSensorDataPackingBenchmarkAdapter.StateCount,
            7200);
        cooldownFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-cooldown-frames",
                cooldownFrames),
            0,
            600);
        elementCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-element-count",
                elementCount),
            1024,
            16776960);
        binCount = ReadInt(
            args,
            "-gpu-sensor-data-packing-bin-count",
            binCount);
        queryCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-query-count",
                queryCount),
            1,
            65535);
        seed = ReadInt(args, "-gpu-sensor-data-packing-seed", seed);
        commandSlotCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-sensor-data-packing-command-slot-count",
                commandSlotCount),
            2,
            16);
        validationTimeoutSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-sensor-data-packing-validation-timeout-seconds",
                validationTimeoutSeconds),
            5.0f,
            600.0f);
        requireCompleteGpuTimings =
            ReadInt(
                args,
                "-gpu-sensor-data-packing-require-complete-gpu-timings",
                requireCompleteGpuTimings ? 1 : 0) != 0;
        buildCommit = ReadString(
            args,
            "-gpu-sensor-data-packing-build-commit",
            buildCommit);
        runtimeShaderSha256 = ReadString(
            args,
            "-gpu-sensor-data-packing-runtime-shader-sha256",
            runtimeShaderSha256);
        runtimeApiSha256 = ReadString(
            args,
            "-gpu-sensor-data-packing-runtime-api-sha256",
            runtimeApiSha256);
        nativeTimestampDllSha256 = ReadString(
            args,
            "-gpu-sensor-data-packing-native-timestamp-dll-sha256",
            nativeTimestampDllSha256);
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
        if (binCount != GpuSensorDataPackingBenchmarkAdapter.StateCount *
            GpuSensorDataPackingBenchmarkAdapter.StateCount *
            GpuSensorDataPackingBenchmarkAdapter.StateCount)
        {
            WriteRunSummary(false, "fixed-bin-count-mismatch");
            Finish(false, "fixed-bin-count-mismatch");
            yield break;
        }

        try
        {
            adapter = new GpuSensorDataPackingBenchmarkAdapter(
                elementCount,
                queryCount,
                seed,
                commandSlotCount);
            InitializeTimestampBackend();
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

        yield return WarmupTimestampBackend();
        WriteConfiguration();
        if (requireCompleteGpuTimings && !timestampWarmupPassed)
        {
            WriteAllOutputs(false, "native-timestamp-warmup-failed");
            Finish(false, "native-timestamp-warmup-failed");
            yield break;
        }

        bool correctnessPassed = true;
        yield return RunValidationSweep(
            "before",
            passed => correctnessPassed &= passed);
        if (!correctnessPassed)
        {
            WriteAllOutputs(false, "before-validation-failed");
            Finish(false, "before-validation-failed");
            yield break;
        }

        IReadOnlyList<GpuSensorDataPackingScheduleEntry> schedule =
            GpuSensorDataPackingBenchmarkSchedule.Build(superRounds);
        foreach (GpuSensorDataPackingScheduleEntry entry in schedule)
        {
            for (int frame = 0; frame < cooldownFrames; frame++)
            {
                yield return EndOfFrame;
            }
            for (int frame = 0; frame < warmupFrames; frame++)
            {
                int state =
                    GpuSensorDataPackingBenchmarkSchedule.LogicalStateForSample(
                        frame);
                yield return IssueUntimed(entry.Variant, state);
                yield return EndOfFrame;
            }
            yield return DrainWorkSlots();

            int blockSampleFrames = entry.BlockType == "precondition"
                ? GpuSensorDataPackingBenchmarkSchedule
                    .PreconditionSampleCount
                : sampleFrames;
            int sampleStart = rawSamples.Count;
            for (int sample = 0; sample < blockSampleFrames; sample++)
            {
                int state =
                    GpuSensorDataPackingBenchmarkSchedule.LogicalStateForSample(
                        sample);
                yield return IssueMeasured(entry, sample + 1, state);
                yield return EndOfFrame;
                rawSamples[rawSamples.Count - 1].FrameMs =
                    Time.unscaledDeltaTime * 1000.0;
                PollTimestampResults();
            }

            yield return DrainTimestamps(sampleStart, blockSampleFrames);
            yield return DrainWorkSlots();
            bool blockFencePassed = false;
            yield return CompleteWithGraphicsFence(
                passed => blockFencePassed = passed);
            if (entry.BlockType == "measurement")
            {
                bool capturePassed = false;
                yield return CaptureBlock(
                    entry.Variant,
                    GpuSensorDataPackingBenchmarkSchedule
                        .LogicalStateForSample(blockSampleFrames - 1),
                    passed => capturePassed = passed);
                correctnessPassed &= capturePassed;
            }
            blockSummaries.Add(SummarizeBlock(
                entry,
                sampleStart,
                blockSampleFrames,
                blockFencePassed));

            if (entry.BlockType == "measurement" &&
                entry.WithinPairPosition == 2)
            {
                bool pairPassed = false;
                yield return CompareCapturedPair(
                    "post-pair-" + entry.PairIndex,
                    passed => pairPassed = passed);
                correctnessPassed &= pairPassed;
            }
        }

        yield return DrainAllTimestamps();
        yield return RunValidationSweep(
            "after",
            passed => correctnessPassed &= passed);
        bool timingComplete = IsTimestampEvidenceComplete();
        bool passedBenchmark =
            correctnessPassed &&
            (!requireCompleteGpuTimings || timingComplete);
        string status = passedBenchmark
            ? timingComplete ? "completed" : "completed-correctness-only"
            : correctnessPassed ? "gpu-timing-incomplete" :
                "correctness-failed";
        WriteAllOutputs(passedBenchmark, status);
        Finish(passedBenchmark, status);
    }

    private IEnumerator IssueMeasured(
        GpuSensorDataPackingScheduleEntry entry,
        int sampleIndex,
        int logicalState)
    {
        int commandSlotWaitFrames = 0;
        int slotIndex = -1;
        while (!adapter.TryAcquireCommandSlot(out slotIndex))
        {
            commandSlotWaitFrames++;
            yield return null;
        }


        int sourceFrame = Time.frameCount;
        ulong userTag = checked((ulong)rawSamples.Count + 1UL);
        GpuTimestampSampleFlags flags =
            entry.Variant == GpuSensorDataPackingBenchmarkVariant.Control
                ? GpuTimestampSampleFlags.EmptyScope
                : GpuTimestampSampleFlags.None;
        GpuTimestampStatus status = GpuTimestampStatus.Unsupported;
        GpuTimestampToken token = default;
        bool timestampSubmitted = false;
        if (timestampOperational && timestampBackend != null)
        {
            status = timestampBackend.Acquire(
                userTag,
                flags,
                sourceFrame,
                out token);
            if (status == GpuTimestampStatus.Ready)
            {
                status = timestampBackend.MarkSubmitted(token);
                timestampSubmitted = status == GpuTimestampStatus.Ready;
                if (!timestampSubmitted)
                {
                    timestampResultFailures++;
                    timestampBackend.Cancel(token);
                }
            }
            else
            {
                timestampAcquireFailures++;
            }
        }

        CommandBuffer commands = adapter.Commands(slotIndex);
        long recordStart = Stopwatch.GetTimestamp();
        commands.Clear();
        commands.name = adapter.Marker(entry.Variant) +
            "/State/" + logicalState;
        if (timestampSubmitted)
        {
            timestampBackend.RecordBegin(token.ScopeIndex, commands);
        }
        double pipelineRecordMs =
            adapter.RecordWorkload(slotIndex, entry.Variant, logicalState);
        if (timestampSubmitted)
        {
            timestampBackend.RecordEnd(token.ScopeIndex, commands);
        }
        GraphicsFence lifetimeFence =
            adapter.AppendLifetimeFence(slotIndex);
        double commandRecordMs = TicksToMilliseconds(
            Stopwatch.GetTimestamp() - recordStart);

        long submitStart = Stopwatch.GetTimestamp();
        Graphics.ExecuteCommandBuffer(commands);
        double submissionMs = TicksToMilliseconds(
            Stopwatch.GetTimestamp() - submitStart);
        adapter.MarkCommandSlotSubmitted(slotIndex, lifetimeFence);

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
            SampleIndex = sampleIndex,
            LogicalState = logicalState,
            CommandSlot = slotIndex,
            CommandSlotWaitFrames = commandSlotWaitFrames,
            SourceUnityFrame = sourceFrame,
            ElapsedSeconds =
                Time.realtimeSinceStartupAsDouble - benchmarkStart,
            CpuPipelineRecordMs = pipelineRecordMs,
            CommandRecordCpuMs = commandRecordMs,
            SubmissionCpuMs = submissionMs,
            ProducerMaterializedWriteBytes =
                entry.Variant ==
                    GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView
                    ? adapter.BaselineProducerLogicalWriteBytesPerFrame
                    : entry.Variant ==
                        GpuSensorDataPackingBenchmarkVariant
                            .PackedSoaFusedEndCursor
                        ? adapter.PackedProducerLogicalWriteBytesPerFrame
                        : 0L,
            PipelineElementMaterializedWriteBytes =
                entry.Variant ==
                    GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView
                    ? adapter
                        .BaselinePipelineElementMaterializedWriteBytesPerFrame
                    : entry.Variant ==
                        GpuSensorDataPackingBenchmarkVariant
                            .PackedSoaFusedEndCursor
                        ? adapter
                            .PackedPipelineElementMaterializedWriteBytesPerFrame
                        : 0L,
            SpatialBuildAddressedReadBytes =
                entry.Variant ==
                    GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView
                    ? adapter.BaselineSpatialBuildAddressedReadBytesPerFrame
                    : entry.Variant ==
                        GpuSensorDataPackingBenchmarkVariant
                            .PackedSoaFusedEndCursor
                        ? adapter.PackedSpatialBuildAddressedReadBytesPerFrame
                        : 0L,
            BinCountAtomicOperations =
                entry.Variant == GpuSensorDataPackingBenchmarkVariant.Control
                    ? 0L
                    : entry.Variant ==
                        GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView
                        ? adapter.BaselineCountAtomicOperationsPerFrame
                        : adapter.PackedFusedCountAtomicOperationsPerFrame,
            ScatterAtomicOperations =
                entry.Variant == GpuSensorDataPackingBenchmarkVariant.Control
                    ? 0L
                    : entry.Variant ==
                        GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView
                        ? adapter.BaselineScatterReservationAtomicOperationsPerFrame
                        : adapter.PackedScatterReservationAtomicOperationsPerFrame,
            NativeTimestampToken = token.Value,
            NativeTimestampUserTag = userTag,
            NativeTimestampFlags = (uint)flags,
            NativeTimestampStatus = timestampSubmitted
                ? "pending"
                : StatusName(status),
            MeasurementReadbackBytes =
                GpuSensorDataPackingBenchmarkAdapter.WorkloadReadbackBytesPerFrame
        };
        rawSamples.Add(row);
        if (timestampSubmitted)
        {
            pendingTimestamps.Add(new PendingTimestamp
            {
                Token = token,
                Row = row,
                ExpectedFlags = flags
            });
        }
    }

    private IEnumerator IssueUntimed(
        GpuSensorDataPackingBenchmarkVariant variant,
        int logicalState)
    {
        int slotIndex = -1;
        while (!adapter.TryAcquireCommandSlot(out slotIndex))
        {
            yield return null;
        }
        CommandBuffer commands = adapter.Commands(slotIndex);
        commands.Clear();
        commands.name = adapter.Marker(variant) + "/Untimed";
        adapter.RecordWorkload(slotIndex, variant, logicalState);
        GraphicsFence fence = adapter.AppendLifetimeFence(slotIndex);
        Graphics.ExecuteCommandBuffer(commands);
        adapter.MarkCommandSlotSubmitted(slotIndex, fence);
    }

    private IEnumerator DrainWorkSlots()
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (adapter.HasInFlightWork &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
        }
        if (adapter.HasInFlightWork)
        {
            throw new TimeoutException(
                "Persistent command slots did not become fence-safe.");
        }
    }

    private IEnumerator RunValidationSweep(
        string phase,
        Action<bool> completion)
    {
        bool passed = true;
        foreach (GpuSensorDataPackingBenchmarkVariant variant in new[]
        {
            GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView,
            GpuSensorDataPackingBenchmarkVariant
                .PackedSoaFusedEndCursor
        })
        {
            for (int state = 0;
                state < GpuSensorDataPackingBenchmarkAdapter.StateCount;
                state++)
            {
                yield return IssueUntimed(variant, state);
            }
            yield return DrainWorkSlots();
            yield return CaptureBlock(
                variant,
                GpuSensorDataPackingBenchmarkAdapter.StateCount - 1,
                capturePassed => passed &= capturePassed);
        }
        yield return CompareCapturedPair(
            phase,
            comparePassed => passed &= comparePassed);
        completion(passed);
    }

    private IEnumerator CaptureBlock(
        GpuSensorDataPackingBenchmarkVariant variant,
        int logicalState,
        Action<bool> completion)
    {
        adapter.BeginCaptureBlock(variant, logicalState);
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (adapter.TryCompleteCapture(out bool passed))
            {
                completion(passed);
                yield break;
            }
            yield return null;
        }
        completion(false);
    }

    private IEnumerator CompareCapturedPair(
        string phase,
        Action<bool> completion)
    {
        adapter.BeginCompareCapturedBlocks(phase);
        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (adapter.TryCompleteComparison(
                    out GpuSensorDataPackingValidationResult result))
            {
                validationResults.Add(result);
                completion(result.Passed);
                yield break;
            }
            yield return null;
        }
        GpuSensorDataPackingValidationResult timeout =
            new GpuSensorDataPackingValidationResult
            {
                Phase = phase,
                Passed = false,
                Message = "Digest comparison timed out.",
                ResultHash = "unavailable",
                StateCount = GpuSensorDataPackingBenchmarkAdapter.StateCount
            };
        validationResults.Add(timeout);
        completion(false);
    }

    private void InitializeTimestampBackend()
    {
        timestampOperational =
            GpuSensorPipelineNativeTimestampBackend.TryCreate(
                out timestampBackend,
                out timestampSupport);
        if (timestampOperational)
        {
            timestampBackend.ExecuteFrequencyInitialization();
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
        if (status != GpuTimestampStatus.Ready)
        {
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
        CommandBuffer commands = new CommandBuffer
        {
            name = "GPU.SensorDataPacking/NativeTimestamp/Warmup"
        };
        timestampBackend.RecordBegin(token.ScopeIndex, commands);
        timestampBackend.RecordEnd(token.ScopeIndex, commands);
        Graphics.ExecuteCommandBuffer(commands);
        commands.Dispose();

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
                timestampWarmupStatus = "result-" + StatusName(status);
            }
            timestampOperational = timestampWarmupPassed;
            yield break;
        }
        timestampWarmupStatus = "timeout";
        timestampOperational = false;
        timestampTimeouts++;
    }

    private void PollTimestampResults()
    {
        if (timestampBackend == null)
        {
            return;
        }
        for (int index = pendingTimestamps.Count - 1; index >= 0; index--)
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
            RawSample row = pending.Row;
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
                    CopyTimestampResult(row, result);
                }
            }
            else
            {
                timestampResultFailures++;
                timestampOperational = false;
            }
            pendingTimestamps.RemoveAt(index);
        }
    }

    private static void CopyTimestampResult(
        RawSample row,
        GpuTimestampResult result)
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
        row.NativeTimestampDeviceGeneration = result.DeviceGeneration;
        row.GpuRegionElapsedMs = result.ElapsedMilliseconds;
        row.TimestampInstrumentationReadbackBytes = 16;
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
            MarkTimeouts(0, rawSamples.Count);
        }
    }

    private bool HasPendingRows(int sampleStart, int count)
    {
        int end = sampleStart + count;
        foreach (PendingTimestamp pending in pendingTimestamps)
        {
            int rowIndex = pending.Row.SourceRowIndex;
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
        for (int index = pendingTimestamps.Count - 1; index >= 0; index--)
        {
            PendingTimestamp pending = pendingTimestamps[index];
            int rowIndex = pending.Row.SourceRowIndex;
            if (rowIndex < sampleStart || rowIndex >= end)
            {
                continue;
            }
            pending.Row.NativeTimestampStatus = "timeout";
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
            name = "GPU.SensorDataPacking/CompletionFence"
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
        GpuSensorDataPackingScheduleEntry entry,
        int sampleStart,
        int count,
        bool fencePassed)
    {
        double[] gpu = new double[count];
        double[] frame = new double[count];
        double[] producer = new double[count];
        double[] pipelineRecord = new double[count];
        double[] commandRecord = new double[count];
        double[] submission = new double[count];
        int gpuCount = 0;
        long producerMaterializedWriteBytes = 0;
        long pipelineElementMaterializedWriteBytes = 0;
        long spatialBuildAddressedReadBytes = 0;
        long binCountAtomicOperations = 0;
        long scatterAtomicOperations = 0;
        long measurementReadback = 0;
        long instrumentationReadback = 0;
        int commandSlotWaitFrames = 0;
        for (int index = 0; index < count; index++)
        {
            RawSample row = rawSamples[sampleStart + index];
            frame[index] = row.FrameMs;
            producer[index] = row.CpuProducerMs;
            pipelineRecord[index] = row.CpuPipelineRecordMs;
            commandRecord[index] = row.CommandRecordCpuMs;
            submission[index] = row.SubmissionCpuMs;
            if (row.NativeTimestampStatus == "ready")
            {
                gpu[gpuCount++] = row.GpuRegionElapsedMs;
            }
            producerMaterializedWriteBytes +=
                row.ProducerMaterializedWriteBytes;
            pipelineElementMaterializedWriteBytes +=
                row.PipelineElementMaterializedWriteBytes;
            spatialBuildAddressedReadBytes +=
                row.SpatialBuildAddressedReadBytes;
            binCountAtomicOperations += row.BinCountAtomicOperations;
            scatterAtomicOperations += row.ScatterAtomicOperations;
            measurementReadback += row.MeasurementReadbackBytes;
            instrumentationReadback +=
                row.TimestampInstrumentationReadbackBytes;
            commandSlotWaitFrames += row.CommandSlotWaitFrames;
        }
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
            CaseId = adapter.CaseId(entry.Variant),
            Variant = adapter.VariantName(entry.Variant),
            Marker = adapter.Marker(entry.Variant),
            Samples = count,
            GpuValidSamples = gpuCount,
            StateCount = GpuSensorDataPackingBenchmarkAdapter.StateCount,
            Gpu = MetricStats.Compute(gpu, gpuCount),
            Frame = MetricStats.Compute(frame, count),
            CpuProducer = MetricStats.Compute(producer, count),
            CpuPipelineRecord = MetricStats.Compute(pipelineRecord, count),
            CommandRecord = MetricStats.Compute(commandRecord, count),
            Submission = MetricStats.Compute(submission, count),
            ProducerMaterializedWriteBytes =
                producerMaterializedWriteBytes,
            PipelineElementMaterializedWriteBytes =
                pipelineElementMaterializedWriteBytes,
            SpatialBuildAddressedReadBytes =
                spatialBuildAddressedReadBytes,
            BinCountAtomicOperations =
                binCountAtomicOperations,
            ScatterAtomicOperations =
                scatterAtomicOperations,
            CommandSlotWaitFrames = commandSlotWaitFrames,
            FenceSupported = SystemInfo.supportsGraphicsFence,
            FencePassed = fencePassed,
            MeasurementReadbackBytes = measurementReadback,
            TimestampInstrumentationReadbackBytes = instrumentationReadback
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
            if (rawSamples[i].NativeTimestampStatus != "ready")
            {
                return false;
            }
        }
        return true;
    }

    private void WriteAllOutputs(bool passed, string status)
    {
        WriteConfiguration();
        WriteDeviceMetadata();
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
        IReadOnlyList<GpuSensorDataPackingScheduleEntry> schedule =
            GpuSensorDataPackingBenchmarkSchedule.Build(superRounds);
        string[] order = new string[schedule.Count];
        for (int i = 0; i < schedule.Count; i++)
        {
            order[i] =
                schedule[i].BlockType + ":" +
                adapter.VariantName(schedule[i].Variant);
        }
        BenchmarkConfiguration config = new BenchmarkConfiguration
        {
            schemaVersion = 12,
            suite = "summit.gpu-sensor-data-packing",
            processId = processId,
            unityVersion = Application.unityVersion,
            startedUtc = DateTime.UtcNow.ToString("O"),
            scenarioId = scenarioId,
            superRounds = superRounds,
            warmupFrames = warmupFrames,
            sampleFrames = sampleFrames,
            cooldownFrames = cooldownFrames,
            elementCount = elementCount,
            binCount = binCount,
            queryCount = queryCount,
            logicalStateCount =
                GpuSensorDataPackingBenchmarkAdapter.StateCount,
            seed = seed,
            commandSlotCount = commandSlotCount,
            scanBackend = "wave-ops",
            profilerMarkers = adapter.EmitsProfilerMarkers,
            baselineCaseId =
                GpuSensorDataPackingBenchmarkAdapter.BaselineCaseId,
            packedCaseId =
                GpuSensorDataPackingBenchmarkAdapter.PackedCaseId,
            controlCaseId =
                GpuSensorDataPackingBenchmarkAdapter.ControlCaseId,
            schedule = order,
            preconditioningBlockCount = 2,
            preconditioningSampleFramesPerBlock =
                GpuSensorDataPackingBenchmarkSchedule
                    .PreconditionSampleCount,
            preconditioningExcludedFromScoring = true,
            preconditioningGpuTimingsRecorded = true,
            scheduleContract =
                "control-pre;unscored-precondition-A-B;" +
                superRounds +
                " balanced scored super-rounds;ABBA/BAAB;control-post",
            caseLocalWarmup = true,
            sameProcessPaired = true,
            informationalPlayerLogsSuppressed = true,
            mainGraphicsQueueTimestamp = true,
            hostUploadEliminationClaim = false,
            uploadQueueCoverageVerified = false,
            asyncComputeClaim = false,
            copyQueueClaim = false,
            pcieTrafficClaim = false,
            measuredDramTrafficClaim = false,
            driverReportedVramClaim = false,
            endToEndSensorLatencyClaim = false,
            liveSensorInputClaim = false,
            sensorFidelityClaim = false,
            citySceneClaim = false,
            fpsClaim = false,
            nvidiaValidationClaim = false,
            gpuWorkloadMetricCoverage =
                "Main graphics queue timestamp around producer, spatial " +
                "build, range query, and digest reduction.",
            performanceAttribution =
                "combined-packed-soa-q16-producer-count-offset-cursor-" +
                "fusion-lazy-payload",
            digestComparisonCoverage =
                "64 aggregate frame digests; no full-buffer readback",
            csrValidationCoverage =
                "in-place end-offset/count/membership plus " +
                "count/xor/sum/mixed-sum invariants",
            gpuResidentAccountingCoverage =
                "pipeline-owned GraphicsBuffers plus block digests; " +
                "excludes timestamp/command/driver allocations",
            cpuProducerStagePresent = false,
            baselineFinalStateKeyValidation = true,
            packedSampleValidation = true,
            packedCsrValidation = true,
            allStateDigestComparison = true,
            allStateDigestCount =
                GpuSensorDataPackingBenchmarkAdapter.StateCount,
            expectedValidationRows = superRounds * 2 + 2,
            coordinateLogicalBits = 16,
            baselineCoordinateStorageBits = 32,
            packedCoordinateStorageBits = 16,
            payloadSourceBits = 32,
            payloadQuantizedBits = 16,
            baselinePayloadStorageBits = 32,
            packedPayloadStorageBits = 16,
            payloadQuantization =
                "UNORM32_TO_UNORM16_RNE_DIV65537",
            payloadRawMaxErrorBound =
                (int)adapter.IntensityQuantizationMaxRawError,
            payloadNormalizedMaxErrorBound =
                adapter.IntensityQuantizationMaxNormalizedError,
            baselineMaterializedKeys = true,
            packedMaterializedKeys = false,
            baselineMaterializedStableIds = true,
            packedMaterializedStableIds = false,
            packedPairwiseProducer = true,
            packedCountFusedIntoProducer = true,
            packedPairwiseScatter = true,
            packedQueryDecodesInConsumer = true,
            packedQueryLoadsIntensityOnlyForAccepted = true,
            packedDecodedAosBufferBytes = 0,
            baselineProducerLogicalWriteBytesPerFrame =
                adapter.BaselineProducerLogicalWriteBytesPerFrame,
            packedProducerLogicalWriteBytesPerFrame =
                adapter.PackedProducerLogicalWriteBytesPerFrame,
            baselinePipelineElementMaterializedWriteBytesPerFrame =
                adapter.BaselinePipelineElementMaterializedWriteBytesPerFrame,
            packedPipelineElementMaterializedWriteBytesPerFrame =
                adapter.PackedPipelineElementMaterializedWriteBytesPerFrame,
            baselineSpatialBuildAddressedReadBytesPerFrame =
                adapter.BaselineSpatialBuildAddressedReadBytesPerFrame,
            packedSpatialBuildAddressedReadBytesPerFrame =
                adapter.PackedSpatialBuildAddressedReadBytesPerFrame,
            baselineCountAtomicOperationsPerFrame =
                adapter.BaselineCountAtomicOperationsPerFrame,
            packedCountAtomicOperationsPerFrame =
                adapter.PackedFusedCountAtomicOperationsPerFrame,
            baselineScatterReservationAtomicOperationsPerFrame =
                adapter.BaselineScatterReservationAtomicOperationsPerFrame,
            packedScatterReservationAtomicOperationsPerFrame =
                adapter.PackedScatterReservationAtomicOperationsPerFrame,
            baselineTotalAtomicOperationsPerFrame =
                adapter.BaselineTotalAtomicOperationsPerFrame,
            packedTotalAtomicOperationsPerFrame =
                adapter.PackedTotalAtomicOperationsPerFrame,
            baselinePipelineResidentBytes =
                adapter.BaselinePipelineResidentBytes,
            packedPipelineResidentBytes =
                adapter.PackedPipelineResidentBytes,
            baselineIsolatedGpuResidentBytes =
                adapter.BaselineIsolatedGpuResidentBytes,
            packedIsolatedGpuResidentBytes =
                adapter.PackedIsolatedGpuResidentBytes,
            blockDigestBufferBytes = adapter.BlockDigestBufferBytes,
            actualBenchmarkGpuResidentBytes =
                adapter.ActualBenchmarkGpuResidentBytes,
            measurementReadbackBytesPerFrame =
                GpuSensorDataPackingBenchmarkAdapter
                    .WorkloadReadbackBytesPerFrame,
            validationReadbackBytesPerComparison =
                GpuSensorDataPackingBenchmarkAdapter
                    .ValidationReadbackBytesPerComparison,
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
            nativeTimestampWarmupElapsedMs = timestampWarmupElapsedMs,
            nativeTimestampWarmupFrequency = timestampWarmupFrequency,
            nativeTimestampWarmupFenceValue = timestampWarmupFence,
            nativeTimestampWarmupDeviceGeneration =
                timestampWarmupGeneration,
            buildCommit = buildCommit,
            runtimeShaderSha256 = runtimeShaderSha256,
            runtimeApiSha256 = runtimeApiSha256,
            nativeTimestampDllSha256 = nativeTimestampDllSha256,
            commandLine = SanitizeCommandLine(originalArguments)
        };
        File.WriteAllText(
            Path.Combine(reportDirectory, "config.json"),
            JsonUtility.ToJson(config, true),
            new UTF8Encoding(false));
    }

    private void WriteDeviceMetadata()
    {
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            return;
        }
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
                timestampSupport.CapabilityFlags,
            uploadQueueCoverageVerified = false,
            asyncComputeClaim = false,
            copyQueueClaim = false,
            hostUploadEliminationClaim = false,
            pcieTrafficClaim = false,
            measuredDramTrafficClaim = false,
            driverReportedVramClaim = false,
            endToEndSensorLatencyClaim = false,
            liveSensorInputClaim = false,
            sensorFidelityClaim = false,
            citySceneClaim = false,
            fpsClaim = false,
            nvidiaValidationClaim = false,
            mainGraphicsQueueTimestamp = true
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
                "processId,scenarioId,superRound,sequencePosition,pairIndex," +
                "pairOrder,withinPairPosition,blockIndex,blockType,caseId," +
                "variant,marker,sampleIndex,logicalState,commandSlot," +
                "commandSlotWaitFrames,sourceUnityFrame,resultUnityFrame," +
                "elapsedSeconds,cpuProducerMs,cpuPipelineRecordMs," +
                "cpuCommandRecordMs,submissionCpuMs," +
                "producerMaterializedWriteBytes," +
                "pipelineElementMaterializedWriteBytes," +
                "spatialBuildAddressedReadBytes,binCountAtomicOperations," +
                "scatterAtomicOperations,nativeTimestampToken," +
                "nativeTimestampUserTag,nativeTimestampFlags," +
                "nativeTimestampStatus,nativeTimestampBeginTicks," +
                "nativeTimestampEndTicks,nativeTimestampElapsedTicks," +
                "nativeTimestampFrequency,nativeTimestampElapsedNanoseconds," +
                "nativeTimestampFenceValue,nativeTimestampDeviceGeneration," +
                "gpuRegionElapsedMs,frameMs,measurementReadbackBytes," +
                "timestampInstrumentationReadbackBytes");
            foreach (RawSample row in rawSamples)
            {
                writer.WriteLine(string.Join(",",
                    row.ProcessId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ScenarioId),
                    row.SuperRound.ToString(CultureInfo.InvariantCulture),
                    row.SequencePosition.ToString(CultureInfo.InvariantCulture),
                    row.PairIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.PairOrder),
                    row.WithinPairPosition.ToString(
                        CultureInfo.InvariantCulture),
                    row.BlockIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.BlockType),
                    Csv(row.CaseId),
                    Csv(row.Variant),
                    Csv(row.Marker),
                    row.SampleIndex.ToString(CultureInfo.InvariantCulture),
                    row.LogicalState.ToString(CultureInfo.InvariantCulture),
                    row.CommandSlot.ToString(CultureInfo.InvariantCulture),
                    row.CommandSlotWaitFrames.ToString(
                        CultureInfo.InvariantCulture),
                    row.SourceUnityFrame.ToString(
                        CultureInfo.InvariantCulture),
                    row.ResultUnityFrame.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.ElapsedSeconds),
                    Number(row.CpuProducerMs),
                    Number(row.CpuPipelineRecordMs),
                    Number(row.CommandRecordCpuMs),
                    Number(row.SubmissionCpuMs),
                    row.ProducerMaterializedWriteBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.PipelineElementMaterializedWriteBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.SpatialBuildAddressedReadBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.BinCountAtomicOperations.ToString(
                        CultureInfo.InvariantCulture),
                    row.ScatterAtomicOperations.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampToken.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampUserTag.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampFlags.ToString(
                        CultureInfo.InvariantCulture),
                    Csv(row.NativeTimestampStatus),
                    row.NativeTimestampBeginTicks.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampEndTicks.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampFrequency.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedNanoseconds.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampFenceValue.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampDeviceGeneration.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.GpuRegionElapsedMs),
                    Number(row.FrameMs),
                    row.MeasurementReadbackBytes.ToString(
                        CultureInfo.InvariantCulture),
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
                "processId,scenarioId,superRound,sequencePosition,pairIndex," +
                "pairOrder,withinPairPosition,blockIndex,blockType,caseId," +
                "variant,marker,samples,gpuRegionValidSamples,stateCount," +
                "producerMaterializedWriteBytes," +
                "pipelineElementMaterializedWriteBytes," +
                "spatialBuildAddressedReadBytes,binCountAtomicOperations," +
                "scatterAtomicOperations,commandSlotWaitFrames," +
                "gpuRegionAverageMs,gpuRegionP50Ms," +
                "gpuRegionP95Ms,gpuRegionP99Ms,frameAverageMs,frameP99Ms," +
                "cpuProducerAverageMs,cpuProducerP99Ms," +
                "cpuPipelineRecordAverageMs,cpuPipelineRecordP99Ms," +
                "cpuCommandRecordAverageMs,cpuCommandRecordP99Ms," +
                "submissionAverageMs,submissionP99Ms,fenceSupported," +
                "fencePassed,measurementReadbackBytes," +
                "timestampInstrumentationReadbackBytes");
            foreach (BlockSummary row in blockSummaries)
            {
                writer.WriteLine(string.Join(",",
                    row.ProcessId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ScenarioId),
                    row.SuperRound.ToString(CultureInfo.InvariantCulture),
                    row.SequencePosition.ToString(CultureInfo.InvariantCulture),
                    row.PairIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.PairOrder),
                    row.WithinPairPosition.ToString(
                        CultureInfo.InvariantCulture),
                    row.BlockIndex.ToString(CultureInfo.InvariantCulture),
                    Csv(row.BlockType),
                    Csv(row.CaseId),
                    Csv(row.Variant),
                    Csv(row.Marker),
                    row.Samples.ToString(CultureInfo.InvariantCulture),
                    row.GpuValidSamples.ToString(
                        CultureInfo.InvariantCulture),
                    row.StateCount.ToString(CultureInfo.InvariantCulture),
                    row.ProducerMaterializedWriteBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.PipelineElementMaterializedWriteBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.SpatialBuildAddressedReadBytes.ToString(
                        CultureInfo.InvariantCulture),
                    row.BinCountAtomicOperations.ToString(
                        CultureInfo.InvariantCulture),
                    row.ScatterAtomicOperations.ToString(
                        CultureInfo.InvariantCulture),
                    row.CommandSlotWaitFrames.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.Gpu.Average),
                    Number(row.Gpu.P50),
                    Number(row.Gpu.P95),
                    Number(row.Gpu.P99),
                    Number(row.Frame.Average),
                    Number(row.Frame.P99),
                    Number(row.CpuProducer.Average),
                    Number(row.CpuProducer.P99),
                    Number(row.CpuPipelineRecord.Average),
                    Number(row.CpuPipelineRecord.P99),
                    Number(row.CommandRecord.Average),
                    Number(row.CommandRecord.P99),
                    Number(row.Submission.Average),
                    Number(row.Submission.P99),
                    row.FenceSupported ? "1" : "0",
                    row.FencePassed ? "1" : "0",
                    row.MeasurementReadbackBytes.ToString(
                        CultureInfo.InvariantCulture),
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
                "phase,passed,message,readbackBytes,resultHash,stateCount," +
                "digestMismatchCount,digestDeltaXor,digestDeltaSum0," +
                "digestDeltaSum1,baselineInvalidKeyCount," +
                "baselineInvalidKeyHash,packedSampleMismatchCount," +
                "packedSampleMismatchHash,packedCsrMismatchCount," +
                "packedCsrMismatchHash,packedCsrElementCount," +
                "packedCsrIdXor,packedCsrIdSum,packedCsrIdMixedSum," +
                "expectedPackedCsrElementCount,expectedPackedCsrIdXor," +
                "expectedPackedCsrIdSum,expectedPackedCsrIdMixedSum");
            foreach (GpuSensorDataPackingValidationResult row in validationResults)
            {
                writer.WriteLine(string.Join(",",
                    Csv(row.Phase),
                    row.Passed ? "1" : "0",
                    Csv(row.Message),
                    row.ReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ResultHash),
                    row.StateCount.ToString(CultureInfo.InvariantCulture),
                    row.DigestMismatchCount.ToString(
                        CultureInfo.InvariantCulture),
                    row.DigestDeltaXor.ToString(
                        CultureInfo.InvariantCulture),
                    row.DigestDeltaSum0.ToString(
                        CultureInfo.InvariantCulture),
                    row.DigestDeltaSum1.ToString(
                        CultureInfo.InvariantCulture),
                    row.BaselineInvalidKeyCount.ToString(
                        CultureInfo.InvariantCulture),
                    row.BaselineInvalidKeyHash.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedSampleMismatchCount.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedSampleMismatchHash.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedCsrMismatchCount.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedCsrMismatchHash.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedCsrElementCount.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedCsrIdXor.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedCsrIdSum.ToString(
                        CultureInfo.InvariantCulture),
                    row.PackedCsrIdMixedSum.ToString(
                        CultureInfo.InvariantCulture),
                    row.ExpectedPackedCsrElementCount.ToString(
                        CultureInfo.InvariantCulture),
                    row.ExpectedPackedCsrIdXor.ToString(
                        CultureInfo.InvariantCulture),
                    row.ExpectedPackedCsrIdSum.ToString(
                        CultureInfo.InvariantCulture),
                    row.ExpectedPackedCsrIdMixedSum.ToString(
                        CultureInfo.InvariantCulture)));
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
        long producerMaterializedWriteBytes = 0;
        long pipelineElementMaterializedWriteBytes = 0;
        long spatialBuildAddressedReadBytes = 0;
        long binCountAtomicOperations = 0;
        long scatterAtomicOperations = 0;
        int readyRows = 0;
        foreach (RawSample row in rawSamples)
        {
            measurementReadback += row.MeasurementReadbackBytes;
            instrumentationReadback +=
                row.TimestampInstrumentationReadbackBytes;
            producerMaterializedWriteBytes +=
                row.ProducerMaterializedWriteBytes;
            pipelineElementMaterializedWriteBytes +=
                row.PipelineElementMaterializedWriteBytes;
            spatialBuildAddressedReadBytes +=
                row.SpatialBuildAddressedReadBytes;
            binCountAtomicOperations += row.BinCountAtomicOperations;
            scatterAtomicOperations += row.ScatterAtomicOperations;
            if (row.NativeTimestampStatus == "ready")
            {
                readyRows++;
            }
        }
        int validationFailures = 0;
        long validationReadback = 0;
        foreach (GpuSensorDataPackingValidationResult result in validationResults)
        {
            validationReadback += result.ReadbackBytes;
            if (!result.Passed)
            {
                validationFailures++;
            }
        }
        string[] lines =
        {
            "GPU sensor data packing and fusion benchmark",
            "schemaVersion=12",
            "suite=summit.gpu-sensor-data-packing",
            "passed=" + (passed ? "1" : "0"),
            "status=" + status,
            "processId=" + processId.ToString(CultureInfo.InvariantCulture),
            "scenarioId=" + scenarioId,
            "rawSampleCount=" +
                rawSamples.Count.ToString(CultureInfo.InvariantCulture),
            "blockCount=" +
                blockSummaries.Count.ToString(CultureInfo.InvariantCulture),
            "validationRows=" +
                validationResults.Count.ToString(
                    CultureInfo.InvariantCulture),
            "expectedValidationRows=" +
                (superRounds * 2 + 2).ToString(
                    CultureInfo.InvariantCulture),
            "validationFailures=" +
                validationFailures.ToString(CultureInfo.InvariantCulture),
            "validationReadbackBytes=" +
                validationReadback.ToString(CultureInfo.InvariantCulture),
            "measurementReadbackBytes=" +
                measurementReadback.ToString(CultureInfo.InvariantCulture),
            "timestampInstrumentationReadbackBytes=" +
                instrumentationReadback.ToString(
                    CultureInfo.InvariantCulture),
            "producerMaterializedWriteBytes=" +
                producerMaterializedWriteBytes.ToString(
                    CultureInfo.InvariantCulture),
            "pipelineElementMaterializedWriteBytes=" +
                pipelineElementMaterializedWriteBytes.ToString(
                    CultureInfo.InvariantCulture),
            "spatialBuildAddressedReadBytes=" +
                spatialBuildAddressedReadBytes.ToString(
                    CultureInfo.InvariantCulture),
            "binCountAtomicOperations=" +
                binCountAtomicOperations.ToString(
                    CultureInfo.InvariantCulture),
            "scatterAtomicOperations=" +
                scatterAtomicOperations.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampReadyRows=" +
                readyRows.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampWarmupPassed=" +
                (timestampWarmupPassed ? "1" : "0"),
            "nativeTimestampWarmupStatus=" + timestampWarmupStatus,
            "nativeTimestampAbiVersion=" +
                timestampSupport.AbiVersion.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampCapabilityFlags=" +
                timestampSupport.CapabilityFlags.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampAcquireFailures=" +
                timestampAcquireFailures.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampResultFailures=" +
                timestampResultFailures.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampTimeouts=" +
                timestampTimeouts.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampPendingRows=" +
                pendingTimestamps.Count.ToString(
                    CultureInfo.InvariantCulture),
            "gpuRegionTimingComplete=" +
                (IsTimestampEvidenceComplete() ? "1" : "0"),
            "logicalStateCount=64",
            "baselineFinalStateKeyValidation=1",
            "packedSampleValidation=1",
            "packedCsrValidation=1",
            "allStateDigestComparison=1",
            "profilerMarkers=" +
                (adapter != null && adapter.EmitsProfilerMarkers ? "1" : "0"),
            "performanceAttribution=" +
                "combined-packed-soa-q16-producer-count-offset-cursor-" +
                "fusion-lazy-payload",
            "digestComparisonCoverage=" +
                "64 aggregate frame digests; no full-buffer readback",
            "csrValidationCoverage=in-place end-offset/count/membership plus " +
                "count/xor/sum/mixed-sum invariants",
            "gpuResidentAccountingCoverage=pipeline-owned GraphicsBuffers " +
                "plus block digests; excludes timestamp/command/driver allocations",
            "hostUploadEliminationClaim=0",
            "uploadQueueCoverageVerified=0",
            "asyncComputeClaim=0",
            "copyQueueClaim=0",
            "pcieTrafficClaim=0",
            "measuredDramTrafficClaim=0",
            "driverReportedVramClaim=0",
            "endToEndSensorLatencyClaim=0",
            "liveSensorInputClaim=0",
            "sensorFidelityClaim=0",
            "citySceneClaim=0",
            "fpsClaim=0",
            "nvidiaValidationClaim=0",
            "mainGraphicsQueueTimestamp=1"
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
            "GPU sensor-pipeline benchmark " + status +
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
        timestampBackend?.Dispose();
        timestampBackend = null;
        adapter?.Dispose();
        adapter = null;
    }

    private static string StatusName(GpuTimestampStatus status)
    {
        return status.ToString().Replace('_', '-').ToLowerInvariant();
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
        return args != null && HasArgument(args, EnableArgument);
    }

    private static bool HasArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(
                args[i],
                name,
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
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(
                args[i],
                name,
                StringComparison.OrdinalIgnoreCase))
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
        return arguments == null
            ? string.Empty
            : string.Join(" ", arguments);
    }

    private sealed class PendingTimestamp
    {
        public GpuTimestampToken Token;
        public RawSample Row;
        public GpuTimestampSampleFlags ExpectedFlags;
    }

    private sealed class RawSample
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
        public int LogicalState;
        public int CommandSlot;
        public int CommandSlotWaitFrames;
        public int SourceUnityFrame;
        public int ResultUnityFrame;
        public double ElapsedSeconds;
        public double CpuProducerMs;
        public double CpuPipelineRecordMs;
        public double CommandRecordCpuMs;
        public double SubmissionCpuMs;
        public long ProducerMaterializedWriteBytes;
        public long PipelineElementMaterializedWriteBytes;
        public long SpatialBuildAddressedReadBytes;
        public long BinCountAtomicOperations;
        public long ScatterAtomicOperations;
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

        private static double Percentile(
            double[] sorted,
            double percentile)
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
        public int StateCount;
        public long ProducerMaterializedWriteBytes;
        public long PipelineElementMaterializedWriteBytes;
        public long SpatialBuildAddressedReadBytes;
        public long BinCountAtomicOperations;
        public long ScatterAtomicOperations;
        public int CommandSlotWaitFrames;
        public MetricStats Gpu;
        public MetricStats Frame;
        public MetricStats CpuProducer;
        public MetricStats CpuPipelineRecord;
        public MetricStats CommandRecord;
        public MetricStats Submission;
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
        public int superRounds;
        public int warmupFrames;
        public int sampleFrames;
        public int cooldownFrames;
        public int elementCount;
        public int binCount;
        public int queryCount;
        public int logicalStateCount;
        public int seed;
        public int commandSlotCount;
        public string scanBackend;
        public bool profilerMarkers;
        public string baselineCaseId;
        public string packedCaseId;
        public string controlCaseId;
        public string[] schedule;
        public string scheduleContract;
        public int preconditioningBlockCount;
        public int preconditioningSampleFramesPerBlock;
        public bool preconditioningExcludedFromScoring;
        public bool preconditioningGpuTimingsRecorded;
        public bool caseLocalWarmup;
        public bool sameProcessPaired;
        public bool informationalPlayerLogsSuppressed;
        public bool mainGraphicsQueueTimestamp;
        public bool hostUploadEliminationClaim;
        public bool uploadQueueCoverageVerified;
        public bool asyncComputeClaim;
        public bool copyQueueClaim;
        public bool pcieTrafficClaim;
        public bool measuredDramTrafficClaim;
        public bool driverReportedVramClaim;
        public bool endToEndSensorLatencyClaim;
        public bool liveSensorInputClaim;
        public bool sensorFidelityClaim;
        public bool citySceneClaim;
        public bool fpsClaim;
        public bool nvidiaValidationClaim;
        public string gpuWorkloadMetricCoverage;
        public string performanceAttribution;
        public string digestComparisonCoverage;
        public string csrValidationCoverage;
        public string gpuResidentAccountingCoverage;
        public bool cpuProducerStagePresent;
        public bool baselineFinalStateKeyValidation;
        public bool packedSampleValidation;
        public bool packedCsrValidation;
        public bool allStateDigestComparison;
        public int allStateDigestCount;
        public int expectedValidationRows;
        public int coordinateLogicalBits;
        public int baselineCoordinateStorageBits;
        public int packedCoordinateStorageBits;
        public int payloadSourceBits;
        public int payloadQuantizedBits;
        public int baselinePayloadStorageBits;
        public int packedPayloadStorageBits;
        public string payloadQuantization;
        public int payloadRawMaxErrorBound;
        public double payloadNormalizedMaxErrorBound;
        public bool baselineMaterializedKeys;
        public bool packedMaterializedKeys;
        public bool baselineMaterializedStableIds;
        public bool packedMaterializedStableIds;
        public bool packedPairwiseProducer;
        public bool packedCountFusedIntoProducer;
        public bool packedPairwiseScatter;
        public bool packedQueryDecodesInConsumer;
        public bool packedQueryLoadsIntensityOnlyForAccepted;
        public long packedDecodedAosBufferBytes;
        public long baselineProducerLogicalWriteBytesPerFrame;
        public long packedProducerLogicalWriteBytesPerFrame;
        public long baselinePipelineElementMaterializedWriteBytesPerFrame;
        public long packedPipelineElementMaterializedWriteBytesPerFrame;
        public long baselineSpatialBuildAddressedReadBytesPerFrame;
        public long packedSpatialBuildAddressedReadBytesPerFrame;
        public long baselineCountAtomicOperationsPerFrame;
        public long packedCountAtomicOperationsPerFrame;
        public long baselineScatterReservationAtomicOperationsPerFrame;
        public long packedScatterReservationAtomicOperationsPerFrame;
        public long baselineTotalAtomicOperationsPerFrame;
        public long packedTotalAtomicOperationsPerFrame;
        public long baselinePipelineResidentBytes;
        public long packedPipelineResidentBytes;
        public long baselineIsolatedGpuResidentBytes;
        public long packedIsolatedGpuResidentBytes;
        public long blockDigestBufferBytes;
        public long actualBenchmarkGpuResidentBytes;
        public long measurementReadbackBytesPerFrame;
        public long validationReadbackBytesPerComparison;
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
        public string runtimeShaderSha256;
        public string runtimeApiSha256;
        public string nativeTimestampDllSha256;
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
        public bool uploadQueueCoverageVerified;
        public bool asyncComputeClaim;
        public bool copyQueueClaim;
        public bool hostUploadEliminationClaim;
        public bool pcieTrafficClaim;
        public bool measuredDramTrafficClaim;
        public bool driverReportedVramClaim;
        public bool endToEndSensorLatencyClaim;
        public bool liveSensorInputClaim;
        public bool sensorFidelityClaim;
        public bool citySceneClaim;
        public bool fpsClaim;
        public bool nvidiaValidationClaim;
        public bool mainGraphicsQueueTimestamp;
    }
}
