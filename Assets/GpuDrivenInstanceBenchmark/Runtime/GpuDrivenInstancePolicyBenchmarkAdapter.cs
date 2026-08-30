using System;
using System.Diagnostics;
using Summit.GpuAutotuning;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

internal readonly struct GpuDrivenInstancePolicyPreparationReceipt
{
    internal GpuDrivenInstancePolicyPreparationReceipt(
        int slotIndex,
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
        GpuDrivenInstancePolicyUpdatePlan updatePlan,
        uint logicalOrdinal,
        int stateRecordsWritten,
        ulong stateRevision,
        ulong expectedStateHash)
    {
        SlotIndex = slotIndex;
        BenchmarkCase = benchmarkCase;
        UpdatePlan = updatePlan;
        LogicalOrdinal = logicalOrdinal;
        StateRecordsWritten = stateRecordsWritten;
        StateRevision = stateRevision;
        ExpectedStateHash = expectedStateHash;
        UpdateHash = GpuDrivenInstancePolicyInputGenerator
            .ComputeUpdateHash(updatePlan, logicalOrdinal);
    }

    internal int SlotIndex { get; }

    internal GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase { get; }

    internal GpuDrivenInstancePolicyUpdatePlan UpdatePlan { get; }

    internal uint LogicalOrdinal { get; }

    internal int StateRecordsWritten { get; }

    internal ulong StateRevision { get; }

    internal ulong ExpectedStateHash { get; }

    internal ulong UpdateHash { get; }
}

internal readonly struct GpuDrivenInstancePolicyRecordReceipt
{
    internal GpuDrivenInstancePolicyRecordReceipt(
        int slotIndex,
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
        uint logicalOrdinal,
        GpuDrivenInstancePolicyResolvedDecision decision,
        GpuInstanceUploadReceipt plannedUpload,
        GpuInstanceUploadReceipt recordedUpload,
        long planCpuTicks,
        long selectorCpuTicks,
        long recordCpuTicks,
        bool selectorInvoked,
        int renderApiCallCount,
        int logicalDrawCommandCount)
    {
        SlotIndex = slotIndex;
        BenchmarkCase = benchmarkCase;
        LogicalOrdinal = logicalOrdinal;
        Decision = decision;
        PlannedUpload = plannedUpload;
        RecordedUpload = recordedUpload;
        PlanCpuTicks = planCpuTicks;
        SelectorCpuTicks = selectorCpuTicks;
        RecordCpuTicks = recordCpuTicks;
        SelectorInvoked = selectorInvoked;
        RenderApiCallCount = renderApiCallCount;
        LogicalDrawCommandCount = logicalDrawCommandCount;
    }

    internal int SlotIndex { get; }

    internal GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase { get; }

    internal uint LogicalOrdinal { get; }

    internal GpuDrivenInstancePolicyResolvedDecision Decision { get; }

    /// <summary>
    /// Exact receipt observed by the selector before any upload was recorded.
    /// </summary>
    internal GpuInstanceUploadReceipt PlannedUpload { get; }

    /// <summary>
    /// Exact receipt for the upload path actually recorded by the decision.
    /// </summary>
    internal GpuInstanceUploadReceipt RecordedUpload { get; }

    internal long PlanCpuTicks { get; }

    internal long SelectorCpuTicks { get; }

    internal long RecordCpuTicks { get; }

    internal bool SelectorInvoked { get; }

    internal int RenderApiCallCount { get; }

    internal int LogicalDrawCommandCount { get; }
}

internal sealed class GpuDrivenInstancePolicyValidationResult
{
    internal string Phase;
    internal string CaseId;
    internal bool Passed;
    internal string Message;
    internal long ReadbackBytes;
    internal string ExpectedOutputHash;
    internal string ActualOutputHash;
    internal ulong ExpectedStateHash;
    internal ulong ActualStateHash;
    internal uint InvalidKeyCount;
    internal uint DiagnosticFlags;
    internal bool HierarchyStatisticsAvailable;
    internal uint ExpectedCoarseVisibleClusterViewCount;
    internal uint ExpectedCandidateInstanceViewCount;
    internal uint ExpectedHierarchicalVisiblePairCount;
    internal uint CoarseVisibleClusterViewCount;
    internal uint CandidateInstanceViewCount;
    internal uint HierarchicalVisiblePairCount;
}

/// <summary>
/// Real upload, flat/hierarchical culling, and engine-indirect replay adapter
/// used to calibrate and hold out the automatic instance policy.
/// </summary>
/// <remarks>
/// Preparation owns no GPU measurement claims. RecordWorkload creates one
/// actual planned upload token, invokes the selector only for ActualAuto,
/// records the chosen upload and visibility paths, copies indirect arguments,
/// and records the same engine draw loop for every case. Validation readbacks
/// are an explicit post-submit API and never occur in RecordWorkload.
///
/// Each slot owns Persistent state and a command buffer. Only one slot may be
/// acquired but not yet submitted, because a newer uploader plan invalidates
/// an outstanding token. Submitted slots remain immutable until their
/// AllGPUOperations fence passes; other completed slots may then be queued.
/// The caller must call AppendLifetimeFence, execute Commands(slot) exactly
/// once, and then immediately call MarkWorkSlotSubmitted. The slot retains the
/// exact appended fence; callers cannot substitute a different or default
/// fence at submission.
/// </remarks>
internal sealed class GpuDrivenInstancePolicyBenchmarkAdapter : IDisposable
{
    internal const int DefaultStagingSlotCount = 8;
    internal const int FixedDrawGroupCount = 8;
    internal const int RenderTargetSize = 512;
    internal const GraphicsFenceType LifetimeFenceType =
        GraphicsFenceType.AsyncQueueSynchronisation;
    internal static readonly long StopwatchFrequency = Stopwatch.Frequency;

    private const string ShaderResource =
        "GpuDrivenInstanceBenchmark/GpuDrivenInstanceMacro";
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const uint LayoutRevisionDomain = 0x8F31A6D5u;
    private const uint VisibilityRevisionDomain = 0x6B46C2E9u;

    private int instanceCount;
    private int viewCount;
    private int drawGroupCount;
    private int visibleBinCount;
    private int clusterCount;
    private int dirtyBasisPoints;
    private int seed;
    private int visibleInstanceCount;
    private int visiblePairCount;
    private GpuDrivenInstanceOutputMode requiredOutputMode;
    private ulong baseStateRevision;
    private ulong instanceLayoutRevision;
    private ulong visibilityInputRevision;
    private bool hasResidentState;
    private int residentInstanceCount;
    private ulong residentStateRevision;
    private int activeSlotIndex = -1;
    private int nextSlot;
    private int consecutiveSlotWaitFrames;
    private int lastSubmittedSlotIndex = -1;
    private ulong submissionSerial;
    private GpuDrivenInstancePolicySelector selector;
    private GpuDrivenInstancePolicyState selectorState;
    private GpuPrimitiveBackendResolver primitiveResolver;
    private string primitiveWorkloadId;
    private GpuDrivenInstancePolicyObservation selectorOverheadObservation;
    private GpuDrivenInstanceExpectedResult expectedOutput;
    private GpuDrivenInstanceExpectedResult visibleOnlyExpected;
    private uint expectedCoarseVisibleClusterViewCount;
    private uint expectedCandidateInstanceViewCount;
    private Vector4[] viewParameterData;
    private NativeArray<GpuInstanceState> immutableBase;
    private WorkSlot[] workSlots;
    private GpuInstanceStateUploader uploader;
    private GpuDrivenInstancePipeline pipeline;
    private GraphicsBuffer instances;
    private GraphicsBuffer clusters;
    private GraphicsBuffer viewPlanes;
    private GraphicsBuffer viewParameters;
    private GraphicsBuffer drawTemplates;
    private GraphicsBuffer groupCounts;
    private GraphicsBuffer groupOffsets;
    private GraphicsBuffer groupedInstanceIndices;
    private GraphicsBuffer indirectArgumentWords;
    private GraphicsBuffer renderIndirectArguments;
    private GraphicsBuffer hierarchyStatistics;
    private GraphicsBuffer diagnostics;
    private Mesh mesh;
    private Material gpuMaterial;
    private RenderTexture renderTarget;
    private GameObject[] cameraHosts;
    private Camera[] cameras;
    private MaterialPropertyBlock[] gpuDrawProperties;
    private PendingValidation pendingValidation;
    private bool disposed;

