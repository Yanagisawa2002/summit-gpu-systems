using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances
{
    /// <summary>
    /// Records multi-view visibility, LOD grouping, CSR compaction, and
    /// indexed-indirect argument generation without CPU readback.
    /// </summary>
    public sealed class GpuDrivenInstancePipeline : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int MaximumViewCount = 32;
        public const int FrustumPlaneCount = 6;
        public const int IndirectArgumentWordCount = 5;
        public const int DiagnosticWordCount = 2;
        public const int ContractViolationCountWord = 0;
        public const int ErrorFlagsWord = 1;
        public const int HierarchyStatisticWordCount = 3;
        public const int CoarseVisibleClusterViewCountWord = 0;
        public const int CandidateInstanceViewCountWord = 1;
        public const int HierarchicalVisiblePairCountWord = 2;
        public const int HierarchicalFineThreadGroupSize =
            GpuInstanceCluster.MaximumInstanceCount;

        private const string ResourcePath =
            "GpuDrivenInstances/GpuDrivenInstances";
        private const string PipelineSample =
            "Summit.GpuDrivenInstances/Pipeline";
        private const string ClassifySample =
            "Summit.GpuDrivenInstances/Classify";
        private const string ArgumentsSample =
            "Summit.GpuDrivenInstances/BuildIndirectArguments";
        private const string HierarchicalSample =
            "Summit.GpuDrivenInstances/HierarchicalVisibleOnly";
        private const string HierarchicalCoarseSample =
            "Summit.GpuDrivenInstances/HierarchicalValidateAndCoarse";
        private const string HierarchicalFineSample =
            "Summit.GpuDrivenInstances/HierarchicalFine";

        private static readonly int ClearCountId =
            Shader.PropertyToID("_ClearCount");
        private static readonly int InstanceCountId =
            Shader.PropertyToID("_InstanceCount");
        private static readonly int ViewCountId =
            Shader.PropertyToID("_ViewCount");
        private static readonly int ClusterCountId =
            Shader.PropertyToID("_ClusterCount");
        private static readonly int PairCapacityId =
            Shader.PropertyToID("_PairCapacity");
        private static readonly int FineDispatchGroupsXId =
            Shader.PropertyToID("_FineDispatchGroupsX");
        private static readonly int DrawGroupCountId =
            Shader.PropertyToID("_DrawGroupCount");
        private static readonly int VisibleBinCountId =
            Shader.PropertyToID("_VisibleBinCount");
        private static readonly int CulledKeyId =
            Shader.PropertyToID("_CulledKey");
        private static readonly int ClearBufferId =
            Shader.PropertyToID("_ClearBuffer");
        private static readonly int InstancesId =
            Shader.PropertyToID("_Instances");
        private static readonly int ClustersId =
            Shader.PropertyToID("_Clusters");
        private static readonly int ClusterViewMasksId =
            Shader.PropertyToID("_ClusterViewMasks");
        private static readonly int HierarchyStatisticsId =
            Shader.PropertyToID("_HierarchyStatistics");
        private static readonly int HierarchyValidityId =
            Shader.PropertyToID("_HierarchyValidity");
        private static readonly int BinningDiagnosticsId =
            Shader.PropertyToID("_BinningDiagnostics");
        private static readonly int HierarchyBinningDiagnosticsId =
            Shader.PropertyToID("_HierarchyBinningDiagnostics");
        private static readonly int ViewPlanesId =
            Shader.PropertyToID("_ViewPlanes");
        private static readonly int ViewParametersId =
            Shader.PropertyToID("_ViewParameters");
        private static readonly int DrawTemplatesId =
            Shader.PropertyToID("_DrawTemplates");
        private static readonly int KeysId =
            Shader.PropertyToID("_Keys");
        private static readonly int ValuesId =
            Shader.PropertyToID("_Values");
        private static readonly int BinCountsId =
            Shader.PropertyToID("_BinCounts");
        private static readonly int BinOffsetsId =
            Shader.PropertyToID("_BinOffsets");
        private static readonly int IndirectArgumentsId =
            Shader.PropertyToID("_IndirectArguments");
        private static readonly int DiagnosticsId =
            Shader.PropertyToID("_Diagnostics");

        private readonly ComputeShader shader;
        private readonly int clearUintKernel;
        private readonly int classifyInstancesKernel;
        private readonly int buildIndirectArgumentsKernel;
        private readonly int buildHierarchicalIndirectArgumentsKernel;
        private readonly int initializeHierarchyFrameKernel;
        private readonly int validateAndClassifyClustersKernel;
        private readonly int classifyClusterInstancesKernel;
        private readonly int mergeHierarchyDiagnosticsKernel;
        private readonly GpuDirectSpatialBinner binner;
        private readonly GraphicsBuffer keys;
        private readonly GraphicsBuffer values;
        private readonly GraphicsBuffer clusterViewMasks;
        private readonly GraphicsBuffer hierarchyValidity;
        private readonly GraphicsBuffer hierarchyBinningDiagnostics;
        private readonly GraphicsBuffer hierarchyValidationDispatchArguments;
        private readonly GraphicsBuffer hierarchyScatterDispatchArguments;
        private readonly bool emitProfilerMarkers;
        private bool disposed;

        public GpuDrivenInstancePipeline(
            int instanceCapacity,
            int viewCapacity,
            int drawGroupCapacity,
            ComputeShader shader = null,
            bool emitProfilerMarkers = true)
            : this(
                instanceCapacity,
                viewCapacity,
                drawGroupCapacity,
                0,
                shader,
                emitProfilerMarkers)
        {
        }

        /// <summary>
        /// Creates a pipeline with opt-in hierarchical visible-only capacity.
        /// </summary>
        /// <remarks>
        /// A zero <paramref name="hierarchicalClusterCapacity"/> keeps the
        /// original flat-only allocation contract. A positive capacity owns
        /// persistent coarse masks and GPU-count scatter scratch used only by
        /// <see cref="RecordHierarchicalVisibleOnly"/>.
        /// </remarks>
        public GpuDrivenInstancePipeline(
            int instanceCapacity,
            int viewCapacity,
            int drawGroupCapacity,
            int hierarchicalClusterCapacity,
            ComputeShader shader = null,
            bool emitProfilerMarkers = true)
        {
            ValidatePositiveCapacity(
                instanceCapacity,
                nameof(instanceCapacity));
            ValidateViewCapacity(viewCapacity);
            ValidatePositiveCapacity(
                drawGroupCapacity,
                nameof(drawGroupCapacity));
            if (hierarchicalClusterCapacity < 0 ||
                hierarchicalClusterCapacity > instanceCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hierarchicalClusterCapacity),
                    "Hierarchical cluster capacity must be zero (disabled) " +
                    "or no greater than instanceCapacity.");
            }

            int pairCapacity = CheckedProduct(
                instanceCapacity,
                viewCapacity,
                nameof(instanceCapacity));
            int visibleBinCapacity = CheckedProduct(
                viewCapacity,
                drawGroupCapacity,
                nameof(drawGroupCapacity));
            if (visibleBinCapacity >=
                global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawGroupCapacity),
                    "Visible bins plus the culled bin exceed the supported " +
                    "GPU primitive capacity.");
            }

            ComputeShader selectedShader = shader != null
                ? shader
                : Resources.Load<ComputeShader>(ResourcePath);
            if (selectedShader == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{ResourcePath}' could not " +
                    "be loaded.");
            }

            int selectedClearKernel = selectedShader.FindKernel("ClearUint");
            int selectedClassifyKernel =
                selectedShader.FindKernel("ClassifyInstances");
            int selectedArgumentsKernel =
                selectedShader.FindKernel("BuildIndirectArguments");
            int selectedHierarchicalArgumentsKernel = -1;
            int selectedInitializeHierarchyFrameKernel = -1;
            int selectedValidateAndClassifyClustersKernel = -1;
            int selectedClassifyClusterInstancesKernel = -1;
            int selectedMergeHierarchyDiagnosticsKernel = -1;
            if (hierarchicalClusterCapacity > 0)
            {
                selectedHierarchicalArgumentsKernel =
                    selectedShader.FindKernel(
                        "BuildHierarchicalIndirectArguments");
                selectedInitializeHierarchyFrameKernel =
                    selectedShader.FindKernel("InitializeHierarchyFrame");
                selectedValidateAndClassifyClustersKernel =
                    selectedShader.FindKernel("ValidateAndClassifyClusters");
                selectedClassifyClusterInstancesKernel =
                    selectedShader.FindKernel("ClassifyClusterInstances");
                selectedMergeHierarchyDiagnosticsKernel =
                    selectedShader.FindKernel("MergeHierarchyDiagnostics");
            }

            GpuDirectSpatialBinner selectedBinner = null;
            GraphicsBuffer selectedKeys = null;
            GraphicsBuffer selectedValues = null;
            GraphicsBuffer selectedClusterViewMasks = null;
            GraphicsBuffer selectedHierarchyValidity = null;
            GraphicsBuffer selectedHierarchyBinningDiagnostics = null;
            GraphicsBuffer selectedHierarchyValidationDispatchArguments = null;
            GraphicsBuffer selectedHierarchyScatterDispatchArguments = null;
            try
            {
                selectedBinner = new GpuDirectSpatialBinner(
                    pairCapacity,
                    checked(visibleBinCapacity + 1),
                    emitProfilerMarkers: emitProfilerMarkers);
                selectedKeys = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    pairCapacity,
                    sizeof(uint));
                selectedValues = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    pairCapacity,
                    sizeof(uint));
                selectedKeys.name = "GPU Driven Instance Classification Keys";
                selectedValues.name = "GPU Driven Instance Source Indices";
                if (hierarchicalClusterCapacity > 0)
                {
                    selectedClusterViewMasks = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        hierarchicalClusterCapacity,
                        sizeof(uint));
                    selectedHierarchyValidity = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        1,
                        sizeof(uint));
                    selectedHierarchyBinningDiagnostics = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        DiagnosticWordCount,
                        sizeof(uint));
                    selectedHierarchyValidationDispatchArguments =
                        new GraphicsBuffer(
                            GraphicsBuffer.Target.Structured |
                            GraphicsBuffer.Target.IndirectArguments,
                            3,
                            sizeof(uint));
                    selectedHierarchyScatterDispatchArguments =
                        new GraphicsBuffer(
                            GraphicsBuffer.Target.Structured |
                            GraphicsBuffer.Target.IndirectArguments,
                            3,
                            sizeof(uint));
                    selectedClusterViewMasks.name =
                        "GPU Driven Cluster View Masks";
                    selectedHierarchyValidity.name =
                        "GPU Driven Hierarchy Validity";
                    selectedHierarchyBinningDiagnostics.name =
                        "GPU Driven Hierarchical Binning Diagnostics";
                    selectedHierarchyValidationDispatchArguments.name =
                        "GPU Driven Hierarchical Validation Dispatch Arguments";
                    selectedHierarchyScatterDispatchArguments.name =
                        "GPU Driven Hierarchical Scatter Dispatch Arguments";
                }
            }
            catch
            {
                selectedHierarchyScatterDispatchArguments?.Dispose();
                selectedHierarchyValidationDispatchArguments?.Dispose();
                selectedHierarchyBinningDiagnostics?.Dispose();
                selectedHierarchyValidity?.Dispose();
                selectedClusterViewMasks?.Dispose();
                selectedValues?.Dispose();
                selectedKeys?.Dispose();
                selectedBinner?.Dispose();
                throw;
            }

            InstanceCapacity = instanceCapacity;
            ViewCapacity = viewCapacity;
            DrawGroupCapacity = drawGroupCapacity;
            PairCapacity = pairCapacity;
            VisibleBinCapacity = visibleBinCapacity;
            HierarchicalClusterCapacity = hierarchicalClusterCapacity;
            this.shader = selectedShader;
            clearUintKernel = selectedClearKernel;
            classifyInstancesKernel = selectedClassifyKernel;
            buildIndirectArgumentsKernel = selectedArgumentsKernel;
            buildHierarchicalIndirectArgumentsKernel =
                selectedHierarchicalArgumentsKernel;
            initializeHierarchyFrameKernel =
                selectedInitializeHierarchyFrameKernel;
            validateAndClassifyClustersKernel =
                selectedValidateAndClassifyClustersKernel;
            classifyClusterInstancesKernel =
                selectedClassifyClusterInstancesKernel;
            mergeHierarchyDiagnosticsKernel =
                selectedMergeHierarchyDiagnosticsKernel;
            binner = selectedBinner;
            keys = selectedKeys;
            values = selectedValues;
            clusterViewMasks = selectedClusterViewMasks;
            hierarchyValidity = selectedHierarchyValidity;
            hierarchyBinningDiagnostics =
                selectedHierarchyBinningDiagnostics;
            hierarchyValidationDispatchArguments =
                selectedHierarchyValidationDispatchArguments;
            hierarchyScatterDispatchArguments =
                selectedHierarchyScatterDispatchArguments;
            this.emitProfilerMarkers = emitProfilerMarkers;
        }

        public int InstanceCapacity { get; }

        public int ViewCapacity { get; }

        public int DrawGroupCapacity { get; }

        public int PairCapacity { get; }

        public int VisibleBinCapacity { get; }

        public int HierarchicalClusterCapacity { get; }

        public bool SupportsHierarchicalVisibleOnly =>
            HierarchicalClusterCapacity > 0;

        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        public static bool SupportsCurrentDevice =>
            SystemInfo.supportsComputeShaders &&
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        public long ClassificationScratchBytes =>
            checked((long)PairCapacity * sizeof(uint) * 2L);

        public long BinningScratchBytes => binner.ScratchBytes;

        public long HierarchicalScratchBytes =>
            HierarchicalClusterCapacity == 0
                ? 0L
                : checked(
                    (long)HierarchicalClusterCapacity * sizeof(uint) +
                    sizeof(uint) +
                    DiagnosticWordCount * sizeof(uint) +
                    6L * sizeof(uint));

        public long ScratchBytes =>
            checked(
                ClassificationScratchBytes +
                BinningScratchBytes +
                HierarchicalScratchBytes);

        public long ResidentBytes => ScratchBytes;

        public static int GetVisibleBinCount(
            int viewCount,
            int drawGroupCount)
        {
            if (viewCount < 1 || viewCount > MaximumViewCount)
            {
                throw new ArgumentOutOfRangeException(nameof(viewCount));
            }
            if (drawGroupCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawGroupCount));
            }
            return checked(viewCount * drawGroupCount);
        }

        public static int GetCulledBinIndex(
            int viewCount,
            int drawGroupCount)
        {
            return GetVisibleBinCount(viewCount, drawGroupCount);
        }

        public static int GetOutputBinCount(
            int viewCount,
            int drawGroupCount,
            GpuDrivenInstanceOutputMode outputMode)
        {
            int visibleBinCount = GetVisibleBinCount(
                viewCount,
                drawGroupCount);
            switch (outputMode)
            {
                case GpuDrivenInstanceOutputMode.CulledTail:
                    return checked(visibleBinCount + 1);
                case GpuDrivenInstanceOutputMode.VisibleOnly:
                    return visibleBinCount;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(outputMode));
            }
        }

        /// <summary>
        /// Returns the two-dimensional dispatch shape used by the one-group-
        /// per-cluster fine pass.
        /// </summary>
        public static void GetHierarchicalFineDispatchDimensions(
            int clusterCount,
            out int groupsX,
            out int groupsY)
        {
            if (clusterCount < 0 ||
                clusterCount >
                    global::Summit.GpuPrimitives.GpuPrimitives
                        .MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(nameof(clusterCount));
            }
            if (clusterCount == 0)
            {
                groupsX = 0;
                groupsY = 0;
                return;
            }

            groupsX = Math.Min(
                clusterCount,
                global::Summit.GpuPrimitives.GpuPrimitives
                    .MaxDispatchGroups);
            groupsY = DivideRoundUp(clusterCount, groupsX);
            if (groupsY >
                global::Summit.GpuPrimitives.GpuPrimitives.MaxDispatchGroups)
            {
                throw new ArgumentOutOfRangeException(nameof(clusterCount));
            }
        }

        /// <summary>
        /// Records the complete GPU classification and argument pipeline.
        /// </summary>
        /// <remarks>
        /// In <see cref="GpuDrivenInstanceOutputMode.CulledTail"/> mode,
        /// <paramref name="groupCounts"/> contains VisibleBinCount + 1 entries
        /// and the final bin receives rejected pairs. In
        /// <see cref="GpuDrivenInstanceOutputMode.VisibleOnly"/> mode, rejected
        /// pairs are discarded before scatter and outputs contain visible bins
        /// only. <paramref name="groupOffsets"/> always contains one terminal
        /// offset after the active output bins.
        /// </remarks>
        public void Record(
            CommandBuffer commands,
            GraphicsBuffer instances,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer groupedInstanceIndices,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer diagnostics,
            int instanceCount,
            int viewCount,
            int drawGroupCount,
            GpuPrimitiveBackend scanBackend = GpuPrimitiveBackend.Auto,
            GpuDrivenInstanceOutputMode outputMode =
                GpuDrivenInstanceOutputMode.CulledTail)
        {
            ValidateRecordArguments(
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
                drawGroupCount,
                scanBackend,
                outputMode);

            int pairCount = checked(instanceCount * viewCount);
            int visibleBinCount = checked(viewCount * drawGroupCount);
            bool visibleOnly =
                outputMode == GpuDrivenInstanceOutputMode.VisibleOnly;
            int totalBinCount = visibleOnly
                ? visibleBinCount
                : checked(visibleBinCount + 1);
            uint culledKey = visibleOnly
                ? uint.MaxValue
                : checked((uint)visibleBinCount);

            BeginSample(commands, PipelineSample);
            RecordClearDiagnostics(commands, diagnostics);

            BeginSample(commands, ClassifySample);
            if (pairCount > 0)
            {
                commands.SetComputeIntParam(
                    shader,
                    InstanceCountId,
                    instanceCount);
                commands.SetComputeIntParam(shader, ViewCountId, viewCount);
                commands.SetComputeIntParam(
                    shader,
                    DrawGroupCountId,
                    drawGroupCount);
                commands.SetComputeIntParam(
                    shader,
                    VisibleBinCountId,
                    visibleBinCount);
                commands.SetComputeIntParam(
                    shader,
                    CulledKeyId,
                    unchecked((int)culledKey));
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    InstancesId,
                    instances);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    ViewPlanesId,
                    viewPlanes);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    ViewParametersId,
                    viewParameters);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    KeysId,
                    keys);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    ValuesId,
                    values);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    DiagnosticsId,
                    diagnostics);
                commands.DispatchCompute(
                    shader,
                    classifyInstancesKernel,
                    DivideRoundUp(pairCount, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, ClassifySample);

            if (visibleOnly)
            {
                binner.RecordWithDiscardKeyWithoutDiagnosticClear(
                    commands,
                    keys,
                    values,
                    groupCounts,
                    groupOffsets,
                    groupedInstanceIndices,
                    diagnostics,
                    pairCount,
                    totalBinCount,
                    culledKey,
                    scanBackend);
            }
            else
            {
                binner.RecordGuaranteedInRangeWithoutDiagnosticClear(
                    commands,
                    keys,
                    values,
                    groupCounts,
                    groupOffsets,
                    groupedInstanceIndices,
                    diagnostics,
                    pairCount,
                    totalBinCount,
                    scanBackend);
            }

            RecordBuildIndirectArguments(
                commands,
                drawTemplates,
                groupCounts,
                groupOffsets,
                indirectArguments,
                visibleBinCount,
                drawGroupCount);
            EndSample(commands, PipelineSample);
        }

        /// <summary>
        /// Records the opt-in hierarchical, visible-only classification path.
        /// </summary>
        /// <remarks>
        /// Clusters must be a contiguous, non-overlapping exact cover of the
        /// active instance prefix. Every cluster contains between one and 64
        /// instances and supplies a conservative sphere plus the union of its
        /// members' view masks. The GPU validates the range cover before any
        /// fine work and fails closed with diagnostics when it is malformed.
        /// Coarse classification emits one view mask per cluster; one 64-lane
        /// fine group per cluster then examines only views surviving both that
        /// mask and the instance mask. Visible pairs are appended densely,
        /// pre-counted by bin, scanned, and scattered from a GPU-owned count.
        /// <paramref name="hierarchyStatistics"/> receives exactly three
        /// words: coarse-visible cluster/view pairs, fine candidate
        /// instance/view pairs, and visible pairs. The final word is also the
        /// GPU count consumed by the indirect scatter.
        /// </remarks>
        public void RecordHierarchicalVisibleOnly(
            CommandBuffer commands,
            GraphicsBuffer instances,
            GraphicsBuffer clusters,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer groupedInstanceIndices,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer hierarchyStatistics,
            GraphicsBuffer diagnostics,
            int instanceCount,
            int clusterCount,
            int viewCount,
            int drawGroupCount,
            GpuPrimitiveBackend scanBackend = GpuPrimitiveBackend.Auto)
        {
            ValidateHierarchicalRecordArguments(
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
                clusterCount,
                viewCount,
                drawGroupCount,
                scanBackend);

            int pairCount = checked(instanceCount * viewCount);
            int visibleBinCount = checked(viewCount * drawGroupCount);

            BeginSample(commands, PipelineSample);
            BeginSample(commands, HierarchicalSample);
            commands.SetComputeIntParam(
                shader,
                VisibleBinCountId,
                visibleBinCount);
            commands.SetComputeBufferParam(
                shader,
                initializeHierarchyFrameKernel,
                BinCountsId,
                groupCounts);
            commands.SetComputeBufferParam(
                shader,
                initializeHierarchyFrameKernel,
                HierarchyStatisticsId,
                hierarchyStatistics);
            commands.SetComputeBufferParam(
                shader,
                initializeHierarchyFrameKernel,
                HierarchyValidityId,
                hierarchyValidity);
            commands.SetComputeBufferParam(
                shader,
                initializeHierarchyFrameKernel,
                HierarchyBinningDiagnosticsId,
                hierarchyBinningDiagnostics);
            commands.SetComputeBufferParam(
                shader,
                initializeHierarchyFrameKernel,
                DiagnosticsId,
                diagnostics);
            commands.DispatchCompute(
                shader,
                initializeHierarchyFrameKernel,
                DivideRoundUp(
                    Math.Max(visibleBinCount, HierarchyStatisticWordCount),
                    ThreadGroupSize),
                1,
                1);
            if (clusterCount > 0)
            {
                BeginSample(commands, HierarchicalCoarseSample);
                commands.SetComputeIntParam(
                    shader,
                    InstanceCountId,
                    instanceCount);
                commands.SetComputeIntParam(
                    shader,
                    ClusterCountId,
                    clusterCount);
                commands.SetComputeBufferParam(
                    shader,
                    validateAndClassifyClustersKernel,
                    ClustersId,
                    clusters);
                commands.SetComputeBufferParam(
                    shader,
                    validateAndClassifyClustersKernel,
                    HierarchyValidityId,
                    hierarchyValidity);
                commands.SetComputeBufferParam(
                    shader,
                    validateAndClassifyClustersKernel,
                    DiagnosticsId,
                    diagnostics);
                commands.SetComputeIntParam(shader, ViewCountId, viewCount);
                commands.SetComputeBufferParam(
                    shader,
                    validateAndClassifyClustersKernel,
                    ViewPlanesId,
                    viewPlanes);
                commands.SetComputeBufferParam(
                    shader,
                    validateAndClassifyClustersKernel,
                    ViewParametersId,
                    viewParameters);
                commands.SetComputeBufferParam(
                    shader,
                    validateAndClassifyClustersKernel,
                    ClusterViewMasksId,
                    clusterViewMasks);
                commands.DispatchCompute(
                    shader,
                    validateAndClassifyClustersKernel,
                    DivideRoundUp(clusterCount, ThreadGroupSize),
                    1,
                    1);
                EndSample(commands, HierarchicalCoarseSample);

                BeginSample(commands, HierarchicalFineSample);
                GetHierarchicalFineDispatchDimensions(
                    clusterCount,
                    out int fineGroupsX,
                    out int fineGroupsY);
                commands.SetComputeIntParam(
                    shader,
                    InstanceCountId,
                    instanceCount);
                commands.SetComputeIntParam(
                    shader,
                    ClusterCountId,
                    clusterCount);
                commands.SetComputeIntParam(shader, ViewCountId, viewCount);
                commands.SetComputeIntParam(
                    shader,
                    DrawGroupCountId,
                    drawGroupCount);
                commands.SetComputeIntParam(
                    shader,
                    PairCapacityId,
                    pairCount);
                commands.SetComputeIntParam(
                    shader,
                    FineDispatchGroupsXId,
                    fineGroupsX);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    InstancesId,
                    instances);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    ClustersId,
                    clusters);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    ClusterViewMasksId,
                    clusterViewMasks);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    ViewPlanesId,
                    viewPlanes);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    ViewParametersId,
                    viewParameters);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    KeysId,
                    keys);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    ValuesId,
                    values);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    BinCountsId,
                    groupCounts);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    HierarchyStatisticsId,
                    hierarchyStatistics);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    HierarchyValidityId,
                    hierarchyValidity);
                commands.SetComputeBufferParam(
                    shader,
                    classifyClusterInstancesKernel,
                    DiagnosticsId,
                    diagnostics);
                commands.DispatchCompute(
                    shader,
                    classifyClusterInstancesKernel,
                    fineGroupsX,
                    fineGroupsY,
                    1);
                EndSample(commands, HierarchicalFineSample);
            }

            binner.RecordPrecountedPrefixIndirect(
                commands,
                keys,
                values,
                groupCounts,
                groupOffsets,
                groupedInstanceIndices,
                hierarchyBinningDiagnostics,
                hierarchyStatistics,
                HierarchicalVisiblePairCountWord,
                hierarchyValidationDispatchArguments,
                0u,
                hierarchyScatterDispatchArguments,
                0u,
                visibleBinCount,
                scanBackend);

            commands.SetComputeBufferParam(
                shader,
                mergeHierarchyDiagnosticsKernel,
                BinningDiagnosticsId,
                hierarchyBinningDiagnostics);
            commands.SetComputeBufferParam(
                shader,
                mergeHierarchyDiagnosticsKernel,
                DiagnosticsId,
                diagnostics);
            commands.DispatchCompute(
                shader,
                mergeHierarchyDiagnosticsKernel,
                1,
                1,
                1);

            RecordBuildHierarchicalIndirectArguments(
                commands,
                drawTemplates,
                groupCounts,
                groupOffsets,
                indirectArguments,
                diagnostics,
                visibleBinCount,
                drawGroupCount);
            EndSample(commands, HierarchicalSample);
            EndSample(commands, PipelineSample);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            hierarchyScatterDispatchArguments?.Dispose();
            hierarchyValidationDispatchArguments?.Dispose();
            hierarchyBinningDiagnostics?.Dispose();
            hierarchyValidity?.Dispose();
            clusterViewMasks?.Dispose();
            values?.Dispose();
            keys?.Dispose();
            binner?.Dispose();
        }

        private void RecordClearDiagnostics(
            CommandBuffer commands,
            GraphicsBuffer diagnostics)
        {
            RecordClearBuffer(
                commands,
                diagnostics,
                DiagnosticWordCount);
        }

        private void RecordClearBuffer(
            CommandBuffer commands,
            GraphicsBuffer buffer,
            int count)
        {
            if (count <= 0)
            {
                return;
            }
            commands.SetComputeIntParam(shader, ClearCountId, count);
            commands.SetComputeBufferParam(
                shader,
                clearUintKernel,
                ClearBufferId,
                buffer);
            commands.DispatchCompute(
                shader,
                clearUintKernel,
                DivideRoundUp(count, ThreadGroupSize),
                1,
                1);
        }

        private void RecordBuildIndirectArguments(
            CommandBuffer commands,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer indirectArguments,
            int visibleBinCount,
            int drawGroupCount)
        {
            BeginSample(commands, ArgumentsSample);
            commands.SetComputeIntParam(
                shader,
                DrawGroupCountId,
                drawGroupCount);
            commands.SetComputeIntParam(
                shader,
                VisibleBinCountId,
                visibleBinCount);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                DrawTemplatesId,
                drawTemplates);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                BinCountsId,
                groupCounts);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                BinOffsetsId,
                groupOffsets);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                IndirectArgumentsId,
                indirectArguments);
            commands.DispatchCompute(
                shader,
                buildIndirectArgumentsKernel,
                DivideRoundUp(visibleBinCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, ArgumentsSample);
        }

        private void RecordBuildHierarchicalIndirectArguments(
            CommandBuffer commands,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer diagnostics,
            int visibleBinCount,
            int drawGroupCount)
        {
            BeginSample(commands, ArgumentsSample);
            commands.SetComputeIntParam(
                shader,
                DrawGroupCountId,
                drawGroupCount);
            commands.SetComputeIntParam(
                shader,
                VisibleBinCountId,
                visibleBinCount);
            commands.SetComputeBufferParam(
                shader,
                buildHierarchicalIndirectArgumentsKernel,
                DrawTemplatesId,
                drawTemplates);
            commands.SetComputeBufferParam(
                shader,
                buildHierarchicalIndirectArgumentsKernel,
                BinCountsId,
                groupCounts);
            commands.SetComputeBufferParam(
                shader,
                buildHierarchicalIndirectArgumentsKernel,
                BinOffsetsId,
                groupOffsets);
            commands.SetComputeBufferParam(
                shader,
                buildHierarchicalIndirectArgumentsKernel,
                IndirectArgumentsId,
                indirectArguments);
            commands.SetComputeBufferParam(
                shader,
                buildHierarchicalIndirectArgumentsKernel,
                DiagnosticsId,
                diagnostics);
            commands.DispatchCompute(
                shader,
                buildHierarchicalIndirectArgumentsKernel,
                DivideRoundUp(visibleBinCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, ArgumentsSample);
        }

        private void ValidateRecordArguments(
            CommandBuffer commands,
            GraphicsBuffer instances,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer groupedInstanceIndices,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer diagnostics,
            int instanceCount,
            int viewCount,
            int drawGroupCount,
            GpuPrimitiveBackend scanBackend,
            GpuDrivenInstanceOutputMode outputMode)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (instanceCount < 0 || instanceCount > InstanceCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(instanceCount),
                    $"Instance count must be in [0, {InstanceCapacity}].");
            }
            if (viewCount < 1 || viewCount > ViewCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(viewCount),
                    $"View count must be in [1, {ViewCapacity}].");
            }
            if (drawGroupCount < 1 ||
                drawGroupCount > DrawGroupCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawGroupCount),
                    "Draw-group count exceeds the configured capacity.");
            }
            if (scanBackend != GpuPrimitiveBackend.Auto &&
                scanBackend != GpuPrimitiveBackend.Portable &&
                scanBackend != GpuPrimitiveBackend.WaveOps)
            {
                throw new ArgumentOutOfRangeException(nameof(scanBackend));
            }
            if (outputMode != GpuDrivenInstanceOutputMode.CulledTail &&
                outputMode != GpuDrivenInstanceOutputMode.VisibleOnly)
            {
                throw new ArgumentOutOfRangeException(nameof(outputMode));
            }

            int pairCount = checked(instanceCount * viewCount);
            int visibleBinCount = checked(viewCount * drawGroupCount);
            int totalBinCount = outputMode ==
                GpuDrivenInstanceOutputMode.VisibleOnly
                    ? visibleBinCount
                    : checked(visibleBinCount + 1);
            ValidateStructuredBuffer(
                instances,
                instanceCount,
                GpuInstanceState.Stride,
                nameof(instances));
            ValidateStructuredBuffer(
                viewPlanes,
                checked(viewCount * FrustumPlaneCount),
                sizeof(float) * 4,
                nameof(viewPlanes));
            ValidateStructuredBuffer(
                viewParameters,
                viewCount,
                sizeof(float) * 4,
                nameof(viewParameters));
            ValidateStructuredBuffer(
                drawTemplates,
                drawGroupCount,
                GpuDrawTemplate.Stride,
                nameof(drawTemplates));
            ValidateStructuredBuffer(
                groupCounts,
                totalBinCount,
                sizeof(uint),
                nameof(groupCounts));
            ValidateStructuredBuffer(
                groupOffsets,
                checked(totalBinCount + 1),
                sizeof(uint),
                nameof(groupOffsets));
            ValidateStructuredBuffer(
                groupedInstanceIndices,
                pairCount,
                sizeof(uint),
                nameof(groupedInstanceIndices));
            ValidateIndirectArgumentsBuffer(
                indirectArguments,
                checked(visibleBinCount * IndirectArgumentWordCount),
                nameof(indirectArguments));
            ValidateStructuredBuffer(
                diagnostics,
                DiagnosticWordCount,
                sizeof(uint),
                nameof(diagnostics));

            RequireNotInput(
                groupCounts,
                nameof(groupCounts),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                groupOffsets,
                nameof(groupOffsets),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                indirectArguments,
                nameof(indirectArguments),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                diagnostics,
                nameof(diagnostics),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);

            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                groupOffsets,
                nameof(groupOffsets));
            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                groupedInstanceIndices,
                nameof(groupedInstanceIndices));
            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                groupedInstanceIndices,
                nameof(groupedInstanceIndices));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                indirectArguments,
                nameof(indirectArguments),
                diagnostics,
                nameof(diagnostics));
        }

        private void ValidateHierarchicalRecordArguments(
            CommandBuffer commands,
            GraphicsBuffer instances,
            GraphicsBuffer clusters,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer groupedInstanceIndices,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer hierarchyStatistics,
            GraphicsBuffer diagnostics,
            int instanceCount,
            int clusterCount,
            int viewCount,
            int drawGroupCount,
            GpuPrimitiveBackend scanBackend)
        {
            ThrowIfDisposed();
            if (!SupportsHierarchicalVisibleOnly)
            {
                throw new InvalidOperationException(
                    "Hierarchical visible-only recording was not enabled " +
                    "for this pipeline. Construct it with a positive " +
                    "hierarchicalClusterCapacity.");
            }

            ValidateRecordArguments(
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
                drawGroupCount,
                scanBackend,
                GpuDrivenInstanceOutputMode.VisibleOnly);

            int minimumClusterCount = instanceCount == 0
                ? 0
                : DivideRoundUp(
                    instanceCount,
                    GpuInstanceCluster.MaximumInstanceCount);
            int maximumClusterCount = instanceCount;
            if (clusterCount < minimumClusterCount ||
                clusterCount > maximumClusterCount ||
                clusterCount > HierarchicalClusterCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(clusterCount),
                    $"Cluster count must be in [{minimumClusterCount}, " +
                    $"{Math.Min(maximumClusterCount, HierarchicalClusterCapacity)}] " +
                    "and fit the configured hierarchy capacity.");
            }
            ValidateStructuredBuffer(
                clusters,
                clusterCount,
                GpuInstanceCluster.Stride,
                nameof(clusters));
            ValidateStructuredBuffer(
                hierarchyStatistics,
                HierarchyStatisticWordCount,
                sizeof(uint),
                nameof(hierarchyStatistics));

            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                clusters,
                nameof(clusters));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                clusters,
                nameof(clusters));
            RequireDistinct(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                clusters,
                nameof(clusters));
            RequireDistinct(
                indirectArguments,
                nameof(indirectArguments),
                clusters,
                nameof(clusters));
            RequireDistinct(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                clusters,
                nameof(clusters));
            RequireDistinct(
                diagnostics,
                nameof(diagnostics),
                clusters,
                nameof(clusters));

            RequireNotInput(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireDistinct(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                groupCounts,
                nameof(groupCounts));
            RequireDistinct(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                groupOffsets,
                nameof(groupOffsets));
            RequireDistinct(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                groupedInstanceIndices,
                nameof(groupedInstanceIndices));
            RequireDistinct(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                hierarchyStatistics,
                nameof(hierarchyStatistics),
                diagnostics,
                nameof(diagnostics));
        }

        private void BeginSample(
            CommandBuffer commands,
            string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.BeginSample(sampleName);
            }
        }

        private void EndSample(
            CommandBuffer commands,
            string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.EndSample(sampleName);
            }
        }

        private static void ValidatePositiveCapacity(
            int capacity,
            string parameterName)
        {
            if (capacity < 1 ||
                capacity >
                global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private static void ValidateViewCapacity(int viewCapacity)
        {
            if (viewCapacity < 1 || viewCapacity > MaximumViewCount)
            {
                throw new ArgumentOutOfRangeException(nameof(viewCapacity));
            }
        }

        private static int CheckedProduct(
            int first,
            int second,
            string parameterName)
        {
            long product = checked((long)first * second);
            if (product < 1L ||
                product >
                global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The configured capacity product exceeds the supported " +
                    "GPU primitive capacity.");
            }
            return checked((int)product);
        }

        private static void ValidateStructuredBuffer(
            GraphicsBuffer buffer,
            int requiredCount,
            int requiredStride,
            string parameterName)
        {
            int allocationCount = Math.Max(1, requiredCount);
            if (buffer == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != requiredStride ||
                buffer.count < allocationCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a structured GraphicsBuffer " +
                    $"with stride {requiredStride} and at least " +
                    $"{allocationCount} elements.",
                    parameterName);
            }
        }

        private static void ValidateIndirectArgumentsBuffer(
            GraphicsBuffer buffer,
            int requiredCount,
            string parameterName)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            GraphicsBuffer.Target requiredTargets =
                GraphicsBuffer.Target.Structured |
                GraphicsBuffer.Target.IndirectArguments;
            if ((buffer.target & requiredTargets) != requiredTargets ||
                buffer.stride != sizeof(uint) ||
                buffer.count < requiredCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a structured indirect-" +
                    $"arguments GraphicsBuffer with stride {sizeof(uint)} " +
                    $"and at least {requiredCount} elements.",
                    parameterName);
            }
        }

        private static void RequireNotInput(
            GraphicsBuffer writable,
            string writableName,
            GraphicsBuffer instances,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates)
        {
            RequireDistinct(
                writable,
                writableName,
                instances,
                nameof(instances));
            RequireDistinct(
                writable,
                writableName,
                viewPlanes,
                nameof(viewPlanes));
            RequireDistinct(
                writable,
                writableName,
                viewParameters,
                nameof(viewParameters));
            RequireDistinct(
                writable,
                writableName,
                drawTemplates,
                nameof(drawTemplates));
        }

        private static void RequireDistinct(
            GraphicsBuffer first,
            string firstName,
            GraphicsBuffer second,
            string secondName)
        {
            if (ReferenceEquals(first, second))
            {
                throw new ArgumentException(
                    $"{firstName} must not alias {secondName}.",
                    firstName);
            }
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuDrivenInstancePipeline));
            }
        }
    }
}
