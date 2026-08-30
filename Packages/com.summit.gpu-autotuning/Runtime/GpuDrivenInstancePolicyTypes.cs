using System;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using UnityEngine;

namespace Summit.GpuAutotuning
{
    /// <summary>
    /// Selects how instance state reaches an already allocated GPU buffer.
    /// </summary>
    public enum GpuDrivenInstanceUploadMode
    {
        None = 0,
        Dirty = 1,
        Full = 2,
    }

    /// <summary>
    /// Selects the visibility algorithm. Output layout remains a caller contract.
    /// </summary>
    public enum GpuDrivenInstanceCullingMode
    {
        Flat = 0,
        Hierarchy = 1,
    }

    [Flags]
    public enum GpuDrivenInstancePolicyDecisionFlags
    {
        None = 0,
        ProfileFallback = 1 << 0,
        InvalidObservation = 1 << 1,
        NoMatchingRule = 1 << 2,
        HysteresisPending = 1 << 3,
        OverrideApplied = 1 << 4,
        InvalidOverride = 1 << 5,
        UploadGateFallback = 1 << 6,
        CullingGateFallback = 1 << 7,
        BackendGateFallback = 1 << 8,
    }

    public enum GpuDrivenInstancePolicyValidationError
    {
        None = 0,
        MissingProfile = 1,
        InvalidEnvironment = 2,
        SchemaMismatch = 3,
        ContractMismatch = 4,
        EnvironmentMismatch = 5,
        HoldoutNotAccepted = 6,
        MissingRules = 7,
        InvalidRule = 8,
        OverlappingRules = 9,
        SelectorGenerationExhausted = 10,
    }

    /// <summary>
    /// Exact, immutable-at-selection-time environment contract for a profile.
    /// String comparisons happen only while constructing a selector.
    /// </summary>
    public readonly struct GpuDrivenInstancePolicyEnvironment
    {
        public GpuDrivenInstancePolicyEnvironment(
            GpuDeviceFingerprint device,
            string unityVersion,
            string autotuningPackageVersion,
            string gpuDrivenInstancesPackageVersion,
            string processorType,
            string operatingSystem,
            string pipelineContractFingerprint,
            string shaderContractFingerprint,
            string calibrationProtocol,
            string measurementContractFingerprint,
            int policyContractVersion =
                GpuDrivenInstancePolicyContract.CurrentPolicyContractVersion)
        {
            Device = device;
            UnityVersion = unityVersion;
            AutotuningPackageVersion = autotuningPackageVersion;
            GpuDrivenInstancesPackageVersion = gpuDrivenInstancesPackageVersion;
            ProcessorType = processorType;
            OperatingSystem = operatingSystem;
            PipelineContractFingerprint = pipelineContractFingerprint;
            ShaderContractFingerprint = shaderContractFingerprint;
            CalibrationProtocol = calibrationProtocol;
            MeasurementContractFingerprint =
                measurementContractFingerprint;
            PolicyContractVersion = policyContractVersion;
        }

        public GpuDeviceFingerprint Device { get; }
        public string UnityVersion { get; }
        public string AutotuningPackageVersion { get; }
        public string GpuDrivenInstancesPackageVersion { get; }
        public string ProcessorType { get; }
        public string OperatingSystem { get; }
        public string PipelineContractFingerprint { get; }
        public string ShaderContractFingerprint { get; }
        public string CalibrationProtocol { get; }
        public string MeasurementContractFingerprint { get; }
        public int PolicyContractVersion { get; }

        public static GpuDrivenInstancePolicyEnvironment Capture(
            string pipelineContractFingerprint,
            string shaderContractFingerprint,
            string calibrationProtocol,
            string measurementContractFingerprint)
        {
            return new GpuDrivenInstancePolicyEnvironment(
                GpuDeviceFingerprint.Capture(),
                Application.unityVersion,
                GpuDrivenInstancePolicyContract
                    .CurrentAutotuningPackageVersion,
                GpuDrivenInstancePolicyContract
                    .CurrentGpuDrivenInstancesPackageVersion,
                SystemInfo.processorType ?? string.Empty,
                SystemInfo.operatingSystem ?? string.Empty,
                pipelineContractFingerprint,
                shaderContractFingerprint,
                calibrationProtocol,
                measurementContractFingerprint);
        }
    }

