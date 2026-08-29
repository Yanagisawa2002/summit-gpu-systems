using System;
using System.Collections.Generic;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuDrivenInstanceValidationResult
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

internal sealed class GpuDrivenInstanceBenchmarkAdapter : IDisposable
{
    public const int FixedDrawGroupCount = 8;
    public const string CulledTailCaseId =
        "gpu-driven-instances/culled-tail-portable";
    public const string VisibleOnlyCaseId =
        "gpu-driven-instances/visible-only-discard-key-portable";
    public const string CulledTailMarker =
        "GPU.DrivenInstance/CulledTail/Portable";
    public const string VisibleOnlyMarker =
        "GPU.DrivenInstance/VisibleOnlyDiscardKey/Portable";

    private readonly int instanceCount;
    private readonly int viewCount;
    private readonly int dispatchesPerFrame;
    private readonly int visibleBinCount;
    private readonly GpuDrivenInstancePipeline pipeline;
    private readonly GraphicsBuffer instances;
    private readonly GraphicsBuffer viewPlanes;
    private readonly GraphicsBuffer viewParameters;
    private readonly GraphicsBuffer drawTemplates;
    private readonly GraphicsBuffer groupCounts;
    private readonly GraphicsBuffer groupOffsets;
    private readonly GraphicsBuffer groupedInstanceIndices;
    private readonly GraphicsBuffer indirectArguments;
    private readonly GraphicsBuffer diagnostics;
    private readonly GpuDrivenInstanceExpectedResult culledTailExpected;
    private readonly GpuDrivenInstanceExpectedResult visibleOnlyExpected;
    private PendingValidation pendingValidation;
    private bool disposed;