    internal GpuDrivenInstancePolicyBenchmarkAdapter(
        int instanceCount,
        int viewCount,
        string visibility,
        int seed,
        int dirtyBasisPoints,
        GpuDrivenInstanceOutputMode requiredOutputMode,
        GpuDrivenInstancePolicySelector selector,
        int drawGroupCount = FixedDrawGroupCount,
        int stagingSlotCount = DefaultStagingSlotCount,
        int maximumMergedGapRecords = 0,
        GpuPrimitiveBackendResolver primitiveResolver = null,
        string primitiveWorkloadId = null)
    {
        ValidateConstructionInputs(
            instanceCount,
            viewCount,
            visibility,
            dirtyBasisPoints,
            requiredOutputMode,
            drawGroupCount,
            stagingSlotCount,
            maximumMergedGapRecords);

        this.instanceCount = instanceCount;
        this.viewCount = viewCount;
        this.drawGroupCount = drawGroupCount;
        this.dirtyBasisPoints = dirtyBasisPoints;
        this.seed = seed;
        this.requiredOutputMode = requiredOutputMode;
        this.selector = selector;
        this.primitiveResolver = primitiveResolver;
        this.primitiveWorkloadId = primitiveWorkloadId ?? string.Empty;
        visibleBinCount = checked(viewCount * drawGroupCount);

        try
        {
            mesh = CreateCubeMesh();
            var instanceData = new GpuInstanceState[instanceCount];
            var planeData = new Vector4[checked(
                viewCount * GpuDrivenInstancePipeline.FrustumPlaneCount)];
            viewParameterData = new Vector4[viewCount];
            var drawData = new GpuDrawTemplate[drawGroupCount];
            int generatedVisibleInstanceCount =
                GpuDrivenInstanceHierarchicalInputGenerator.Populate(
                    instanceData,
                    planeData,
                    viewParameterData,
                    drawData,
                    visibility,
                    seed);
            for (int group = 0; group < drawGroupCount; group++)
            {
                drawData[group] = new GpuDrawTemplate(
                    mesh.GetIndexCount(0),
                    mesh.GetIndexStart(0),
                    checked((uint)mesh.GetBaseVertex(0)));
            }

            visibleOnlyExpected = GpuDrivenInstanceBenchmarkCpuOracle.Build(
                instanceData,
                planeData,
                viewParameterData,
                drawData,
                GpuDrivenInstanceOutputMode.VisibleOnly);
            expectedOutput = requiredOutputMode ==
                GpuDrivenInstanceOutputMode.VisibleOnly
                ? visibleOnlyExpected
                : GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instanceData,
                    planeData,
                    viewParameterData,
                    drawData,
                    GpuDrivenInstanceOutputMode.CulledTail);
            visiblePairCount = visibleOnlyExpected
                .GroupedInstanceIndices.Length;
            visibleInstanceCount = CountDistinctVisibleInstances(
                visibleOnlyExpected.GroupedInstanceIndices,
                instanceCount);
            if (visibleInstanceCount != generatedVisibleInstanceCount)
            {
                throw new InvalidOperationException(
                    "The hierarchical input generator and CPU oracle " +
                    "disagree on visible instance count.");
            }

            immutableBase = new NativeArray<GpuInstanceState>(
                instanceData,
                Allocator.Persistent);
            clusterCount = GpuInstanceClusterBuilder
                .GetRequiredClusterCount(
                    instanceCount,
                    GpuDrivenInstanceHierarchicalInputGenerator
                        .InstancesPerCluster);
            GpuInstanceCluster[] clusterData = BuildClusters(
                immutableBase,
                clusterCount);
            GpuDrivenInstanceHierarchyExpectedStatistics expectedStatistics =
                GpuDrivenInstanceHierarchicalInputGenerator
                    .ComputeExpectedHierarchyStatistics(
                        instanceData,
                        clusterData,
                        planeData,
                        viewCount);
            expectedCoarseVisibleClusterViewCount =
                expectedStatistics.CoarseVisibleClusterViewCount;
            expectedCandidateInstanceViewCount =
                expectedStatistics.CandidateInstanceViewCount;

            instanceLayoutRevision = ComputeLayoutRevision(
                instanceData,
                clusterData);
            visibilityInputRevision = ComputeVisibilityRevision(
                instanceLayoutRevision,
                planeData,
                viewParameterData);
            baseStateRevision = NormalizeRevision(
                ComputeStateHash(immutableBase, instanceCount));
            hasResidentState = true;
            residentInstanceCount = instanceCount;
            residentStateRevision = baseStateRevision;

            workSlots = new WorkSlot[stagingSlotCount];
            for (int index = 0; index < workSlots.Length; index++)
            {
                workSlots[index] = new WorkSlot(
                    immutableBase,
                    GpuDrivenInstancePolicyInputGenerator
                        .MaximumRangeCount,
                    index);
            }

            uploader = new GpuInstanceStateUploader(
                GpuDrivenInstancePolicyInputGenerator.MaximumRangeCount,
                maximumMergedGapRecords);
            CreateGpuResources(
                clusterData,
                planeData,
                viewParameterData,
                drawData);
            pipeline = new GpuDrivenInstancePipeline(
                instanceCount,
                viewCount,
                drawGroupCount,
                clusterCount,
                emitProfilerMarkers: false);
            selectorOverheadObservation =
                BuildSelectorOverheadObservation();
            CreateRenderingResources();
        }
        catch
        {
            DisposeCreatedResources();
            disposed = true;
            throw;
        }
    }

    internal int InstanceCount => instanceCount;

    internal int ViewCount => viewCount;

    internal int DrawGroupCount => drawGroupCount;

    internal int DirtyBasisPoints => dirtyBasisPoints;

    internal int VisibleInstanceCount => visibleInstanceCount;

    internal int VisiblePairCount => visiblePairCount;

    internal int ClusterCount => clusterCount;

    internal uint ExpectedCoarseVisibleClusterViewCount =>
        expectedCoarseVisibleClusterViewCount;

    internal uint ExpectedCandidateInstanceViewCount =>
        expectedCandidateInstanceViewCount;

    internal int HierarchyCandidateBasisPoints => checked((int)(
        (long)expectedCandidateInstanceViewCount *
        GpuDrivenInstancePolicyContract.BasisPointScale /
        checked((long)instanceCount * viewCount)));

    internal int StagingSlotCount => workSlots.Length;

    internal GpuDrivenInstanceOutputMode RequiredOutputMode =>
        requiredOutputMode;

    internal string ExpectedOutputHash => expectedOutput.ResultHash;

    internal ulong InstanceLayoutRevision => instanceLayoutRevision;

    internal ulong ClusterLayoutRevision => instanceLayoutRevision;

    internal ulong VisibilityInputRevision => visibilityInputRevision;

    internal ulong VisibilityEstimateRevision => visibilityInputRevision;

    internal bool HasResidentState => hasResidentState;

    internal ulong ResidentStateRevision => residentStateRevision;

    internal GraphicsBuffer InstanceStateBuffer => instances;

    internal RenderTexture RenderTarget => renderTarget;

    internal long PersistentStagingPayloadBytes => checked(
        (long)(workSlots.Length + 1) *
        instanceCount * GpuInstanceState.Stride +
        (long)workSlots.Length *
        GpuDrivenInstancePolicyInputGenerator.MaximumRangeCount *
        sizeof(int) * 2L);

    internal bool TryAcquireWorkSlot(
        out int slotIndex,
        out int waitFrames)
    {
        ThrowIfDisposed();
        if (pendingValidation != null || activeSlotIndex >= 0)
        {
            consecutiveSlotWaitFrames = checked(
                consecutiveSlotWaitFrames + 1);
            slotIndex = -1;
            waitFrames = consecutiveSlotWaitFrames;
            return false;
        }

        for (int attempt = 0; attempt < workSlots.Length; attempt++)
        {
            int candidate = (nextSlot + attempt) % workSlots.Length;
            if (!workSlots[candidate].TryAcquire())
            {
                continue;
            }

            activeSlotIndex = candidate;
            nextSlot = (candidate + 1) % workSlots.Length;
            slotIndex = candidate;
            waitFrames = consecutiveSlotWaitFrames;
            consecutiveSlotWaitFrames = 0;
            return true;
        }

        consecutiveSlotWaitFrames = checked(
            consecutiveSlotWaitFrames + 1);
        slotIndex = -1;
        waitFrames = consecutiveSlotWaitFrames;
        return false;
    }

    internal CommandBuffer Commands(int slotIndex)
    {
        ThrowIfDisposed();
        return GetSlot(slotIndex).Commands;
    }

    internal void ResetSelectorState()
    {
        ThrowIfDisposed();
        selectorState.Reset();
    }

    /// <summary>
    /// Returns immutable facts backed by one real, unconsumed planned-upload
    /// token. It is only for the construction-time selector-only CPU overhead
    /// loop. The first workload plan intentionally invalidates this token.
    /// </summary>
    internal GpuDrivenInstancePolicyObservation
        CreateSelectorOverheadObservation()
    {
        ThrowIfDisposed();
        if (!selectorOverheadObservation.UploadPlan.TokenIsValid)
        {
            throw new InvalidOperationException(
                "The selector-overhead upload plan is no longer live. " +
                "Measure selector overhead before recording any workload.");
        }
        return selectorOverheadObservation;
    }

    internal GpuDrivenInstancePolicyPreparationReceipt PrepareSlot(
        int slotIndex,
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
        uint logicalOrdinal)
    {
        ThrowIfDisposed();
        RequireActiveSlot(slotIndex);
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequireAcquired();

        GpuDrivenInstancePolicyUpdatePlan updatePlan =
            GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                instanceCount,
                dirtyBasisPoints,
                seed,
                slot.DirtyRanges);
        GpuDrivenInstancePolicyInputGenerator.PopulateFullState(
            immutableBase.AsReadOnly(),
            slot.States,
            slot.DirtyRanges,
            updatePlan,
            logicalOrdinal);
        ulong expectedStateHash = ComputeStateHash(
            slot.States,
            instanceCount);
        ulong stateRevision = NormalizeRevision(expectedStateHash);
        slot.MarkPrepared(
            benchmarkCase,
            updatePlan,
            logicalOrdinal,
            stateRevision,
            expectedStateHash);

        return new GpuDrivenInstancePolicyPreparationReceipt(
            slotIndex,
            benchmarkCase,
            updatePlan,
            logicalOrdinal,
            checked(instanceCount + updatePlan.ChangedInstanceCount),
            stateRevision,
            expectedStateHash);
    }

    /// <summary>
    /// Records one planned upload, selected dispatch, indirect-argument copy,
    /// and engine draw loop. It never issues validation readbacks.
    /// </summary>
    internal GpuDrivenInstancePolicyRecordReceipt RecordWorkload(
        int slotIndex)
    {
        ThrowIfDisposed();
        RequireActiveSlot(slotIndex);
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequirePrepared();
        if (slot.BenchmarkCase.InvokesSelector && selector == null)
        {
            throw new InvalidOperationException(
                "ActualAuto requires a policy selector.");
        }

        long planStart = Stopwatch.GetTimestamp();
        GpuInstanceDirtyUploadPlan uploadPlan =
            residentStateRevision != 0UL
                ? uploader.PlanDirtyUpload(
                    instances,
                    slot.States,
                    instanceCount,
                    slot.StateRevision,
                    residentStateRevision,
                    slot.DirtyRanges,
                    slot.UpdatePlan.RangeCount)
                : uploader.PlanDirtyUpload(
                    instances,
                    slot.States,
                    instanceCount,
                    slot.StateRevision,
                    slot.DirtyRanges,
                    slot.UpdatePlan.RangeCount);
        GpuDrivenInstancePolicyObservation observation = BuildObservation(
            slot,
            in uploadPlan);
        long planCpuTicks = checked(
            Stopwatch.GetTimestamp() - planStart);

        bool selectorInvoked = false;
        long selectorCpuTicks = 0L;
        GpuDrivenInstancePolicyResolvedDecision decision;
        switch (slot.BenchmarkCase.Kind)
        {
            case GpuDrivenInstancePolicyBenchmarkCaseKind.SafeBaseline:
                decision = ResolveSafeBaseline(in observation);
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedSelected:
                GpuDrivenInstancePolicyDecision supplied =
                    slot.BenchmarkCase.SuppliedDecision;
                decision = ComposePolicyDecision(
                    in supplied,
                    GpuDrivenInstancePolicyDecisionSource.ForcedSelected,
                    observation.SupportsWaveOps);
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind.ActualAuto:
                long selectorStart = Stopwatch.GetTimestamp();
                GpuDrivenInstancePolicyDecision selected = selector.Select(
                    in observation,
                    ref selectorState);
                selectorCpuTicks = checked(
                    Stopwatch.GetTimestamp() - selectorStart);
                selectorInvoked = true;
                decision = ComposePolicyDecision(
                    in selected,
                    GpuDrivenInstancePolicyDecisionSource.ActualAuto,
                    observation.SupportsWaveOps);
                break;
            default:
                decision = slot.BenchmarkCase.ResolveCalibration(
                    requiredOutputMode);
                break;
        }
        decision = ComposePrimitiveForNonPolicyCase(
            in decision,
            observation.SupportsWaveOps);

        long recordStart = Stopwatch.GetTimestamp();
        ValidateDecision(in decision, in observation, in uploadPlan);
        GpuInstanceUploadReceipt recordedUpload = RecordUpload(
            slot,
            in decision,
            in uploadPlan);
        RecordPipelineAndDraws(slot.Commands, in decision);
        long recordCpuTicks = checked(
            Stopwatch.GetTimestamp() - recordStart);
        slot.MarkWorkloadRecorded(
            in decision,
            uploadPlan.Receipt,
            recordedUpload,
            planCpuTicks,
            selectorCpuTicks,
            recordCpuTicks,
            selectorInvoked);

        return new GpuDrivenInstancePolicyRecordReceipt(
            slotIndex,
            slot.BenchmarkCase,
            slot.LogicalOrdinal,
            decision,
            uploadPlan.Receipt,
            recordedUpload,
            planCpuTicks,
            selectorCpuTicks,
            recordCpuTicks,
            selectorInvoked,
            visibleBinCount,
            visibleBinCount);
    }

    private bool UsesPrimitiveProfile =>
        primitiveResolver != null &&
        !string.IsNullOrWhiteSpace(primitiveWorkloadId);

    private GpuDrivenInstancePolicyResolvedDecision ComposePolicyDecision(
        in GpuDrivenInstancePolicyDecision decision,
        GpuDrivenInstancePolicyDecisionSource source,
        bool supportsWaveOps)
    {
        if (!UsesPrimitiveProfile)
        {
            return GpuDrivenInstancePolicyResolvedDecision.FromPolicy(
                in decision,
                source);
        }
        GpuDrivenInstanceExecutionPolicy composed =
            GpuDrivenInstancePolicyComposition.Compose(
                in decision,
                requiredOutputMode,
                primitiveResolver,
                primitiveWorkloadId,
                supportsWaveOps);
        return GpuDrivenInstancePolicyResolvedDecision.FromExecutionPolicy(
            in composed,
            source);
    }

    private GpuDrivenInstancePolicyResolvedDecision
        ComposePrimitiveForNonPolicyCase(
            in GpuDrivenInstancePolicyResolvedDecision decision,
            bool supportsWaveOps)
    {
        if (!UsesPrimitiveProfile ||
            decision.Source ==
                GpuDrivenInstancePolicyDecisionSource.ForcedSelected ||
            decision.Source ==
                GpuDrivenInstancePolicyDecisionSource.ActualAuto)
        {
            return decision;
        }
        bool accepted = primitiveResolver.TryResolveMeasured(
            primitiveWorkloadId,
            out GpuPrimitiveBackend backend) &&
            (backend == GpuPrimitiveBackend.Portable ||
             (backend == GpuPrimitiveBackend.WaveOps && supportsWaveOps));
        return decision.WithPrimitiveBackend(backend, accepted);
    }

    internal GraphicsFence AppendLifetimeFence(int slotIndex)
    {
        ThrowIfDisposed();
        RequireActiveSlot(slotIndex);
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequireWorkloadRecorded();
        GraphicsFence fence = slot.Commands.CreateGraphicsFence(
            LifetimeFenceType,
            SynchronisationStageFlags.AllGPUOperations);
        slot.MarkFenceAppended(fence);
        return fence;
    }

    internal void MarkWorkSlotSubmitted(int slotIndex)
    {
        ThrowIfDisposed();
        RequireActiveSlot(slotIndex);
        WorkSlot slot = GetSlot(slotIndex);
        submissionSerial = NextNonzero(submissionSerial);
        slot.MarkSubmitted(submissionSerial);
        lastSubmittedSlotIndex = slotIndex;
        residentInstanceCount = instanceCount;
        residentStateRevision = slot.StateRevision;
        hasResidentState = true;
        activeSlotIndex = -1;
    }

    internal void AbandonUnsubmittedWorkSlot(int slotIndex)
    {
        ThrowIfDisposed();
        RequireActiveSlot(slotIndex);
        GetSlot(slotIndex).AbandonUnsubmitted();
        activeSlotIndex = -1;
    }

    internal int PendingCompletionFenceCount
    {
        get
        {
            ThrowIfDisposed();
            int count = 0;
            foreach (WorkSlot slot in workSlots)
            {
                if (!slot.TryMakeAvailable())
                {
                    count++;
                }
            }
            return count;
        }
    }

    internal bool AllCompletionFencesPassed =>
        PendingCompletionFenceCount == 0;

    internal ulong ComputeSlotStateHash(int slotIndex)
    {
        ThrowIfDisposed();
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequirePreparedOrLater();
        return ComputeStateHash(slot.States, instanceCount);
    }

    internal static GpuDrivenInstancePolicyResolvedDecision
        ResolveSafeBaseline(
            in GpuDrivenInstancePolicyObservation observation)
    {
        if (observation.RequiredOutputMode !=
                GpuDrivenInstanceOutputMode.CulledTail &&
            observation.RequiredOutputMode !=
                GpuDrivenInstanceOutputMode.VisibleOnly)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "A valid caller-required output mode is required.");
        }

        bool canSkipUpload =
            observation.DirtyInstanceCount == 0 &&
            observation.HasResidentState &&
            observation.StateRevision != 0UL &&
            observation.ResidentInstanceCount ==
                observation.ActiveInstanceCount &&
            observation.ResidentStateRevision ==
                observation.StateRevision;
        return new GpuDrivenInstancePolicyResolvedDecision(
            canSkipUpload
                ? GpuDrivenInstanceUploadMode.None
                : GpuDrivenInstanceUploadMode.Full,
            observation.RequiredOutputMode,
            GpuDrivenInstanceCullingMode.Flat,
            GpuPrimitiveBackend.Portable,
            -1,
            GpuDrivenInstancePolicyDecisionFlags.None,
            GpuDrivenInstancePolicyDecisionSource.SafeBaseline);
    }

    internal static bool ValidateStateHash(
        NativeArray<GpuInstanceState> readback,
        int expectedCount,
        ulong expectedHash,
        out ulong actualHash)
    {
        actualHash = ComputeStateHash(readback, expectedCount);
        return actualHash == expectedHash;
    }

    internal static ulong ComputeStateHash(
        NativeArray<GpuInstanceState> states,
        int count)
    {
        if (!states.IsCreated || count < 0 || count > states.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ulong hash = FnvOffsetBasis;
        hash = HashUInt32(hash, checked((uint)count));
        for (int index = 0; index < count; index++)
        {
            GpuInstanceState state = states[index];
            Vector4 positionRadius = state.PositionRadius;
            Vector4 lodDistances = state.LodDistances;
            hash = HashUInt32(hash, math.asuint(positionRadius.x));
            hash = HashUInt32(hash, math.asuint(positionRadius.y));
            hash = HashUInt32(hash, math.asuint(positionRadius.z));
            hash = HashUInt32(hash, math.asuint(positionRadius.w));
            hash = HashUInt32(hash, math.asuint(lodDistances.x));
            hash = HashUInt32(hash, math.asuint(lodDistances.y));
            hash = HashUInt32(hash, math.asuint(lodDistances.z));
            hash = HashUInt32(hash, math.asuint(lodDistances.w));
            hash = HashUInt32(hash, state.ApplicationId);
            hash = HashUInt32(hash, state.DrawGroupBase);
            hash = HashUInt32(hash, state.LodCount);
            hash = HashUInt32(hash, state.ViewMask);
        }
        return hash;
    }

    /// <summary>
    /// Starts exact readback of state plus all pipeline outputs from the most
    /// recently submitted workload. No newer workload may be acquired until
    /// this validation completes.
    /// </summary>
    internal void BeginValidation(int slotIndex, string phase)
    {
        ThrowIfDisposed();
        if (pendingValidation != null)
        {
            throw new InvalidOperationException(
                "A policy validation readback is already pending.");
        }
        if (activeSlotIndex >= 0)
        {
            throw new InvalidOperationException(
                "Submit or abandon the active slot before validation.");
        }
        if (slotIndex != lastSubmittedSlotIndex)
        {
            throw new InvalidOperationException(
                "Validation is allowed only for the latest submission.");
        }

        WorkSlot slot = GetSlot(slotIndex);
        slot.RequireSubmittedAt(submissionSerial);
        bool readHierarchy = slot.Decision.CullingMode ==
            GpuDrivenInstanceCullingMode.Hierarchy;
        pendingValidation = new PendingValidation
        {
            SlotIndex = slotIndex,
            Phase = phase ?? string.Empty,
            BenchmarkCase = slot.BenchmarkCase,
            Decision = slot.Decision,
            ExpectedStateHash = slot.ExpectedStateHash,
            ReadHierarchyStatistics = readHierarchy,
            ReadbackData = new uint[readHierarchy ? 6 : 5][],
            ReadbackBytes = ValidationReadbackBytes(readHierarchy),
        };
        IssueNextValidationReadback(pendingValidation);
    }

    internal bool TryCompleteValidation(
        out GpuDrivenInstancePolicyValidationResult result)
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
            result.ActualOutputHash = "unavailable";
            return true;
        }

        if (active.NextRequestIndex == 0)
        {
            NativeArray<GpuInstanceState> stateData =
                request.GetData<GpuInstanceState>();
            active.ActualStateHash = ComputeStateHash(
                stateData,
                instanceCount);
        }
        else
        {
            active.ReadbackData[active.NextRequestIndex - 1] =
                ToArray(request.GetData<uint>());
        }
        active.NextRequestIndex++;
        int requestCount = active.ReadHierarchyStatistics ? 7 : 6;
        if (active.NextRequestIndex < requestCount)
        {
            IssueNextValidationReadback(active);
            return false;
        }

        pendingValidation = null;
        result = CompleteValidation(active);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (pendingValidation != null)
        {
            throw new InvalidOperationException(
                "Cannot dispose while validation readback is pending.");
        }
        if (!AllCompletionFencesPassed)
        {
            throw new InvalidOperationException(
                "Cannot dispose policy benchmark resources while a slot " +
                "is submitted or acquired work is unaccounted for.");
        }

        disposed = true;
        DisposeCreatedResources();
    }

    private GpuDrivenInstancePolicyObservation BuildObservation(
        WorkSlot slot,
        in GpuInstanceDirtyUploadPlan uploadPlan)
    {
        return new GpuDrivenInstancePolicyObservation
        {
            RequiredOutputMode = requiredOutputMode,
            ActiveInstanceCount = instanceCount,
            DirtyInstanceCount = slot.UpdatePlan.ChangedInstanceCount,
            DirtyRangeCount = slot.UpdatePlan.RangeCount,
            ViewCount = viewCount,
            VisiblePairCount = visiblePairCount,
            HasResidentState = hasResidentState,
            ResidentInstanceCount = residentInstanceCount,
            StateRevision = slot.StateRevision,
            ResidentStateRevision = residentStateRevision,
            SupportsDirtyRangeUpload = true,
            UploadPlan = GpuDrivenInstanceUploadPlanFacts.Capture(
                in uploadPlan),
            SupportsHierarchy = pipeline.SupportsHierarchicalVisibleOnly,
            ClusterMetadataValid = true,
            ClusterCount = clusterCount,
            ClusterInstanceCount = instanceCount,
            InstanceLayoutRevision = instanceLayoutRevision,
            ClusterLayoutRevision = instanceLayoutRevision,
            VisibilityEstimateValid = true,
            VisibilityInputRevision = visibilityInputRevision,
            VisibilityEstimateRevision = visibilityInputRevision,
            HierarchyCandidateEstimateValid = true,
            HierarchyCandidatePairCount =
                expectedCandidateInstanceViewCount,
            HierarchyCandidateEstimateRevision =
                visibilityInputRevision,
            HierarchyCandidateLayoutRevision = instanceLayoutRevision,
            HierarchyInstanceCapacity = pipeline.InstanceCapacity,
            HierarchyClusterCapacity =
                pipeline.HierarchicalClusterCapacity,
            HierarchyViewCapacity = pipeline.ViewCapacity,
            HierarchyPairCapacity = pipeline.PairCapacity,
            SupportsWaveOps = global::Summit.GpuPrimitives.GpuPrimitives
                .SupportsWaveOperations,
        };
    }

    private GpuDrivenInstancePolicyObservation
        BuildSelectorOverheadObservation()
    {
        using (var ranges = new NativeArray<GpuInstanceDirtyRange>(
                   GpuDrivenInstancePolicyInputGenerator.MaximumRangeCount,
                   Allocator.Temp,
                   NativeArrayOptions.UninitializedMemory))
        {
            GpuDrivenInstancePolicyUpdatePlan updatePlan =
                GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                    instanceCount,
                    dirtyBasisPoints,
                    seed,
                    ranges);
            const uint overheadOrdinal = 1u;
            NativeArray<GpuInstanceState> source = workSlots[0].States;
            GpuDrivenInstancePolicyInputGenerator.PopulateFullState(
                immutableBase.AsReadOnly(),
                source,
                ranges,
                updatePlan,
                overheadOrdinal);
            ulong stateRevision = NormalizeRevision(
                ComputeStateHash(source, instanceCount));
            if (updatePlan.ChangedInstanceCount > 0 &&
                stateRevision == baseStateRevision)
            {
                stateRevision = NextNonzero(stateRevision);
            }
            GpuInstanceDirtyUploadPlan plan = uploader.PlanDirtyUpload(
                instances,
                source,
                instanceCount,
                stateRevision,
                baseStateRevision,
                ranges,
                updatePlan.RangeCount);
            return new GpuDrivenInstancePolicyObservation
            {
                RequiredOutputMode = requiredOutputMode,
                ActiveInstanceCount = instanceCount,
                DirtyInstanceCount = updatePlan.ChangedInstanceCount,
                DirtyRangeCount = updatePlan.RangeCount,
                ViewCount = viewCount,
                VisiblePairCount = visiblePairCount,
                HasResidentState = true,
                ResidentInstanceCount = instanceCount,
                StateRevision = stateRevision,
                ResidentStateRevision = baseStateRevision,
                SupportsDirtyRangeUpload = true,
                UploadPlan = GpuDrivenInstanceUploadPlanFacts.Capture(
                    in plan),
                SupportsHierarchy = pipeline.SupportsHierarchicalVisibleOnly,
                ClusterMetadataValid = true,
                ClusterCount = clusterCount,
                ClusterInstanceCount = instanceCount,
                InstanceLayoutRevision = instanceLayoutRevision,
                ClusterLayoutRevision = instanceLayoutRevision,
                VisibilityEstimateValid = true,
                VisibilityInputRevision = visibilityInputRevision,
                VisibilityEstimateRevision = visibilityInputRevision,
                HierarchyCandidateEstimateValid = true,
                HierarchyCandidatePairCount =
                    expectedCandidateInstanceViewCount,
                HierarchyCandidateEstimateRevision =
                    visibilityInputRevision,
                HierarchyCandidateLayoutRevision = instanceLayoutRevision,
                HierarchyInstanceCapacity = pipeline.InstanceCapacity,
                HierarchyClusterCapacity =
                    pipeline.HierarchicalClusterCapacity,
                HierarchyViewCapacity = pipeline.ViewCapacity,
                HierarchyPairCapacity = pipeline.PairCapacity,
                SupportsWaveOps = global::Summit.GpuPrimitives.GpuPrimitives
                    .SupportsWaveOperations,
            };
        }
    }

    private static void ValidateDecision(
        in GpuDrivenInstancePolicyResolvedDecision decision,
        in GpuDrivenInstancePolicyObservation observation,
        in GpuInstanceDirtyUploadPlan uploadPlan)
    {
        if (decision.OutputMode != observation.RequiredOutputMode)
        {
            throw new InvalidOperationException(
                "Policy decisions must preserve caller-required output.");
        }

        switch (decision.UploadMode)
        {
            case GpuDrivenInstanceUploadMode.None:
                if (observation.DirtyInstanceCount != 0 ||
                    !observation.HasResidentState ||
                    observation.StateRevision == 0UL ||
                    observation.ResidentInstanceCount !=
                        observation.ActiveInstanceCount ||
                    observation.ResidentStateRevision !=
                        observation.StateRevision ||
                    uploadPlan.Receipt.Mode != GpuInstanceUploadMode.None)
                {
                    throw new InvalidOperationException(
                        "None upload requires zero changes and the exact " +
                        "current resident state.");
                }
                break;
            case GpuDrivenInstanceUploadMode.Dirty:
                if (!observation.SupportsDirtyRangeUpload ||
                    !observation.HasResidentState ||
                    observation.DirtyInstanceCount <= 0 ||
                    observation.DirtyRangeCount <= 0 ||
                    observation.ResidentInstanceCount !=
                        observation.ActiveInstanceCount ||
                    observation.ResidentStateRevision == 0UL ||
                    uploadPlan.ExpectedResidentStateRevision !=
                        observation.ResidentStateRevision ||
                    observation.ResidentStateRevision ==
                        observation.StateRevision ||
                    !uploadPlan.IsValid ||
                    uploadPlan.SourceRevision != observation.StateRevision ||
                    uploadPlan.Receipt.Mode !=
                        GpuInstanceUploadMode.DirtyRanges)
                {
                    throw new InvalidOperationException(
                        "Dirty upload requires a live, exact planned token " +
                        "for a changed resident stream.");
                }
                break;
            case GpuDrivenInstanceUploadMode.Full:
                break;
            default:
                throw new InvalidOperationException(
                    "The policy selected an invalid upload mode.");
        }

        if (decision.CullingMode ==
            GpuDrivenInstanceCullingMode.Hierarchy)
        {
            long pairCount = (long)observation.ActiveInstanceCount *
                observation.ViewCount;
            if (decision.OutputMode !=
                    GpuDrivenInstanceOutputMode.VisibleOnly ||
                !observation.SupportsHierarchy ||
                !observation.ClusterMetadataValid ||
                observation.ClusterCount <= 0 ||
                observation.ClusterInstanceCount !=
                    observation.ActiveInstanceCount ||
                observation.InstanceLayoutRevision == 0UL ||
                observation.ClusterLayoutRevision !=
                    observation.InstanceLayoutRevision ||
                !observation.VisibilityEstimateValid ||
                observation.VisibilityInputRevision == 0UL ||
                observation.VisibilityEstimateRevision !=
                    observation.VisibilityInputRevision ||
                !observation.HierarchyCandidateEstimateValid ||
                observation.HierarchyCandidatePairCount < 0 ||
                observation.HierarchyCandidatePairCount > pairCount ||
                observation.HierarchyCandidateEstimateRevision == 0UL ||
                observation.HierarchyCandidateEstimateRevision !=
                    observation.VisibilityInputRevision ||
                observation.HierarchyInstanceCapacity <
                    observation.ActiveInstanceCount ||
                observation.HierarchyClusterCapacity <
                    observation.ClusterCount ||
                observation.HierarchyViewCapacity < observation.ViewCount ||
                observation.HierarchyPairCapacity < pairCount)
            {
                throw new InvalidOperationException(
                    "Hierarchy is valid only for VisibleOnly with exact " +
                    "layout, visibility, and capacity revisions.");
            }
        }
        else if (decision.CullingMode !=
                 GpuDrivenInstanceCullingMode.Flat)
        {
            throw new InvalidOperationException(
                "The policy selected an invalid culling mode.");
        }

        if (decision.PrimitiveBackend != GpuPrimitiveBackend.Portable &&
            (decision.PrimitiveBackend != GpuPrimitiveBackend.WaveOps ||
             !observation.SupportsWaveOps))
        {
            throw new InvalidOperationException(
                "The selected primitive backend is unavailable.");
        }
    }

    private GpuInstanceUploadReceipt RecordUpload(
        WorkSlot slot,
        in GpuDrivenInstancePolicyResolvedDecision decision,
        in GpuInstanceDirtyUploadPlan uploadPlan)
    {
        switch (decision.UploadMode)
        {
            case GpuDrivenInstanceUploadMode.None:
                return uploadPlan.Receipt;
            case GpuDrivenInstanceUploadMode.Dirty:
                return uploader.RecordPlanned(
                    slot.Commands,
                    instances,
                    slot.States,
                    instanceCount,
                    slot.StateRevision,
                    in uploadPlan);
            case GpuDrivenInstanceUploadMode.Full:
                return uploader.RecordFull(
                    slot.Commands,
                    instances,
                    slot.States,
                    instanceCount);
            default:
                throw new ArgumentOutOfRangeException(nameof(decision));
        }
    }

    private void RecordPipelineAndDraws(
        CommandBuffer commands,
        in GpuDrivenInstancePolicyResolvedDecision decision)
    {
        if (decision.CullingMode ==
            GpuDrivenInstanceCullingMode.Hierarchy)
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
                indirectArgumentWords,
                hierarchyStatistics,
                diagnostics,
                instanceCount,
                clusterCount,
                viewCount,
                drawGroupCount,
                decision.PrimitiveBackend);
        }
        else
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
                indirectArgumentWords,
                diagnostics,
                instanceCount,
                viewCount,
                drawGroupCount,
                decision.PrimitiveBackend,
                decision.OutputMode);
        }

        commands.CopyBuffer(
            indirectArgumentWords,
            renderIndirectArguments);
        RecordRenderTargetPreamble(commands);
        for (int view = 0; view < viewCount; view++)
        {
            RecordView(commands, view);
            for (int group = 0; group < drawGroupCount; group++)
            {
                int bin = checked(view * drawGroupCount + group);
                commands.DrawMeshInstancedIndirect(
                    mesh,
                    0,
                    gpuMaterial,
                    0,
                    renderIndirectArguments,
                    checked(
                        bin *
                        GraphicsBuffer.IndirectDrawIndexedArgs.size),
                    gpuDrawProperties[bin]);
            }
        }
    }

    private void CreateGpuResources(
        GpuInstanceCluster[] clusterData,
        Vector4[] viewPlaneData,
        Vector4[] viewParametersData,
        GpuDrawTemplate[] drawTemplateData)
    {
        instances = CreateStructured(
            instanceCount,
            GpuInstanceState.Stride,
            "GPU Driven Policy Instance States");
        clusters = CreateStructured(
            clusterCount,
            GpuInstanceCluster.Stride,
            "GPU Driven Policy Clusters");
        viewPlanes = CreateStructured(
            viewPlaneData.Length,
            sizeof(float) * 4,
            "GPU Driven Policy View Planes");
        viewParameters = CreateStructured(
            viewCount,
            sizeof(float) * 4,
            "GPU Driven Policy View Parameters");
        drawTemplates = CreateStructured(
            drawGroupCount,
            GpuDrawTemplate.Stride,
            "GPU Driven Policy Draw Templates");
        groupCounts = CreateStructured(
            visibleBinCount + 1,
            sizeof(uint),
            "GPU Driven Policy Group Counts");
        groupOffsets = CreateStructured(
            visibleBinCount + 2,
            sizeof(uint),
            "GPU Driven Policy Group Offsets");
        groupedInstanceIndices = CreateStructured(
            checked(instanceCount * viewCount),
            sizeof(uint),
            "GPU Driven Policy Grouped Indices");
        indirectArgumentWords = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured |
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopySource,
            checked(
                visibleBinCount *
                GpuDrivenInstancePipeline.IndirectArgumentWordCount),
            sizeof(uint))
        {
            name = "GPU Driven Policy Pipeline Indirect Words"
        };
        renderIndirectArguments = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopyDestination,
            visibleBinCount,
            GraphicsBuffer.IndirectDrawIndexedArgs.size)
        {
            name = "GPU Driven Policy Engine Indirect Arguments"
        };
        hierarchyStatistics = CreateStructured(
            GpuDrivenInstancePipeline.HierarchyStatisticWordCount,
            sizeof(uint),
            "GPU Driven Policy Hierarchy Statistics");
        diagnostics = CreateStructured(
            GpuDrivenInstancePipeline.DiagnosticWordCount,
            sizeof(uint),
            "GPU Driven Policy Diagnostics");

        instances.SetData(immutableBase);
        SetDataFromArray(clusters, clusterData);
        SetDataFromArray(viewPlanes, viewPlaneData);
        SetDataFromArray(viewParameters, viewParametersData);
        SetDataFromArray(drawTemplates, drawTemplateData);
    }

    private void CreateRenderingResources()
    {
        Shader shader = Resources.Load<Shader>(ShaderResource);
        if (shader == null)
        {
            throw new InvalidOperationException(
                "Policy benchmark shader resource could not be loaded: " +
                ShaderResource);
        }
        gpuMaterial = new Material(shader)
        {
            name = "GPU Driven Policy GPU Material",
            enableInstancing = true
        };
        gpuMaterial.EnableKeyword("GPU_DRIVEN_MACRO");

        renderTarget = new RenderTexture(
            RenderTargetSize,
            RenderTargetSize,
            24,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear)
        {
            name = "GPU Driven Policy Offscreen Target",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        if (!renderTarget.Create())
        {
            throw new InvalidOperationException(
                "Policy benchmark render target creation failed.");
        }

        cameraHosts = new GameObject[viewCount];
        cameras = new Camera[viewCount];
        CreateCameras();
        gpuDrawProperties = new MaterialPropertyBlock[visibleBinCount];
        CreateDrawProperties();
    }

    private void RecordRenderTargetPreamble(CommandBuffer commands)
    {
        commands.SetRenderTarget(renderTarget);
        commands.SetViewport(
            new Rect(0f, 0f, RenderTargetSize, RenderTargetSize));
        commands.ClearRenderTarget(true, true, Color.black);
    }

    private void RecordView(CommandBuffer commands, int view)
    {
        Camera camera = cameras[view];
        Rect normalized = camera.rect;
        commands.SetViewport(new Rect(
            normalized.x * RenderTargetSize,
            normalized.y * RenderTargetSize,
            normalized.width * RenderTargetSize,
            normalized.height * RenderTargetSize));
        commands.SetViewProjectionMatrices(
            camera.worldToCameraMatrix,
            GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
    }

    private void CreateCameras()
    {
        int columns = Mathf.CeilToInt(Mathf.Sqrt(viewCount));
        int rows = Mathf.CeilToInt(viewCount / (float)columns);
        for (int view = 0; view < viewCount; view++)
        {
            var host = new GameObject("GPU Driven Policy Camera " + view);
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.targetTexture = renderTarget;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = unchecked(1 << view);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depthTextureMode = DepthTextureMode.None;
            camera.orthographic = true;
            camera.orthographicSize = 100f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;
            camera.depth = view;
            int column = view % columns;
            int row = view / columns;
            camera.rect = new Rect(
                column / (float)columns,
                row / (float)rows,
                1f / columns,
                1f / rows);
            Vector4 viewParameter = viewParameterData[view];
            host.transform.position = new Vector3(
                viewParameter.x,
                viewParameter.y,
                -150f);
            host.transform.rotation = Quaternion.identity;
            cameraHosts[view] = host;
            cameras[view] = camera;
        }
    }

    private void CreateDrawProperties()
    {
        for (int view = 0; view < viewCount; view++)
        {
            for (int group = 0; group < drawGroupCount; group++)
            {
                int bin = checked(view * drawGroupCount + group);
                var properties = new MaterialPropertyBlock();
                properties.SetBuffer("_GpuInstanceStates", instances);
                properties.SetBuffer(
                    "_GpuGroupedInstanceIndices",
                    groupedInstanceIndices);
                properties.SetBuffer("_GpuGroupOffsets", groupOffsets);
                properties.SetInt("_GpuBinIndex", bin);
                properties.SetInt("_GpuGroupIndex", group);
                gpuDrawProperties[bin] = properties;
            }
        }
    }

    private GpuDrivenInstancePolicyValidationResult CompleteValidation(
        PendingValidation completed)
    {
        GpuDrivenInstancePolicyValidationResult result =
            CreateValidationResult(completed);
        uint[] actualCounts = completed.ReadbackData[0];
        uint[] actualOffsets = completed.ReadbackData[1];
        uint[] actualGrouped = completed.ReadbackData[2];
        uint[] actualArguments = completed.ReadbackData[3];
        uint[] actualDiagnostics = completed.ReadbackData[4];
        result.ActualStateHash = completed.ActualStateHash;
        bool statePassed = completed.ActualStateHash ==
            completed.ExpectedStateHash;
        bool outputPassed = GpuDrivenInstanceBenchmarkCpuOracle.Validate(
            expectedOutput,
            actualCounts,
            actualOffsets,
            actualGrouped,
            actualArguments,
            actualDiagnostics,
            out string outputMessage,
            out string outputHash);
        result.InvalidKeyCount = actualDiagnostics[0];
        result.DiagnosticFlags = actualDiagnostics[1];
        result.ActualOutputHash = outputHash;
        result.Passed = statePassed && outputPassed;
        result.Message = statePassed
            ? "State hash exactly matches the prepared CPU state. " +
              outputMessage
            : "State hash disagrees with the prepared CPU state. " +
              outputMessage;

        if (completed.ReadHierarchyStatistics)
        {
            uint[] statistics = completed.ReadbackData[5];
            result.HierarchyStatisticsAvailable = true;
            result.CoarseVisibleClusterViewCount = statistics[
                GpuDrivenInstancePipeline
                    .CoarseVisibleClusterViewCountWord];
            result.CandidateInstanceViewCount = statistics[
                GpuDrivenInstancePipeline.CandidateInstanceViewCountWord];
            result.HierarchicalVisiblePairCount = statistics[
                GpuDrivenInstancePipeline
                    .HierarchicalVisiblePairCountWord];
            bool hierarchyPassed =
                result.CoarseVisibleClusterViewCount ==
                    expectedCoarseVisibleClusterViewCount &&
                result.CandidateInstanceViewCount ==
                    expectedCandidateInstanceViewCount &&
                result.HierarchicalVisiblePairCount ==
                    checked((uint)visiblePairCount);
            result.Passed &= hierarchyPassed;
            result.Message += hierarchyPassed
                ? " Hierarchy statistics exactly match the CPU oracle."
                : " Hierarchy statistics disagree with the CPU oracle.";
        }
        return result;
    }

    private GpuDrivenInstancePolicyValidationResult CreateValidationResult(
        PendingValidation completed)
    {
        return new GpuDrivenInstancePolicyValidationResult
        {
            Phase = completed.Phase,
            CaseId = completed.BenchmarkCase.CaseId,
            ReadbackBytes = completed.ReadbackBytes,
            ExpectedOutputHash = expectedOutput.ResultHash,
            ExpectedStateHash = completed.ExpectedStateHash,
            ExpectedCoarseVisibleClusterViewCount =
                expectedCoarseVisibleClusterViewCount,
            ExpectedCandidateInstanceViewCount =
                expectedCandidateInstanceViewCount,
            ExpectedHierarchicalVisiblePairCount =
                checked((uint)visiblePairCount),
        };
    }

    private void IssueNextValidationReadback(PendingValidation validation)
    {
        int requestIndex = validation.NextRequestIndex;
        validation.InFlightRequest = RequestValidationBuffer(requestIndex);
        validation.InFlightRequestName =
            ValidationReadbackName(requestIndex);
        validation.HasInFlightRequest = true;
    }

    private AsyncGPUReadbackRequest RequestValidationBuffer(int requestIndex)
    {
        switch (requestIndex)
        {
            case 0:
                return AsyncGPUReadback.Request(instances);
            case 1:
                return AsyncGPUReadback.Request(groupCounts);
            case 2:
                return AsyncGPUReadback.Request(groupOffsets);
            case 3:
                return AsyncGPUReadback.Request(groupedInstanceIndices);
            case 4:
                return AsyncGPUReadback.Request(indirectArgumentWords);
            case 5:
                return AsyncGPUReadback.Request(diagnostics);
            case 6:
                return AsyncGPUReadback.Request(hierarchyStatistics);
            default:
                throw new ArgumentOutOfRangeException(nameof(requestIndex));
        }
    }

    private static string ValidationReadbackName(int requestIndex)
    {
        switch (requestIndex)
        {
            case 0:
                return "instanceStates";
            case 1:
                return "groupCounts";
            case 2:
                return "groupOffsets";
            case 3:
                return "groupedInstanceIndices";
            case 4:
                return "indirectArguments";
            case 5:
                return "diagnostics";
            case 6:
                return "hierarchyStatistics";
            default:
                return "unknown validation buffer";
        }
    }

    private long ValidationReadbackBytes(bool readHierarchy)
    {
        return checked(
            (long)instanceCount * GpuInstanceState.Stride +
            (long)(visibleBinCount + 1) * sizeof(uint) +
            (long)(visibleBinCount + 2) * sizeof(uint) +
            (long)instanceCount * viewCount * sizeof(uint) +
            (long)visibleBinCount *
                GpuDrivenInstancePipeline.IndirectArgumentWordCount *
                sizeof(uint) +
            GpuDrivenInstancePipeline.DiagnosticWordCount * sizeof(uint) +
            (readHierarchy
                ? GpuDrivenInstancePipeline.HierarchyStatisticWordCount *
                  sizeof(uint)
                : 0L));
    }

    private static GpuInstanceCluster[] BuildClusters(
        NativeArray<GpuInstanceState> states,
        int requiredClusterCount)
    {
        using (var nativeClusters = new NativeArray<GpuInstanceCluster>(
                   requiredClusterCount,
                   Allocator.TempJob,
                   NativeArrayOptions.UninitializedMemory))
        {
            int built = GpuInstanceClusterBuilder.BuildContiguous(
                states,
                states.Length,
                GpuDrivenInstanceHierarchicalInputGenerator
                    .InstancesPerCluster,
                nativeClusters);
            if (built != requiredClusterCount)
            {
                throw new InvalidOperationException(
                    "Cluster builder returned an unexpected count.");
            }
            var result = new GpuInstanceCluster[requiredClusterCount];
            nativeClusters.CopyTo(result);
            return result;
        }
    }

    private static ulong ComputeLayoutRevision(
        GpuInstanceState[] states,
        GpuInstanceCluster[] clusterData)
    {
        ulong hash = HashUInt32(FnvOffsetBasis, LayoutRevisionDomain);
        hash = HashUInt32(hash, checked((uint)states.Length));
        for (int index = 0; index < states.Length; index++)
        {
            GpuInstanceState state = states[index];
            Vector4 position = state.PositionRadius;
            hash = HashUInt32(hash, math.asuint(position.x));
            hash = HashUInt32(hash, math.asuint(position.y));
            hash = HashUInt32(hash, math.asuint(position.z));
            hash = HashUInt32(hash, math.asuint(position.w));
            Vector4 lodDistances = state.LodDistances;
            hash = HashUInt32(hash, math.asuint(lodDistances.x));
            hash = HashUInt32(hash, math.asuint(lodDistances.y));
            hash = HashUInt32(hash, math.asuint(lodDistances.z));
            hash = HashUInt32(hash, math.asuint(lodDistances.w));
            hash = HashUInt32(hash, state.DrawGroupBase);
            hash = HashUInt32(hash, state.LodCount);
            hash = HashUInt32(hash, state.ViewMask);
        }
        hash = HashUInt32(hash, checked((uint)clusterData.Length));
        for (int index = 0; index < clusterData.Length; index++)
        {
            GpuInstanceCluster cluster = clusterData[index];
            Vector4 position = cluster.PositionRadius;
            hash = HashUInt32(hash, math.asuint(position.x));
            hash = HashUInt32(hash, math.asuint(position.y));
            hash = HashUInt32(hash, math.asuint(position.z));
            hash = HashUInt32(hash, math.asuint(position.w));
            hash = HashUInt32(hash, cluster.FirstInstance);
            hash = HashUInt32(hash, cluster.InstanceCount);
            hash = HashUInt32(hash, cluster.UnionViewMask);
            hash = HashUInt32(hash, cluster.Reserved);
        }
        return NormalizeRevision(hash);
    }

    private static ulong ComputeVisibilityRevision(
        ulong layoutRevision,
        Vector4[] planes,
        Vector4[] parameters)
    {
        ulong hash = HashUInt32(
            FnvOffsetBasis,
            VisibilityRevisionDomain);
        hash = HashUInt64(hash, layoutRevision);
        hash = HashUInt32(hash, checked((uint)planes.Length));
        for (int index = 0; index < planes.Length; index++)
        {
            Vector4 value = planes[index];
            hash = HashUInt32(hash, math.asuint(value.x));
            hash = HashUInt32(hash, math.asuint(value.y));
            hash = HashUInt32(hash, math.asuint(value.z));
            hash = HashUInt32(hash, math.asuint(value.w));
        }
        hash = HashUInt32(hash, checked((uint)parameters.Length));
        for (int index = 0; index < parameters.Length; index++)
        {
            Vector4 value = parameters[index];
            hash = HashUInt32(hash, math.asuint(value.x));
            hash = HashUInt32(hash, math.asuint(value.y));
            hash = HashUInt32(hash, math.asuint(value.z));
            hash = HashUInt32(hash, math.asuint(value.w));
        }
        return NormalizeRevision(hash);
    }

    private static int CountDistinctVisibleInstances(
        uint[] groupedIndices,
        int instanceCount)
    {
        var seen = new bool[instanceCount];
        int result = 0;
        for (int index = 0; index < groupedIndices.Length; index++)
        {
            int instanceIndex = checked((int)groupedIndices[index]);
            if (instanceIndex < 0 || instanceIndex >= instanceCount)
            {
                throw new InvalidOperationException(
                    "The CPU oracle returned an invalid instance index.");
            }
            if (!seen[instanceIndex])
            {
                seen[instanceIndex] = true;
                result++;
            }
        }
        return result;
    }

    private WorkSlot GetSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= workSlots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }
        return workSlots[slotIndex];
    }

    private void RequireActiveSlot(int slotIndex)
    {
        if (activeSlotIndex != slotIndex)
        {
            throw new InvalidOperationException(
                "The slot is not the adapter's active unsubmitted slot.");
        }
    }

    private static void ValidateConstructionInputs(
        int instanceCount,
        int viewCount,
        string visibility,
        int dirtyBasisPoints,
        GpuDrivenInstanceOutputMode requiredOutputMode,
        int drawGroupCount,
        int stagingSlotCount,
        int maximumMergedGapRecords)
    {
        if (instanceCount < 1 ||
            instanceCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        if (viewCount < 1 ||
            viewCount > GpuDrivenInstancePipeline.MaximumViewCount ||
            (long)instanceCount * viewCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(viewCount));
        }
        if (string.IsNullOrWhiteSpace(visibility))
        {
            throw new ArgumentException(
                "A frozen visibility layout is required.",
                nameof(visibility));
        }
        if (dirtyBasisPoints < 0 ||
            dirtyBasisPoints >
                GpuDrivenInstancePolicyInputGenerator.BasisPointScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dirtyBasisPoints));
        }
        if (requiredOutputMode != GpuDrivenInstanceOutputMode.CulledTail &&
            requiredOutputMode != GpuDrivenInstanceOutputMode.VisibleOnly)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredOutputMode));
        }
        if (drawGroupCount < 1 || drawGroupCount > FixedDrawGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(drawGroupCount));
        }
        if (stagingSlotCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stagingSlotCount));
        }
        if (maximumMergedGapRecords < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMergedGapRecords));
        }
        if (!GpuDrivenInstancePipeline.SupportsCurrentDevice ||
            !SystemInfo.supportsInstancing ||
            !SystemInfo.supportsIndirectArgumentsBuffer ||
            !SystemInfo.supportsGraphicsFence ||
            !SystemInfo.supportsAsyncCompute)
        {
            throw new NotSupportedException(
                "The policy macrobenchmark requires compute shaders, " +
                "instancing, indirect arguments, graphics fences, and " +
                "CPU-queryable asynchronous-compute fences.");
        }
    }

    private static Mesh CreateCubeMesh()
    {
        var result = new Mesh
        {
            name = "GPU Driven Policy Procedural Cube",
            indexFormat = IndexFormat.UInt16
        };
        result.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f)
        };
        result.triangles = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5
        };
        result.bounds = new Bounds(Vector3.zero, Vector3.one);
        result.UploadMeshData(true);
        return result;
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
            name = name
        };
    }

    private static void SetDataFromArray<T>(
        GraphicsBuffer buffer,
        T[] source)
        where T : struct
    {
        using (var native = new NativeArray<T>(source, Allocator.Temp))
        {
            buffer.SetData(native);
        }
    }

    private static uint[] ToArray(NativeArray<uint> data)
    {
        var result = new uint[data.Length];
        data.CopyTo(result);
        return result;
    }

    private static ulong NormalizeRevision(ulong value)
    {
        return value == 0UL ? 1UL : value;
    }

    private static ulong NextNonzero(ulong value)
    {
        value = unchecked(value + 1UL);
        return value == 0UL ? 1UL : value;
    }

    private static ulong HashUInt32(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= FnvPrime;
        }
        return hash;
    }

    private static ulong HashUInt64(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= FnvPrime;
        }
        return hash;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDrivenInstancePolicyBenchmarkAdapter));
        }
    }

    private void DisposeCreatedResources()
    {
        if (workSlots != null)
        {
            foreach (WorkSlot slot in workSlots)
            {
                slot?.Dispose();
            }
        }
        uploader?.Dispose();
        pipeline?.Dispose();
        diagnostics?.Dispose();
        hierarchyStatistics?.Dispose();
        renderIndirectArguments?.Dispose();
        indirectArgumentWords?.Dispose();
        groupedInstanceIndices?.Dispose();
        groupOffsets?.Dispose();
        groupCounts?.Dispose();
        drawTemplates?.Dispose();
        viewParameters?.Dispose();
        viewPlanes?.Dispose();
        clusters?.Dispose();
        instances?.Dispose();
        if (immutableBase.IsCreated)
        {
            immutableBase.Dispose();
        }
        if (cameraHosts != null)
        {
            foreach (GameObject host in cameraHosts)
            {
                if (host != null)
                {
                    DestroyOwnedObject(host);
                }
            }
        }
        if (renderTarget != null)
        {
            renderTarget.Release();
            DestroyOwnedObject(renderTarget);
        }
        if (gpuMaterial != null)
        {
            DestroyOwnedObject(gpuMaterial);
        }
        if (mesh != null)
        {
            DestroyOwnedObject(mesh);
        }
    }

    private static void DestroyOwnedObject(UnityEngine.Object value)
    {
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(value);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(value);
        }
    }

    private sealed class PendingValidation
    {
        internal int SlotIndex;
        internal string Phase;
        internal GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase;
        internal GpuDrivenInstancePolicyResolvedDecision Decision;
        internal ulong ExpectedStateHash;
        internal ulong ActualStateHash;
        internal bool ReadHierarchyStatistics;
        internal uint[][] ReadbackData;
        internal int NextRequestIndex;
        internal AsyncGPUReadbackRequest InFlightRequest;
        internal string InFlightRequestName;
        internal bool HasInFlightRequest;
        internal long ReadbackBytes;
    }

    private enum WorkSlotState
    {
        Available = 0,
        Acquired = 1,
        Prepared = 2,
        WorkloadRecorded = 3,
        FenceAppended = 4,
        Submitted = 5,
    }

    private sealed class WorkSlot : IDisposable
    {
        private WorkSlotState state;
        private GraphicsFence fence;
        private GraphicsFence appendedFence;
        private bool hasAppendedFence;
        private bool hasSubmitted;

        internal WorkSlot(
            NativeArray<GpuInstanceState> immutableBase,
            int rangeCapacity,
            int slotIndex)
        {
            States = new NativeArray<GpuInstanceState>(
                immutableBase.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            NativeArray<GpuInstanceState>.Copy(immutableBase, States);
            DirtyRanges = new NativeArray<GpuInstanceDirtyRange>(
                rangeCapacity,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            Commands = new CommandBuffer
            {
                name = "GPU.DrivenPolicy/StagingSlot/" + slotIndex
            };
            state = WorkSlotState.Available;
        }

        internal NativeArray<GpuInstanceState> States;

        internal NativeArray<GpuInstanceDirtyRange> DirtyRanges;

        internal CommandBuffer Commands { get; }

        internal GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase
        {
            get;
            private set;
        }

        internal GpuDrivenInstancePolicyUpdatePlan UpdatePlan
        {
            get;
            private set;
        }

        internal uint LogicalOrdinal { get; private set; }

        internal ulong StateRevision { get; private set; }

        internal ulong ExpectedStateHash { get; private set; }

        internal GpuDrivenInstancePolicyResolvedDecision Decision
        {
            get;
            private set;
        }

        internal GpuInstanceUploadReceipt PlannedUpload { get; private set; }

        internal GpuInstanceUploadReceipt RecordedUpload { get; private set; }

        internal long SelectorCpuTicks { get; private set; }

        internal long PlanCpuTicks { get; private set; }

        internal long RecordCpuTicks { get; private set; }

        internal bool SelectorInvoked { get; private set; }

        internal ulong SubmissionSerial { get; private set; }

        internal bool TryAcquire()
        {
            if (!TryMakeAvailable())
            {
                return false;
            }
            Commands.Clear();
            ResetGenerationState();
            state = WorkSlotState.Acquired;
            return true;
        }

        internal bool TryMakeAvailable()
        {
            if (state == WorkSlotState.Available)
            {
                return true;
            }
            if (state != WorkSlotState.Submitted || !fence.passed)
            {
                return false;
            }
            state = WorkSlotState.Available;
            return true;
        }

        internal void RequireAcquired()
        {
            RequireState(WorkSlotState.Acquired);
        }

        internal void MarkPrepared(
            GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
            GpuDrivenInstancePolicyUpdatePlan updatePlan,
            uint logicalOrdinal,
            ulong stateRevision,
            ulong expectedStateHash)
        {
            RequireState(WorkSlotState.Acquired);
            BenchmarkCase = benchmarkCase;
            UpdatePlan = updatePlan;
            LogicalOrdinal = logicalOrdinal;
            StateRevision = stateRevision;
            ExpectedStateHash = expectedStateHash;
            state = WorkSlotState.Prepared;
        }

        internal void RequirePrepared()
        {
            RequireState(WorkSlotState.Prepared);
        }

        internal void MarkWorkloadRecorded(
            in GpuDrivenInstancePolicyResolvedDecision decision,
            GpuInstanceUploadReceipt plannedUpload,
            GpuInstanceUploadReceipt recordedUpload,
            long planCpuTicks,
            long selectorCpuTicks,
            long recordCpuTicks,
            bool selectorInvoked)
        {
            RequireState(WorkSlotState.Prepared);
            Decision = decision;
            PlannedUpload = plannedUpload;
            RecordedUpload = recordedUpload;
            PlanCpuTicks = planCpuTicks;
            SelectorCpuTicks = selectorCpuTicks;
            RecordCpuTicks = recordCpuTicks;
            SelectorInvoked = selectorInvoked;
            state = WorkSlotState.WorkloadRecorded;
        }

        internal void RequireWorkloadRecorded()
        {
            RequireState(WorkSlotState.WorkloadRecorded);
        }

        internal void MarkFenceAppended(GraphicsFence lifetimeFence)
        {
            RequireState(WorkSlotState.WorkloadRecorded);
            appendedFence = lifetimeFence;
            hasAppendedFence = true;
            state = WorkSlotState.FenceAppended;
        }

        internal void MarkSubmitted(ulong submissionSerial)
        {
            RequireState(WorkSlotState.FenceAppended);
            if (!hasAppendedFence)
            {
                throw new InvalidOperationException(
                    "The slot has no exact appended lifetime fence.");
            }
            fence = appendedFence;
            SubmissionSerial = submissionSerial;
            hasSubmitted = true;
            hasAppendedFence = false;
            state = WorkSlotState.Submitted;
        }

        internal void RequireSubmittedAt(ulong expectedSubmissionSerial)
        {
            if (!hasSubmitted ||
                SubmissionSerial != expectedSubmissionSerial ||
                (state != WorkSlotState.Submitted &&
                 state != WorkSlotState.Available))
            {
                throw new InvalidOperationException(
                    "The slot is not the latest submitted workload.");
            }
        }

        internal void AbandonUnsubmitted()
        {
            if (state == WorkSlotState.Available ||
                state == WorkSlotState.Submitted)
            {
                throw new InvalidOperationException(
                    "Only acquired, unsubmitted work can be abandoned.");
            }
            Commands.Clear();
            ResetGenerationState();
            state = WorkSlotState.Available;
        }

        internal void RequirePreparedOrLater()
        {
            if (state < WorkSlotState.Prepared && !hasSubmitted)
            {
                throw new InvalidOperationException(
                    "The slot does not contain a prepared state.");
            }
        }

        public void Dispose()
        {
            Commands.Dispose();
            if (DirtyRanges.IsCreated)
            {
                DirtyRanges.Dispose();
            }
            if (States.IsCreated)
            {
                States.Dispose();
            }
        }

        private void RequireState(WorkSlotState expected)
        {
            if (state != expected)
            {
                throw new InvalidOperationException(
                    "Invalid policy slot transition. Expected " +
                    expected + ", actual " + state + ".");
            }
        }

        private void ResetGenerationState()
        {
            fence = default;
            appendedFence = default;
            hasAppendedFence = false;
            hasSubmitted = false;
            BenchmarkCase = default;
            UpdatePlan = default;
            LogicalOrdinal = 0u;
            StateRevision = 0UL;
            ExpectedStateHash = 0UL;
            Decision = default;
            PlannedUpload = default;
            RecordedUpload = default;
            SelectorCpuTicks = 0L;
            PlanCpuTicks = 0L;
            RecordCpuTicks = 0L;
            SelectorInvoked = false;
            SubmissionSerial = 0UL;
        }
    }
}
