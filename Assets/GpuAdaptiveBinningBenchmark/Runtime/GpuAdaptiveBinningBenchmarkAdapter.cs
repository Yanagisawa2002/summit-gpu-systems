using System;
using System.Collections.Generic;
using Summit.GpuAdaptiveBinning;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuAdaptiveBinningValidationResult
{
    public string Phase;
    public string CaseId;
    public string Variant;
    public bool Passed;
    public string Message;
    public long ReadbackBytes;
    public string ResultHash;
    public int ValidCount;
    public uint InvalidKeyCount;
    public uint DiagnosticFlags;
}

internal sealed class GpuAdaptiveBinningBenchmarkAdapter : IDisposable
{
    public const string RadixCaseId =
        "spatial-binning/radix-low-bit-wave-ops";
    public const string DirectCaseId =
        "spatial-binning/direct-trusted-count-scan-scatter-wave-ops";
    public const string RadixMarker =
        "GPU.AdaptiveBinning/RadixLowBits/WaveOps";
    public const string DirectMarker =
        "GPU.AdaptiveBinning/DirectTrustedCountScanScatter/WaveOps";
    public const string PrimitiveBackendName = "wave-ops";
    public const string KeyDomainName = "guaranteed-in-range";
    public const string OrderingContractName = "unspecified-within-bin";
    public const string DirectStageContract =
        "clear;trusted-count;exclusive-scan;prepare;trusted-scatter";
    public const string RadixStageContract =
        "clear;low-bit-radix-sort;range-extract;exclusive-scan;terminal-offset";
    public const string DirectValidationContract =
        "guaranteed-in-range-no-per-element-validation";
    public const bool InnerProfilerMarkersEnabled = false;

    private const GpuPrimitiveBackend ForcedPrimitiveBackend =
        GpuPrimitiveBackend.WaveOps;
    private const GpuAdaptiveBinningKeyDomain ForcedKeyDomain =
        GpuAdaptiveBinningKeyDomain.GuaranteedInRange;

    private readonly int elementCount;
    private readonly int binCount;
    private readonly int dispatchesPerFrame;
    private readonly GpuDirectSpatialBinner directBinner;
    private readonly GpuRadixSpatialBinner radixBinner;
    private readonly GraphicsBuffer keys;
    private readonly GraphicsBuffer values;
    private readonly GraphicsBuffer binCounts;
    private readonly GraphicsBuffer binOffsets;
    private readonly GraphicsBuffer binnedValues;
    private readonly GraphicsBuffer diagnostics;
    private readonly GpuAdaptiveBinningCpuOracle oracle;
    private PendingValidation pendingValidation;
    private bool disposed;