    public GpuDrivenInstanceBenchmarkAdapter(
        int instanceCount,
        int viewCount,
        string visibility,
        int seed,
        int dispatchesPerFrame)
    {
        if (instanceCount < 1 ||
            instanceCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        if (viewCount < 1 ||
            viewCount > GpuDrivenInstancePipeline.MaximumViewCount)
        {
            throw new ArgumentOutOfRangeException(nameof(viewCount));
        }
        if ((long)instanceCount * viewCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewCount),
                "Instance/view pair count exceeds the primitive capacity.");
        }
        if (dispatchesPerFrame < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dispatchesPerFrame));
        }

        this.instanceCount = instanceCount;
        this.viewCount = viewCount;
        this.dispatchesPerFrame = dispatchesPerFrame;
        visibleBinCount = checked(viewCount * FixedDrawGroupCount);
        var instanceData = new GpuInstanceState[instanceCount];
        var planeData = new Vector4[
            viewCount * GpuDrivenInstancePipeline.FrustumPlaneCount];
        var viewData = new Vector4[viewCount];
        var drawData = new GpuDrawTemplate[FixedDrawGroupCount];
        VisibleInstanceCount = GpuDrivenInstanceInputGenerator.Populate(
            instanceData,
            planeData,
            viewData,
            drawData,
            visibility,
            seed);
        VisiblePairCount = checked(VisibleInstanceCount * viewCount);
        culledTailExpected = GpuDrivenInstanceBenchmarkCpuOracle.Build(
            instanceData,
            planeData,
            viewData,
            drawData,
            GpuDrivenInstanceOutputMode.CulledTail);
        visibleOnlyExpected = GpuDrivenInstanceBenchmarkCpuOracle.Build(
            instanceData,
            planeData,
            viewData,
            drawData,
            GpuDrivenInstanceOutputMode.VisibleOnly);

        instances = CreateStructured(
            instanceCount,
            GpuInstanceState.Stride,
            "GPU Driven Instance Benchmark States");
        viewPlanes = CreateStructured(
            planeData.Length,
            sizeof(float) * 4,
            "GPU Driven Instance Benchmark View Planes");
        viewParameters = CreateStructured(
            viewCount,
            sizeof(float) * 4,
            "GPU Driven Instance Benchmark View Parameters");
        drawTemplates = CreateStructured(
            FixedDrawGroupCount,
            GpuDrawTemplate.Stride,
            "GPU Driven Instance Benchmark Draw Templates");
        groupCounts = CreateStructured(
            visibleBinCount + 1,
            sizeof(uint),
            "GPU Driven Instance Benchmark Group Counts");
        groupOffsets = CreateStructured(
            visibleBinCount + 2,
            sizeof(uint),
            "GPU Driven Instance Benchmark Group Offsets");
        groupedInstanceIndices = CreateStructured(
            checked(instanceCount * viewCount),
            sizeof(uint),
            "GPU Driven Instance Benchmark Grouped Indices");
        indirectArguments = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured |
            GraphicsBuffer.Target.IndirectArguments,
            checked(
                visibleBinCount *
                GpuDrivenInstancePipeline.IndirectArgumentWordCount),
            sizeof(uint))
        {
            name = "GPU Driven Instance Benchmark Indirect Arguments",
        };
        diagnostics = CreateStructured(
            GpuDrivenInstancePipeline.DiagnosticWordCount,
            sizeof(uint),
            "GPU Driven Instance Benchmark Diagnostics");
        instances.SetData(instanceData);
        viewPlanes.SetData(planeData);
        viewParameters.SetData(viewData);
        drawTemplates.SetData(drawData);
        pipeline = new GpuDrivenInstancePipeline(
            instanceCount,
            viewCount,
            FixedDrawGroupCount,
            emitProfilerMarkers: false);
    }

    public int InstanceCount => instanceCount;

    public int ViewCount => viewCount;

    public int DrawGroupCount => FixedDrawGroupCount;

    public int VisibleInstanceCount { get; }

    public int VisiblePairCount { get; }

    public int DispatchesPerFrame => dispatchesPerFrame;

    public string ExpectedResultHash =>
        culledTailExpected.ResultHash + "/" +
        visibleOnlyExpected.ResultHash;

    public long SharedInputBytes => checked(
        (long)instanceCount * GpuInstanceState.Stride +
        (long)viewCount *
        GpuDrivenInstancePipeline.FrustumPlaneCount * sizeof(float) * 4L +
        (long)viewCount * sizeof(float) * 4L +
        (long)FixedDrawGroupCount * GpuDrawTemplate.Stride);

    public long SharedOutputBytes => checked(
        (long)(visibleBinCount + 1) * sizeof(uint) +
        (long)(visibleBinCount + 2) * sizeof(uint) +
        (long)instanceCount * viewCount * sizeof(uint) +
        (long)visibleBinCount *
        GpuDrivenInstancePipeline.IndirectArgumentWordCount * sizeof(uint) +
        GpuDrivenInstancePipeline.DiagnosticWordCount * sizeof(uint));

    public long LogicalProblemBytesPerDispatch => checked(
        SharedInputBytes + SharedOutputBytes);

    public long PrimitiveScratchBytes => pipeline.BinningScratchBytes;

    public long DirectInternalScratchBytes =>
        pipeline.ClassificationScratchBytes;

    public long ReferenceInternalScratchBytes =>
        pipeline.ClassificationScratchBytes;

    public long DirectCaseScratchBytes => pipeline.ScratchBytes;

    public long ReferenceCaseScratchBytes => pipeline.ScratchBytes;

    public long DirectCaseResidentBytes => checked(
        SharedInputBytes + SharedOutputBytes + pipeline.ScratchBytes);

    public long ReferenceCaseResidentBytes => DirectCaseResidentBytes;

    public long ActualBenchmarkBufferResidentBytes =>
        DirectCaseResidentBytes;

    public string CaseId(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return CulledTailCaseId;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return VisibleOnlyCaseId;
            case GpuDrivenInstanceBenchmarkVariant.Control:
                return "control/empty-command-buffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string VariantName(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return "culled-tail-portable";
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return "visible-only-discard-key-portable";
            case GpuDrivenInstanceBenchmarkVariant.Control:
                return "empty-command-buffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public string Marker(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return CulledTailMarker;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return VisibleOnlyMarker;
            case GpuDrivenInstanceBenchmarkVariant.Control:
                return "GPU.DrivenInstance/Control/EmptyCommandBuffer";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    public long CaseScratchBytes(GpuDrivenInstanceBenchmarkVariant variant)
    {
        return variant == GpuDrivenInstanceBenchmarkVariant.Control
            ? 0L
            : pipeline.ScratchBytes;
    }

    public long CaseResidentBytes(GpuDrivenInstanceBenchmarkVariant variant)
    {
        return variant == GpuDrivenInstanceBenchmarkVariant.Control
            ? 0L
            : DirectCaseResidentBytes;
    }

    public CommandBuffer CreateMeasurementCommandBuffer(
        GpuDrivenInstanceBenchmarkVariant variant)
    {
        var commands = new CommandBuffer { name = Marker(variant) };
        if (variant == GpuDrivenInstanceBenchmarkVariant.Control)
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
        GpuDrivenInstanceBenchmarkVariant variant)
    {
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        if (variant == GpuDrivenInstanceBenchmarkVariant.Control)
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
        GpuDrivenInstanceBenchmarkVariant variant,
        string phase)
    {
        ThrowIfDisposed();
        if (variant == GpuDrivenInstanceBenchmarkVariant.Control)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        if (pendingValidation != null)
        {
            throw new InvalidOperationException(
                "A validation readback is already pending.");
        }

        using (var commands = new CommandBuffer
               {
                   name = Marker(variant) + "/Validation",
               })
        {
            commands.BeginSample(Marker(variant) + "/Validation");
            RecordOnce(commands, variant);
            commands.EndSample(Marker(variant) + "/Validation");
            Graphics.ExecuteCommandBuffer(commands);
        }

        pendingValidation = new PendingValidation
        {
            Variant = variant,
            Phase = phase,
            Requests = new List<AsyncGPUReadbackRequest>
            {
                AsyncGPUReadback.Request(groupCounts),
                AsyncGPUReadback.Request(groupOffsets),
                AsyncGPUReadback.Request(groupedInstanceIndices),
                AsyncGPUReadback.Request(indirectArguments),
                AsyncGPUReadback.Request(diagnostics),
            },
            ReadbackBytes = SharedOutputBytes,
        };
    }

    public bool TryCompleteValidation(
        out GpuDrivenInstanceValidationResult result)
    {
        result = null;
        if (pendingValidation == null)
        {
            return false;
        }
        foreach (AsyncGPUReadbackRequest request in
                 pendingValidation.Requests)
        {
            if (!request.done)
            {
                return false;
            }
        }

        PendingValidation completed = pendingValidation;
        pendingValidation = null;
        GpuDrivenInstanceExpectedResult expected =
            completed.Variant == GpuDrivenInstanceBenchmarkVariant.Reference
                ? culledTailExpected
                : visibleOnlyExpected;
        result = new GpuDrivenInstanceValidationResult
        {
            Phase = completed.Phase,
            CaseId = CaseId(completed.Variant),
            Variant = VariantName(completed.Variant),
            ReadbackBytes = completed.ReadbackBytes,
            ValidCount = VisiblePairCount,
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
        uint[] actualGrouped = ToArray(
            completed.Requests[2].GetData<uint>());
        uint[] actualArguments = ToArray(
            completed.Requests[3].GetData<uint>());
        uint[] actualDiagnostics = ToArray(
            completed.Requests[4].GetData<uint>());
        result.InvalidKeyCount = actualDiagnostics[0];
        result.DiagnosticFlags = actualDiagnostics[1];
        result.Passed = GpuDrivenInstanceBenchmarkCpuOracle.Validate(
            expected,
            actualCounts,
            actualOffsets,
            actualGrouped,
            actualArguments,
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
        pipeline.Dispose();
        diagnostics.Dispose();
        indirectArguments.Dispose();
        groupedInstanceIndices.Dispose();
        groupOffsets.Dispose();
        groupCounts.Dispose();
        drawTemplates.Dispose();
        viewParameters.Dispose();
        viewPlanes.Dispose();
        instances.Dispose();
    }

    private void RecordOnce(
        CommandBuffer commands,
        GpuDrivenInstanceBenchmarkVariant variant)
    {
        GpuDrivenInstanceOutputMode outputMode;
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                outputMode = GpuDrivenInstanceOutputMode.CulledTail;
                break;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                outputMode = GpuDrivenInstanceOutputMode.VisibleOnly;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
        pipeline.Record(
            commands,
            instances,
            viewPlanes,
            viewParameters,
            drawTemplates,
            groupCounts,
            groupOffsets,
            groupedInstanceIndices,
            indirectArguments,
            diagnostics,
            instanceCount,
            viewCount,
            FixedDrawGroupCount,
            GpuPrimitiveBackend.Portable,
            outputMode);
    }

    private static GraphicsBuffer CreateStructured(
        int count,
        int stride,
        string name)
    {
        return new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            count,
            stride)
        {
            name = name,
        };
    }

    private static uint[] ToArray(NativeArray<uint> data)
    {
        var result = new uint[data.Length];
        data.CopyTo(result);
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDrivenInstanceBenchmarkAdapter));
        }
    }

    private sealed class PendingValidation
    {
        public GpuDrivenInstanceBenchmarkVariant Variant;
        public string Phase;
        public List<AsyncGPUReadbackRequest> Requests;
        public long ReadbackBytes;
    }
}