    public static class GpuDrivenInstancePolicyContract
    {
        public const int CurrentPolicyContractVersion = 1;
        public const string CurrentAutotuningPackageVersion = "0.2.0";
        public const string CurrentGpuDrivenInstancesPackageVersion = "0.4.0";
        public const int BasisPointScale = 10000;
        public const int MaximumRuleCount = 1024;
        public const int MaximumConsecutiveFrames = 120;
    }

    /// <summary>
    /// Exact receipt snapshot bound to a planned upload token. Capture keeps a
    /// live token validity check; RecordPlanned independently rejects a token
    /// that becomes stale after selection.
    /// </summary>
    public readonly struct GpuDrivenInstanceUploadPlanFacts
    {
        private readonly GpuInstanceDirtyUploadPlan livePlan;
        private readonly bool hasLivePlan;
        private readonly bool capturedTokenIsValid;

        /// <summary>
        /// Creates detached accounting facts for diagnostics and validation.
        /// Detached facts are never sufficient to authorize a Dirty decision;
        /// only <see cref="Capture"/> binds a live, consumable upload token.
        /// </summary>
        public GpuDrivenInstanceUploadPlanFacts(
            bool hasPlan,
            bool tokenIsValid,
            ulong sourceRevision,
            GpuInstanceUploadMode plannedMode,
            int activeCount,
            int inputRangeCount,
            int dirtyRecordCount,
            bool dirtyRecordCountExact,
            int uploadedRecordCount,
            int uploadCallCount,
            ulong expectedResidentStateRevision = 0UL)
        {
            livePlan = default;
            hasLivePlan = false;
            capturedTokenIsValid = tokenIsValid;
            HasPlan = hasPlan;
            SourceRevision = sourceRevision;
            PlannedMode = plannedMode;
            ActiveCount = activeCount;
            InputRangeCount = inputRangeCount;
            DirtyRecordCount = dirtyRecordCount;
            DirtyRecordCountExact = dirtyRecordCountExact;
            UploadedRecordCount = uploadedRecordCount;
            UploadCallCount = uploadCallCount;
            ExpectedResidentStateRevision =
                expectedResidentStateRevision;
        }

        private GpuDrivenInstanceUploadPlanFacts(
            in GpuInstanceDirtyUploadPlan plan,
            GpuInstanceUploadReceipt receipt)
        {
            livePlan = plan;
            hasLivePlan = true;
            capturedTokenIsValid = false;
            HasPlan = true;
            SourceRevision = plan.SourceRevision;
            PlannedMode = receipt.Mode;
            ActiveCount = plan.ActiveCount;
            InputRangeCount = receipt.InputRangeCount;
            DirtyRecordCount = receipt.DirtyRecordCount;
            DirtyRecordCountExact = receipt.DirtyRecordCountExact;
            UploadedRecordCount = receipt.UploadedRecordCount;
            UploadCallCount = receipt.UploadCallCount;
            ExpectedResidentStateRevision =
                plan.ExpectedResidentStateRevision;
        }

        public bool HasPlan { get; }
        public bool TokenIsValid => hasLivePlan
            ? livePlan.IsValid
            : capturedTokenIsValid;
        /// <summary>
        /// True only when these facts retain the real upload token that the
        /// caller can pass to RecordPlanned after policy selection.
        /// </summary>
        public bool IsLiveTokenBound => hasLivePlan;
        public ulong SourceRevision { get; }
        public GpuInstanceUploadMode PlannedMode { get; }
        public int ActiveCount { get; }
        public int InputRangeCount { get; }
        public int DirtyRecordCount { get; }
        public bool DirtyRecordCountExact { get; }
        public int UploadedRecordCount { get; }
        public int UploadCallCount { get; }
        /// <summary>
        /// Nonzero destination revision from which the bound dirty ranges were
        /// computed. Zero indicates detached or legacy, unbound facts.
        /// </summary>
        public ulong ExpectedResidentStateRevision { get; }

        public static GpuDrivenInstanceUploadPlanFacts Capture(
            in GpuInstanceDirtyUploadPlan plan)
        {
            GpuInstanceUploadReceipt receipt = plan.Receipt;
            return new GpuDrivenInstanceUploadPlanFacts(in plan, receipt);
        }
    }