    public GpuAdaptiveBinningBenchmarkAdapter(
        int elementCount,
        int binCount,
        string distribution,
        int seed,
        int dispatchesPerFrame)
    {
        if (elementCount < 1 ||
            elementCount > GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }
        if (binCount < 1 ||
            binCount > GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "Bin count must be in the supported positive range.");
        }
        if (dispatchesPerFrame < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchesPerFrame));
        }
        if (!GpuPrimitives.SupportsWaveOperations)
        {
            throw new PlatformNotSupportedException(
                "The forced Direct-vs-Radix benchmark requires the WaveOps " +
                "GPU primitive backend.");
        }

        this.elementCount = elementCount;
        this.binCount = binCount;
        this.dispatchesPerFrame = dispatchesPerFrame;

        uint[] keyData = new uint[elementCount];
        uint[] valueData = new uint[elementCount];
        GpuAdaptiveBinningInputGenerator.Populate(
            keyData,
            valueData,
            binCount,
            distribution,
            seed);
        oracle = new GpuAdaptiveBinningCpuOracle(
            keyData,
            valueData,
            binCount);
        if (oracle.InvalidKeyCount != 0)
        {
            throw new InvalidOperationException(
                "Performance benchmark distributions must contain only valid keys.");
        }

        keys = CreateBuffer(elementCount, "GPU Adaptive Binning Benchmark Keys");
        values = CreateBuffer(elementCount, "GPU Adaptive Binning Benchmark Values");
        binCounts = CreateBuffer(binCount, "GPU Adaptive Binning Benchmark Counts");
        binOffsets = CreateBuffer(
            binCount + 1,
            "GPU Adaptive Binning Benchmark Offsets");
        binnedValues = CreateBuffer(
            elementCount,
            "GPU Adaptive Binning Benchmark Output Values");
        diagnostics = CreateBuffer(2, "GPU Adaptive Binning Benchmark Diagnostics");
        keys.SetData(keyData);
        values.SetData(valueData);

        directBinner = new GpuDirectSpatialBinner(
            elementCount,
            binCount,
            emitProfilerMarkers: InnerProfilerMarkersEnabled);
        radixBinner = new GpuRadixSpatialBinner(
            elementCount,
            binCount,
            emitProfilerMarkers: InnerProfilerMarkersEnabled);
    }

    public int ElementCount => elementCount;

    public int BinCount => binCount;

    public int DispatchesPerFrame => dispatchesPerFrame;

    public string ExpectedResultHash => oracle.ResultHash;

    public int RequiredKeyBitCount =>
        GpuRadixSpatialBinner.GetRequiredKeyBitCount(binCount);

    public int RadixPassCount =>
        (RequiredKeyBitCount + GpuPrimitives.RadixBitsPerPass - 1) /
        GpuPrimitives.RadixBitsPerPass;

    public long LogicalProblemBytesPerDispatch =>
        checked((long)elementCount * sizeof(uint) * 3L +
            (long)binCount * sizeof(uint) * 2L +
            sizeof(uint) * 3L);

    public long SharedInputBytes =>
        checked((long)elementCount * sizeof(uint) * 2L);

    public long SharedOutputBytes =>
        checked((long)elementCount * sizeof(uint) +
            (long)binCount * sizeof(uint) * 2L +
            sizeof(uint) * 3L);

    public long DirectPrimitiveScratchBytes =>
        directBinner.PrimitiveScratchBytes;

    public long RadixPrimitiveScratchBytes =>
        radixBinner.PrimitiveScratchBytes;

    /// <summary>
    /// Actual union of the independently resident primitive allocations.
    /// Use CasePrimitiveScratchBytes for per-variant reporting.
    /// </summary>
    public long PrimitiveScratchBytes =>
        checked(DirectPrimitiveScratchBytes + RadixPrimitiveScratchBytes);

    public long DirectInternalScratchBytes =>
        directBinner.InternalScratchBytes;

    public long RadixInternalScratchBytes =>
        radixBinner.InternalScratchBytes;

    public long DirectCaseScratchBytes =>
        directBinner.ScratchBytes;

    public long RadixCaseScratchBytes =>
        radixBinner.ScratchBytes;

    public long DirectCaseResidentBytes =>
        SharedInputBytes + SharedOutputBytes + DirectCaseScratchBytes;

    public long RadixCaseResidentBytes =>
        SharedInputBytes + SharedOutputBytes + RadixCaseScratchBytes;

    public long ActualBenchmarkBufferResidentBytes =>
        SharedInputBytes +
        SharedOutputBytes +
        DirectCaseScratchBytes +
        RadixCaseScratchBytes;

    public string CaseId(GpuAdaptiveBinningBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuAdaptiveBinningBenchmarkVariant.Radix:
                return RadixCaseId;
            case GpuAdaptiveBinningBenchmarkVariant.Direct:
                return DirectCaseId;
            case GpuAdaptiveBinningBenchmarkVariant.Control:
                return "control/empty-command-buffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string VariantName(GpuAdaptiveBinningBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuAdaptiveBinningBenchmarkVariant.Radix:
                return "radix-low-bit-wave-ops";
            case GpuAdaptiveBinningBenchmarkVariant.Direct:
                return "direct-trusted-count-scan-scatter-wave-ops";
            case GpuAdaptiveBinningBenchmarkVariant.Control:
                return "empty-command-buffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string Marker(GpuAdaptiveBinningBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuAdaptiveBinningBenchmarkVariant.Radix:
                return RadixMarker;
            case GpuAdaptiveBinningBenchmarkVariant.Direct:
                return DirectMarker;
            case GpuAdaptiveBinningBenchmarkVariant.Control:
                return "GPU.AdaptiveBinning/Control/EmptyCommandBuffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public long CaseScratchBytes(GpuAdaptiveBinningBenchmarkVariant variant)
    {
        return variant == GpuAdaptiveBinningBenchmarkVariant.Direct
            ? DirectCaseScratchBytes
            : variant == GpuAdaptiveBinningBenchmarkVariant.Radix
                ? RadixCaseScratchBytes
                : 0L;
    }

    public long CasePrimitiveScratchBytes(
        GpuAdaptiveBinningBenchmarkVariant variant)
    {
        return variant == GpuAdaptiveBinningBenchmarkVariant.Direct
            ? DirectPrimitiveScratchBytes
            : variant == GpuAdaptiveBinningBenchmarkVariant.Radix
                ? RadixPrimitiveScratchBytes
                : 0L;
    }

    public long CaseResidentBytes(GpuAdaptiveBinningBenchmarkVariant variant)
    {
        return variant == GpuAdaptiveBinningBenchmarkVariant.Direct
            ? DirectCaseResidentBytes
            : variant == GpuAdaptiveBinningBenchmarkVariant.Radix
                ? RadixCaseResidentBytes
                : 0L;
    }

    public CommandBuffer CreateMeasurementCommandBuffer(
        GpuAdaptiveBinningBenchmarkVariant variant)
    {
        CommandBuffer commands = new CommandBuffer
        {
            name = Marker(variant)
        };
        if (variant == GpuAdaptiveBinningBenchmarkVariant.Control)
        {
            return commands;
        }
        commands.BeginSample(Marker(variant));
        for (int dispatch = 0; dispatch < dispatchesPerFrame; dispatch++)
        {
            RecordOnce(commands, variant);
        }
        commands.EndSample(Marker(variant));
        return commands;
    }

    public void RecordMeasurement(
        CommandBuffer commands,
        GpuAdaptiveBinningBenchmarkVariant variant)
    {
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        if (variant == GpuAdaptiveBinningBenchmarkVariant.Control)
        {
            return;
        }
        commands.BeginSample(Marker(variant));
        for (int dispatch = 0; dispatch < dispatchesPerFrame; dispatch++)
        {
            RecordOnce(commands, variant);
        }
        commands.EndSample(Marker(variant));
    }

    public void BeginValidation(
        GpuAdaptiveBinningBenchmarkVariant variant,
        string phase)
    {
        ThrowIfDisposed();
        if (variant == GpuAdaptiveBinningBenchmarkVariant.Control)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        if (pendingValidation != null)
        {
            throw new InvalidOperationException(
                "An adaptive-binning validation readback is already pending.");
        }

        CommandBuffer commands = new CommandBuffer
        {
            name = Marker(variant) + "/Validation"
        };
        commands.BeginSample(Marker(variant) + "/Validation");
        RecordOnce(commands, variant);
        commands.EndSample(Marker(variant) + "/Validation");
        Graphics.ExecuteCommandBuffer(commands);
        commands.Dispose();

        pendingValidation = new PendingValidation
        {
            Variant = variant,
            Phase = phase,
            Requests = new List<AsyncGPUReadbackRequest>
            {
                AsyncGPUReadback.Request(binCounts),
                AsyncGPUReadback.Request(binOffsets),
                AsyncGPUReadback.Request(binnedValues),
                AsyncGPUReadback.Request(diagnostics)
            },
            ReadbackBytes =
                checked(((long)elementCount + (long)binCount * 2L + 3L) *
                    sizeof(uint))
        };
    }

    public bool TryCompleteValidation(
        out GpuAdaptiveBinningValidationResult result)
    {
        result = null;
        if (pendingValidation == null)
        {
            return false;
        }
        foreach (AsyncGPUReadbackRequest request in pendingValidation.Requests)
        {
            if (!request.done)
            {
                return false;
            }
        }

        PendingValidation completed = pendingValidation;
        pendingValidation = null;
        result = new GpuAdaptiveBinningValidationResult
        {
            Phase = completed.Phase,
            CaseId = CaseId(completed.Variant),
            Variant = VariantName(completed.Variant),
            ReadbackBytes = completed.ReadbackBytes,
            ValidCount = oracle.ValidCount
        };
        foreach (AsyncGPUReadbackRequest request in completed.Requests)
        {
            if (request.hasError)
            {
                result.Passed = false;
                result.Message = "Async GPU readback failed.";
                result.ResultHash = "unavailable";
                return true;
            }
        }

        uint[] actualCounts = ToArray(
            completed.Requests[0].GetData<uint>());
        uint[] actualOffsets = ToArray(
            completed.Requests[1].GetData<uint>());
        uint[] actualValues = ToArray(
            completed.Requests[2].GetData<uint>());
        uint[] actualDiagnostics = ToArray(
            completed.Requests[3].GetData<uint>());
        result.InvalidKeyCount = actualDiagnostics[0];
        result.DiagnosticFlags = actualDiagnostics[1];
        result.Passed = oracle.Validate(
            actualCounts,
            actualOffsets,
            actualValues,
            actualDiagnostics,
            out string message,
            out string resultHash);
        result.Message = message;
        result.ResultHash = resultHash;
        return true;
    }

    /// <summary>
    /// Drops ownership of a timed-out validation batch. AsyncGPUReadback has
    /// no cancellation API, so queued requests finish unobserved. A caller
    /// that uses this method must invalidate the run's evidence.
    /// </summary>
    public long AbandonPendingValidation()
    {
        if (pendingValidation == null)
        {
            return 0L;
        }

        long readbackBytes = pendingValidation.ReadbackBytes;
        pendingValidation = null;
        return readbackBytes;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        directBinner.Dispose();
        radixBinner.Dispose();
        keys.Dispose();
        values.Dispose();
        binCounts.Dispose();
        binOffsets.Dispose();
        binnedValues.Dispose();
        diagnostics.Dispose();
    }

    private void RecordOnce(
        CommandBuffer commands,
        GpuAdaptiveBinningBenchmarkVariant variant)
    {
        if (variant == GpuAdaptiveBinningBenchmarkVariant.Radix)
        {
            radixBinner.Record(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                ForcedKeyDomain,
                ForcedPrimitiveBackend);
            return;
        }
        if (variant == GpuAdaptiveBinningBenchmarkVariant.Direct)
        {
            directBinner.RecordGuaranteedInRange(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                ForcedPrimitiveBackend);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(variant));
    }

    private static GraphicsBuffer CreateBuffer(int count, string name)
    {
        return new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            count,
            sizeof(uint))
        {
            name = name
        };
    }

    private static uint[] ToArray(NativeArray<uint> data)
    {
        uint[] result = new uint[data.Length];
        data.CopyTo(result);
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuAdaptiveBinningBenchmarkAdapter));
        }
    }

    private sealed class PendingValidation
    {
        public GpuAdaptiveBinningBenchmarkVariant Variant;
        public string Phase;
        public List<AsyncGPUReadbackRequest> Requests;
        public long ReadbackBytes;
    }
}
