using System;
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
    public uint ExpectedCoarseVisibleClusterViewCount;
    public uint ExpectedCandidateInstanceViewCount;
    public bool HierarchyStatisticsAvailable;
    public uint CoarseVisibleClusterViewCount;
    public uint CandidateInstanceViewCount;
    public uint HierarchicalVisiblePairCount;
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
    public const string FlatVisibleOnlyCaseId =
        "gpu-driven-instances/flat-visible-only-portable";
    public const string HierarchicalVisibleOnlyCaseId =
        "gpu-driven-instances/hierarchical-visible-only-portable";
    public const string FlatVisibleOnlyMarker =
        "GPU.DrivenInstance/FlatVisibleOnly/Portable";
    public const string HierarchicalVisibleOnlyMarker =
        "GPU.DrivenInstance/HierarchicalVisibleOnly/Portable";

    private readonly int instanceCount;
    private readonly int viewCount;
    private readonly int dispatchesPerFrame;
    private readonly int visibleBinCount;
    private readonly GpuDrivenInstanceBenchmarkMode benchmarkMode;
    private readonly GpuDrivenInstancePipeline pipeline;
    private readonly GraphicsBuffer instances;
    private readonly GraphicsBuffer clusters;
    private readonly GraphicsBuffer viewPlanes;
    private readonly GraphicsBuffer viewParameters;
    private readonly GraphicsBuffer drawTemplates;
    private readonly GraphicsBuffer groupCounts;
    private readonly GraphicsBuffer groupOffsets;
    private readonly GraphicsBuffer groupedInstanceIndices;
    private readonly GraphicsBuffer indirectArguments;
    private readonly GraphicsBuffer hierarchyStatistics;
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
        : this(
            instanceCount,
            viewCount,
            visibility,
            seed,
            dispatchesPerFrame,
            GpuDrivenInstanceBenchmarkModes.FilteredBinningId)
    {
    }

    public GpuDrivenInstanceBenchmarkAdapter(
        int instanceCount,
        int viewCount,
        string visibility,
        int seed,
        int dispatchesPerFrame,
        string benchmarkMode)
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
        this.benchmarkMode =
            GpuDrivenInstanceBenchmarkModes.Parse(benchmarkMode);
        visibleBinCount = checked(viewCount * FixedDrawGroupCount);
        var instanceData = new GpuInstanceState[instanceCount];
        var planeData = new Vector4[
            viewCount * GpuDrivenInstancePipeline.FrustumPlaneCount];
        var viewData = new Vector4[viewCount];
        var drawData = new GpuDrawTemplate[FixedDrawGroupCount];
        int generatedVisibleInstanceCount;
        if (this.benchmarkMode ==
            GpuDrivenInstanceBenchmarkMode.HierarchicalCulling)
        {
            generatedVisibleInstanceCount =
                GpuDrivenInstanceHierarchicalInputGenerator.Populate(
                    instanceData,
                    planeData,
                    viewData,
                    drawData,
                    visibility,
                    seed);
        }
        else
        {
            generatedVisibleInstanceCount =
                GpuDrivenInstanceInputGenerator.Populate(
                instanceData,
                planeData,
                viewData,
                drawData,
                visibility,
                seed);
        }
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
        VisiblePairCount = visibleOnlyExpected.GroupedInstanceIndices.Length;
        VisibleInstanceCount = CountDistinctVisibleInstances(
            visibleOnlyExpected.GroupedInstanceIndices,
            instanceCount);
        if (VisibleInstanceCount != generatedVisibleInstanceCount)
        {
            throw new InvalidOperationException(
                "The input generator's visible-instance count disagrees " +
                "with the independent visible-only CPU oracle.");
        }

        if (this.benchmarkMode ==
            GpuDrivenInstanceBenchmarkMode.HierarchicalCulling)
        {
            ClusterCount = GpuInstanceClusterBuilder.GetRequiredClusterCount(
                instanceCount,
                GpuDrivenInstanceHierarchicalInputGenerator
                    .InstancesPerCluster);
            NativeArray<GpuInstanceState> nativeInstances = default;
            NativeArray<GpuInstanceCluster> nativeClusters = default;
            try
            {
                nativeInstances = new NativeArray<GpuInstanceState>(
                    instanceData,
                    Allocator.TempJob);
                nativeClusters = new NativeArray<GpuInstanceCluster>(
                    ClusterCount,
                    Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);
                int builtClusterCount =
                    GpuInstanceClusterBuilder.BuildContiguous(
                        nativeInstances,
                        instanceCount,
                        GpuDrivenInstanceHierarchicalInputGenerator
                            .InstancesPerCluster,
                        nativeClusters);
                if (builtClusterCount != ClusterCount)
                {
                    throw new InvalidOperationException(
                        "Cluster builder returned an unexpected count.");
                }
                var clusterData = new GpuInstanceCluster[ClusterCount];
                nativeClusters.CopyTo(clusterData);
                GpuDrivenInstanceHierarchyExpectedStatistics statistics =
                    GpuDrivenInstanceHierarchicalInputGenerator
                        .ComputeExpectedHierarchyStatistics(
                            instanceData,
                            clusterData,
                            planeData,
                            viewCount);
                ExpectedCoarseVisibleClusterViewCount =
                    statistics.CoarseVisibleClusterViewCount;
                ExpectedCandidateInstanceViewCount =
                    statistics.CandidateInstanceViewCount;
                GraphicsBuffer selectedClusters = CreateStructured(
                    ClusterCount,
                    GpuInstanceCluster.Stride,
                    "GPU Driven Instance Benchmark Clusters");
                try
                {
                    selectedClusters.SetData(clusterData);
                    clusters = selectedClusters;
                }
                catch
                {
                    selectedClusters.Dispose();
                    throw;
                }
            }
            finally
            {
                if (nativeClusters.IsCreated)
                {
                    nativeClusters.Dispose();
                }
                if (nativeInstances.IsCreated)
                {
                    nativeInstances.Dispose();
                }
            }
        }
        else
        {
            ClusterCount = 0;
            clusters = null;
            ExpectedCoarseVisibleClusterViewCount = 0u;
            ExpectedCandidateInstanceViewCount = 0u;
        }

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
        hierarchyStatistics = this.benchmarkMode ==
            GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                ? CreateStructured(
                    GpuDrivenInstancePipeline.HierarchyStatisticWordCount,
                    sizeof(uint),
                    "GPU Driven Instance Benchmark Hierarchy Statistics")
                : null;
        diagnostics = CreateStructured(
            GpuDrivenInstancePipeline.DiagnosticWordCount,
            sizeof(uint),
            "GPU Driven Instance Benchmark Diagnostics");
        instances.SetData(instanceData);
        viewPlanes.SetData(planeData);
        viewParameters.SetData(viewData);
        drawTemplates.SetData(drawData);
        pipeline = this.benchmarkMode ==
            GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                ? new GpuDrivenInstancePipeline(
                    instanceCount,
                    viewCount,
                    FixedDrawGroupCount,
                    ClusterCount,
                    emitProfilerMarkers: false)
                : new GpuDrivenInstancePipeline(
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

    public string BenchmarkMode =>
        GpuDrivenInstanceBenchmarkModes.ToId(benchmarkMode);

    public string VisibilityLayout =>
        benchmarkMode == GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
            ? GpuDrivenInstanceHierarchicalInputGenerator.VisibilityLayoutId
            : GpuDrivenInstanceInputGenerator.VisibilityLayoutId;

    public int ClusterCount { get; }

    public uint ExpectedCoarseVisibleClusterViewCount { get; }

    public uint ExpectedCandidateInstanceViewCount { get; }

    public int InstancesPerCluster =>
        benchmarkMode == GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
            ? GpuDrivenInstanceHierarchicalInputGenerator.InstancesPerCluster
            : 0;

    public long ClusterBytes =>
        checked((long)ClusterCount * GpuInstanceCluster.Stride);

    public long HierarchyStatisticsBytes =>
        hierarchyStatistics == null
            ? 0L
            : checked(
                (long)GpuDrivenInstancePipeline.HierarchyStatisticWordCount *
                sizeof(uint));

    public string BaselineId =>
        benchmarkMode == GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
            ? "flat-visible-only-portable-v1"
            : "culled-tail-portable-v1";

    public string OptimizedId =>
        benchmarkMode == GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
            ? "hierarchical-visible-only-portable-v1"
            : "visible-only-discard-key-portable-v1";

    public string ExpectedResultHash =>
        benchmarkMode == GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
            ? visibleOnlyExpected.ResultHash + "/" +
              visibleOnlyExpected.ResultHash
            : culledTailExpected.ResultHash + "/" +
              visibleOnlyExpected.ResultHash;

    public long SharedInputBytes => checked(
        (long)instanceCount * GpuInstanceState.Stride +
        (long)viewCount *
        GpuDrivenInstancePipeline.FrustumPlaneCount * sizeof(float) * 4L +
        (long)viewCount * sizeof(float) * 4L +
        (long)FixedDrawGroupCount * GpuDrawTemplate.Stride +
        ClusterBytes);

    public long SharedOutputBytes => checked(
        (long)(visibleBinCount + 1) * sizeof(uint) +
        (long)(visibleBinCount + 2) * sizeof(uint) +
        (long)instanceCount * viewCount * sizeof(uint) +
        (long)visibleBinCount *
        GpuDrivenInstancePipeline.IndirectArgumentWordCount * sizeof(uint) +
        GpuDrivenInstancePipeline.DiagnosticWordCount * sizeof(uint) +
        HierarchyStatisticsBytes);

    public long LogicalProblemBytesPerDispatch => checked(
        SharedInputBytes + SharedOutputBytes);

    public long PrimitiveScratchBytes => pipeline.BinningScratchBytes;

    public long DirectInternalScratchBytes =>
        checked(
            pipeline.ClassificationScratchBytes +
            (benchmarkMode ==
                GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                    ? pipeline.HierarchicalScratchBytes
                    : 0L));

    public long ReferenceInternalScratchBytes =>
        pipeline.ClassificationScratchBytes;

    public long DirectCaseScratchBytes => pipeline.ScratchBytes;

    public long ReferenceCaseScratchBytes =>
        benchmarkMode == GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
            ? checked(
                pipeline.ClassificationScratchBytes +
                pipeline.BinningScratchBytes)
            : pipeline.ScratchBytes;

    public long DirectCaseResidentBytes => checked(
        SharedInputBytes + SharedOutputBytes + pipeline.ScratchBytes);

    public long ReferenceCaseResidentBytes => checked(
        SharedInputBytes + SharedOutputBytes + ReferenceCaseScratchBytes);

    public long ActualBenchmarkBufferResidentBytes =>
        DirectCaseResidentBytes;

    public string CaseId(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return benchmarkMode ==
                    GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                        ? FlatVisibleOnlyCaseId
                        : CulledTailCaseId;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return benchmarkMode ==
                    GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                        ? HierarchicalVisibleOnlyCaseId
                        : VisibleOnlyCaseId;
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
                return benchmarkMode ==
                    GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                        ? "flat-visible-only-portable"
                        : "culled-tail-portable";
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return benchmarkMode ==
                    GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                        ? "hierarchical-visible-only-portable"
                        : "visible-only-discard-key-portable";
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
                return benchmarkMode ==
                    GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                        ? FlatVisibleOnlyMarker
                        : CulledTailMarker;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return benchmarkMode ==
                    GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                        ? HierarchicalVisibleOnlyMarker
                        : VisibleOnlyMarker;
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
            : variant == GpuDrivenInstanceBenchmarkVariant.Reference
                ? ReferenceCaseScratchBytes
                : DirectCaseScratchBytes;
    }

    public long CaseResidentBytes(GpuDrivenInstanceBenchmarkVariant variant)
    {
        return variant == GpuDrivenInstanceBenchmarkVariant.Control
            ? 0L
            : variant == GpuDrivenInstanceBenchmarkVariant.Reference
                ? ReferenceCaseResidentBytes
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

        bool readHierarchyStatistics =
            benchmarkMode ==
                GpuDrivenInstanceBenchmarkMode.HierarchicalCulling &&
            variant == GpuDrivenInstanceBenchmarkVariant.Direct;
        int requestCount = readHierarchyStatistics ? 6 : 5;
        PendingValidation validation = new PendingValidation
        {
            Variant = variant,
            Phase = phase,
            ReadbackData = new uint[requestCount][],
            ReadbackBytes = readHierarchyStatistics
                ? SharedOutputBytes
                : SharedOutputBytes - HierarchyStatisticsBytes,
        };
        pendingValidation = validation;
        IssueNextValidationReadback(validation);
    }

    public bool TryCompleteValidation(
        out GpuDrivenInstanceValidationResult result)
    {
        result = null;
        if (pendingValidation == null)
        {
            return false;
        }
        PendingValidation active = pendingValidation;
        if (!active.HasInFlightRequest || !active.InFlightRequest.done)
        {
            return false;
        }

        AsyncGPUReadbackRequest request = active.InFlightRequest;
        string requestName = active.InFlightRequestName;
        active.HasInFlightRequest = false;
        if (request.hasError)
        {
            pendingValidation = null;
            result = CreateValidationResult(active);
            result.Passed = false;
            result.Message =
                "Async GPU readback failed for " + requestName + ".";
            result.ResultHash = "unavailable";
            return true;
        }

        active.ReadbackData[active.NextRequestIndex] =
            ToArray(request.GetData<uint>());
        active.NextRequestIndex++;
        if (active.NextRequestIndex < active.ReadbackData.Length)
        {
            IssueNextValidationReadback(active);
            return false;
        }

        PendingValidation completed = active;
        pendingValidation = null;
        GpuDrivenInstanceExpectedResult expected =
            benchmarkMode ==
                GpuDrivenInstanceBenchmarkMode.HierarchicalCulling
                ? visibleOnlyExpected
                : completed.Variant ==
                    GpuDrivenInstanceBenchmarkVariant.Reference
                    ? culledTailExpected
                    : visibleOnlyExpected;
        result = CreateValidationResult(completed);

        uint[] actualCounts = completed.ReadbackData[0];
        uint[] actualOffsets = completed.ReadbackData[1];
        uint[] actualGrouped = completed.ReadbackData[2];
        uint[] actualArguments = completed.ReadbackData[3];
        uint[] actualDiagnostics = completed.ReadbackData[4];
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
        if (completed.ReadbackData.Length == 6)
        {
            uint[] statistics = completed.ReadbackData[5];
            result.CoarseVisibleClusterViewCount = statistics[
                GpuDrivenInstancePipeline
                    .CoarseVisibleClusterViewCountWord];
            result.CandidateInstanceViewCount = statistics[
                GpuDrivenInstancePipeline.CandidateInstanceViewCountWord];
            result.HierarchicalVisiblePairCount = statistics[
                GpuDrivenInstancePipeline.HierarchicalVisiblePairCountWord];
            result.HierarchyStatisticsAvailable = true;
            bool statisticsPassed =
                result.CoarseVisibleClusterViewCount ==
                    ExpectedCoarseVisibleClusterViewCount &&
                result.CandidateInstanceViewCount ==
                    ExpectedCandidateInstanceViewCount &&
                result.HierarchicalVisiblePairCount ==
                    (uint)VisiblePairCount;
            if (!statisticsPassed)
            {
                result.Passed = false;
                result.Message +=
                    " Hierarchy statistics disagree with the exact frozen " +
                    "cluster CPU oracle.";
            }
            else
            {
                result.Message +=
                    " Hierarchy statistics exactly match the frozen " +
                    "cluster CPU oracle.";
            }
        }
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
        hierarchyStatistics?.Dispose();
        indirectArguments.Dispose();
        groupedInstanceIndices.Dispose();
        groupOffsets.Dispose();
        groupCounts.Dispose();
        drawTemplates.Dispose();
        viewParameters.Dispose();
        viewPlanes.Dispose();
        clusters?.Dispose();
        instances.Dispose();
    }

    private void RecordOnce(
        CommandBuffer commands,
        GpuDrivenInstanceBenchmarkVariant variant)
    {
        if (benchmarkMode ==
            GpuDrivenInstanceBenchmarkMode.HierarchicalCulling)
        {
            if (variant == GpuDrivenInstanceBenchmarkVariant.Reference)
            {
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
                    GpuDrivenInstanceOutputMode.VisibleOnly);
                return;
            }
            if (variant == GpuDrivenInstanceBenchmarkVariant.Direct)
            {
                pipeline.RecordHierarchicalVisibleOnly(
                    commands,
                    instances,
                    clusters,
                    viewPlanes,
                    viewParameters,
                    drawTemplates,
                    groupCounts,
                    groupOffsets,
                    groupedInstanceIndices,
                    indirectArguments,
                    hierarchyStatistics,
                    diagnostics,
                    instanceCount,
                    ClusterCount,
                    viewCount,
                    FixedDrawGroupCount,
                    GpuPrimitiveBackend.Portable);
                return;
            }
            throw new ArgumentOutOfRangeException(nameof(variant));
        }

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

    private static string ValidationReadbackName(int requestIndex)
    {
        switch (requestIndex)
        {
            case 0:
                return "groupCounts";
            case 1:
                return "groupOffsets";
            case 2:
                return "groupedInstanceIndices";
            case 3:
                return "indirectArguments";
            case 4:
                return "diagnostics";
            case 5:
                return "hierarchyStatistics";
            default:
                return "unknown validation buffer";
        }
    }

    private GpuDrivenInstanceValidationResult CreateValidationResult(
        PendingValidation completed)
    {
        return new GpuDrivenInstanceValidationResult
        {
            Phase = completed.Phase,
            CaseId = CaseId(completed.Variant),
            Variant = VariantName(completed.Variant),
            ReadbackBytes = completed.ReadbackBytes,
            ValidCount = VisiblePairCount,
            ExpectedCoarseVisibleClusterViewCount =
                ExpectedCoarseVisibleClusterViewCount,
            ExpectedCandidateInstanceViewCount =
                ExpectedCandidateInstanceViewCount,
        };
    }

    private void IssueNextValidationReadback(PendingValidation validation)
    {
        int requestIndex = validation.NextRequestIndex;
        validation.InFlightRequest =
            RequestValidationBuffer(requestIndex);
        validation.InFlightRequestName =
            ValidationReadbackName(requestIndex);
        validation.HasInFlightRequest = true;
    }

    private AsyncGPUReadbackRequest RequestValidationBuffer(int requestIndex)
    {
        switch (requestIndex)
        {
            case 0:
                return AsyncGPUReadback.Request(groupCounts);
            case 1:
                return AsyncGPUReadback.Request(groupOffsets);
            case 2:
                return AsyncGPUReadback.Request(groupedInstanceIndices);
            case 3:
                return AsyncGPUReadback.Request(indirectArguments);
            case 4:
                return AsyncGPUReadback.Request(diagnostics);
            case 5:
                if (hierarchyStatistics == null)
                {
                    throw new InvalidOperationException(
                        "Hierarchy statistics are unavailable for validation.");
                }
                return AsyncGPUReadback.Request(hierarchyStatistics);
            default:
                throw new ArgumentOutOfRangeException(nameof(requestIndex));
        }
    }

    private static int CountDistinctVisibleInstances(
        uint[] groupedInstanceIndices,
        int instanceCount)
    {
        var seen = new bool[instanceCount];
        int result = 0;
        for (int index = 0; index < groupedInstanceIndices.Length; index++)
        {
            int instanceIndex = checked((int)groupedInstanceIndices[index]);
            if (instanceIndex < 0 || instanceIndex >= instanceCount)
            {
                throw new InvalidOperationException(
                    "The visible-only CPU oracle returned an invalid " +
                    "instance index.");
            }
            if (seen[instanceIndex])
            {
                continue;
            }
            seen[instanceIndex] = true;
            result++;
        }
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
        public uint[][] ReadbackData;
        public int NextRequestIndex;
        public AsyncGPUReadbackRequest InFlightRequest;
        public string InFlightRequestName;
        public bool HasInFlightRequest;
        public long ReadbackBytes;
    }
}