    /// <summary>
    /// Allocation-free per-frame facts. Revisions and capacities are explicit so
    /// stale hierarchy or dirty-upload metadata fail closed.
    /// </summary>
    public struct GpuDrivenInstancePolicyObservation
    {
        public GpuDrivenInstanceOutputMode RequiredOutputMode;

        public int ActiveInstanceCount;
        public int DirtyInstanceCount;
        public int DirtyRangeCount;
        public int ViewCount;
        public long VisiblePairCount;

        public bool HasResidentState;
        public int ResidentInstanceCount;
        public ulong StateRevision;
        public ulong ResidentStateRevision;

        public bool SupportsDirtyRangeUpload;
        public GpuDrivenInstanceUploadPlanFacts UploadPlan;

        public bool SupportsHierarchy;
        public bool ClusterMetadataValid;
        public int ClusterCount;
        public int ClusterInstanceCount;
        public ulong InstanceLayoutRevision;
        public ulong ClusterLayoutRevision;
        public bool VisibilityEstimateValid;
        public ulong VisibilityInputRevision;
        public ulong VisibilityEstimateRevision;
        public bool HierarchyCandidateEstimateValid;
        public long HierarchyCandidatePairCount;
        public ulong HierarchyCandidateEstimateRevision;
        public ulong HierarchyCandidateLayoutRevision;
        public int HierarchyInstanceCapacity;
        public int HierarchyClusterCapacity;
        public int HierarchyViewCapacity;
        public long HierarchyPairCapacity;

        public bool SupportsWaveOps;
    }

    /// <summary>
    /// Optional manual choices. Every choice is still checked by the same hard
    /// semantic, revision, capacity, and capability gates as profile output.
    /// </summary>
    public struct GpuDrivenInstancePolicyOverrides
    {
        public bool HasUploadMode;
        public GpuDrivenInstanceUploadMode UploadMode;
        public bool HasCullingMode;
        public GpuDrivenInstanceCullingMode CullingMode;
        public bool HasPrimitiveBackend;
        public GpuPrimitiveBackend PrimitiveBackend;
    }

    public readonly struct GpuDrivenInstancePolicyDecision
    {
        internal GpuDrivenInstancePolicyDecision(
            GpuDrivenInstanceUploadMode uploadMode,
            GpuDrivenInstanceOutputMode outputMode,
            GpuDrivenInstanceCullingMode cullingMode,
            GpuPrimitiveBackend primitiveBackend,
            int profileRuleIndex,
            GpuDrivenInstancePolicyDecisionFlags flags)
        {
            UploadMode = uploadMode;
            OutputMode = outputMode;
            CullingMode = cullingMode;
            PrimitiveBackend = primitiveBackend;
            ProfileRuleIndex = profileRuleIndex;
            Flags = flags;
        }

        public GpuDrivenInstanceUploadMode UploadMode { get; }
        public GpuDrivenInstanceOutputMode OutputMode { get; }
        public GpuDrivenInstanceCullingMode CullingMode { get; }
        public GpuPrimitiveBackend PrimitiveBackend { get; }
        public int ProfileRuleIndex { get; }
        public GpuDrivenInstancePolicyDecisionFlags Flags { get; }
        public bool UsesProfileRule => ProfileRuleIndex >= 0;
    }

    /// <summary>
    /// Caller-owned state. Keep one instance per independently changing stream.
    /// </summary>
    public struct GpuDrivenInstancePolicyState
    {
        internal long SelectorGeneration;
        internal int ActiveRulePlusOne;
        internal int PendingRulePlusOne;
        internal int PendingFrameCount;

        public int ActiveRuleIndex => ActiveRulePlusOne - 1;
        public int PendingRuleIndex => PendingRulePlusOne - 1;
        public int ConsecutivePendingFrames => PendingFrameCount;

        public void Reset()
        {
            SelectorGeneration = 0;
            ActiveRulePlusOne = 0;
            PendingRulePlusOne = 0;
            PendingFrameCount = 0;
        }

        internal void ResetFor(long selectorGeneration)
        {
            SelectorGeneration = selectorGeneration;
            ActiveRulePlusOne = 0;
            PendingRulePlusOne = 0;
            PendingFrameCount = 0;
        }
    }
}
