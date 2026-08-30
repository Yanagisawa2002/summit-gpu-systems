using System;
using System.Globalization;
using System.Threading;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    /// <summary>
    /// Allocation-free runtime selector compiled from an exact-match, holdout-
    /// accepted profile. Invalid profiles produce a usable fail-safe selector.
    /// </summary>
    public sealed class GpuDrivenInstancePolicySelector
    {
        private static long nextGeneration;
        private static int generationExhausted;

        private readonly CompiledRule[] rules;
        private readonly long generation;

        private GpuDrivenInstancePolicySelector(
            CompiledRule[] rules,
            bool profileAccepted,
            GpuDrivenInstancePolicyValidationError validationError)
        {
            long assignedGeneration = AllocateGeneration();
            generation = assignedGeneration;
            if (assignedGeneration == 0)
            {
                this.rules = Array.Empty<CompiledRule>();
                ProfileAccepted = false;
                ValidationError = GpuDrivenInstancePolicyValidationError
                    .SelectorGenerationExhausted;
            }
            else
            {
                this.rules = rules ?? Array.Empty<CompiledRule>();
                ProfileAccepted = profileAccepted;
                ValidationError = validationError;
            }
        }

        public bool ProfileAccepted { get; }
        public GpuDrivenInstancePolicyValidationError ValidationError { get; }

        /// <summary>
        /// Creates either a validated selector or a fail-safe selector. On false,
        /// <paramref name="selector"/> remains usable and always starts from
        /// Full + caller output + Flat + Portable.
        /// </summary>
        public static bool TryCreate(
            GpuDrivenInstancePolicyProfile profile,
            in GpuDrivenInstancePolicyEnvironment environment,
            out GpuDrivenInstancePolicySelector selector,
            out GpuDrivenInstancePolicyValidationError validationError)
        {
            if (!TryCompile(
                profile,
                in environment,
                out CompiledRule[] compiled,
                out validationError))
            {
                selector = new GpuDrivenInstancePolicySelector(
                    Array.Empty<CompiledRule>(),
                    false,
                    validationError);
                validationError = selector.ValidationError;
                return false;
            }

            selector = new GpuDrivenInstancePolicySelector(
                compiled,
                true,
                GpuDrivenInstancePolicyValidationError.None);
            validationError = selector.ValidationError;
            return selector.ProfileAccepted;
        }

        public GpuDrivenInstancePolicyDecision Select(
            in GpuDrivenInstancePolicyObservation observation,
            ref GpuDrivenInstancePolicyState state)
        {
            GpuDrivenInstancePolicyOverrides overrides = default;
            return Select(in observation, ref state, in overrides);
        }

        public GpuDrivenInstancePolicyDecision Select(
            in GpuDrivenInstancePolicyObservation observation,
            ref GpuDrivenInstancePolicyState state,
            in GpuDrivenInstancePolicyOverrides overrides)
        {
            GpuDrivenInstanceOutputMode outputMode =
                IsOutputModeValid(observation.RequiredOutputMode)
                    ? observation.RequiredOutputMode
                    : GpuDrivenInstanceOutputMode.CulledTail;

            if (!TryBuildMetrics(in observation, out RuleMetrics metrics))
            {
                state.ResetFor(generation);
                return Fallback(
                    outputMode,
                    GpuDrivenInstancePolicyDecisionFlags.InvalidObservation |
                    GpuDrivenInstancePolicyDecisionFlags.ProfileFallback);
            }

            if (state.SelectorGeneration != generation)
            {
                state.ResetFor(generation);
            }

            GpuDrivenInstancePolicyDecisionFlags flags =
                GpuDrivenInstancePolicyDecisionFlags.None;
            int ruleIndex = -1;
            GpuDrivenInstanceUploadMode uploadMode =
                GpuDrivenInstanceUploadMode.Full;
            GpuDrivenInstanceCullingMode cullingMode =
                GpuDrivenInstanceCullingMode.Flat;
            GpuPrimitiveBackend primitiveBackend =
                GpuPrimitiveBackend.Portable;

            if (!ProfileAccepted)
            {
                state.ResetFor(generation);
                flags |= GpuDrivenInstancePolicyDecisionFlags.ProfileFallback;
            }
            else
            {
                ruleIndex = SelectRule(
                    outputMode,
                    in metrics,
                    ref state,
                    ref flags);
                if (ruleIndex >= 0)
                {
                    CompiledRule rule = rules[ruleIndex];
                    uploadMode = rule.UploadMode;
                    cullingMode = rule.CullingMode;
                    primitiveBackend = rule.PrimitiveBackend;
                }
            }

            ApplyOverrides(
                in overrides,
                ref uploadMode,
                ref cullingMode,
                ref primitiveBackend,
                ref flags);

            bool hardGateFellBack = false;
            if (!UploadPassesHardGates(uploadMode, in observation))
            {
                uploadMode = GpuDrivenInstanceUploadMode.Full;
                flags |= GpuDrivenInstancePolicyDecisionFlags
                    .UploadGateFallback;
                hardGateFellBack = true;
            }
            if (!CullingPassesHardGates(
                cullingMode,
                outputMode,
                in observation))
            {
                cullingMode = GpuDrivenInstanceCullingMode.Flat;
                flags |= GpuDrivenInstancePolicyDecisionFlags
                    .CullingGateFallback;
                hardGateFellBack = true;
            }
            if (!BackendPassesHardGates(primitiveBackend, in observation))
            {
                primitiveBackend = GpuPrimitiveBackend.Portable;
                flags |= GpuDrivenInstancePolicyDecisionFlags
                    .BackendGateFallback;
                hardGateFellBack = true;
            }

            if (hardGateFellBack ||
                (flags & GpuDrivenInstancePolicyDecisionFlags
                    .InvalidOverride) != 0)
            {
                // Capability recovery must re-enter through the full consecutive-
                // frame gate instead of reviving a stale optimized state.
                state.ResetFor(generation);
            }

            return new GpuDrivenInstancePolicyDecision(
                uploadMode,
                outputMode,
                cullingMode,
                primitiveBackend,
                ruleIndex,
                flags);
        }

        private static long AllocateGeneration()
        {
            if (Volatile.Read(ref generationExhausted) != 0)
            {
                return 0;
            }

            long value = Interlocked.Increment(ref nextGeneration);
            if (value <= 0)
            {
                Interlocked.Exchange(ref generationExhausted, 1);
                return 0;
            }
            return value;
        }

        private int SelectRule(
            GpuDrivenInstanceOutputMode outputMode,
            in RuleMetrics metrics,
            ref GpuDrivenInstancePolicyState state,
            ref GpuDrivenInstancePolicyDecisionFlags flags)
        {
            int activeRule = state.ActiveRulePlusOne - 1;
            if (activeRule >= 0 &&
                activeRule < rules.Length &&
                rules[activeRule].OutputMode == outputMode &&
                rules[activeRule].Exit.Contains(in metrics))
            {
                state.PendingRulePlusOne = 0;
                state.PendingFrameCount = 0;
                return activeRule;
            }

            state.ActiveRulePlusOne = 0;
            int candidate = FindEnteringRule(outputMode, in metrics);
            if (candidate < 0)
            {
                state.PendingRulePlusOne = 0;
                state.PendingFrameCount = 0;
                flags |= GpuDrivenInstancePolicyDecisionFlags.NoMatchingRule |
                    GpuDrivenInstancePolicyDecisionFlags.ProfileFallback;
                return -1;
            }

            int candidatePlusOne = candidate + 1;
            if (state.PendingRulePlusOne != candidatePlusOne)
            {
                state.PendingRulePlusOne = candidatePlusOne;
                state.PendingFrameCount = 1;
            }
            else if (state.PendingFrameCount < int.MaxValue)
            {
                state.PendingFrameCount++;
            }

            if (state.PendingFrameCount <
                rules[candidate].RequiredConsecutiveFrames)
            {
                flags |= GpuDrivenInstancePolicyDecisionFlags
                    .HysteresisPending |
                    GpuDrivenInstancePolicyDecisionFlags.ProfileFallback;
                return -1;
            }

            state.ActiveRulePlusOne = candidatePlusOne;
            state.PendingRulePlusOne = 0;
            state.PendingFrameCount = 0;
            return candidate;
        }

        private int FindEnteringRule(
            GpuDrivenInstanceOutputMode outputMode,
            in RuleMetrics metrics)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                if (rules[i].OutputMode == outputMode &&
                    rules[i].Enter.Contains(in metrics))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void ApplyOverrides(
            in GpuDrivenInstancePolicyOverrides overrides,
            ref GpuDrivenInstanceUploadMode uploadMode,
            ref GpuDrivenInstanceCullingMode cullingMode,
            ref GpuPrimitiveBackend primitiveBackend,
            ref GpuDrivenInstancePolicyDecisionFlags flags)
        {
            if (overrides.HasUploadMode)
            {
                flags |= GpuDrivenInstancePolicyDecisionFlags.OverrideApplied;
                if (IsUploadModeValid(overrides.UploadMode))
                {
                    uploadMode = overrides.UploadMode;
                }
                else
                {
                    uploadMode = GpuDrivenInstanceUploadMode.Full;
                    flags |= GpuDrivenInstancePolicyDecisionFlags
                        .InvalidOverride;
                }
            }

            if (overrides.HasCullingMode)
            {
                flags |= GpuDrivenInstancePolicyDecisionFlags.OverrideApplied;
                if (IsCullingModeValid(overrides.CullingMode))
                {
                    cullingMode = overrides.CullingMode;
                }
                else
                {
                    cullingMode = GpuDrivenInstanceCullingMode.Flat;
                    flags |= GpuDrivenInstancePolicyDecisionFlags
                        .InvalidOverride;
                }
            }

            if (overrides.HasPrimitiveBackend)
            {
                flags |= GpuDrivenInstancePolicyDecisionFlags.OverrideApplied;
                if (IsPrimitiveBackendValid(overrides.PrimitiveBackend))
                {
                    primitiveBackend = overrides.PrimitiveBackend;
                }
                else
                {
                    primitiveBackend = GpuPrimitiveBackend.Portable;
                    flags |= GpuDrivenInstancePolicyDecisionFlags
                        .InvalidOverride;
                }
            }
        }

        private static bool UploadPassesHardGates(
            GpuDrivenInstanceUploadMode uploadMode,
            in GpuDrivenInstancePolicyObservation observation)
        {
            switch (uploadMode)
            {
                case GpuDrivenInstanceUploadMode.Full:
                    return true;
                case GpuDrivenInstanceUploadMode.None:
                    return observation.HasResidentState &&
                        observation.StateRevision != 0 &&
                        observation.ResidentInstanceCount ==
                            observation.ActiveInstanceCount &&
                        observation.ResidentStateRevision ==
                            observation.StateRevision &&
                        observation.DirtyInstanceCount == 0;
                case GpuDrivenInstanceUploadMode.Dirty:
                    GpuDrivenInstanceUploadPlanFacts plan =
                        observation.UploadPlan;
                    return observation.SupportsDirtyRangeUpload &&
                        observation.HasResidentState &&
                        observation.StateRevision != 0 &&
                        plan.HasPlan &&
                        plan.IsLiveTokenBound &&
                        plan.TokenIsValid &&
                        plan.PlannedMode ==
                            GpuInstanceUploadMode.DirtyRanges &&
                        plan.ActiveCount == observation.ActiveInstanceCount &&
                        plan.SourceRevision == observation.StateRevision &&
                        plan.DirtyRecordCountExact &&
                        plan.DirtyRecordCount ==
                            observation.DirtyInstanceCount &&
                        plan.InputRangeCount == observation.DirtyRangeCount &&
                        plan.UploadedRecordCount >= plan.DirtyRecordCount &&
                        plan.UploadedRecordCount <=
                            observation.ActiveInstanceCount &&
                        plan.UploadCallCount > 0 &&
                        observation.ResidentInstanceCount ==
                            observation.ActiveInstanceCount &&
                        observation.ResidentStateRevision != 0 &&
                        plan.ExpectedResidentStateRevision ==
                            observation.ResidentStateRevision &&
                        observation.ResidentStateRevision !=
                            observation.StateRevision &&
                        observation.DirtyInstanceCount > 0 &&
                        observation.DirtyRangeCount > 0;
                default:
                    return false;
            }
        }

        private static bool CullingPassesHardGates(
            GpuDrivenInstanceCullingMode cullingMode,
            GpuDrivenInstanceOutputMode outputMode,
            in GpuDrivenInstancePolicyObservation observation)
        {
            if (cullingMode == GpuDrivenInstanceCullingMode.Flat)
            {
                return true;
            }
            if (cullingMode != GpuDrivenInstanceCullingMode.Hierarchy)
            {
                return false;
            }

            long pairCount = (long)observation.ActiveInstanceCount *
                observation.ViewCount;
            return outputMode == GpuDrivenInstanceOutputMode.VisibleOnly &&
                observation.SupportsHierarchy &&
                observation.ActiveInstanceCount > 0 &&
                observation.ClusterMetadataValid &&
                ClusterTopologyIsValid(
                    observation.ActiveInstanceCount,
                    observation.ClusterCount) &&
                observation.ClusterCount > 0 &&
                observation.ClusterInstanceCount ==
                    observation.ActiveInstanceCount &&
                observation.InstanceLayoutRevision != 0 &&
                observation.ClusterLayoutRevision != 0 &&
                observation.ClusterLayoutRevision ==
                    observation.InstanceLayoutRevision &&
                observation.VisibilityEstimateValid &&
                observation.VisibilityInputRevision != 0 &&
                observation.VisibilityEstimateRevision != 0 &&
                observation.VisibilityEstimateRevision ==
                    observation.VisibilityInputRevision &&
                observation.HierarchyCandidateEstimateValid &&
                observation.HierarchyCandidatePairCount >= 0 &&
                observation.HierarchyCandidatePairCount >=
                    observation.VisiblePairCount &&
                observation.HierarchyCandidatePairCount <= pairCount &&
                observation.HierarchyCandidateEstimateRevision != 0 &&
                observation.HierarchyCandidateEstimateRevision ==
                    observation.VisibilityInputRevision &&
                observation.HierarchyCandidateLayoutRevision != 0 &&
                observation.HierarchyCandidateLayoutRevision ==
                    observation.ClusterLayoutRevision &&
                observation.HierarchyInstanceCapacity >=
                    observation.ActiveInstanceCount &&
                observation.HierarchyClusterCapacity >=
                    observation.ClusterCount &&
                observation.HierarchyViewCapacity >= observation.ViewCount &&
                observation.HierarchyPairCapacity >= pairCount;
        }

        private static bool BackendPassesHardGates(
            GpuPrimitiveBackend backend,
            in GpuDrivenInstancePolicyObservation observation)
        {
            return backend == GpuPrimitiveBackend.Portable ||
                (backend == GpuPrimitiveBackend.WaveOps &&
                 observation.SupportsWaveOps);
        }

        private static GpuDrivenInstancePolicyDecision Fallback(
            GpuDrivenInstanceOutputMode outputMode,
            GpuDrivenInstancePolicyDecisionFlags flags)
        {
            return new GpuDrivenInstancePolicyDecision(
                GpuDrivenInstanceUploadMode.Full,
                outputMode,
                GpuDrivenInstanceCullingMode.Flat,
                GpuPrimitiveBackend.Portable,
                -1,
                flags);
        }

        private static bool TryBuildMetrics(
            in GpuDrivenInstancePolicyObservation observation,
            out RuleMetrics metrics)
        {
            metrics = default;
            if (!IsOutputModeValid(observation.RequiredOutputMode) ||
                observation.ActiveInstanceCount < 0 ||
                observation.DirtyInstanceCount < 0 ||
                observation.DirtyInstanceCount >
                    observation.ActiveInstanceCount ||
                observation.DirtyRangeCount < 0 ||
                observation.ViewCount < 1 ||
                observation.ViewCount > 32 ||
                observation.ResidentInstanceCount < 0 ||
                observation.ClusterCount < 0 ||
                observation.ClusterInstanceCount < 0 ||
                observation.HierarchyInstanceCapacity < 0 ||
                observation.HierarchyClusterCapacity < 0 ||
                observation.HierarchyViewCapacity < 0 ||
                observation.HierarchyPairCapacity < 0)
            {
                return false;
            }

            long pairCount = (long)observation.ActiveInstanceCount *
                observation.ViewCount;
            if (observation.VisiblePairCount < 0 ||
                observation.VisiblePairCount > pairCount ||
                observation.HierarchyCandidatePairCount < 0 ||
                (observation.HierarchyCandidateEstimateValid &&
                 observation.HierarchyCandidatePairCount <
                    observation.VisiblePairCount) ||
                observation.HierarchyCandidatePairCount > pairCount)
            {
                return false;
            }
            if (!UploadPlanFactsAreStructurallyValid(
                in observation.UploadPlan,
                observation.ActiveInstanceCount,
                observation.DirtyInstanceCount,
                observation.DirtyRangeCount))
            {
                return false;
            }
            if (observation.ClusterMetadataValid &&
                (!ClusterTopologyIsValid(
                    observation.ActiveInstanceCount,
                    observation.ClusterCount) ||
                 observation.ClusterInstanceCount !=
                    observation.ActiveInstanceCount))
            {
                return false;
            }

            int dirtyBasisPoints = observation.ActiveInstanceCount == 0
                ? 0
                : (int)(((long)observation.DirtyInstanceCount *
                    GpuDrivenInstancePolicyContract.BasisPointScale) /
                    observation.ActiveInstanceCount);
            int visibleBasisPoints = pairCount == 0
                ? 0
                : (int)((observation.VisiblePairCount *
                    GpuDrivenInstancePolicyContract.BasisPointScale) /
                    pairCount);
            int uploadCallCount = observation.UploadPlan.HasPlan
                ? observation.UploadPlan.UploadCallCount
                : 0;
            int uploadAmplificationBasisPoints =
                observation.UploadPlan.HasPlan &&
                observation.DirtyInstanceCount > 0
                    ? SaturatingBasisPointRatio(
                        observation.UploadPlan.UploadedRecordCount,
                        observation.DirtyInstanceCount)
                    : 0;
            int hierarchyCandidateBasisPoints =
                !observation.HierarchyCandidateEstimateValid
                    ? GpuDrivenInstancePolicyContract.BasisPointScale
                    : pairCount == 0
                        ? 0
                        : (int)((observation.HierarchyCandidatePairCount *
                            GpuDrivenInstancePolicyContract.BasisPointScale) /
                            pairCount);
            metrics = new RuleMetrics(
                observation.ActiveInstanceCount,
                dirtyBasisPoints,
                uploadCallCount,
                uploadAmplificationBasisPoints,
                visibleBasisPoints,
                hierarchyCandidateBasisPoints,
                observation.ClusterCount,
                observation.ViewCount);
            return true;
        }

        private static int SaturatingBasisPointRatio(
            int numerator,
            int denominator)
        {
            long ratio = ((long)numerator *
                GpuDrivenInstancePolicyContract.BasisPointScale) /
                denominator;
            return ratio >= int.MaxValue ? int.MaxValue : (int)ratio;
        }

        private static bool ClusterTopologyIsValid(
            int activeInstanceCount,
            int clusterCount)
        {
            if (activeInstanceCount <= 0)
            {
                return clusterCount == 0;
            }
            int minimumClusterCount = 1 +
                ((activeInstanceCount - 1) /
                    GpuInstanceCluster.MaximumInstanceCount);
            return clusterCount >= minimumClusterCount &&
                clusterCount <= activeInstanceCount;
        }

        private static bool TryCompile(
            GpuDrivenInstancePolicyProfile profile,
            in GpuDrivenInstancePolicyEnvironment environment,
            out CompiledRule[] compiled,
            out GpuDrivenInstancePolicyValidationError validationError)
        {
            compiled = Array.Empty<CompiledRule>();
            if (profile == null)
            {
                validationError =
                    GpuDrivenInstancePolicyValidationError.MissingProfile;
                return false;
            }
            if (!EnvironmentIsValid(in environment))
            {
                validationError =
                    GpuDrivenInstancePolicyValidationError.InvalidEnvironment;
                return false;
            }
            if (profile.schemaVersion !=
                GpuDrivenInstancePolicyProfile.CurrentSchemaVersion)
            {
                validationError =
                    GpuDrivenInstancePolicyValidationError.SchemaMismatch;
                return false;
            }
            if (profile.policyContractVersion !=
                    GpuDrivenInstancePolicyContract
                        .CurrentPolicyContractVersion ||
                profile.policyContractVersion !=
                    environment.PolicyContractVersion)
            {
                validationError =
                    GpuDrivenInstancePolicyValidationError.ContractMismatch;
                return false;
            }
            if (!ProfileMatchesEnvironment(profile, in environment))
            {
                validationError =
                    GpuDrivenInstancePolicyValidationError.EnvironmentMismatch;
                return false;
            }
            if (!profile.holdoutAccepted ||
                profile.profileRevision <= 0 ||
                !IsCanonicalUtc(profile.generatedUtc) ||
                !IsHex(profile.sourceCommit, 40) ||
                string.IsNullOrWhiteSpace(profile.calibrationProtocol) ||
                !IsHex(profile.measurementContractFingerprint, 64) ||
                !IsHex(profile.holdoutEvidenceSetId, 64))
            {
                validationError = GpuDrivenInstancePolicyValidationError
                    .HoldoutNotAccepted;
                return false;
            }
            if (profile.rules == null ||
                profile.rules.Length == 0 ||
                profile.rules.Length >
                    GpuDrivenInstancePolicyContract.MaximumRuleCount)
            {
                validationError =
                    GpuDrivenInstancePolicyValidationError.MissingRules;
                return false;
            }

            compiled = new CompiledRule[profile.rules.Length];
            for (int i = 0; i < profile.rules.Length; i++)
            {
                GpuDrivenInstancePolicyRule rule = profile.rules[i];
                if (!TryCompileRule(rule, out compiled[i]))
                {
                    compiled = Array.Empty<CompiledRule>();
                    validationError =
                        GpuDrivenInstancePolicyValidationError.InvalidRule;
                    return false;
                }

                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(
                            rule.ruleId,
                            profile.rules[j]?.ruleId,
                            StringComparison.Ordinal) ||
                        (compiled[i].OutputMode == compiled[j].OutputMode &&
                         compiled[i].Enter.Overlaps(compiled[j].Enter)))
                    {
                        compiled = Array.Empty<CompiledRule>();
                        validationError = GpuDrivenInstancePolicyValidationError
                            .OverlappingRules;
                        return false;
                    }
                }
            }

            validationError = GpuDrivenInstancePolicyValidationError.None;
            return true;
        }

        private static bool TryCompileRule(
            GpuDrivenInstancePolicyRule rule,
            out CompiledRule compiled)
        {
            compiled = default;
            if (rule == null ||
                !rule.holdoutAccepted ||
                string.IsNullOrWhiteSpace(rule.ruleId) ||
                !IsHex(rule.holdoutEvidenceId, 64) ||
                rule.holdoutSampleCount <= 0 ||
                !IsOutputModeValid(rule.requiredOutputMode) ||
                !IsUploadModeValid(rule.uploadMode) ||
                !IsCullingModeValid(rule.cullingMode) ||
                !IsPrimitiveBackendValid(rule.primitiveBackend) ||
                (rule.cullingMode ==
                    GpuDrivenInstanceCullingMode.Hierarchy &&
                 rule.requiredOutputMode !=
                    GpuDrivenInstanceOutputMode.VisibleOnly) ||
                rule.requiredConsecutiveFrames < 1 ||
                rule.requiredConsecutiveFrames >
                    GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames ||
                !CompiledRange.TryCreate(rule.enter, out CompiledRange enter) ||
                !CompiledRange.TryCreate(rule.exit, out CompiledRange exit) ||
                !exit.Contains(enter) ||
                !exit.IsStrictlyWiderThan(enter))
            {
                return false;
            }

            compiled = new CompiledRule(
                rule.requiredOutputMode,
                rule.uploadMode,
                rule.cullingMode,
                rule.primitiveBackend,
                rule.requiredConsecutiveFrames,
                enter,
                exit);
            return true;
        }

        private static bool EnvironmentIsValid(
            in GpuDrivenInstancePolicyEnvironment environment)
        {
            GpuDeviceFingerprint device = environment.Device;
            return environment.PolicyContractVersion ==
                    GpuDrivenInstancePolicyContract
                        .CurrentPolicyContractVersion &&
                device != null &&
                device.schemaVersion == GpuDeviceFingerprint.CurrentSchemaVersion &&
                !string.IsNullOrWhiteSpace(device.vendor) &&
                !string.IsNullOrWhiteSpace(device.deviceName) &&
                !string.IsNullOrWhiteSpace(device.graphicsApi) &&
                !string.IsNullOrWhiteSpace(device.graphicsVersion) &&
                !string.IsNullOrWhiteSpace(environment.UnityVersion) &&
                !string.IsNullOrWhiteSpace(
                    environment.AutotuningPackageVersion) &&
                !string.IsNullOrWhiteSpace(
                    environment.GpuDrivenInstancesPackageVersion) &&
                !string.IsNullOrWhiteSpace(environment.ProcessorType) &&
                !string.IsNullOrWhiteSpace(environment.OperatingSystem) &&
                !string.IsNullOrWhiteSpace(
                    environment.PipelineContractFingerprint) &&
                !string.IsNullOrWhiteSpace(
                    environment.ShaderContractFingerprint) &&
                !string.IsNullOrWhiteSpace(
                    environment.CalibrationProtocol) &&
                IsHex(environment.MeasurementContractFingerprint, 64);
        }

        private static bool ProfileMatchesEnvironment(
            GpuDrivenInstancePolicyProfile profile,
            in GpuDrivenInstancePolicyEnvironment environment)
        {
            GpuDeviceFingerprint expected = profile.device;
            GpuDeviceFingerprint actual = environment.Device;
            return expected != null &&
                expected.schemaVersion == actual.schemaVersion &&
                expected.vendorId == actual.vendorId &&
                expected.deviceId == actual.deviceId &&
                expected.shaderLevel == actual.shaderLevel &&
                string.Equals(expected.vendor, actual.vendor,
                    StringComparison.Ordinal) &&
                string.Equals(expected.deviceName, actual.deviceName,
                    StringComparison.Ordinal) &&
                string.Equals(expected.graphicsApi, actual.graphicsApi,
                    StringComparison.Ordinal) &&
                string.Equals(expected.graphicsVersion, actual.graphicsVersion,
                    StringComparison.Ordinal) &&
                string.Equals(profile.unityVersion, environment.UnityVersion,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.autotuningPackageVersion,
                    environment.AutotuningPackageVersion,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.gpuDrivenInstancesPackageVersion,
                    environment.GpuDrivenInstancesPackageVersion,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.processorType,
                    environment.ProcessorType,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.operatingSystem,
                    environment.OperatingSystem,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.pipelineContractFingerprint,
                    environment.PipelineContractFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.shaderContractFingerprint,
                    environment.ShaderContractFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.calibrationProtocol,
                    environment.CalibrationProtocol,
                    StringComparison.Ordinal) &&
                string.Equals(
                    profile.measurementContractFingerprint,
                    environment.MeasurementContractFingerprint,
                    StringComparison.Ordinal);
        }

        private static bool IsOutputModeValid(
            GpuDrivenInstanceOutputMode outputMode)
        {
            return outputMode == GpuDrivenInstanceOutputMode.CulledTail ||
                outputMode == GpuDrivenInstanceOutputMode.VisibleOnly;
        }

        private static bool UploadPlanFactsAreStructurallyValid(
            in GpuDrivenInstanceUploadPlanFacts plan,
            int activeCount,
            int dirtyCount,
            int dirtyRangeCount)
        {
            if (!plan.HasPlan)
            {
                return true;
            }
            if (plan.ActiveCount < 0 ||
                plan.SourceRevision == 0 ||
                plan.InputRangeCount < 0 ||
                plan.DirtyRecordCount < 0 ||
                plan.UploadedRecordCount < 0 ||
                plan.UploadedRecordCount > plan.ActiveCount ||
                plan.UploadCallCount < 0 ||
                (plan.DirtyRecordCountExact &&
                 plan.DirtyRecordCount > plan.ActiveCount))
            {
                return false;
            }

            switch (plan.PlannedMode)
            {
                case GpuInstanceUploadMode.None:
                    return plan.ActiveCount == activeCount &&
                        plan.DirtyRecordCountExact &&
                        plan.DirtyRecordCount == 0 &&
                        dirtyCount == 0 &&
                        plan.InputRangeCount == dirtyRangeCount &&
                        plan.UploadedRecordCount == 0 &&
                        plan.UploadCallCount == 0;
                case GpuInstanceUploadMode.DirtyRanges:
                    return plan.ActiveCount == activeCount &&
                        plan.DirtyRecordCountExact &&
                        plan.DirtyRecordCount == dirtyCount &&
                        plan.InputRangeCount == dirtyRangeCount &&
                        plan.InputRangeCount > 0 &&
                        plan.DirtyRecordCount > 0 &&
                        plan.UploadedRecordCount >= plan.DirtyRecordCount &&
                        plan.UploadCallCount > 0 &&
                        plan.UploadCallCount <= plan.InputRangeCount;
                case GpuInstanceUploadMode.Full:
                    return plan.ActiveCount == activeCount &&
                        plan.ActiveCount > 0 &&
                        plan.UploadedRecordCount == plan.ActiveCount &&
                        plan.UploadCallCount == 1;
                default:
                    return false;
            }
        }

        private static bool IsUploadModeValid(
            GpuDrivenInstanceUploadMode uploadMode)
        {
            return uploadMode == GpuDrivenInstanceUploadMode.None ||
                uploadMode == GpuDrivenInstanceUploadMode.Dirty ||
                uploadMode == GpuDrivenInstanceUploadMode.Full;
        }

        private static bool IsCullingModeValid(
            GpuDrivenInstanceCullingMode cullingMode)
        {
            return cullingMode == GpuDrivenInstanceCullingMode.Flat ||
                cullingMode == GpuDrivenInstanceCullingMode.Hierarchy;
        }

        private static bool IsPrimitiveBackendValid(GpuPrimitiveBackend backend)
        {
            return backend == GpuPrimitiveBackend.Portable ||
                backend == GpuPrimitiveBackend.WaveOps;
        }

        private static bool IsHex(string value, int requiredLength)
        {
            if (value == null || value.Length != requiredLength)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool digit = character >= '0' && character <= '9';
                bool lower = character >= 'a' && character <= 'f';
                bool upper = character >= 'A' && character <= 'F';
                if (!digit && !lower && !upper)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsCanonicalUtc(string value)
        {
            return DateTimeOffset.TryParseExact(
                    value,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed) &&
                parsed.Offset == TimeSpan.Zero;
        }

        private readonly struct RuleMetrics
        {
            public RuleMetrics(
                int activeInstanceCount,
                int dirtyBasisPoints,
                int uploadCallCount,
                int uploadAmplificationBasisPoints,
                int visibleBasisPoints,
                int hierarchyCandidateBasisPoints,
                int clusterCount,
                int viewCount)
            {
                ActiveInstanceCount = activeInstanceCount;
                DirtyBasisPoints = dirtyBasisPoints;
                UploadCallCount = uploadCallCount;
                UploadAmplificationBasisPoints =
                    uploadAmplificationBasisPoints;
                VisibleBasisPoints = visibleBasisPoints;
                HierarchyCandidateBasisPoints =
                    hierarchyCandidateBasisPoints;
                ClusterCount = clusterCount;
                ViewCount = viewCount;
            }

            public int ActiveInstanceCount { get; }
            public int DirtyBasisPoints { get; }
            public int UploadCallCount { get; }
            public int UploadAmplificationBasisPoints { get; }
            public int VisibleBasisPoints { get; }
            public int HierarchyCandidateBasisPoints { get; }
            public int ClusterCount { get; }
            public int ViewCount { get; }
        }

        private readonly struct CompiledRange
        {
            private CompiledRange(
                int minActive,
                int maxActive,
                int minDirty,
                int maxDirty,
                int minUploadCalls,
                int maxUploadCalls,
                int minUploadAmplification,
                int maxUploadAmplification,
                int minVisible,
                int maxVisible,
                int minHierarchyCandidates,
                int maxHierarchyCandidates,
                int minClusters,
                int maxClusters,
                int minViews,
                int maxViews)
            {
                MinActive = minActive;
                MaxActive = maxActive;
                MinDirty = minDirty;
                MaxDirty = maxDirty;
                MinUploadCalls = minUploadCalls;
                MaxUploadCalls = maxUploadCalls;
                MinUploadAmplification = minUploadAmplification;
                MaxUploadAmplification = maxUploadAmplification;
                MinVisible = minVisible;
                MaxVisible = maxVisible;
                MinHierarchyCandidates = minHierarchyCandidates;
                MaxHierarchyCandidates = maxHierarchyCandidates;
                MinClusters = minClusters;
                MaxClusters = maxClusters;
                MinViews = minViews;
                MaxViews = maxViews;
            }

            private int MinActive { get; }
            private int MaxActive { get; }
            private int MinDirty { get; }
            private int MaxDirty { get; }
            private int MinUploadCalls { get; }
            private int MaxUploadCalls { get; }
            private int MinUploadAmplification { get; }
            private int MaxUploadAmplification { get; }
            private int MinVisible { get; }
            private int MaxVisible { get; }
            private int MinHierarchyCandidates { get; }
            private int MaxHierarchyCandidates { get; }
            private int MinClusters { get; }
            private int MaxClusters { get; }
            private int MinViews { get; }
            private int MaxViews { get; }

            public static bool TryCreate(
                GpuDrivenInstancePolicyRuleRange range,
                out CompiledRange compiled)
            {
                compiled = default;
                if (range == null ||
                    range.minActiveInstanceCount < 0 ||
                    range.maxActiveInstanceCount <
                        range.minActiveInstanceCount ||
                    range.minDirtyBasisPoints < 0 ||
                    range.maxDirtyBasisPoints < range.minDirtyBasisPoints ||
                    range.maxDirtyBasisPoints >
                        GpuDrivenInstancePolicyContract.BasisPointScale ||
                    range.minUploadCallCount < 0 ||
                    range.maxUploadCallCount < range.minUploadCallCount ||
                    range.minUploadAmplificationBasisPoints < 0 ||
                    range.maxUploadAmplificationBasisPoints <
                        range.minUploadAmplificationBasisPoints ||
                    range.minVisibleBasisPoints < 0 ||
                    range.maxVisibleBasisPoints <
                        range.minVisibleBasisPoints ||
                    range.maxVisibleBasisPoints >
                        GpuDrivenInstancePolicyContract.BasisPointScale ||
                    range.minHierarchyCandidateBasisPoints < 0 ||
                    range.maxHierarchyCandidateBasisPoints <
                        range.minHierarchyCandidateBasisPoints ||
                    range.maxHierarchyCandidateBasisPoints >
                        GpuDrivenInstancePolicyContract.BasisPointScale ||
                    range.minClusterCount < 0 ||
                    range.maxClusterCount < range.minClusterCount ||
                    range.minViewCount < 1 ||
                    range.maxViewCount < range.minViewCount ||
                    range.maxViewCount > 32)
                {
                    return false;
                }

                compiled = new CompiledRange(
                    range.minActiveInstanceCount,
                    range.maxActiveInstanceCount,
                    range.minDirtyBasisPoints,
                    range.maxDirtyBasisPoints,
                    range.minUploadCallCount,
                    range.maxUploadCallCount,
                    range.minUploadAmplificationBasisPoints,
                    range.maxUploadAmplificationBasisPoints,
                    range.minVisibleBasisPoints,
                    range.maxVisibleBasisPoints,
                    range.minHierarchyCandidateBasisPoints,
                    range.maxHierarchyCandidateBasisPoints,
                    range.minClusterCount,
                    range.maxClusterCount,
                    range.minViewCount,
                    range.maxViewCount);
                return true;
            }

            public bool Contains(in RuleMetrics metrics)
            {
                return metrics.ActiveInstanceCount >= MinActive &&
                    metrics.ActiveInstanceCount <= MaxActive &&
                    metrics.DirtyBasisPoints >= MinDirty &&
                    metrics.DirtyBasisPoints <= MaxDirty &&
                    metrics.UploadCallCount >= MinUploadCalls &&
                    metrics.UploadCallCount <= MaxUploadCalls &&
                    metrics.UploadAmplificationBasisPoints >=
                        MinUploadAmplification &&
                    metrics.UploadAmplificationBasisPoints <=
                        MaxUploadAmplification &&
                    metrics.VisibleBasisPoints >= MinVisible &&
                    metrics.VisibleBasisPoints <= MaxVisible &&
                    metrics.HierarchyCandidateBasisPoints >=
                        MinHierarchyCandidates &&
                    metrics.HierarchyCandidateBasisPoints <=
                        MaxHierarchyCandidates &&
                    metrics.ClusterCount >= MinClusters &&
                    metrics.ClusterCount <= MaxClusters &&
                    metrics.ViewCount >= MinViews &&
                    metrics.ViewCount <= MaxViews;
            }

            public bool Contains(CompiledRange inner)
            {
                return MinActive <= inner.MinActive &&
                    MaxActive >= inner.MaxActive &&
                    MinDirty <= inner.MinDirty &&
                    MaxDirty >= inner.MaxDirty &&
                    MinUploadCalls <= inner.MinUploadCalls &&
                    MaxUploadCalls >= inner.MaxUploadCalls &&
                    MinUploadAmplification <=
                        inner.MinUploadAmplification &&
                    MaxUploadAmplification >=
                        inner.MaxUploadAmplification &&
                    MinVisible <= inner.MinVisible &&
                    MaxVisible >= inner.MaxVisible &&
                    MinHierarchyCandidates <=
                        inner.MinHierarchyCandidates &&
                    MaxHierarchyCandidates >=
                        inner.MaxHierarchyCandidates &&
                    MinClusters <= inner.MinClusters &&
                    MaxClusters >= inner.MaxClusters &&
                    MinViews <= inner.MinViews &&
                    MaxViews >= inner.MaxViews;
            }

            public bool IsStrictlyWiderThan(CompiledRange inner)
            {
                return MinActive < inner.MinActive ||
                    MaxActive > inner.MaxActive ||
                    MinDirty < inner.MinDirty ||
                    MaxDirty > inner.MaxDirty ||
                    MinUploadCalls < inner.MinUploadCalls ||
                    MaxUploadCalls > inner.MaxUploadCalls ||
                    MinUploadAmplification <
                        inner.MinUploadAmplification ||
                    MaxUploadAmplification >
                        inner.MaxUploadAmplification ||
                    MinVisible < inner.MinVisible ||
                    MaxVisible > inner.MaxVisible ||
                    MinHierarchyCandidates <
                        inner.MinHierarchyCandidates ||
                    MaxHierarchyCandidates >
                        inner.MaxHierarchyCandidates ||
                    MinClusters < inner.MinClusters ||
                    MaxClusters > inner.MaxClusters ||
                    MinViews < inner.MinViews ||
                    MaxViews > inner.MaxViews;
            }

            public bool Overlaps(CompiledRange other)
            {
                return MinActive <= other.MaxActive &&
                    MaxActive >= other.MinActive &&
                    MinDirty <= other.MaxDirty &&
                    MaxDirty >= other.MinDirty &&
                    MinUploadCalls <= other.MaxUploadCalls &&
                    MaxUploadCalls >= other.MinUploadCalls &&
                    MinUploadAmplification <=
                        other.MaxUploadAmplification &&
                    MaxUploadAmplification >=
                        other.MinUploadAmplification &&
                    MinVisible <= other.MaxVisible &&
                    MaxVisible >= other.MinVisible &&
                    MinHierarchyCandidates <=
                        other.MaxHierarchyCandidates &&
                    MaxHierarchyCandidates >=
                        other.MinHierarchyCandidates &&
                    MinClusters <= other.MaxClusters &&
                    MaxClusters >= other.MinClusters &&
                    MinViews <= other.MaxViews &&
                    MaxViews >= other.MinViews;
            }
        }

        private readonly struct CompiledRule
        {
            public CompiledRule(
                GpuDrivenInstanceOutputMode outputMode,
                GpuDrivenInstanceUploadMode uploadMode,
                GpuDrivenInstanceCullingMode cullingMode,
                GpuPrimitiveBackend primitiveBackend,
                int requiredConsecutiveFrames,
                CompiledRange enter,
                CompiledRange exit)
            {
                OutputMode = outputMode;
                UploadMode = uploadMode;
                CullingMode = cullingMode;
                PrimitiveBackend = primitiveBackend;
                RequiredConsecutiveFrames = requiredConsecutiveFrames;
                Enter = enter;
                Exit = exit;
            }

            public GpuDrivenInstanceOutputMode OutputMode { get; }
            public GpuDrivenInstanceUploadMode UploadMode { get; }
            public GpuDrivenInstanceCullingMode CullingMode { get; }
            public GpuPrimitiveBackend PrimitiveBackend { get; }
            public int RequiredConsecutiveFrames { get; }
            public CompiledRange Enter { get; }
            public CompiledRange Exit { get; }
        }
    }
}
