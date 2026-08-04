using System;
using System.Diagnostics;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuSensorDataPackingValidationResult
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
    public uint BaselineInvalidKeyCount;
    public uint BaselineInvalidKeyHash;
    public uint PackedSampleMismatchCount;
    public uint PackedSampleMismatchHash;
    public uint PackedCsrMismatchCount;
    public uint PackedCsrMismatchHash;
    public uint PackedCsrElementCount;
    public uint PackedCsrIdXor;
    public uint PackedCsrIdSum;
    public uint PackedCsrIdMixedSum;
    public uint ExpectedPackedCsrElementCount;
    public uint ExpectedPackedCsrIdXor;
    public uint ExpectedPackedCsrIdSum;
    public uint ExpectedPackedCsrIdMixedSum;
}

internal sealed class GpuSensorDataPackingBenchmarkAdapter : IDisposable
{
    public const string BaselineCaseId =
        "sensor-data-packing/expanded-aos-quantized-view-v1";
    public const string PackedCaseId =
        "sensor-data-packing/packed-soa-fused-end-cursor-v2";
    public const string ControlCaseId =
        "control/empty-main-graphics-command-buffer";
    public const string BaselineMarker =
        "GPU.SensorDataPacking/ExpandedAoSQuantizedView/MainGraphics";
    public const string PackedMarker =
        "GPU.SensorDataPacking/PackedSoAFusedEndCursor/MainGraphics";
    public const string ControlMarker =
        "GPU.SensorDataPacking/Control/EmptyMainGraphics";
    public const int StateCount =
        GpuSensorDeterministicGenerator.StateCount;
    public const int DefaultCommandSlotCount = 4;
    public const long WorkloadReadbackBytesPerFrame = 0L;
    public const long ValidationReadbackBytesPerComparison =
        GpuSensorPipeline.KeyValidationWordCount * sizeof(uint) +
        GpuSensorPackedSoaPipeline.ValidationWordCount * sizeof(uint) +
        GpuSensorPipeline.DigestStride;

    internal static GraphicsBuffer.Target BlockDigestBufferTarget =>
        GraphicsBuffer.Target.Structured |
        GraphicsBuffer.Target.CopyDestination;

    private readonly int elementCount;
    private readonly int queryCount;
    private readonly uint seed;
    private readonly GpuSensorPipeline baselinePipeline;
    private readonly GpuSensorPackedSoaPipeline packedPipeline;
    private readonly CommandSlot[] commandSlots;
    private readonly GraphicsBuffer baselineBlockDigests;
    private readonly GraphicsBuffer packedBlockDigests;
    private readonly CommandBuffer validationCommands;
    private readonly uint expectedCsrElementCount;
    private readonly uint expectedCsrIdXor;
    private readonly uint expectedCsrIdSum;
    private readonly uint expectedCsrIdMixedSum;
    private int nextSlot;
    private PendingCapture pendingCapture;
    private PendingComparison pendingComparison;
    private BaselineValidation baselineValidation;
    private PackedValidation packedValidation;
    private bool hasBaselineValidation;
    private bool hasPackedValidation;
    private bool disposed;

