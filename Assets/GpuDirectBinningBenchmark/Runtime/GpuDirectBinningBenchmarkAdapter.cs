using System;
using System.Collections.Generic;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuDirectBinningValidationResult
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

internal sealed class GpuDirectBinningBenchmarkAdapter : IDisposable
{
    public const string ReferenceCaseId =
        "spatial-binning/reference-compose-portable";
    public const string DirectCaseId =
        "spatial-binning/direct-count-scan-scatter-portable";
    public const string ReferenceMarker =
        "GPU.DirectBinning/ReferenceCompose/Portable";
    public const string DirectMarker =
        "GPU.DirectBinning/DirectCountScanScatter/Portable";

    private readonly int elementCount;
    private readonly int binCount;
    private readonly int dispatchesPerFrame;
    private readonly GpuPrimitives primitives;
    private readonly GpuDirectSpatialBinner directBinner;
    private readonly GpuDirectBinningReferencePipeline reference;
    private readonly GraphicsBuffer keys;
    private readonly GraphicsBuffer values;
    private readonly GraphicsBuffer binCounts;
    private readonly GraphicsBuffer binOffsets;
    private readonly GraphicsBuffer binnedValues;
    private readonly GraphicsBuffer diagnostics;
    private readonly GpuDirectBinningCpuOracle oracle;
    private PendingValidation pendingValidation;
    private bool disposed;

