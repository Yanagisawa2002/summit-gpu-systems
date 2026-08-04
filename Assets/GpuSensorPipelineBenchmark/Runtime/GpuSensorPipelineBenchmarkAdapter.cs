using System;
using System.Diagnostics;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuSensorPipelineValidationResult
{
    public string Phase;
    public bool Passed;
    public string Message;
    public long ReadbackBytes;
    public string ResultHash;
    public int StateCount;
    public uint DigestMismatchCount;
    public uint DigestDeltaXor;
    public uint DigestDeltaSum0;
    public uint DigestDeltaSum1;
    public uint CpuInvalidKeyCount;
    public uint CpuInvalidKeyHash;
    public uint GpuInvalidKeyCount;
    public uint GpuInvalidKeyHash;
}

internal sealed class GpuSensorPipelineBenchmarkAdapter : IDisposable
{
    public const string CpuCaseId =
        "sensor-pipeline/cpu-produced-uploaded-v1";
    public const string GpuCaseId =
        "sensor-pipeline/gpu-produced-resident-v1";
    public const string RebuiltPerSensorCaseId =
        "sensor-pipeline/multi-sensor-rebuilt-per-sensor-v1";
    public const string SharedSensorIndexCaseId =
        "sensor-pipeline/multi-sensor-shared-index-v1";
    public const string ControlCaseId =
        "control/empty-main-graphics-command-buffer";
    public const string CpuMarker =
        "GPU.SensorPipeline/CpuProducedUploaded/MainGraphics";
    public const string GpuMarker =
        "GPU.SensorPipeline/GpuProducedResident/MainGraphics";
    public const string RebuiltPerSensorMarker =
        "GPU.SensorPipeline/MultiSensorRebuilt/MainGraphics";
    public const string SharedSensorIndexMarker =
        "GPU.SensorPipeline/MultiSensorShared/MainGraphics";
    public const string ControlMarker =
        "GPU.SensorPipeline/Control/EmptyMainGraphics";
    public const int StateCount =
        GpuSensorDeterministicGenerator.StateCount;
    public const int DefaultStagingSlotCount = 4;
    internal static GraphicsBuffer.Target BlockDigestBufferTarget =>
        GraphicsBuffer.Target.Structured |
        GraphicsBuffer.Target.CopyDestination;

    private readonly int elementCount;
    private readonly int queryCount;
    private readonly int sensorCount;
    private readonly int queriesPerSensor;
    private readonly uint seed;
    private readonly GpuSensorPipeline pipeline;
    private readonly WorkSlot[] workSlots;
    private readonly GraphicsBuffer cpuBlockDigests;
    private readonly GraphicsBuffer gpuBlockDigests;
    private readonly CommandBuffer validationCommands;
    private readonly long hostStagingBytes;
    private int nextSlot;
    private PendingCapture pendingCapture;
    private PendingComparison pendingComparison;
    private KeyValidation cpuKeyValidation;
    private KeyValidation gpuKeyValidation;
    private bool disposed;

