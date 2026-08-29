using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Summit.GpuDrivenInstances;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[DefaultExecutionOrder(10000)]
public sealed class GpuDrivenInstanceUploadBenchmarkController : MonoBehaviour
{
    private const string EnableArgument =
        "-gpu-driven-instance-upload-benchmark";
    private const string SuiteId = "summit.gpu-driven-instance-upload";
    private const string ScheduleContract = "ABBA;BAAB";
    private const int FrameTimingResultLatencyFrames = 4;
    private const int MeasurementBlockCount = 8;
    private const float FenceTimeoutSeconds = 120f;
    private static readonly WaitForEndOfFrame EndOfFrame =
        new WaitForEndOfFrame();
    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private GpuDrivenInstanceUploadBenchmarkAdapter adapter;
    private GpuDrivenInstanceFrameTimingCollector frameTimingCollector;
    private BlockPlan[] blockPlans;
    private RawSample[] rawSamples;
    private BlockSummary[] blockSummaries;
    private ValidationResult[] validationResults;
    private int rawSampleCount;
    private int blockSummaryCount;
    private int validationResultCount;
    private bool finished;
    private bool allCompletionFencesPassed = true;
    private int processId;
    private double benchmarkStart;
    private string benchmarkStartedUtc;

    private string reportDirectory;
    private string scenarioId = string.Empty;
    private int instanceCount = 100000;
    private int movingPercent = 10;
    private int seed = 20260830;
    private int warmupFrames = 30;
    private int sampleFrames = 240;
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
            "GPU Driven Instance Upload Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuDrivenInstanceUploadBenchmarkController controller =
            host.AddComponent<GpuDrivenInstanceUploadBenchmarkController>();
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
            "-gpu-driven-instance-upload-output",
            ReadString(
                args,
                "-gpu-driven-instance-upload-report-dir",
                string.Empty));
        scenarioId = ReadString(
            args,
            "-gpu-driven-instance-upload-scenario-id",
            scenarioId);
        instanceCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-upload-instance-count",
                instanceCount),
            1,
            16776960);
        movingPercent = ReadInt(
            args,
            "-gpu-driven-instance-upload-moving-percent",
            movingPercent);
        seed = ReadInt(
            args,
            "-gpu-driven-instance-upload-seed",
            seed);
        warmupFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-upload-warmup",
                ReadInt(
                    args,
                    "-gpu-driven-instance-upload-warmup-frames",
                    warmupFrames)),
            0,
            1800);
        sampleFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-upload-sample",
                ReadInt(
                    args,
                    "-gpu-driven-instance-upload-sample-frames",
                    sampleFrames)),
            FrameTimingResultLatencyFrames,
            7200);
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            scenarioId = "upload-n" +
                instanceCount.ToString(Invariant) + "-moving" +
                movingPercent.ToString(Invariant) + "-seed" +
                seed.ToString(Invariant);
        }
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
                WriteAvailableEvidence();
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
        benchmarkStartedUtc = DateTime.UtcNow.ToString("O", Invariant);

        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            Finish(false, "missing-output-directory");
            yield break;
        }
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);

        if (!IsSupportedMovingPercent(movingPercent))
        {
            WriteRunSummary(false, "unsupported-moving-percent");
            Finish(false, "unsupported-moving-percent");
            yield break;
        }
        if (!SystemInfo.supportsGraphicsFence)
        {
            WriteRunSummary(false, "graphics-fence-unavailable");
            Finish(false, "graphics-fence-unavailable");
            yield break;
        }

        try
        {
            adapter = new GpuDrivenInstanceUploadBenchmarkAdapter(
                instanceCount,
                1,
                "visible25",
                seed,
                1,
                GpuDrivenInstanceUploadBenchmarkAdapter
                    .DefaultStagingSlotCount,
                0);
            frameTimingCollector =
                new GpuDrivenInstanceFrameTimingCollector();
            blockPlans = BuildBlockPlans();
            rawSamples = new RawSample[
                checked(MeasurementBlockCount * sampleFrames)];
            blockSummaries = new BlockSummary[MeasurementBlockCount];
            validationResults = new ValidationResult[
                MeasurementBlockCount];
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

        for (int blockOffset = 0;
             blockOffset < blockPlans.Length;
             blockOffset++)
        {
            BlockPlan plan = blockPlans[blockOffset];
            yield return ResetGpuStateToBase();

            for (int frame = 0; frame < warmupFrames; frame++)
            {
                yield return null;
                uint ordinal = checked((uint)frame + 1u);
                yield return SubmitUnmeasured(
                    plan.Variant,
                    movingPercent,
                    ordinal,
                    false,
                    null);
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
                int slotIndex = -1;
                int slotWaitFrames = 0;
                while (!adapter.TryAcquireWorkSlot(
                    out slotIndex,
                    out slotWaitFrames))
                {
                    yield return null;
                }

                uint ordinal = checked(
                    (uint)warmupFrames + (uint)sampleIndex);
                int sourceFrame = Time.frameCount;
                TimedSubmission submission = SubmitMeasured(
                    slotIndex,
                    slotWaitFrames,
                    plan.Variant,
                    ordinal);
                RawSample row = CreateRawSample(
                    plan,
                    sampleIndex,
                    ordinal,
                    sourceFrame,
                    submission);
                int rowIndex = rawSampleCount;
                rawSamples[rowIndex] = row;
                rawSampleCount++;

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
            }

            ulong finalExpectedHash = 0UL;
            uint finalLogicalOrdinal = 0u;
            for (int drain = 0;
                 drain < FrameTimingResultLatencyFrames;
                 drain++)
            {
                yield return null;
                finalLogicalOrdinal = checked(
                    (uint)warmupFrames +
                    (uint)sampleFrames +
                    (uint)drain + 1u);
                yield return SubmitUnmeasured(
                    plan.Variant,
                    movingPercent,
                    finalLogicalOrdinal,
                    true,
                    value => finalExpectedHash = value);
                frameTimingCollector.CaptureFrameTimings();
                yield return EndOfFrame;
                GpuDrivenInstanceFrameTimingSample timing =
                    frameTimingCollector.CollectLatest();
                ApplyFrameTiming(
                    sampleStart + sampleFrames -
                        FrameTimingResultLatencyFrames + drain,
                    timing,
                    Time.frameCount);
            }

            bool fencesPassed = false;
            yield return WaitForAllCompletionFences(
                value => fencesPassed = value);
            allCompletionFencesPassed &= fencesPassed;
            ValidationResult? validation = null;
            yield return ValidateGpuState(
                plan,
                finalLogicalOrdinal,
                finalExpectedHash,
                fencesPassed,
                value => validation = value);
            if (!validation.HasValue)
            {
                throw new InvalidOperationException(
                    "GPU state validation did not return a result.");
            }
            ValidationResult completedValidation = validation.Value;
            validationResults[validationResultCount++] =
                completedValidation;
            blockSummaries[blockSummaryCount++] = SummarizeBlock(
                plan,
                sampleStart,
                sampleFrames,
                completedValidation);
        }

        bool passed =
            rawSampleCount == checked(
                MeasurementBlockCount * sampleFrames) &&
            AllValidationsPassed() &&
            allCompletionFencesPassed &&
            HasNoTimedAllocations();
        string status = passed ? "passed" : "evidence-gate-failed";
        WriteAvailableEvidence();
        WriteRunSummary(passed, status);
        Finish(passed, status);
    }

    private IEnumerator ResetGpuStateToBase()
    {
        bool drained = false;
        yield return WaitForAllCompletionFences(value => drained = value);
        if (!drained)
        {
            throw new TimeoutException(
                "Timed out draining staging slots before block reset.");
        }

        yield return null;
        yield return SubmitUnmeasured(
            GpuDrivenInstanceUploadBenchmarkVariant.FullUpload,
            0,
            0u,
            false,
            null);
        drained = false;
        yield return WaitForAllCompletionFences(value => drained = value);
        if (!drained)
        {
            throw new TimeoutException(
                "Timed out waiting for the out-of-window base reset.");
        }
    }

    private TimedSubmission SubmitMeasured(
        int slotIndex,
        int slotWaitFrames,
        GpuDrivenInstanceUploadBenchmarkVariant variant,
        uint logicalOrdinal)
    {
        bool submitted = false;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = Stopwatch.GetTimestamp();
        try
        {
            long updateStart = Stopwatch.GetTimestamp();
            GpuDrivenInstanceUploadPreparationReceipt preparation =
                adapter.PrepareSlot(
                    slotIndex,
                    variant,
                    movingPercent,
                    seed,
                    logicalOrdinal);
            long updateEnd = Stopwatch.GetTimestamp();

            long recordStart = Stopwatch.GetTimestamp();
            GpuDrivenInstanceUploadRecordReceipt recorded =
                adapter.RecordWorkload(slotIndex, variant);
            GraphicsFence fence = adapter.AppendLifetimeFence(slotIndex);
            long recordEnd = Stopwatch.GetTimestamp();

            long enqueueStart = Stopwatch.GetTimestamp();
            Graphics.ExecuteCommandBuffer(adapter.Commands(slotIndex));
            adapter.MarkWorkSlotSubmitted(slotIndex, fence);
            submitted = true;
            long enqueueEnd = Stopwatch.GetTimestamp();
            long allocationEnd = GC.GetAllocatedBytesForCurrentThread();

            return new TimedSubmission
            {
                SlotIndex = slotIndex,
                SlotWaitFrames = slotWaitFrames,
                Preparation = preparation,
                Recorded = recorded,
                StateUpdateCpuMs = Milliseconds(updateEnd - updateStart),
                CommandRecordCpuMs =
                    Milliseconds(recordEnd - recordStart),
                CommandEnqueueCpuMs =
                    Milliseconds(enqueueEnd - enqueueStart),
                TotalCpuSubmissionMs =
                    Milliseconds(enqueueEnd - totalStart),
                MainThreadAllocatedBytes =
                    allocationEnd - allocationStart
            };
        }
        catch
        {
            if (!submitted)
            {
                adapter.AbandonUnsubmittedWorkSlot(slotIndex);
            }
            throw;
        }
    }

    private IEnumerator SubmitUnmeasured(
        GpuDrivenInstanceUploadBenchmarkVariant variant,
        int requestedMovingPercent,
        uint logicalOrdinal,
        bool computeExpectedHash,
        Action<ulong> expectedHash)
    {
        int slotIndex = -1;
        int ignoredWaitFrames = 0;
        while (!adapter.TryAcquireWorkSlot(
            out slotIndex,
            out ignoredWaitFrames))
        {
            yield return null;
        }

        bool submitted = false;
        try
        {
            adapter.PrepareSlot(
                slotIndex,
                variant,
                requestedMovingPercent,
                seed,
                logicalOrdinal);
            if (computeExpectedHash && expectedHash != null)
            {
                expectedHash(adapter.ComputeSlotStateHash(slotIndex));
            }
            adapter.RecordWorkload(slotIndex, variant);
            GraphicsFence fence = adapter.AppendLifetimeFence(slotIndex);
            Graphics.ExecuteCommandBuffer(adapter.Commands(slotIndex));
            adapter.MarkWorkSlotSubmitted(slotIndex, fence);
            submitted = true;
        }
        finally
        {
            if (!submitted)
            {
                adapter.AbandonUnsubmittedWorkSlot(slotIndex);
            }
        }
    }

    private IEnumerator WaitForAllCompletionFences(Action<bool> completion)
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + FenceTimeoutSeconds;
        while (!adapter.AllCompletionFencesPassed &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
        }
        completion(adapter.AllCompletionFencesPassed);
    }

    private IEnumerator ValidateGpuState(
        BlockPlan plan,
        uint logicalOrdinal,
        ulong expectedHash,
        bool fencesPassed,
        Action<ValidationResult> completion)
    {
        ulong actualHash = 0UL;
        bool hashPassed = false;
        string status;
        long readbackBytes = 0L;
        if (!fencesPassed)
        {
            status = "completion-fence-timeout";
        }
        else if (!SystemInfo.supportsAsyncGPUReadback)
        {
            status = "async-readback-unsupported";
        }
        else
        {
            AsyncGPUReadbackRequest request =
                AsyncGPUReadback.Request(adapter.InstanceStateBuffer);
            double deadline =
                Time.realtimeSinceStartupAsDouble + FenceTimeoutSeconds;
            while (!request.done &&
                Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
            if (!request.done)
            {
                status = "readback-timeout";
            }
            else if (request.hasError)
            {
                status = "readback-error";
            }
            else
            {
                NativeArray<GpuInstanceState> native =
                    request.GetData<GpuInstanceState>();
                readbackBytes = checked(
                    (long)native.Length * GpuInstanceState.Stride);
                hashPassed =
                    GpuDrivenInstanceUploadBenchmarkAdapter
                        .ValidateStateHash(
                            native,
                            instanceCount,
                            expectedHash,
                            out actualHash);
                status = hashPassed ? "passed" : "state-hash-mismatch";
            }
        }

        completion(new ValidationResult
        {
            BlockIndex = plan.BlockIndex,
            SuperRound = plan.SuperRound,
            SequencePosition = plan.SequencePosition,
            PairIndex = plan.PairIndex,
            PairOrder = plan.PairOrder,
            WithinPairPosition = plan.WithinPairPosition,
            Variant = plan.Variant,
            LogicalOrdinal = logicalOrdinal,
            CompletionFencePassed = fencesPassed,
            ExpectedStateHash = expectedHash,
            ActualStateHash = actualHash,
            Passed = fencesPassed && hashPassed,
            Status = status,
            ReadbackBytes = readbackBytes
        });
    }

    private RawSample CreateRawSample(
        BlockPlan plan,
        int sampleIndex,
        uint logicalOrdinal,
        int sourceFrame,
        TimedSubmission submission)
    {
        GpuInstanceUploadReceipt upload = submission.Recorded.Upload;
        return new RawSample
        {
            SourceRowIndex = rawSampleCount,
            ProcessId = processId,
            ScenarioId = scenarioId,
            BlockIndex = plan.BlockIndex,
            SuperRound = plan.SuperRound,
            SequencePosition = plan.SequencePosition,
            PairIndex = plan.PairIndex,
            PairOrder = plan.PairOrder,
            WithinPairPosition = plan.WithinPairPosition,
            Variant = plan.Variant,
            SampleIndex = sampleIndex,
            LogicalOrdinal = logicalOrdinal,
            SourceUnityFrame = sourceFrame,
            ElapsedSeconds =
                Time.realtimeSinceStartupAsDouble - benchmarkStart,
            SlotIndex = submission.SlotIndex,
            SlotWaitFrames = submission.SlotWaitFrames,
            StateUpdateCpuMs = submission.StateUpdateCpuMs,
            CommandRecordCpuMs = submission.CommandRecordCpuMs,
            CommandEnqueueCpuMs = submission.CommandEnqueueCpuMs,
            TotalCpuSubmissionMs = submission.TotalCpuSubmissionMs,
            MainThreadAllocatedBytes =
                submission.MainThreadAllocatedBytes,
            StateRecordsWritten =
                submission.Preparation.StateRecordsWritten,
            ChangedInstanceCount =
                submission.Preparation.Plan.ChangedInstanceCount,
            PlanRangeCount = submission.Preparation.Plan.RangeCount,
            RangePlanHash = submission.Preparation.Plan.RangePlanHash,
            UpdateHash = submission.Preparation.UpdateHash,
            UploadMode = upload.Mode,
            FullUploadReason = upload.FullUploadReason,
            InputRangeCount = upload.InputRangeCount,
            DirtyRecordCount = upload.DirtyRecordCount,
            DirtyRecordCountExact = upload.DirtyRecordCountExact,
            UploadedRecordCount = upload.UploadedRecordCount,
            BridgedCleanRecordCount = upload.BridgedCleanRecordCount,
            UploadCallCount = upload.UploadCallCount,
            LogicalUploadBytes = upload.LogicalUploadBytes,
            RenderApiCallCount =
                submission.Recorded.RenderApiCallCount,
            LogicalDrawCommandCount =
                submission.Recorded.LogicalDrawCommandCount,
            FrameTimingStatus =
                GpuDrivenInstanceFrameTimingStatus.NoTimingAvailable,
            SubmissionWindowStatus =
                GpuDrivenInstanceSubmissionWindowStatus
                    .FrameTimingUnavailable,
            CpuFrameMs = double.NaN,
            CpuMainThreadFrameMs = double.NaN,
            CpuRenderThreadFrameMs = double.NaN,
            GpuFrameMs = double.NaN,
            CpuSubmissionWindowMs = double.NaN,
            MeasurementReadbackBytes = 0
        };
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
        row.SubmissionWindowValid = timing.SubmissionWindowValid;
        row.SubmissionWindowStatus = timing.SubmissionWindowStatus;
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
        row.CpuSubmissionWindowMs = timing.CpuSubmissionMs;
        rawSamples[rowIndex] = row;
    }

    private BlockSummary SummarizeBlock(
        BlockPlan plan,
        int sampleStart,
        int count,
        ValidationResult validation)
    {
        double[] update = new double[count];
        double[] record = new double[count];
        double[] enqueue = new double[count];
        double[] total = new double[count];
        double[] cpuFrame = new double[count];
        int cpuFrameCount = 0;
        long allocationBytes = 0L;
        int allocationRows = 0;
        long logicalBytes = 0L;
        long uploadCalls = 0L;
        long slotWaitFrames = 0L;
        int timingReadyRows = 0;
        for (int offset = 0; offset < count; offset++)
        {
            RawSample row = rawSamples[sampleStart + offset];
            update[offset] = row.StateUpdateCpuMs;
            record[offset] = row.CommandRecordCpuMs;
            enqueue[offset] = row.CommandEnqueueCpuMs;
            total[offset] = row.TotalCpuSubmissionMs;
            allocationBytes += row.MainThreadAllocatedBytes;
            if (row.MainThreadAllocatedBytes != 0L)
            {
                allocationRows++;
            }
            logicalBytes += row.LogicalUploadBytes;
            uploadCalls += row.UploadCallCount;
            slotWaitFrames += row.SlotWaitFrames;
            if (row.FrameTimingValid)
            {
                cpuFrame[cpuFrameCount++] = row.CpuFrameMs;
                timingReadyRows++;
            }
        }

        return new BlockSummary
        {
            BlockIndex = plan.BlockIndex,
            SuperRound = plan.SuperRound,
            SequencePosition = plan.SequencePosition,
            PairIndex = plan.PairIndex,
            PairOrder = plan.PairOrder,
            WithinPairPosition = plan.WithinPairPosition,
            Variant = plan.Variant,
            SampleCount = count,
            StateUpdateMeanMs = Mean(update, count),
            CommandRecordMeanMs = Mean(record, count),
            CommandEnqueueMeanMs = Mean(enqueue, count),
            TotalMeanMs = Mean(total, count),
            TotalP50Ms = Percentile(total, count, 0.50),
            TotalP95Ms = Percentile(total, count, 0.95),
            TotalP99Ms = Percentile(total, count, 0.99),
            CpuFrameMeanMs = Mean(cpuFrame, cpuFrameCount),
            CpuFrameP95Ms = Percentile(
                cpuFrame,
                cpuFrameCount,
                0.95),
            FrameTimingReadyRows = timingReadyRows,
            MainThreadAllocationRows = allocationRows,
            MainThreadAllocatedBytes = allocationBytes,
            LogicalUploadBytes = logicalBytes,
            UploadCallCount = uploadCalls,
            SlotWaitFrames = slotWaitFrames,
            Validation = validation
        };
    }

    private static BlockPlan[] BuildBlockPlans()
    {
        IReadOnlyList<GpuDrivenInstanceScheduleEntry> source =
            GpuDrivenInstanceBenchmarkSchedule.Build(
                GpuDrivenInstanceBenchmarkSchedule
                    .FormalSuperRoundCount);
        BlockPlan[] result = new BlockPlan[MeasurementBlockCount];
        int target = 0;
        for (int index = 0; index < source.Count; index++)
        {
            GpuDrivenInstanceScheduleEntry entry = source[index];
            if (entry.Variant ==
                GpuDrivenInstanceBenchmarkVariant.Control)
            {
                continue;
            }
            result[target] = new BlockPlan
            {
                BlockIndex = target + 1,
                SuperRound = entry.SuperRound,
                SequencePosition = entry.SequencePosition,
                PairIndex = entry.PairIndex,
                PairOrder = entry.PairOrder,
                WithinPairPosition = entry.WithinPairPosition,
                Variant = entry.Variant ==
                    GpuDrivenInstanceBenchmarkVariant.Reference
                        ? GpuDrivenInstanceUploadBenchmarkVariant.FullUpload
                        : GpuDrivenInstanceUploadBenchmarkVariant
                            .DirtyRangeUpload
            };
            target++;
        }
        if (target != MeasurementBlockCount)
        {
            throw new InvalidOperationException(
                "Unexpected paired schedule measurement block count.");
        }
        return result;
    }

    private bool AllValidationsPassed()
    {
        if (validationResults == null ||
            validationResultCount != MeasurementBlockCount)
        {
            return false;
        }
        for (int index = 0; index < validationResultCount; index++)
        {
            if (!validationResults[index].Passed)
            {
                return false;
            }
        }
        return true;
    }

    private bool HasNoTimedAllocations()
    {
        if (rawSamples == null)
        {
            return false;
        }
        for (int index = 0; index < rawSampleCount; index++)
        {
            if (rawSamples[index].MainThreadAllocatedBytes != 0L)
            {
                return false;
            }
        }
        return true;
    }

    private void WriteAvailableEvidence()
    {
        if (rawSamples != null)
        {
            WriteRawFrames();
        }
        if (blockSummaries != null)
        {
            WriteBlockSummaries();
        }
        if (validationResults != null)
        {
            WriteValidations();
        }
    }

    private void WriteConfiguration()
    {
        ConfigurationBlock[] blocks =
            new ConfigurationBlock[blockPlans.Length];
        for (int index = 0; index < blockPlans.Length; index++)
        {
            BlockPlan plan = blockPlans[index];
            blocks[index] = new ConfigurationBlock
            {
                blockIndex = plan.BlockIndex,
                superRound = plan.SuperRound,
                sequencePosition = plan.SequencePosition,
                pairIndex = plan.PairIndex,
                pairOrder = plan.PairOrder,
                withinPairPosition = plan.WithinPairPosition,
                variant = VariantName(plan.Variant)
            };
        }
        Configuration configuration = new Configuration
        {
            schemaVersion = 1,
            suite = SuiteId,
            focusedUploadBenchmark = true,
            processId = processId,
            benchmarkStartedUtc = benchmarkStartedUtc,
            unityVersion = Application.unityVersion,
            scenarioId = scenarioId,
            instanceCount = instanceCount,
            movingPercent = movingPercent,
            seed = seed,
            inputLayout =
                GpuDrivenInstanceUploadInputGenerator.LayoutId,
            warmupFramesPerBlock = warmupFrames,
            sampleFramesPerBlock = sampleFrames,
            measurementBlocks = MeasurementBlockCount,
            superRounds = 2,
            scheduleContract = ScheduleContract,
            frameTimingResultLatencyFrames =
                FrameTimingResultLatencyFrames,
            viewCount = adapter.ViewCount,
            drawGroupCount = adapter.DrawGroupCount,
            stagingSlotCount = adapter.StagingSlotCount,
            persistentStagingPayloadBytes =
                adapter.PersistentStagingPayloadBytes,
            fullCaseId =
                GpuDrivenInstanceUploadBenchmarkAdapter.FullCaseId,
            dirtyCaseId =
                GpuDrivenInstanceUploadBenchmarkAdapter.DirtyCaseId,
            nativeGpuTimestamps = "not-collected",
            imageValidation = "not-collected",
            measurementReadbackBytes = 0L,
            commandLineArguments = originalArguments,
            blocks = blocks
        };
        string json = JsonUtility.ToJson(configuration, true);
        WriteText("configuration.json", json + Environment.NewLine);
        WriteText("config.json", json + Environment.NewLine);
    }

    private void WriteDeviceMetadata()
    {
        DeviceMetadata device = new DeviceMetadata
        {
            schemaVersion = 1,
            suite = SuiteId,
            operatingSystem = SystemInfo.operatingSystem,
            processorType = SystemInfo.processorType,
            processorCount = SystemInfo.processorCount,
            systemMemorySizeMb = SystemInfo.systemMemorySize,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceType =
                SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
            graphicsMemorySizeMb = SystemInfo.graphicsMemorySize,
            graphicsApiVersion = SystemInfo.graphicsDeviceVersion,
            graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded,
            supportsGraphicsFence = SystemInfo.supportsGraphicsFence,
            frameTimingFeatureEnabled =
                FrameTimingManager.IsFeatureEnabled(),
            unityVersion = Application.unityVersion
        };
        WriteText(
            "device.json",
            JsonUtility.ToJson(device, true) + Environment.NewLine);
    }

    private void WriteRawFrames()
    {
        using (StreamWriter writer = CreateWriter("raw-frames.csv"))
        {
            writer.WriteLine(
                "sourceRowIndex,processId,scenarioId,blockIndex," +
                "superRound,sequencePosition,pairIndex,pairOrder," +
                "withinPairPosition,variant,caseId,marker,sampleIndex," +
                "logicalOrdinal,sourceUnityFrame,elapsedSeconds," +
                "slotIndex,slotWaitFrames,stateUpdateCpuMs," +
                "commandRecordCpuMs,commandEnqueueCpuMs," +
                "totalCpuSubmissionMs,mainThreadAllocatedBytes," +
                "stateRecordsWritten,changedInstanceCount," +
                "planRangeCount,inputRangeCount,dirtyRecordCount," +
                "dirtyRecordCountExact,uploadedRecordCount," +
                "bridgedCleanRecordCount,uploadCallCount," +
                "logicalUploadBytes,uploadMode,fullUploadReason," +
                "rangePlanHash,updateHash,renderApiCallCount," +
                "logicalDrawCommandCount,frameTimingValid," +
                "frameTimingStatus,frameTimingResultUnityFrame," +
                "frameTimingCaptureLatencyFrames," +
                "cpuRenderThreadFrameValid,gpuFrameValid," +
                "submissionWindowValid,submissionWindowStatus," +
                "frameStartTimestamp,firstSubmitTimestamp," +
                "cpuTimePresentCalled,cpuTimeFrameComplete," +
                "cpuFrameMs,cpuMainThreadFrameMs," +
                "cpuRenderThreadFrameMs,gpuFrameMs," +
                "cpuSubmissionWindowMs,measurementReadbackBytes");
            for (int index = 0; index < rawSampleCount; index++)
            {
                RawSample row = rawSamples[index];
                writer.WriteLine(string.Join(",", new[]
                {
                    I(row.SourceRowIndex), I(row.ProcessId),
                    Csv(row.ScenarioId), I(row.BlockIndex),
                    I(row.SuperRound), I(row.SequencePosition),
                    I(row.PairIndex), Csv(row.PairOrder),
                    I(row.WithinPairPosition),
                    VariantName(row.Variant),
                    Csv(adapter.CaseId(row.Variant)),
                    Csv(adapter.Marker(row.Variant)),
                    I(row.SampleIndex), U(row.LogicalOrdinal),
                    I(row.SourceUnityFrame), D(row.ElapsedSeconds),
                    I(row.SlotIndex), I(row.SlotWaitFrames),
                    D(row.StateUpdateCpuMs),
                    D(row.CommandRecordCpuMs),
                    D(row.CommandEnqueueCpuMs),
                    D(row.TotalCpuSubmissionMs),
                    L(row.MainThreadAllocatedBytes),
                    I(row.StateRecordsWritten),
                    I(row.ChangedInstanceCount),
                    I(row.PlanRangeCount), I(row.InputRangeCount),
                    I(row.DirtyRecordCount),
                    B(row.DirtyRecordCountExact),
                    I(row.UploadedRecordCount),
                    I(row.BridgedCleanRecordCount),
                    I(row.UploadCallCount),
                    L(row.LogicalUploadBytes),
                    row.UploadMode.ToString(),
                    row.FullUploadReason.ToString(),
                    H(row.RangePlanHash), H(row.UpdateHash),
                    I(row.RenderApiCallCount),
                    I(row.LogicalDrawCommandCount),
                    B(row.FrameTimingValid),
                    row.FrameTimingStatus.ToString(),
                    I(row.FrameTimingResultUnityFrame),
                    I(row.FrameTimingCaptureLatencyFrames),
                    B(row.CpuRenderThreadFrameValid),
                    B(row.GpuFrameValid),
                    B(row.SubmissionWindowValid),
                    row.SubmissionWindowStatus.ToString(),
                    U64(row.FrameStartTimestamp),
                    U64(row.FirstSubmitTimestamp),
                    U64(row.CpuTimePresentCalled),
                    U64(row.CpuTimeFrameComplete),
                    M(row.CpuFrameMs),
                    M(row.CpuMainThreadFrameMs),
                    M(row.CpuRenderThreadFrameMs),
                    M(row.GpuFrameMs),
                    M(row.CpuSubmissionWindowMs),
                    L(row.MeasurementReadbackBytes)
                }));
            }
        }
    }

    private void WriteBlockSummaries()
    {
        using (StreamWriter writer = CreateWriter("block-summary.csv"))
        {
            writer.WriteLine(
                "blockIndex,superRound,sequencePosition,pairIndex," +
                "pairOrder,withinPairPosition,variant,sampleCount," +
                "stateUpdateMeanMs,commandRecordMeanMs," +
                "commandEnqueueMeanMs,totalMeanMs,totalP50Ms," +
                "totalP95Ms,totalP99Ms,cpuFrameMeanMs," +
                "cpuFrameP95Ms,frameTimingReadyRows," +
                "mainThreadAllocationRows,mainThreadAllocatedBytes," +
                "logicalUploadBytes,uploadCallCount,slotWaitFrames," +
                "completionFencePassed,stateValidationPassed," +
                "expectedStateHash,actualStateHash," +
                "validationReadbackBytes,validationStatus");
            for (int index = 0; index < blockSummaryCount; index++)
            {
                BlockSummary block = blockSummaries[index];
                writer.WriteLine(string.Join(",", new[]
                {
                    I(block.BlockIndex), I(block.SuperRound),
                    I(block.SequencePosition), I(block.PairIndex),
                    Csv(block.PairOrder), I(block.WithinPairPosition),
                    VariantName(block.Variant), I(block.SampleCount),
                    D(block.StateUpdateMeanMs),
                    D(block.CommandRecordMeanMs),
                    D(block.CommandEnqueueMeanMs),
                    D(block.TotalMeanMs), D(block.TotalP50Ms),
                    D(block.TotalP95Ms), D(block.TotalP99Ms),
                    M(block.CpuFrameMeanMs), M(block.CpuFrameP95Ms),
                    I(block.FrameTimingReadyRows),
                    I(block.MainThreadAllocationRows),
                    L(block.MainThreadAllocatedBytes),
                    L(block.LogicalUploadBytes),
                    L(block.UploadCallCount), L(block.SlotWaitFrames),
                    B(block.Validation.CompletionFencePassed),
                    B(block.Validation.Passed),
                    H(block.Validation.ExpectedStateHash),
                    H(block.Validation.ActualStateHash),
                    L(block.Validation.ReadbackBytes),
                    block.Validation.Status
                }));
            }
        }
    }

    private void WriteValidations()
    {
        using (StreamWriter writer = CreateWriter("validation.csv"))
        {
            writer.WriteLine(
                "blockIndex,superRound,sequencePosition,pairIndex," +
                "pairOrder,withinPairPosition,variant,logicalOrdinal," +
                "completionFencePassed,expectedStateHash," +
                "actualStateHash,passed,status,readbackBytes");
            for (int index = 0; index < validationResultCount; index++)
            {
                ValidationResult row = validationResults[index];
                writer.WriteLine(string.Join(",", new[]
                {
                    I(row.BlockIndex), I(row.SuperRound),
                    I(row.SequencePosition), I(row.PairIndex),
                    Csv(row.PairOrder), I(row.WithinPairPosition),
                    VariantName(row.Variant), U(row.LogicalOrdinal),
                    B(row.CompletionFencePassed),
                    H(row.ExpectedStateHash), H(row.ActualStateHash),
                    B(row.Passed), row.Status, L(row.ReadbackBytes)
                }));
            }
        }
    }

    private void WriteRunSummary(bool passed, string status)
    {
        int frameTimingReadyRows = 0;
        int submissionWindowReadyRows = 0;
        int allocationRows = 0;
        long allocationBytes = 0L;
        long logicalUploadBytes = 0L;
        long uploadCalls = 0L;
        long slotWaitFrames = 0L;
        if (rawSamples != null)
        {
            for (int index = 0; index < rawSampleCount; index++)
            {
                RawSample row = rawSamples[index];
                if (row.FrameTimingValid)
                {
                    frameTimingReadyRows++;
                }
                if (row.SubmissionWindowValid)
                {
                    submissionWindowReadyRows++;
                }
                if (row.MainThreadAllocatedBytes != 0L)
                {
                    allocationRows++;
                }
                allocationBytes += row.MainThreadAllocatedBytes;
                logicalUploadBytes += row.LogicalUploadBytes;
                uploadCalls += row.UploadCallCount;
                slotWaitFrames += row.SlotWaitFrames;
            }
        }
        int validationFailures = 0;
        long validationReadbackBytes = 0L;
        if (validationResults != null)
        {
            for (int index = 0; index < validationResultCount; index++)
            {
                if (!validationResults[index].Passed)
                {
                    validationFailures++;
                }
                validationReadbackBytes +=
                    validationResults[index].ReadbackBytes;
            }
        }
        string[] lines =
        {
            "suite=" + SuiteId,
            "focusedUploadBenchmark=1",
            "passed=" + B(passed),
            "status=" + status,
            "processId=" + I(processId),
            "scenarioId=" + scenarioId,
            "benchmarkStartedUtc=" + (benchmarkStartedUtc ?? "unavailable"),
            "elapsedSeconds=" + D(
                Time.realtimeSinceStartupAsDouble - benchmarkStart),
            "instanceCount=" + I(instanceCount),
            "movingPercent=" + I(movingPercent),
            "seed=" + I(seed),
            "inputLayout=" +
                GpuDrivenInstanceUploadInputGenerator.LayoutId,
            "scheduleContract=" + ScheduleContract,
            "measurementBlockCount=" + I(blockSummaryCount),
            "expectedMeasurementBlockCount=" +
                I(MeasurementBlockCount),
            "rawFrameCount=" + I(rawSampleCount),
            "expectedRawFrameCount=" + I(
                checked(MeasurementBlockCount * sampleFrames)),
            "warmupFramesPerBlock=" + I(warmupFrames),
            "sampleFramesPerBlock=" + I(sampleFrames),
            "frameTimingResultLatencyFrames=" +
                I(FrameTimingResultLatencyFrames),
            "frameTimingReadyRows=" + I(frameTimingReadyRows),
            "frameTimingUnavailableRows=" +
                I(rawSampleCount - frameTimingReadyRows),
            "submissionWindowReadyRows=" +
                I(submissionWindowReadyRows),
            "mainThreadAllocationRows=" + I(allocationRows),
            "mainThreadAllocatedBytes=" + L(allocationBytes),
            "timedAllocationFree=" + B(allocationRows == 0),
            "slotWaitFrames=" + L(slotWaitFrames),
            "logicalUploadBytes=" + L(logicalUploadBytes),
            "uploadCallCount=" + L(uploadCalls),
            "validationCount=" + I(validationResultCount),
            "validationFailures=" + I(validationFailures),
            "stateValidationPassed=" +
                B(validationResultCount == MeasurementBlockCount &&
                    validationFailures == 0),
            "validationReadbackBytes=" +
                L(validationReadbackBytes),
            "measurementReadbackBytes=0",
            "completionFencesComplete=" +
                B(allCompletionFencesPassed),
            "nativeGpuTimestamps=not-collected",
            "imageValidation=not-collected"
        };
        File.WriteAllLines(
            Path.Combine(reportDirectory, "run-summary.txt"),
            lines,
            new UTF8Encoding(false));
    }

    private void WriteText(string fileName, string content)
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, fileName),
            content,
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
        Debug.LogWarning(
            "GPU driven-instance focused upload benchmark " + status +
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
            Application.Quit(passed ? 0 : 1);
        }
    }

    private void DisposeResources()
    {
        GpuDrivenInstanceUploadBenchmarkAdapter current = adapter;
        adapter = null;
        current?.Dispose();
    }

    private static bool HasArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(
                args[index],
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
        for (int index = 0; index + 1 < args.Length; index++)
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
        string text = ReadString(args, name, string.Empty);
        return int.TryParse(
            text,
            NumberStyles.Integer,
            Invariant,
            out int value)
                ? value
                : fallback;
    }

    private static bool IsSupportedMovingPercent(int value)
    {
        return value == 0 || value == 1 || value == 10 || value == 100;
    }

    private static double Milliseconds(long ticks)
    {
        return (double)ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static double Mean(double[] values, int count)
    {
        if (count <= 0)
        {
            return double.NaN;
        }
        double sum = 0.0;
        for (int index = 0; index < count; index++)
        {
            sum += values[index];
        }
        return sum / count;
    }

    private static double Percentile(
        double[] values,
        int count,
        double percentile)
    {
        if (count <= 0)
        {
            return double.NaN;
        }
        Array.Sort(values, 0, count);
        int index = Math.Max(
            0,
            Math.Min(
                count - 1,
                (int)Math.Ceiling(percentile * count) - 1));
        return values[index];
    }

    private static string VariantName(
        GpuDrivenInstanceUploadBenchmarkVariant variant)
    {
        return variant ==
            GpuDrivenInstanceUploadBenchmarkVariant.FullUpload
                ? "full"
                : "dirty";
    }

    private static string Csv(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string I(int value) => value.ToString(Invariant);

    private static string U(uint value) => value.ToString(Invariant);

    private static string U64(ulong value) => value.ToString(Invariant);

    private static string L(long value) => value.ToString(Invariant);

    private static string B(bool value) => value ? "1" : "0";

    private static string D(double value) => value.ToString("R", Invariant);

    private static string M(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? "unavailable"
            : value.ToString("R", Invariant);
    }

    private static string H(ulong value)
    {
        return value.ToString("x16", Invariant);
    }

    private struct BlockPlan
    {
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public GpuDrivenInstanceUploadBenchmarkVariant Variant;
    }

    private struct TimedSubmission
    {
        public int SlotIndex;
        public int SlotWaitFrames;
        public GpuDrivenInstanceUploadPreparationReceipt Preparation;
        public GpuDrivenInstanceUploadRecordReceipt Recorded;
        public double StateUpdateCpuMs;
        public double CommandRecordCpuMs;
        public double CommandEnqueueCpuMs;
        public double TotalCpuSubmissionMs;
        public long MainThreadAllocatedBytes;
    }

    private struct RawSample
    {
        public int SourceRowIndex;
        public int ProcessId;
        public string ScenarioId;
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public GpuDrivenInstanceUploadBenchmarkVariant Variant;
        public int SampleIndex;
        public uint LogicalOrdinal;
        public int SourceUnityFrame;
        public double ElapsedSeconds;
        public int SlotIndex;
        public int SlotWaitFrames;
        public double StateUpdateCpuMs;
        public double CommandRecordCpuMs;
        public double CommandEnqueueCpuMs;
        public double TotalCpuSubmissionMs;
        public long MainThreadAllocatedBytes;
        public int StateRecordsWritten;
        public int ChangedInstanceCount;
        public int PlanRangeCount;
        public ulong RangePlanHash;
        public ulong UpdateHash;
        public GpuInstanceUploadMode UploadMode;
        public GpuInstanceFullUploadReason FullUploadReason;
        public int InputRangeCount;
        public int DirtyRecordCount;
        public bool DirtyRecordCountExact;
        public int UploadedRecordCount;
        public int BridgedCleanRecordCount;
        public int UploadCallCount;
        public long LogicalUploadBytes;
        public int RenderApiCallCount;
        public int LogicalDrawCommandCount;
        public bool FrameTimingValid;
        public GpuDrivenInstanceFrameTimingStatus FrameTimingStatus;
        public int FrameTimingResultUnityFrame;
        public int FrameTimingCaptureLatencyFrames;
        public bool CpuRenderThreadFrameValid;
        public bool GpuFrameValid;
        public bool SubmissionWindowValid;
        public GpuDrivenInstanceSubmissionWindowStatus
            SubmissionWindowStatus;
        public ulong FrameStartTimestamp;
        public ulong FirstSubmitTimestamp;
        public ulong CpuTimePresentCalled;
        public ulong CpuTimeFrameComplete;
        public double CpuFrameMs;
        public double CpuMainThreadFrameMs;
        public double CpuRenderThreadFrameMs;
        public double GpuFrameMs;
        public double CpuSubmissionWindowMs;
        public long MeasurementReadbackBytes;
    }

    private struct ValidationResult
    {
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public GpuDrivenInstanceUploadBenchmarkVariant Variant;
        public uint LogicalOrdinal;
        public bool CompletionFencePassed;
        public ulong ExpectedStateHash;
        public ulong ActualStateHash;
        public bool Passed;
        public string Status;
        public long ReadbackBytes;
    }

    private struct BlockSummary
    {
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public GpuDrivenInstanceUploadBenchmarkVariant Variant;
        public int SampleCount;
        public double StateUpdateMeanMs;
        public double CommandRecordMeanMs;
        public double CommandEnqueueMeanMs;
        public double TotalMeanMs;
        public double TotalP50Ms;
        public double TotalP95Ms;
        public double TotalP99Ms;
        public double CpuFrameMeanMs;
        public double CpuFrameP95Ms;
        public int FrameTimingReadyRows;
        public int MainThreadAllocationRows;
        public long MainThreadAllocatedBytes;
        public long LogicalUploadBytes;
        public long UploadCallCount;
        public long SlotWaitFrames;
        public ValidationResult Validation;
    }

    [Serializable]
    private sealed class Configuration
    {
        public int schemaVersion;
        public string suite;
        public bool focusedUploadBenchmark;
        public int processId;
        public string benchmarkStartedUtc;
        public string unityVersion;
        public string scenarioId;
        public int instanceCount;
        public int movingPercent;
        public int seed;
        public string inputLayout;
        public int warmupFramesPerBlock;
        public int sampleFramesPerBlock;
        public int measurementBlocks;
        public int superRounds;
        public string scheduleContract;
        public int frameTimingResultLatencyFrames;
        public int viewCount;
        public int drawGroupCount;
        public int stagingSlotCount;
        public long persistentStagingPayloadBytes;
        public string fullCaseId;
        public string dirtyCaseId;
        public string nativeGpuTimestamps;
        public string imageValidation;
        public long measurementReadbackBytes;
        public string[] commandLineArguments;
        public ConfigurationBlock[] blocks;
    }

    [Serializable]
    private sealed class ConfigurationBlock
    {
        public int blockIndex;
        public int superRound;
        public int sequencePosition;
        public int pairIndex;
        public string pairOrder;
        public int withinPairPosition;
        public string variant;
    }

    [Serializable]
    private sealed class DeviceMetadata
    {
        public int schemaVersion;
        public string suite;
        public string operatingSystem;
        public string processorType;
        public int processorCount;
        public int systemMemorySizeMb;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public string graphicsDeviceVendor;
        public int graphicsMemorySizeMb;
        public string graphicsApiVersion;
        public bool graphicsMultiThreaded;
        public bool supportsGraphicsFence;
        public bool frameTimingFeatureEnabled;
        public string unityVersion;
    }
}