    public GpuSensorDataPackingBenchmarkAdapter(
        int elementCount,
        int queryCount,
        int seed,
        int commandSlotCount = DefaultCommandSlotCount)
    {
        if (elementCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }
        if (queryCount < 1 || queryCount > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(queryCount));
        }
        if (commandSlotCount < 2 || commandSlotCount > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandSlotCount));
        }

        this.elementCount = elementCount;
        this.queryCount = queryCount;
        this.seed = unchecked((uint)seed);
        GpuSensorPipeline selectedBaseline = null;
        GpuSensorPackedSoaPipeline selectedPacked = null;
        GraphicsBuffer selectedBaselineDigests = null;
        GraphicsBuffer selectedPackedDigests = null;
        CommandBuffer selectedValidationCommands = null;
        CommandSlot[] selectedSlots = null;
        uint selectedExpectedIdXor = 0u;
        uint selectedExpectedIdSum = 0u;
        uint selectedExpectedIdMixedSum = 0u;
        try
        {
            selectedBaseline = new GpuSensorPipeline(
                elementCount,
                queryCount,
                GpuPrimitiveBackend.WaveOps,
                emitProfilerMarkers: true);
            selectedPacked = new GpuSensorPackedSoaPipeline(
                elementCount,
                queryCount,
                GpuPrimitiveBackend.WaveOps,
                emitProfilerMarkers: true);

            uint[] stableIds = new uint[elementCount];
            unchecked
            {
                for (int index = 0; index < stableIds.Length; index++)
                {
                    uint id = (uint)index;
                    stableIds[index] = id;
                    selectedExpectedIdXor ^= id;
                    selectedExpectedIdSum += id;
                    selectedExpectedIdMixedSum +=
                        GpuSensorDeterministicGenerator.Mix(
                            id ^ 0x27D4EB2Fu);
                }
            }
            selectedBaseline.SetStableIds(stableIds);

            GpuSensorRangeQuery[] queries =
                BuildQueries(queryCount, this.seed);
            selectedBaseline.SetQueries(queries);
            selectedPacked.SetQueries(queries);

            selectedBaselineDigests = CreateDigestBuffer(
                "GPU Sensor Data Packing Baseline Block Digests");
            selectedPackedDigests = CreateDigestBuffer(
                "GPU Sensor Data Packing Packed Block Digests");
            selectedValidationCommands = new CommandBuffer
            {
                name = "GPU.SensorDataPacking/ValidationOutsideTiming"
            };
            selectedSlots = new CommandSlot[commandSlotCount];
            for (int slot = 0; slot < selectedSlots.Length; slot++)
            {
                selectedSlots[slot] = new CommandSlot(slot);
            }
        }
        catch
        {
            if (selectedSlots != null)
            {
                foreach (CommandSlot slot in selectedSlots)
                {
                    slot?.Dispose();
                }
            }
            selectedValidationCommands?.Dispose();
            selectedBaselineDigests?.Dispose();
            selectedPackedDigests?.Dispose();
            selectedPacked?.Dispose();
            selectedBaseline?.Dispose();
            throw;
        }

        baselinePipeline = selectedBaseline;
        packedPipeline = selectedPacked;
        baselineBlockDigests = selectedBaselineDigests;
        packedBlockDigests = selectedPackedDigests;
        validationCommands = selectedValidationCommands;
        commandSlots = selectedSlots;
        expectedCsrElementCount = checked((uint)elementCount);
        expectedCsrIdXor = selectedExpectedIdXor;
        expectedCsrIdSum = selectedExpectedIdSum;
        expectedCsrIdMixedSum = selectedExpectedIdMixedSum;
    }

    public int ElementCount => elementCount;
    public int QueryCount => queryCount;
    public int BinCount => GpuSensorPipeline.FixedBinCount;
    public int LogicalStateCount => StateCount;
    public int CommandSlotCount => commandSlots.Length;
    public uint Seed => seed;
    public GpuPrimitiveBackend Backend => GpuPrimitiveBackend.WaveOps;
    public bool EmitsProfilerMarkers =>
        baselinePipeline.EmitsProfilerMarkers ||
        packedPipeline.EmitsProfilerMarkers;

    public long BaselinePipelineResidentBytes =>
        baselinePipeline.ResidentBytes;
    public long PackedPipelineResidentBytes =>
        packedPipeline.ResidentBytes;
    public long PerPathBlockDigestBufferBytes =>
        checked((long)StateCount * GpuSensorPipeline.DigestStride);
    public long BlockDigestBufferBytes =>
        checked(PerPathBlockDigestBufferBytes * 2L);
    public long BaselineIsolatedGpuResidentBytes =>
        checked(
            BaselinePipelineResidentBytes +
            PerPathBlockDigestBufferBytes);
    public long PackedIsolatedGpuResidentBytes =>
        checked(
            PackedPipelineResidentBytes +
            PerPathBlockDigestBufferBytes);
    public long ActualBenchmarkGpuResidentBytes =>
        checked(
            BaselinePipelineResidentBytes +
            PackedPipelineResidentBytes +
            BlockDigestBufferBytes);

    public long BaselineProducerLogicalWriteBytesPerFrame =>
        baselinePipeline.GpuProducerLogicalWriteBytes(elementCount);
    public long PackedProducerLogicalWriteBytesPerFrame =>
        packedPipeline.GpuProducerLogicalWriteBytes(elementCount);
    public long BaselinePipelineElementMaterializedWriteBytesPerFrame =>
        checked(
            BaselineProducerLogicalWriteBytesPerFrame +
            (long)elementCount * GpuSensorPipeline.UintStride);
    public long PackedPipelineElementMaterializedWriteBytesPerFrame =>
        packedPipeline.PipelineElementMaterializedWriteBytes(elementCount);
    public long BaselineSpatialBuildAddressedReadBytesPerFrame =>
        packedPipeline.ReferenceSpatialBuildAddressedReadBytes(
            elementCount);
    public long PackedSpatialBuildAddressedReadBytesPerFrame =>
        packedPipeline.SpatialBuildAddressedReadBytes(elementCount);
    public long BaselineCountAtomicOperationsPerFrame => elementCount;
    public long PackedFusedCountAtomicOperationsPerFrame =>
        packedPipeline.FusedCountAtomicOperations(elementCount);
    public long BaselineScatterReservationAtomicOperationsPerFrame =>
        elementCount;
    public long PackedScatterReservationAtomicOperationsPerFrame =>
        packedPipeline.ScatterReservationAtomicOperations(elementCount);
    public long BaselineTotalAtomicOperationsPerFrame =>
        checked(
            BaselineCountAtomicOperationsPerFrame +
            BaselineScatterReservationAtomicOperationsPerFrame);
    public long PackedTotalAtomicOperationsPerFrame =>
        checked(
            PackedFusedCountAtomicOperationsPerFrame +
            PackedScatterReservationAtomicOperationsPerFrame);
    public uint IntensityQuantizationMaxRawError =>
        GpuSensorIntensityQuantizer.MaxRawError;
    public double IntensityQuantizationMaxNormalizedError =>
        GpuSensorIntensityQuantizer.MaxRawError /
        (double)uint.MaxValue;

    public string CaseId(GpuSensorDataPackingBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorDataPackingBenchmarkVariant
                .ExpandedAosQuantizedView:
                return BaselineCaseId;
            case GpuSensorDataPackingBenchmarkVariant
                .PackedSoaFusedEndCursor:
                return PackedCaseId;
            case GpuSensorDataPackingBenchmarkVariant.Control:
                return ControlCaseId;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string VariantName(
        GpuSensorDataPackingBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorDataPackingBenchmarkVariant
                .ExpandedAosQuantizedView:
                return "expanded-aos-quantized-view";
            case GpuSensorDataPackingBenchmarkVariant
                .PackedSoaFusedEndCursor:
                return "packed-soa-fused-end-cursor";
            case GpuSensorDataPackingBenchmarkVariant.Control:
                return "empty-main-graphics-control";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string Marker(GpuSensorDataPackingBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuSensorDataPackingBenchmarkVariant
                .ExpandedAosQuantizedView:
                return BaselineMarker;
            case GpuSensorDataPackingBenchmarkVariant
                .PackedSoaFusedEndCursor:
                return PackedMarker;
            case GpuSensorDataPackingBenchmarkVariant.Control:
                return ControlMarker;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public bool TryAcquireCommandSlot(out int slotIndex)
    {
        ThrowIfDisposed();
        for (int attempt = 0; attempt < commandSlots.Length; attempt++)
        {
            int candidate = (nextSlot + attempt) % commandSlots.Length;
            if (!commandSlots[candidate].TryMakeAvailable())
            {
                continue;
            }

            nextSlot = (candidate + 1) % commandSlots.Length;
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

    public double RecordWorkload(
        int slotIndex,
        GpuSensorDataPackingBenchmarkVariant variant,
        int logicalState)
    {
        CommandSlot slot = GetSlot(slotIndex);
        GpuSensorDeterministicGenerator.ValidateLogicalState(
            checked((uint)logicalState));
        long start = Stopwatch.GetTimestamp();
        switch (variant)
        {
            case GpuSensorDataPackingBenchmarkVariant
                .ExpandedAosQuantizedView:
                baselinePipeline.RecordGpuProducedQuantizedView(
                    slot.Commands,
                    seed,
                    checked((uint)logicalState),
                    elementCount,
                    queryCount);
                break;
            case GpuSensorDataPackingBenchmarkVariant
                .PackedSoaFusedEndCursor:
                packedPipeline.RecordGpuProduced(
                    slot.Commands,
                    seed,
                    checked((uint)logicalState),
                    elementCount,
                    queryCount);
                break;
            case GpuSensorDataPackingBenchmarkVariant.Control:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        return ElapsedMilliseconds(start);
    }

    public GraphicsFence AppendLifetimeFence(int slotIndex)
    {
        CommandSlot slot = GetSlot(slotIndex);
        return slot.Commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
    }

    public void MarkCommandSlotSubmitted(
        int slotIndex,
        GraphicsFence fence)
    {
        GetSlot(slotIndex).MarkSubmitted(fence);
    }

    public bool HasInFlightWork
    {
        get
        {
            foreach (CommandSlot slot in commandSlots)
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
        GpuSensorDataPackingBenchmarkVariant variant,
        int logicalState)
    {
        ThrowIfDisposed();
        if (variant == GpuSensorDataPackingBenchmarkVariant.Control)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        if (pendingCapture != null || pendingComparison != null)
        {
            throw new InvalidOperationException(
                "A validation operation is already pending.");
        }

        uint validatedState = checked((uint)logicalState);
        GpuSensorDeterministicGenerator.ValidateLogicalState(
            validatedState);
        validationCommands.Clear();
        GraphicsBuffer validationBuffer;
        if (variant ==
            GpuSensorDataPackingBenchmarkVariant
                .ExpandedAosQuantizedView)
        {
            if (hasBaselineValidation)
            {
                throw new InvalidOperationException(
                    "The baseline block was already captured.");
            }
            baselinePipeline.RecordValidateKeys(
                validationCommands,
                elementCount);
            validationCommands.CopyBuffer(
                baselinePipeline.FrameDigest,
                baselineBlockDigests);
            validationBuffer =
                baselinePipeline.KeyValidationDiagnostics;
        }
        else
        {
            if (hasPackedValidation)
            {
                throw new InvalidOperationException(
                    "The packed block was already captured.");
            }
            packedPipeline.RecordValidate(
                validationCommands,
                seed,
                validatedState,
                elementCount);
            validationCommands.CopyBuffer(
                packedPipeline.FrameDigest,
                packedBlockDigests);
            validationBuffer = packedPipeline.ValidationDiagnostics;
        }

        Graphics.ExecuteCommandBuffer(validationCommands);
        pendingCapture = new PendingCapture
        {
            Variant = variant,
            Request = AsyncGPUReadback.Request(validationBuffer)
        };
    }

    public bool TryCompleteCapture(out bool passed)
    {
        passed = false;
        if (pendingCapture == null ||
            !pendingCapture.Request.done)
        {
            return false;
        }

        PendingCapture completed = pendingCapture;
        pendingCapture = null;
        if (completed.Variant ==
            GpuSensorDataPackingBenchmarkVariant
                .ExpandedAosQuantizedView)
        {
            baselineValidation = ReadBaselineValidation(
                completed.Request);
            hasBaselineValidation = true;
            passed = BaselineValidationPassed(baselineValidation);
        }
        else
        {
            packedValidation = ReadPackedValidation(
                completed.Request);
            hasPackedValidation = true;
            passed = PackedValidationPassed(packedValidation);
        }

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
        if (!hasBaselineValidation || !hasPackedValidation)
        {
            throw new InvalidOperationException(
                "Both baseline and packed blocks must be captured first.");
        }

        validationCommands.Clear();
        baselinePipeline.RecordClearComparison(validationCommands);
        baselinePipeline.RecordCompareDigests(
            validationCommands,
            baselineBlockDigests,
            packedBlockDigests,
            StateCount);
        Graphics.ExecuteCommandBuffer(validationCommands);
        pendingComparison = new PendingComparison
        {
            Phase = phase,
            Request = AsyncGPUReadback.Request(
                baselinePipeline.ComparisonDigest),
            Baseline = baselineValidation,
            Packed = packedValidation
        };
        hasBaselineValidation = false;
        hasPackedValidation = false;
    }

    public bool TryCompleteComparison(
        out GpuSensorDataPackingValidationResult result)
    {
        result = null;
        if (pendingComparison == null ||
            !pendingComparison.Request.done)
        {
            return false;
        }

        PendingComparison completed = pendingComparison;
        pendingComparison = null;
        result = new GpuSensorDataPackingValidationResult
        {
            Phase = completed.Phase,
            StateCount = StateCount,
            ReadbackBytes = ValidationReadbackBytesPerComparison,
            BaselineInvalidKeyCount =
                completed.Baseline.InvalidKeyCount,
            BaselineInvalidKeyHash =
                completed.Baseline.InvalidKeyHash,
            PackedSampleMismatchCount =
                completed.Packed.SampleMismatchCount,
            PackedSampleMismatchHash =
                completed.Packed.SampleMismatchHash,
            PackedCsrMismatchCount =
                completed.Packed.CsrMismatchCount,
            PackedCsrMismatchHash =
                completed.Packed.CsrMismatchHash,
            PackedCsrElementCount =
                completed.Packed.CsrElementCount,
            PackedCsrIdXor = completed.Packed.CsrIdXor,
            PackedCsrIdSum = completed.Packed.CsrIdSum,
            PackedCsrIdMixedSum =
                completed.Packed.CsrIdMixedSum,
            ExpectedPackedCsrElementCount =
                expectedCsrElementCount,
            ExpectedPackedCsrIdXor = expectedCsrIdXor,
            ExpectedPackedCsrIdSum = expectedCsrIdSum,
            ExpectedPackedCsrIdMixedSum =
                expectedCsrIdMixedSum
        };

        if (completed.Request.hasError ||
            completed.Baseline.ReadbackFailed ||
            completed.Packed.ReadbackFailed)
        {
            result.Passed = false;
            result.Message = "Validation readback failed.";
            result.ResultHash = "unavailable";
            return true;
        }

        NativeArray<uint> words =
            completed.Request.GetData<uint>();
        if (words.Length <
            GpuSensorPipeline.DigestStride / sizeof(uint))
        {
            result.Passed = false;
            result.Message =
                "Comparison digest readback had an invalid length.";
            result.ResultHash = "unavailable";
            return true;
        }

        result.DigestMismatchCount = words[0];
        result.DigestDeltaXor = words[1];
        result.DigestDeltaSum0 = words[2];
        result.DigestDeltaSum1 = words[3];
        result.Passed =
            result.DigestMismatchCount == 0u &&
            BaselineValidationPassed(completed.Baseline) &&
            PackedValidationPassed(completed.Packed);
        result.Message = result.Passed
            ? "All 64 aggregate frame digests match; baseline keys, " +
              "packed samples, and packed CSR layout, membership, and " +
              "aggregate ID invariants match deterministic oracles."
            : "Digest mismatch, invalid key, packed sample mismatch, " +
              "or packed CSR invariant mismatch detected.";
        result.ResultHash = FormatResultHash(result);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (CommandSlot slot in commandSlots)
        {
            slot.Dispose();
        }
        validationCommands.Dispose();
        baselineBlockDigests.Dispose();
        packedBlockDigests.Dispose();
        packedPipeline.Dispose();
        baselinePipeline.Dispose();
    }

    private CommandSlot GetSlot(int slotIndex)
    {
        ThrowIfDisposed();
        if (slotIndex < 0 || slotIndex >= commandSlots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        return commandSlots[slotIndex];
    }

    private BaselineValidation ReadBaselineValidation(
        AsyncGPUReadbackRequest request)
    {
        BaselineValidation result = new BaselineValidation
        {
            ReadbackFailed = request.hasError
        };
        if (result.ReadbackFailed)
        {
            return result;
        }

        NativeArray<uint> words = request.GetData<uint>();
        if (words.Length <
            GpuSensorPipeline.KeyValidationWordCount)
        {
            result.ReadbackFailed = true;
            return result;
        }

        result.InvalidKeyCount = words[0];
        result.InvalidKeyHash = words[1];
        return result;
    }

    private PackedValidation ReadPackedValidation(
        AsyncGPUReadbackRequest request)
    {
        PackedValidation result = new PackedValidation
        {
            ReadbackFailed = request.hasError
        };
        if (result.ReadbackFailed)
        {
            return result;
        }

        NativeArray<uint> words = request.GetData<uint>();
        if (words.Length <
            GpuSensorPackedSoaPipeline.ValidationWordCount)
        {
            result.ReadbackFailed = true;
            return result;
        }

        result.SampleMismatchCount =
            words[GpuSensorPackedSoaPipeline
                .PackedSampleMismatchCountWord];
        result.SampleMismatchHash =
            words[GpuSensorPackedSoaPipeline
                .PackedSampleMismatchHashWord];
        result.CsrMismatchCount =
            words[GpuSensorPackedSoaPipeline.CsrMismatchCountWord];
        result.CsrMismatchHash =
            words[GpuSensorPackedSoaPipeline.CsrMismatchHashWord];
        result.CsrElementCount =
            words[GpuSensorPackedSoaPipeline.CsrElementCountWord];
        result.CsrIdXor =
            words[GpuSensorPackedSoaPipeline.CsrIdXorWord];
        result.CsrIdSum =
            words[GpuSensorPackedSoaPipeline.CsrIdSumWord];
        result.CsrIdMixedSum =
            words[GpuSensorPackedSoaPipeline.CsrIdMixedSumWord];
        return result;
    }

    private bool BaselineValidationPassed(
        BaselineValidation validation)
    {
        return !validation.ReadbackFailed &&
            validation.InvalidKeyCount == 0u;
    }

    private bool PackedValidationPassed(PackedValidation validation)
    {
        return !validation.ReadbackFailed &&
            validation.SampleMismatchCount == 0u &&
            validation.CsrMismatchCount == 0u &&
            validation.CsrElementCount == expectedCsrElementCount &&
            validation.CsrIdXor == expectedCsrIdXor &&
            validation.CsrIdSum == expectedCsrIdSum &&
            validation.CsrIdMixedSum == expectedCsrIdMixedSum;
    }

    private static string FormatResultHash(
        GpuSensorDataPackingValidationResult result)
    {
        return string.Format(
            "{0:X8}-{1:X8}-{2:X8}-{3:X8}-" +
            "{4:X8}-{5:X8}-{6:X8}-{7:X8}-" +
            "{8:X8}-{9:X8}-{10:X8}-{11:X8}-" +
            "{12:X8}-{13:X8}",
            result.DigestMismatchCount,
            result.DigestDeltaXor,
            result.DigestDeltaSum0,
            result.DigestDeltaSum1,
            result.BaselineInvalidKeyCount,
            result.BaselineInvalidKeyHash,
            result.PackedSampleMismatchCount,
            result.PackedSampleMismatchHash,
            result.PackedCsrMismatchCount,
            result.PackedCsrMismatchHash,
            result.PackedCsrElementCount,
            result.PackedCsrIdXor,
            result.PackedCsrIdSum,
            result.PackedCsrIdMixedSum);
    }

    private static GpuSensorRangeQuery[] BuildQueries(
        int count,
        uint seed)
    {
        GpuSensorRangeQuery[] result =
            new GpuSensorRangeQuery[count];
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
            result[index] =
                new GpuSensorRangeQuery(x, y, z, 2047u);
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

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuSensorDataPackingBenchmarkAdapter));
        }
    }

    private sealed class CommandSlot : IDisposable
    {
        private bool submitted;
        private GraphicsFence fence;

        public CommandSlot(int slotIndex)
        {
            Commands = new CommandBuffer
            {
                name =
                    "GPU.SensorDataPacking/DynamicSlot/" + slotIndex
            };
        }

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
                    "A command slot cannot be reused before its fence " +
                    "passes.");
            }

            fence = submittedFence;
            submitted = true;
        }

        public void Dispose()
        {
            Commands.Dispose();
        }
    }

    private struct BaselineValidation
    {
        public uint InvalidKeyCount;
        public uint InvalidKeyHash;
        public bool ReadbackFailed;
    }

    private struct PackedValidation
    {
        public uint SampleMismatchCount;
        public uint SampleMismatchHash;
        public uint CsrMismatchCount;
        public uint CsrMismatchHash;
        public uint CsrElementCount;
        public uint CsrIdXor;
        public uint CsrIdSum;
        public uint CsrIdMixedSum;
        public bool ReadbackFailed;
    }

    private sealed class PendingCapture
    {
        public GpuSensorDataPackingBenchmarkVariant Variant;
        public AsyncGPUReadbackRequest Request;
    }

    private sealed class PendingComparison
    {
        public string Phase;
        public AsyncGPUReadbackRequest Request;
        public BaselineValidation Baseline;
        public PackedValidation Packed;
    }
}