    public GpuDirectBinningBenchmarkAdapter(
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
            binCount > GpuPrimitives.MaxElementCount ||
            (binCount & (binCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "Bin count must be a supported power of two.");
        }
        if (dispatchesPerFrame < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchesPerFrame));
        }

        this.elementCount = elementCount;
        this.binCount = binCount;
        this.dispatchesPerFrame = dispatchesPerFrame;

        uint[] keyData = new uint[elementCount];
        uint[] valueData = new uint[elementCount];
        GpuDirectBinningInputGenerator.Populate(
            keyData,
            valueData,
            binCount,
            distribution,
            seed);
        oracle = new GpuDirectBinningCpuOracle(
            keyData,
            valueData,
            binCount);
        if (oracle.InvalidKeyCount != 0)
        {
            throw new InvalidOperationException(
                "Performance benchmark distributions must contain only valid keys.");
        }

        keys = CreateBuffer(elementCount, "GPU Direct Binning Benchmark Keys");
        values = CreateBuffer(elementCount, "GPU Direct Binning Benchmark Values");
        binCounts = CreateBuffer(binCount, "GPU Direct Binning Benchmark Counts");
        binOffsets = CreateBuffer(
            binCount + 1,
            "GPU Direct Binning Benchmark Offsets");
        binnedValues = CreateBuffer(
            elementCount,
            "GPU Direct Binning Benchmark Output Values");
        diagnostics = CreateBuffer(2, "GPU Direct Binning Benchmark Diagnostics");
        keys.SetData(keyData);
        values.SetData(valueData);

        primitives = new GpuPrimitives(Math.Max(elementCount, binCount));
        directBinner = new GpuDirectSpatialBinner(
            elementCount,
            binCount,
            primitives);
        reference = new GpuDirectBinningReferencePipeline(
            elementCount,
            binCount,
            primitives);
    }

    public int ElementCount => elementCount;

    public int BinCount => binCount;

    public int DispatchesPerFrame => dispatchesPerFrame;

    public string ExpectedResultHash => oracle.ResultHash;

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

    public long PrimitiveScratchBytes => primitives.ScratchBytes;

    public long DirectInternalScratchBytes =>
        directBinner.InternalScratchBytes;

    public long ReferenceInternalScratchBytes =>
        reference.InternalScratchBytes;

    public long DirectCaseScratchBytes =>
        PrimitiveScratchBytes + DirectInternalScratchBytes;

    public long ReferenceCaseScratchBytes =>
        PrimitiveScratchBytes + ReferenceInternalScratchBytes;

    public long DirectCaseResidentBytes =>
        SharedInputBytes + SharedOutputBytes + DirectCaseScratchBytes;

    public long ReferenceCaseResidentBytes =>
        SharedInputBytes + SharedOutputBytes + ReferenceCaseScratchBytes;

    public long ActualBenchmarkBufferResidentBytes =>
        SharedInputBytes +
        SharedOutputBytes +
        PrimitiveScratchBytes +
        DirectInternalScratchBytes +
        ReferenceInternalScratchBytes;

    public string CaseId(GpuDirectBinningBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDirectBinningBenchmarkVariant.Reference:
                return ReferenceCaseId;
            case GpuDirectBinningBenchmarkVariant.Direct:
                return DirectCaseId;
            case GpuDirectBinningBenchmarkVariant.Control:
                return "control/empty-command-buffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string VariantName(GpuDirectBinningBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDirectBinningBenchmarkVariant.Reference:
                return "reference-compose-portable";
            case GpuDirectBinningBenchmarkVariant.Direct:
                return "direct-count-scan-scatter-portable";
            case GpuDirectBinningBenchmarkVariant.Control:
                return "empty-command-buffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string Marker(GpuDirectBinningBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDirectBinningBenchmarkVariant.Reference:
                return ReferenceMarker;
            case GpuDirectBinningBenchmarkVariant.Direct:
                return DirectMarker;
            case GpuDirectBinningBenchmarkVariant.Control:
                return "GPU.DirectBinning/Control/EmptyCommandBuffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public long CaseScratchBytes(GpuDirectBinningBenchmarkVariant variant)
    {
        return variant == GpuDirectBinningBenchmarkVariant.Direct
            ? DirectCaseScratchBytes
            : variant == GpuDirectBinningBenchmarkVariant.Reference
                ? ReferenceCaseScratchBytes
                : 0L;
    }

    public long CaseResidentBytes(GpuDirectBinningBenchmarkVariant variant)
    {
        return variant == GpuDirectBinningBenchmarkVariant.Direct
            ? DirectCaseResidentBytes
            : variant == GpuDirectBinningBenchmarkVariant.Reference
                ? ReferenceCaseResidentBytes
                : 0L;
    }

    public CommandBuffer CreateMeasurementCommandBuffer(
        GpuDirectBinningBenchmarkVariant variant)
    {
        CommandBuffer commands = new CommandBuffer
        {
            name = Marker(variant)
        };
        if (variant == GpuDirectBinningBenchmarkVariant.Control)
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
        GpuDirectBinningBenchmarkVariant variant)
    {
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        if (variant == GpuDirectBinningBenchmarkVariant.Control)
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
        GpuDirectBinningBenchmarkVariant variant,
        string phase)
    {
        ThrowIfDisposed();
        if (variant == GpuDirectBinningBenchmarkVariant.Control)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        if (pendingValidation != null)
        {
            throw new InvalidOperationException(
                "A direct-binning validation readback is already pending.");
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
        out GpuDirectBinningValidationResult result)
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
        result = new GpuDirectBinningValidationResult
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        directBinner.Dispose();
        reference.Dispose();
        primitives.Dispose();
        keys.Dispose();
        values.Dispose();
        binCounts.Dispose();
        binOffsets.Dispose();
        binnedValues.Dispose();
        diagnostics.Dispose();
    }

    private void RecordOnce(
        CommandBuffer commands,
        GpuDirectBinningBenchmarkVariant variant)
    {
        if (variant == GpuDirectBinningBenchmarkVariant.Reference)
        {
            reference.Record(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount);
            return;
        }
        if (variant == GpuDirectBinningBenchmarkVariant.Direct)
        {
            directBinner.Record(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                GpuPrimitiveBackend.Portable);
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
                nameof(GpuDirectBinningBenchmarkAdapter));
        }
    }

    private sealed class PendingValidation
    {
        public GpuDirectBinningBenchmarkVariant Variant;
        public string Phase;
        public List<AsyncGPUReadbackRequest> Requests;
        public long ReadbackBytes;
    }
}