    public GpuSensorPipelineBenchmarkAdapter(
        int elementCount,
        int queryCount,
        int seed,
        int stagingSlotCount = DefaultStagingSlotCount,
        int sensorCount = 1,
        int queriesPerSensor = 0)
    {
        if (elementCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }
        if (queryCount < 1 || queryCount > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(queryCount));
        }
        if (stagingSlotCount < 2 || stagingSlotCount > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(stagingSlotCount));
        }
        if (sensorCount != 1 && (sensorCount < 2 || sensorCount > 64))
        {
            throw new ArgumentOutOfRangeException(nameof(sensorCount));
        }
        if (sensorCount == 1)
        {
            queriesPerSensor = queryCount;
        }
        else if (queriesPerSensor < 1 ||
                 checked(sensorCount * queriesPerSensor) != queryCount)
        {
            throw new ArgumentException(
                "queryCount must equal sensorCount * queriesPerSensor.");
        }

        this.elementCount = elementCount;
        this.queryCount = queryCount;
        this.sensorCount = sensorCount;
        this.queriesPerSensor = queriesPerSensor;
        this.seed = unchecked((uint)seed);
        GpuSensorPipeline selectedPipeline = null;
        GraphicsBuffer selectedCpuDigests = null;
        GraphicsBuffer selectedGpuDigests = null;
        CommandBuffer selectedValidationCommands = null;
        WorkSlot[] selectedSlots = null;
        try
        {
            selectedPipeline = new GpuSensorPipeline(
                elementCount,
                queryCount,
                GpuPrimitiveBackend.WaveOps,
                emitProfilerMarkers: false);
            uint[] stableIds = new uint[elementCount];
            for (int index = 0; index < stableIds.Length; index++)
            {
                stableIds[index] = unchecked((uint)index);
            }
            selectedPipeline.SetStableIds(stableIds);
            selectedPipeline.SetQueries(BuildQueries(queryCount, this.seed));

            selectedCpuDigests = CreateDigestBuffer(
                "GPU Sensor Pipeline CPU Block Digests");
            selectedGpuDigests = CreateDigestBuffer(
                "GPU Sensor Pipeline GPU Block Digests");
            selectedValidationCommands = new CommandBuffer
            {
                name = "GPU.SensorPipeline/ValidationOutsideTiming"
            };
            selectedSlots = new WorkSlot[stagingSlotCount];
            for (int slot = 0; slot < selectedSlots.Length; slot++)
            {
                selectedSlots[slot] = new WorkSlot(elementCount, slot);
            }
        }
        catch
        {
            if (selectedSlots != null)
            {
                foreach (WorkSlot slot in selectedSlots)
                {
                    slot?.Dispose();
                }
            }
            selectedValidationCommands?.Dispose();
            selectedCpuDigests?.Dispose();
            selectedGpuDigests?.Dispose();
            selectedPipeline?.Dispose();
            throw;
        }

        pipeline = selectedPipeline;
        cpuBlockDigests = selectedCpuDigests;
        gpuBlockDigests = selectedGpuDigests;
        validationCommands = selectedValidationCommands;
        workSlots = selectedSlots;
        hostStagingBytes = checked(
            (long)stagingSlotCount * elementCount *
            (GpuSensorPipeline.SampleStride + GpuSensorPipeline.UintStride));
    }

    public int ElementCount => elementCount;
    public int QueryCount => queryCount;
    public int SensorCount => sensorCount;
    public int QueriesPerSensor => queriesPerSensor;
    public bool IsMultiSensorMode => sensorCount > 1;
    public int BinCount => GpuSensorPipeline.FixedBinCount;
    public int LogicalStateCount => StateCount;
    public int StagingSlotCount => workSlots.Length;
    public uint Seed => seed;
    public bool EmitsProfilerMarkers => pipeline.EmitsProfilerMarkers;
    public long CpuUploadLogicalBytesPerFrame =>
        pipeline.CpuUploadLogicalBytes(elementCount);
    public long GpuProducerLogicalWriteBytesPerFrame =>
        pipeline.GpuProducerLogicalWriteBytes(elementCount);
    public long RebuiltPerSensorProducerLogicalWriteBytesPerFrame =>
        IsMultiSensorMode
            ? pipeline.RebuiltPerSensorProducerLogicalWriteBytes(
                elementCount,
                sensorCount)
            : pipeline.GpuProducerLogicalWriteBytes(elementCount);
    public long HostStagingBytes => hostStagingBytes;
    public long PipelineOwnedBufferBytes => pipeline.OwnedBufferBytes;
    public long PrimitiveScratchBytes => pipeline.PrimitiveScratchBytes;
    public long BinnerInternalScratchBytes =>
        pipeline.BinnerInternalScratchBytes;
    public long PipelineResidentBytes => pipeline.ResidentBytes;
    public long SharedSpatialIndexResidentBytes =>
        pipeline.SpatialIndexResidentBytes;
    public long IndependentSpatialIndexResidentBytes =>
        IsMultiSensorMode
            ? pipeline.IndependentSensorSpatialIndexBytes(sensorCount)
            : pipeline.SpatialIndexResidentBytes;
    public long MultiSensorSpatialIndexBytesSaved =>
        checked(
            IndependentSpatialIndexResidentBytes -
            SharedSpatialIndexResidentBytes);
    public long BlockDigestBufferBytes =>
        checked((long)StateCount * GpuSensorPipeline.DigestStride * 2L);
    public long ActualBenchmarkGpuResidentBytes =>
        checked(PipelineResidentBytes + BlockDigestBufferBytes);

    public int IndexBuildCount(
        GpuSensorPipelineBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor:
                return sensorCount;
            case GpuSensorPipelineBenchmarkVariant.SharedSensorIndex:
            case GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded:
            case GpuSensorPipelineBenchmarkVariant.GpuProducedResident:
                return 1;
            case GpuSensorPipelineBenchmarkVariant.Control:
                return 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public long GpuProducerLogicalWriteBytes(
        GpuSensorPipelineBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor:
                return RebuiltPerSensorProducerLogicalWriteBytesPerFrame;
            case GpuSensorPipelineBenchmarkVariant.SharedSensorIndex:
            case GpuSensorPipelineBenchmarkVariant.GpuProducedResident:
                return GpuProducerLogicalWriteBytesPerFrame;
            default:
                return 0L;
        }
    }

    public string CaseId(GpuSensorPipelineBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded:
                return CpuCaseId;
            case GpuSensorPipelineBenchmarkVariant.GpuProducedResident:
                return GpuCaseId;
            case GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor:
                return RebuiltPerSensorCaseId;
            case GpuSensorPipelineBenchmarkVariant.SharedSensorIndex:
                return SharedSensorIndexCaseId;
            case GpuSensorPipelineBenchmarkVariant.Control:
                return ControlCaseId;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string VariantName(GpuSensorPipelineBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded:
                return "cpu-produced-uploaded";
            case GpuSensorPipelineBenchmarkVariant.GpuProducedResident:
                return "gpu-produced-resident";
            case GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor:
                return "rebuilt-per-sensor";
            case GpuSensorPipelineBenchmarkVariant.SharedSensorIndex:
                return "shared-sensor-index";
            case GpuSensorPipelineBenchmarkVariant.Control:
                return "empty-main-graphics-control";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string Marker(GpuSensorPipelineBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded:
                return CpuMarker;
            case GpuSensorPipelineBenchmarkVariant.GpuProducedResident:
                return GpuMarker;
            case GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor:
                return RebuiltPerSensorMarker;
            case GpuSensorPipelineBenchmarkVariant.SharedSensorIndex:
                return SharedSensorIndexMarker;
            case GpuSensorPipelineBenchmarkVariant.Control:
                return ControlMarker;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public bool TryAcquireWorkSlot(out int slotIndex)
    {
        ThrowIfDisposed();
        for (int attempt = 0; attempt < workSlots.Length; attempt++)
        {
            int candidate = (nextSlot + attempt) % workSlots.Length;
            if (!workSlots[candidate].TryMakeAvailable())
            {
                continue;
            }
            nextSlot = (candidate + 1) % workSlots.Length;
            slotIndex = candidate;
            return true;
        }
        slotIndex = -1;
        return false;
    }

    public CommandBuffer Commands(int slotIndex)
    {
        return GetSlot(slotIndex).Commands;
    }

    public double PopulateCpuStaging(int slotIndex, int logicalState)
    {
        WorkSlot slot = GetSlot(slotIndex);
        long start = Stopwatch.GetTimestamp();
        GpuSensorDeterministicGenerator.Populate(
            slot.Samples,
            slot.Keys,
            elementCount,
            seed,
            checked((uint)logicalState));
        return ElapsedMilliseconds(start);
    }

    public double RecordWorkload(
        int slotIndex,
        GpuSensorPipelineBenchmarkVariant variant,
        int logicalState)
    {
        WorkSlot slot = GetSlot(slotIndex);
        long start = Stopwatch.GetTimestamp();
        switch (variant)
        {
            case GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded:
                pipeline.RecordCpuProduced(
                    slot.Commands,
                    slot.Samples,
                    slot.Keys,
                    elementCount,
                    queryCount,
                    checked((uint)logicalState));
                break;
            case GpuSensorPipelineBenchmarkVariant.GpuProducedResident:
                pipeline.RecordGpuProduced(
                    slot.Commands,
                    seed,
                    checked((uint)logicalState),
                    elementCount,
                    queryCount);
                break;
            case GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor:
                pipeline.RecordGpuProducedRebuiltPerSensor(
                    slot.Commands,
                    seed,
                    checked((uint)logicalState),
                    elementCount,
                    sensorCount,
                    queriesPerSensor);
                break;
            case GpuSensorPipelineBenchmarkVariant.SharedSensorIndex:
                pipeline.RecordGpuProducedSharedSensorIndex(
                    slot.Commands,
                    seed,
                    checked((uint)logicalState),
                    elementCount,
                    sensorCount,
                    queriesPerSensor);
                break;
            case GpuSensorPipelineBenchmarkVariant.Control:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
        return ElapsedMilliseconds(start);
    }

    public GraphicsFence AppendLifetimeFence(int slotIndex)
    {
        WorkSlot slot = GetSlot(slotIndex);
        return slot.Commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
    }

    public void MarkWorkSlotSubmitted(
        int slotIndex,
        GraphicsFence fence)
    {
        GetSlot(slotIndex).MarkSubmitted(fence);
    }

    public bool HasInFlightWork
    {
        get
        {
            foreach (WorkSlot slot in workSlots)
            {
                if (!slot.TryMakeAvailable())
                {
                    return true;
                }
            }
            return false;
        }
    }

    public void BeginCaptureBlock(
        GpuSensorPipelineBenchmarkVariant variant)
    {
        ThrowIfDisposed();
        if (variant == GpuSensorPipelineBenchmarkVariant.Control)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        if (pendingCapture != null || pendingComparison != null)
        {
            throw new InvalidOperationException(
                "A validation operation is already pending.");
        }

        validationCommands.Clear();
        pipeline.RecordValidateKeys(validationCommands, elementCount);
        validationCommands.CopyBuffer(
            pipeline.FrameDigest,
            IsBaselineVariant(variant)
                ? cpuBlockDigests
                : gpuBlockDigests);
        Graphics.ExecuteCommandBuffer(validationCommands);
        pendingCapture = new PendingCapture
        {
            Variant = variant,
            KeyRequest = AsyncGPUReadback.Request(
                pipeline.KeyValidationDiagnostics)
        };
    }

    public bool TryCompleteCapture(out bool passed)
    {
        passed = false;
        if (pendingCapture == null || !pendingCapture.KeyRequest.done)
        {
            return false;
        }
        PendingCapture completed = pendingCapture;
        pendingCapture = null;
        KeyValidation result = new KeyValidation
        {
            ReadbackBytes =
                GpuSensorPipeline.KeyValidationWordCount * sizeof(uint),
            ReadbackFailed = completed.KeyRequest.hasError
        };
        if (!result.ReadbackFailed)
        {
            NativeArray<uint> words = completed.KeyRequest.GetData<uint>();
            result.InvalidCount = words[0];
            result.InvalidHash = words[1];
        }
        if (IsBaselineVariant(completed.Variant))
        {
            cpuKeyValidation = result;
        }
        else
        {
            gpuKeyValidation = result;
        }
        passed = !result.ReadbackFailed && result.InvalidCount == 0u;
        return true;
    }

    public void BeginCompareCapturedBlocks(string phase)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(phase))
        {
            throw new ArgumentException(
                "Validation phase is required.",
                nameof(phase));
        }
        if (pendingCapture != null || pendingComparison != null)
        {
            throw new InvalidOperationException(
                "A validation operation is already pending.");
        }
        validationCommands.Clear();
        pipeline.RecordClearComparison(validationCommands);
        pipeline.RecordCompareDigests(
            validationCommands,
            cpuBlockDigests,
            gpuBlockDigests,
            StateCount);
        Graphics.ExecuteCommandBuffer(validationCommands);
        pendingComparison = new PendingComparison
        {
            Phase = phase,
            Request = AsyncGPUReadback.Request(pipeline.ComparisonDigest),
            CpuKeys = cpuKeyValidation,
            GpuKeys = gpuKeyValidation
        };
    }

    public bool TryCompleteComparison(
        out GpuSensorPipelineValidationResult result)
    {
        result = null;
        if (pendingComparison == null || !pendingComparison.Request.done)
        {
            return false;
        }
        PendingComparison completed = pendingComparison;
        pendingComparison = null;
        result = new GpuSensorPipelineValidationResult
        {
            Phase = completed.Phase,
            StateCount = StateCount,
            CpuInvalidKeyCount = completed.CpuKeys.InvalidCount,
            CpuInvalidKeyHash = completed.CpuKeys.InvalidHash,
            GpuInvalidKeyCount = completed.GpuKeys.InvalidCount,
            GpuInvalidKeyHash = completed.GpuKeys.InvalidHash,
            ReadbackBytes = checked(
                completed.CpuKeys.ReadbackBytes +
                completed.GpuKeys.ReadbackBytes +
                GpuSensorPipeline.DigestStride)
        };
        if (completed.Request.hasError ||
            completed.CpuKeys.ReadbackFailed ||
            completed.GpuKeys.ReadbackFailed)
        {
            result.Passed = false;
            result.Message = "Validation readback failed.";
            result.ResultHash = "unavailable";
            return true;
        }
        NativeArray<uint> words = completed.Request.GetData<uint>();
        result.DigestMismatchCount = words[0];
        result.DigestDeltaXor = words[1];
        result.DigestDeltaSum0 = words[2];
        result.DigestDeltaSum1 = words[3];
        result.Passed =
            result.DigestMismatchCount == 0u &&
            result.CpuInvalidKeyCount == 0u &&
            result.GpuInvalidKeyCount == 0u;
        result.Message = result.Passed
            ? "All 64 CPU/GPU frame digests match; captured keys are in range."
            : "Digest mismatch or invalid key detected.";
        result.ResultHash = string.Format(
            "{0:X8}-{1:X8}-{2:X8}-{3:X8}",
            result.DigestMismatchCount,
            result.DigestDeltaXor,
            result.DigestDeltaSum0,
            result.DigestDeltaSum1);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (WorkSlot slot in workSlots)
        {
            slot.Dispose();
        }
        validationCommands.Dispose();
        cpuBlockDigests.Dispose();
        gpuBlockDigests.Dispose();
        pipeline.Dispose();
    }

    private WorkSlot GetSlot(int slotIndex)
    {
        ThrowIfDisposed();
        if (slotIndex < 0 || slotIndex >= workSlots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }
        return workSlots[slotIndex];
    }

    private static GpuSensorRangeQuery[] BuildQueries(int count, uint seed)
    {
        GpuSensorRangeQuery[] result = new GpuSensorRangeQuery[count];
        for (int index = 0; index < result.Length; index++)
        {
            uint ordinal = unchecked((uint)index);
            uint x = GpuSensorDeterministicGenerator.Mix(
                seed ^ ordinal ^ 0xB5297A4Du) &
                GpuSensorDeterministicGenerator.CoordinateMask;
            uint y = GpuSensorDeterministicGenerator.Mix(
                seed ^ ordinal ^ 0x68E31DA4u) &
                GpuSensorDeterministicGenerator.CoordinateMask;
            uint z = GpuSensorDeterministicGenerator.Mix(
                seed ^ ordinal ^ 0x1B56C4E9u) &
                GpuSensorDeterministicGenerator.CoordinateMask;
            result[index] = new GpuSensorRangeQuery(x, y, z, 2047u);
        }
        return result;
    }

    private static GraphicsBuffer CreateDigestBuffer(string name)
    {
        return new GraphicsBuffer(
            BlockDigestBufferTarget,
            StateCount,
            GpuSensorPipeline.DigestStride)
        {
            name = name
        };
    }

    private static double ElapsedMilliseconds(long start)
    {
        return (Stopwatch.GetTimestamp() - start) *
            (1000.0 / Stopwatch.Frequency);
    }

    private static bool IsBaselineVariant(
        GpuSensorPipelineBenchmarkVariant variant)
    {
        return variant ==
                   GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded ||
               variant ==
                   GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuSensorPipelineBenchmarkAdapter));
        }
    }

    private sealed class WorkSlot : IDisposable
    {
        private bool submitted;
        private GraphicsFence fence;

        public WorkSlot(int elementCount, int slotIndex)
        {
            Samples = new GpuSensorSample[elementCount];
            Keys = new uint[elementCount];
            Commands = new CommandBuffer
            {
                name = "GPU.SensorPipeline/DynamicSlot/" + slotIndex
            };
        }

        public GpuSensorSample[] Samples { get; }
        public uint[] Keys { get; }
        public CommandBuffer Commands { get; }

        public bool TryMakeAvailable()
        {
            if (!submitted)
            {
                return true;
            }
            if (!fence.passed)
            {
                return false;
            }
            submitted = false;
            return true;
        }

        public void MarkSubmitted(GraphicsFence submittedFence)
        {
            if (submitted)
            {
                throw new InvalidOperationException(
                    "A staging slot cannot be reused before its fence passes.");
            }
            fence = submittedFence;
            submitted = true;
        }

        public void Dispose()
        {
            Commands.Dispose();
        }
    }

    private struct KeyValidation
    {
        public uint InvalidCount;
        public uint InvalidHash;
        public bool ReadbackFailed;
        public long ReadbackBytes;
    }

    private sealed class PendingCapture
    {
        public GpuSensorPipelineBenchmarkVariant Variant;
        public AsyncGPUReadbackRequest KeyRequest;
    }

    private sealed class PendingComparison
    {
        public string Phase;
        public AsyncGPUReadbackRequest Request;
        public KeyValidation CpuKeys;
        public KeyValidation GpuKeys;
    }
}
