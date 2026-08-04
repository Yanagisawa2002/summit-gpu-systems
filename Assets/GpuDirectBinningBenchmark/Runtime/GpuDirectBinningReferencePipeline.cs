using System;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Transparent reference composition used only by the benchmark. It deliberately combines the
/// reusable histogram and scan primitives with benchmark-owned prepare/scatter kernels.
/// Formal performance inputs contain only valid keys because RecordHistogram masks key bits.
/// </summary>
internal sealed class GpuDirectBinningReferencePipeline : IDisposable
{
    private const int ThreadGroupSize = 256;
    private readonly GpuPrimitives primitives;
    private readonly ComputeShader shader;
    private readonly GraphicsBuffer writeHeads;
    private readonly int clearDiagnosticsKernel;
    private readonly int prepareKernel;
    private readonly int scatterKernel;
    private readonly int elementCapacity;
    private readonly int binCapacity;
    private bool disposed;

    private static readonly int ElementCountId =
        Shader.PropertyToID("_ElementCount");
    private static readonly int BinCountId = Shader.PropertyToID("_BinCount");
    private static readonly int KeysId = Shader.PropertyToID("_Keys");
    private static readonly int ValuesId = Shader.PropertyToID("_Values");
    private static readonly int BinCountsId = Shader.PropertyToID("_BinCounts");
    private static readonly int BinOffsetsId = Shader.PropertyToID("_BinOffsets");
    private static readonly int WriteHeadsId = Shader.PropertyToID("_WriteHeads");
    private static readonly int BinnedValuesId =
        Shader.PropertyToID("_BinnedValues");
    private static readonly int DiagnosticsId =
        Shader.PropertyToID("_Diagnostics");

    public GpuDirectBinningReferencePipeline(
        int elementCapacity,
        int binCapacity,
        GpuPrimitives primitives,
        ComputeShader shader = null)
    {
        if (elementCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCapacity));
        }
        if (binCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(binCapacity));
        }
        this.primitives = primitives ??
            throw new ArgumentNullException(nameof(primitives));
        this.elementCapacity = elementCapacity;
        this.binCapacity = binCapacity;
        this.shader = shader != null
            ? shader
            : Resources.Load<ComputeShader>(
                "GpuDirectBinningBenchmarkReference");
        if (this.shader == null)
        {
            throw new InvalidOperationException(
                "GpuDirectBinningBenchmarkReference compute resource is missing.");
        }

        clearDiagnosticsKernel =
            this.shader.FindKernel("ClearDiagnostics");
        prepareKernel = this.shader.FindKernel("PrepareHeadsAndTotal");
        scatterKernel = this.shader.FindKernel("ScatterPortable");
        writeHeads = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            binCapacity,
            sizeof(uint))
        {
            name = "GPU Direct Binning Benchmark Reference Write Heads"
        };
    }

    public long InternalScratchBytes => (long)binCapacity * sizeof(uint);

    public long PrimitiveScratchBytes => primitives.ScratchBytes;

    public long ScratchBytes => InternalScratchBytes + PrimitiveScratchBytes;

    public void Record(
        CommandBuffer commands,
        GraphicsBuffer keys,
        GraphicsBuffer values,
        GraphicsBuffer binCounts,
        GraphicsBuffer binOffsets,
        GraphicsBuffer binnedValues,
        GraphicsBuffer diagnostics,
        int elementCount,
        int binCount)
    {
        ThrowIfDisposed();
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        if (elementCount < 1 || elementCount > elementCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }
        if (binCount < 1 ||
            binCount > binCapacity ||
            (binCount & (binCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "Reference bin count must be a power of two within capacity.");
        }

        commands.BeginSample("GPU.DirectBinning/ReferenceCompose");
        primitives.RecordHistogram(
            commands,
            keys,
            binCounts,
            elementCount,
            binCount,
            0,
            GpuPrimitiveBackend.Portable,
            true);
        primitives.RecordExclusiveScan(
            commands,
            binCounts,
            binOffsets,
            binCount,
            GpuPrimitiveBackend.Portable);

        commands.SetComputeBufferParam(
            shader,
            clearDiagnosticsKernel,
            DiagnosticsId,
            diagnostics);
        commands.DispatchCompute(
            shader,
            clearDiagnosticsKernel,
            1,
            1,
            1);

        commands.SetComputeIntParam(shader, BinCountId, binCount);
        commands.SetComputeBufferParam(
            shader,
            prepareKernel,
            BinCountsId,
            binCounts);
        commands.SetComputeBufferParam(
            shader,
            prepareKernel,
            BinOffsetsId,
            binOffsets);
        commands.SetComputeBufferParam(
            shader,
            prepareKernel,
            WriteHeadsId,
            writeHeads);
        commands.DispatchCompute(
            shader,
            prepareKernel,
            DivideRoundUp(binCount, ThreadGroupSize),
            1,
            1);

        commands.SetComputeIntParam(shader, ElementCountId, elementCount);
        commands.SetComputeIntParam(shader, BinCountId, binCount);
        commands.SetComputeBufferParam(shader, scatterKernel, KeysId, keys);
        commands.SetComputeBufferParam(shader, scatterKernel, ValuesId, values);
        commands.SetComputeBufferParam(
            shader,
            scatterKernel,
            WriteHeadsId,
            writeHeads);
        commands.SetComputeBufferParam(
            shader,
            scatterKernel,
            BinnedValuesId,
            binnedValues);
        commands.SetComputeBufferParam(
            shader,
            scatterKernel,
            DiagnosticsId,
            diagnostics);
        commands.DispatchCompute(
            shader,
            scatterKernel,
            DivideRoundUp(elementCount, ThreadGroupSize),
            1,
            1);
        commands.EndSample("GPU.DirectBinning/ReferenceCompose");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        writeHeads.Dispose();
    }

    private static int DivideRoundUp(int value, int divisor)
    {
        return checked((value + divisor - 1) / divisor);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDirectBinningReferencePipeline));
        }
    }
}
