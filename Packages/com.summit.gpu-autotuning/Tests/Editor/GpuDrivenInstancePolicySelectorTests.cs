using System;
using NUnit.Framework;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuAutotuning.Tests
{
    public sealed class GpuDrivenInstancePolicySelectorTests
    {
        [Test]
        public void SelectsNoneDirtyAndFullForEmptySparseAndDenseUpdates()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "empty",
                            minDirtyBasisPoints: 0,
                            maxDirtyBasisPoints: 0,
                            uploadMode: GpuDrivenInstanceUploadMode.None),
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "sparse",
                            minDirtyBasisPoints: 1,
                            maxDirtyBasisPoints: 1000,
                            uploadMode: GpuDrivenInstanceUploadMode.Dirty),
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "dense",
                            minDirtyBasisPoints: 1501,
                            maxDirtyBasisPoints: 10000,
                            uploadMode: GpuDrivenInstanceUploadMode.Full)));

            GpuDrivenInstancePolicyObservation empty =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 0);
            empty.ResidentStateRevision = empty.StateRevision;
            GpuDrivenInstancePolicyState emptyState = default;
            Assert.That(selector.Select(in empty, ref emptyState).UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.None));

            GpuDrivenInstancePolicyObservation sparse =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 10);
            RequireGraphicsDevice();
            using (var livePlan = new LiveDirtyPlan(
                sparse.ActiveInstanceCount,
                sparse.StateRevision))
            {
                sparse.UploadPlan = livePlan.Facts;
                GpuDrivenInstancePolicyState sparseState = default;
                Assert.That(
                    selector.Select(in sparse, ref sparseState).UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Dirty));
            }

            GpuDrivenInstancePolicyObservation dense =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 500);
            GpuDrivenInstancePolicyState denseState = default;
            Assert.That(selector.Select(in dense, ref denseState).UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
        }

        [Test]
        public void NoneRequiresZeroChangeAndExactResidentRevision()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "none",
                            minDirtyBasisPoints: 0,
                            maxDirtyBasisPoints: 0,
                            uploadMode: GpuDrivenInstanceUploadMode.None)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 0);
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision staleResident = selector.Select(
                in observation,
                ref state);
            Assert.That(staleResident.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            AssertFlag(
                staleResident,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);

            observation.ResidentStateRevision = observation.StateRevision;
            state = default;
            Assert.That(selector.Select(in observation, ref state).UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.None));
        }

        [Test]
        public void DirtyRequiresLiveCurrentExactDirtyRangesPlan()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "dirty",
                            minDirtyBasisPoints: 1,
                            maxDirtyBasisPoints: 1000,
                            uploadMode: GpuDrivenInstanceUploadMode.Dirty)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 10);
            GpuDrivenInstancePolicyState state = default;
            GpuDrivenInstancePolicyDecision detached = selector.Select(
                in observation,
                ref state);
            Assert.That(detached.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            AssertFlag(
                detached,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);

            RequireGraphicsDevice();
            using (var livePlan = new LiveDirtyPlan(
                observation.ActiveInstanceCount,
                observation.StateRevision))
            {
                observation.UploadPlan = livePlan.Facts;
                state = default;
                Assert.That(
                    selector.Select(in observation, ref state).UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Dirty));

                livePlan.Consume();
                state = default;
                GpuDrivenInstancePolicyDecision consumed = selector.Select(
                    in observation,
                    ref state);
                Assert.That(consumed.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
                AssertFlag(
                    consumed,
                    GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);
            }

            observation.UploadPlan = PlanFacts(
                tokenIsValid: false,
                planRevision: observation.StateRevision,
                plannedMode: GpuInstanceUploadMode.DirtyRanges,
                activeCount: observation.ActiveInstanceCount,
                dirtyCount: observation.DirtyInstanceCount,
                dirtyRangeCount: observation.DirtyRangeCount);
            GpuDrivenInstancePolicyDecision staleToken = selector.Select(
                in observation,
                ref state);
            Assert.That(staleToken.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            AssertFlag(
                staleToken,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);

            observation.UploadPlan = PlanFacts(
                tokenIsValid: true,
                planRevision: observation.StateRevision + 1,
                plannedMode: GpuInstanceUploadMode.DirtyRanges,
                activeCount: observation.ActiveInstanceCount,
                dirtyCount: observation.DirtyInstanceCount,
                dirtyRangeCount: observation.DirtyRangeCount);
            state = default;
            GpuDrivenInstancePolicyDecision staleRevision = selector.Select(
                in observation,
                ref state);
            Assert.That(staleRevision.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            AssertFlag(
                staleRevision,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);

            observation.UploadPlan = PlanFacts(
                tokenIsValid: true,
                planRevision: observation.StateRevision,
                plannedMode: GpuInstanceUploadMode.DirtyRanges,
                activeCount: observation.ActiveInstanceCount - 1,
                dirtyCount: observation.DirtyInstanceCount,
                dirtyRangeCount: observation.DirtyRangeCount);
            state = default;
            GpuDrivenInstancePolicyDecision activeCountMismatch =
                selector.Select(in observation, ref state);
            Assert.That(activeCountMismatch.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            AssertFlag(
                activeCountMismatch,
                GpuDrivenInstancePolicyDecisionFlags.InvalidObservation);
        }

        [Test]
        public void DirtyRequiresExactNonzeroResidentBaseRevision()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "dirty-base",
                            minDirtyBasisPoints: 1,
                            maxDirtyBasisPoints: 1000,
                            uploadMode: GpuDrivenInstanceUploadMode.Dirty)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 10);
            ulong expectedResidentRevision =
                observation.ResidentStateRevision;
            RequireGraphicsDevice();
            using (var livePlan = new LiveDirtyPlan(
                observation.ActiveInstanceCount,
                observation.StateRevision,
                expectedResidentRevision))
            {
                observation.UploadPlan = livePlan.Facts;
                Assert.That(
                    observation.UploadPlan.ExpectedResidentStateRevision,
                    Is.EqualTo(expectedResidentRevision));

                observation.ResidentStateRevision =
                    expectedResidentRevision + 2UL;
                GpuDrivenInstancePolicyState state = default;
                GpuDrivenInstancePolicyDecision staleBase = selector.Select(
                    in observation,
                    ref state);
                Assert.That(staleBase.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
                AssertFlag(
                    staleBase,
                    GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);

                observation.ResidentStateRevision = 0UL;
                state = default;
                GpuDrivenInstancePolicyDecision zeroBase = selector.Select(
                    in observation,
                    ref state);
                Assert.That(zeroBase.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
                AssertFlag(
                    zeroBase,
                    GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);
            }
        }

        [Test]
        public void FirstFrameWithoutResidentUsesFullFallbackFromUnboundPlan()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "dirty-first-frame",
                            minDirtyBasisPoints: 1,
                            maxDirtyBasisPoints: 1000,
                            uploadMode: GpuDrivenInstanceUploadMode.Dirty)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 10);
            observation.HasResidentState = false;
            observation.ResidentInstanceCount = 0;
            observation.ResidentStateRevision = 0UL;
            RequireGraphicsDevice();
            using (var livePlan = new LiveDirtyPlan(
                observation.ActiveInstanceCount,
                observation.StateRevision,
                0UL))
            {
                observation.UploadPlan = livePlan.Facts;
                Assert.That(
                    observation.UploadPlan.ExpectedResidentStateRevision,
                    Is.Zero);
                GpuDrivenInstancePolicyState state = default;

                GpuDrivenInstancePolicyDecision decision = selector.Select(
                    in observation,
                    ref state);

                Assert.That(decision.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
                AssertFlag(
                    decision,
                    GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);
            }
        }

        [Test]
        public void RangeCapacityFullPlanCannotBeRelabeledDirty()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "dirty-candidate",
                            minDirtyBasisPoints: 1,
                            maxDirtyBasisPoints: 1000,
                            uploadMode: GpuDrivenInstanceUploadMode.Dirty)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 10);
            observation.UploadPlan = new GpuDrivenInstanceUploadPlanFacts(
                true,
                true,
                observation.StateRevision,
                GpuInstanceUploadMode.Full,
                observation.ActiveInstanceCount,
                observation.DirtyRangeCount,
                0,
                false,
                observation.ActiveInstanceCount,
                1);
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);

            Assert.That(decision.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);
        }

        [Test]
        public void UploadFragmentationAndAmplificationArePolicyDimensions()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "contiguous",
                            minUploadCallCount: 1,
                            maxUploadCallCount: 2,
                            minUploadAmplificationBasisPoints: 10000,
                            maxUploadAmplificationBasisPoints: 10500),
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "fragmented",
                            minUploadCallCount: 3,
                            maxUploadCallCount: 16,
                            minUploadAmplificationBasisPoints: 11000,
                            maxUploadAmplificationBasisPoints: 20000)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(dirtyCount: 10);
            GpuDrivenInstancePolicyState state = default;

            Assert.That(
                selector.Select(in observation, ref state).ProfileRuleIndex,
                Is.EqualTo(0));

            observation.DirtyRangeCount = 8;
            observation.UploadPlan = new GpuDrivenInstanceUploadPlanFacts(
                true,
                true,
                observation.StateRevision,
                GpuInstanceUploadMode.DirtyRanges,
                observation.ActiveInstanceCount,
                8,
                10,
                true,
                12,
                8);
            state = default;

            Assert.That(
                selector.Select(in observation, ref state).ProfileRuleIndex,
                Is.EqualTo(1));
        }

        [Test]
        public void HierarchyCandidateAndClusterTopologyArePolicyDimensions()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "coherent",
                            minHierarchyCandidateBasisPoints: 0,
                            maxHierarchyCandidateBasisPoints: 3000,
                            minClusterCount: 1,
                            maxClusterCount: 64,
                            outputMode:
                                GpuDrivenInstanceOutputMode.VisibleOnly),
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "incoherent",
                            minHierarchyCandidateBasisPoints: 7000,
                            maxHierarchyCandidateBasisPoints: 10000,
                            minClusterCount: 65,
                            maxClusterCount: 256,
                            outputMode:
                                GpuDrivenInstanceOutputMode.VisibleOnly)));
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            observation.HierarchyCandidatePairCount = 1000;
            GpuDrivenInstancePolicyState state = default;

            Assert.That(
                selector.Select(in observation, ref state).ProfileRuleIndex,
                Is.EqualTo(0));

            observation.HierarchyCandidatePairCount = 3200;
            observation.ClusterCount = 128;
            observation.HierarchyClusterCapacity = 128;
            state = default;

            Assert.That(
                selector.Select(in observation, ref state).ProfileRuleIndex,
                Is.EqualTo(1));
        }

        [Test]
        public void CallerCulledTailContractHardGatesHierarchyOverride()
        {
            GpuDrivenInstancePolicySelector selector = FlatSelector(
                GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(
                    GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstancePolicyOverrides overrides =
                new GpuDrivenInstancePolicyOverrides
                {
                    HasCullingMode = true,
                    CullingMode = GpuDrivenInstanceCullingMode.Hierarchy
                };
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state,
                in overrides);

            Assert.That(decision.OutputMode,
                Is.EqualTo(GpuDrivenInstanceOutputMode.CulledTail));
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);
        }

        [Test]
        public void StaleClusterAndVisibilityRevisionsImmediatelyUseFlat()
        {
            GpuDrivenInstancePolicySelector selector = HierarchySelector();
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            GpuDrivenInstancePolicyState state = default;
            Assert.That(selector.Select(in observation, ref state).CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));

            observation.ClusterLayoutRevision++;
            GpuDrivenInstancePolicyDecision staleCluster = selector.Select(
                in observation,
                ref state);
            Assert.That(staleCluster.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                staleCluster,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);

            observation = HierarchyObservation();
            state = default;
            selector.Select(in observation, ref state);
            observation.VisibilityEstimateRevision++;
            GpuDrivenInstancePolicyDecision staleVisibility = selector.Select(
                in observation,
                ref state);
            Assert.That(staleVisibility.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                staleVisibility,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);

            observation = HierarchyObservation();
            state = default;
            selector.Select(in observation, ref state);
            observation.HierarchyCandidateLayoutRevision++;
            GpuDrivenInstancePolicyDecision staleCandidateLayout =
                selector.Select(in observation, ref state);
            Assert.That(staleCandidateLayout.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                staleCandidateLayout,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);
        }

        [Test]
        public void InsufficientHierarchyCapacityImmediatelyUsesFlat()
        {
            GpuDrivenInstancePolicySelector selector = HierarchySelector();
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            GpuDrivenInstancePolicyState state = default;
            Assert.That(selector.Select(in observation, ref state).CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));

            observation.HierarchyPairCapacity--;
            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);
            Assert.That(state.ActiveRuleIndex, Is.EqualTo(-1));
        }

        [TestCase(1)]
        [TestCase(1001)]
        public void ImpossibleClusterTopologyIsRejected(int clusterCount)
        {
            GpuDrivenInstancePolicySelector selector = HierarchySelector();
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            observation.ClusterCount = clusterCount;
            observation.HierarchyClusterCapacity = clusterCount;
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);

            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.InvalidObservation);
        }

        [Test]
        public void HierarchyCandidateEstimateCannotUndercountVisiblePairs()
        {
            GpuDrivenInstancePolicySelector selector = HierarchySelector();
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            observation.HierarchyCandidatePairCount =
                observation.VisiblePairCount - 1;
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);

            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.InvalidObservation);
        }

        [Test]
        public void MissingHierarchyEstimateStillAllowsFlatPolicy()
        {
            GpuDrivenInstancePolicySelector selector = FlatSelector(
                GpuDrivenInstanceOutputMode.VisibleOnly);
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            observation.SupportsHierarchy = false;
            observation.HierarchyCandidateEstimateValid = false;
            observation.HierarchyCandidatePairCount = 0;
            observation.HierarchyCandidateEstimateRevision = 0;
            observation.HierarchyCandidateLayoutRevision = 0;
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);

            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(
                decision.Flags &
                    GpuDrivenInstancePolicyDecisionFlags.InvalidObservation,
                Is.EqualTo(GpuDrivenInstancePolicyDecisionFlags.None));
        }

        [Test]
        public void UsesConsecutiveEntryWiderExitAndResetPerStream()
        {
            GpuDrivenInstancePolicyRule rule =
                GpuDrivenInstancePolicyTestFactory.Rule(
                    "hysteresis",
                    minVisibleBasisPoints: 0,
                    maxVisibleBasisPoints: 3000,
                    outputMode: GpuDrivenInstanceOutputMode.VisibleOnly,
                    cullingMode: GpuDrivenInstanceCullingMode.Hierarchy,
                    requiredConsecutiveFrames: 3,
                    exitVisibleExpansion: 1000);
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(rule));
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            GpuDrivenInstancePolicyState firstStream = default;
            GpuDrivenInstancePolicyState secondStream = default;

            AssertFallbackPending(selector.Select(
                in observation,
                ref firstStream));
            AssertFallbackPending(selector.Select(
                in observation,
                ref firstStream));
            Assert.That(selector.Select(
                    in observation,
                    ref firstStream).CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));

            // 3500 bp is outside the stricter enter range but inside the 4000
            // bp exit range, so the active rule remains stable.
            observation.VisiblePairCount = 1400;
            Assert.That(selector.Select(
                    in observation,
                    ref firstStream).CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));

            // A second stream has independent consecutive-frame history.
            observation.VisiblePairCount = 1000;
            AssertFallbackPending(selector.Select(
                in observation,
                ref secondStream));

            // Leaving the wider exit range fails closed immediately.
            observation.VisiblePairCount = 1800;
            GpuDrivenInstancePolicyDecision outsideExit = selector.Select(
                in observation,
                ref firstStream);
            Assert.That(outsideExit.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                outsideExit,
                GpuDrivenInstancePolicyDecisionFlags.NoMatchingRule);

            observation.VisiblePairCount = 1000;
            AssertFallbackPending(selector.Select(
                in observation,
                ref firstStream));
            firstStream.Reset();
            AssertFallbackPending(selector.Select(
                in observation,
                ref firstStream));
        }

        [Test]
        public void CapabilityLossHardGatesActiveHierarchy()
        {
            GpuDrivenInstancePolicySelector selector = HierarchySelector();
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            GpuDrivenInstancePolicyState state = default;
            GpuDrivenInstancePolicyDecision active = selector.Select(
                in observation,
                ref state);
            Assert.That(active.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));
            Assert.That(active.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));

            observation.SupportsHierarchy = false;
            observation.SupportsWaveOps = false;
            GpuDrivenInstancePolicyDecision gated = selector.Select(
                in observation,
                ref state);

            Assert.That(gated.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(gated.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);
            Assert.That(state.ActiveRuleIndex, Is.EqualTo(-1));
        }

        [Test]
        public void OverridesWorkWhenSafeButCannotBypassAnyHardGate()
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "override-base",
                            outputMode:
                                GpuDrivenInstanceOutputMode.VisibleOnly)));
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            RequireGraphicsDevice();
            using var livePlan = new LiveDirtyPlan(
                observation.ActiveInstanceCount,
                observation.StateRevision);
            observation.UploadPlan = livePlan.Facts;
            GpuDrivenInstancePolicyOverrides overrides =
                new GpuDrivenInstancePolicyOverrides
                {
                    HasUploadMode = true,
                    UploadMode = GpuDrivenInstanceUploadMode.Dirty,
                    HasCullingMode = true,
                    CullingMode = GpuDrivenInstanceCullingMode.Hierarchy,
                    HasPrimitiveBackend = true,
                    PrimitiveBackend = GpuPrimitiveBackend.WaveOps
                };
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision allowed = selector.Select(
                in observation,
                ref state,
                in overrides);
            Assert.That(allowed.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Dirty));
            Assert.That(allowed.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));
            Assert.That(allowed.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.WaveOps));
            AssertFlag(
                allowed,
                GpuDrivenInstancePolicyDecisionFlags.OverrideApplied);

            observation.SupportsDirtyRangeUpload = false;
            observation.SupportsHierarchy = false;
            observation.SupportsWaveOps = false;
            GpuDrivenInstancePolicyDecision gated = selector.Select(
                in observation,
                ref state,
                in overrides);
            Assert.That(gated.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(gated.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(gated.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.BackendGateFallback);
        }

        [Test]
        public void OverridesApplyWithoutProfileButStillUseAllHardGates()
        {
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();
            Assert.That(GpuDrivenInstancePolicySelector.TryCreate(
                null,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out _), Is.False);
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            RequireGraphicsDevice();
            using var livePlan = new LiveDirtyPlan(
                observation.ActiveInstanceCount,
                observation.StateRevision);
            observation.UploadPlan = livePlan.Facts;
            GpuDrivenInstancePolicyOverrides overrides =
                new GpuDrivenInstancePolicyOverrides
                {
                    HasUploadMode = true,
                    UploadMode = GpuDrivenInstanceUploadMode.Dirty,
                    HasCullingMode = true,
                    CullingMode = GpuDrivenInstanceCullingMode.Hierarchy,
                    HasPrimitiveBackend = true,
                    PrimitiveBackend = GpuPrimitiveBackend.WaveOps
                };
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision allowed = selector.Select(
                in observation,
                ref state,
                in overrides);
            Assert.That(allowed.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Dirty));
            Assert.That(allowed.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));
            Assert.That(allowed.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.WaveOps));
            Assert.That(allowed.UsesProfileRule, Is.False);
            AssertFlag(
                allowed,
                GpuDrivenInstancePolicyDecisionFlags.ProfileFallback);
            AssertFlag(
                allowed,
                GpuDrivenInstancePolicyDecisionFlags.OverrideApplied);

            observation.SupportsDirtyRangeUpload = false;
            observation.SupportsHierarchy = false;
            observation.SupportsWaveOps = false;
            GpuDrivenInstancePolicyDecision gated = selector.Select(
                in observation,
                ref state,
                in overrides);
            Assert.That(gated.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(gated.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(gated.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback);
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback);
            AssertFlag(
                gated,
                GpuDrivenInstancePolicyDecisionFlags.BackendGateFallback);
        }

        [Test]
        public void OverrideBypassesNoMatchAndHysteresisPending()
        {
            GpuDrivenInstancePolicyRule rule =
                GpuDrivenInstancePolicyTestFactory.Rule(
                    "override-hysteresis",
                    minVisibleBasisPoints: 0,
                    maxVisibleBasisPoints: 3000,
                    outputMode: GpuDrivenInstanceOutputMode.VisibleOnly,
                    cullingMode: GpuDrivenInstanceCullingMode.Hierarchy,
                    requiredConsecutiveFrames: 3);
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(rule));
            GpuDrivenInstancePolicyOverrides overrides =
                new GpuDrivenInstancePolicyOverrides
                {
                    HasCullingMode = true,
                    CullingMode = GpuDrivenInstanceCullingMode.Hierarchy
                };
            GpuDrivenInstancePolicyObservation observation =
                HierarchyObservation();
            observation.VisiblePairCount = 1400;
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision noMatch = selector.Select(
                in observation,
                ref state,
                in overrides);
            Assert.That(noMatch.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));
            AssertFlag(
                noMatch,
                GpuDrivenInstancePolicyDecisionFlags.NoMatchingRule);

            observation.VisiblePairCount = 1000;
            GpuDrivenInstancePolicyDecision pending = selector.Select(
                in observation,
                ref state,
                in overrides);
            Assert.That(pending.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));
            AssertFlag(
                pending,
                GpuDrivenInstancePolicyDecisionFlags.HysteresisPending);
        }

        [Test]
        public void InvalidOverrideValuesFailClosedPerAxis()
        {
            GpuDrivenInstancePolicySelector selector = FlatSelector(
                GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation();
            GpuDrivenInstancePolicyOverrides overrides =
                new GpuDrivenInstancePolicyOverrides
                {
                    HasUploadMode = true,
                    UploadMode = (GpuDrivenInstanceUploadMode)99,
                    HasCullingMode = true,
                    CullingMode = (GpuDrivenInstanceCullingMode)99,
                    HasPrimitiveBackend = true,
                    PrimitiveBackend = (GpuPrimitiveBackend)99
                };
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state,
                in overrides);

            Assert.That(decision.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(decision.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.InvalidOverride);
        }

        [Test]
        public void InvalidObservationDefaultsOutputToCulledTailAndFailsSafe()
        {
            GpuDrivenInstancePolicySelector selector = FlatSelector(
                GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation();
            observation.RequiredOutputMode =
                (GpuDrivenInstanceOutputMode)99;
            GpuDrivenInstancePolicyState state = default;

            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);

            Assert.That(decision.OutputMode,
                Is.EqualTo(GpuDrivenInstanceOutputMode.CulledTail));
            Assert.That(decision.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(decision.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.InvalidObservation);
        }

        [Test]
        public void WarmSelectPathAllocatesZeroBytesAcrossOneHundredThousandCalls()
        {
            GpuDrivenInstancePolicySelector selector = FlatSelector(
                GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation();
            GpuDrivenInstancePolicyState state = default;
            selector.Select(in observation, ref state);

            int checksum = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++)
            {
                GpuDrivenInstancePolicyDecision decision = selector.Select(
                    in observation,
                    ref state);
                checksum += (int)decision.UploadMode +
                    (int)decision.CullingMode +
                    (int)decision.PrimitiveBackend +
                    decision.ProfileRuleIndex;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(checksum, Is.GreaterThan(0));
            Assert.That(allocated, Is.EqualTo(0));
        }

        private static GpuDrivenInstancePolicySelector FlatSelector(
            GpuDrivenInstanceOutputMode outputMode)
        {
            return GpuDrivenInstancePolicyTestFactory.Selector(
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule(
                        "flat",
                        outputMode: outputMode)));
        }

        private static GpuDrivenInstancePolicySelector HierarchySelector()
        {
            return GpuDrivenInstancePolicyTestFactory.Selector(
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule(
                        "hierarchy",
                        minVisibleBasisPoints: 0,
                        maxVisibleBasisPoints: 3000,
                        outputMode: GpuDrivenInstanceOutputMode.VisibleOnly,
                        cullingMode:
                            GpuDrivenInstanceCullingMode.Hierarchy)));
        }

        private static GpuDrivenInstancePolicyObservation
            HierarchyObservation()
        {
            return GpuDrivenInstancePolicyTestFactory.Observation(
                GpuDrivenInstanceOutputMode.VisibleOnly,
                visiblePairCount: 1000);
        }

        private static void AssertFallbackPending(
            GpuDrivenInstancePolicyDecision decision)
        {
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            AssertFlag(
                decision,
                GpuDrivenInstancePolicyDecisionFlags.HysteresisPending);
        }

        private static void AssertFlag(
            GpuDrivenInstancePolicyDecision decision,
            GpuDrivenInstancePolicyDecisionFlags flag)
        {
            Assert.That(decision.Flags & flag,
                Is.Not.EqualTo(GpuDrivenInstancePolicyDecisionFlags.None));
        }

        private static GpuDrivenInstanceUploadPlanFacts PlanFacts(
            bool tokenIsValid,
            ulong planRevision,
            GpuInstanceUploadMode plannedMode,
            int activeCount,
            int dirtyCount,
            int dirtyRangeCount)
        {
            return new GpuDrivenInstanceUploadPlanFacts(
                true,
                tokenIsValid,
                planRevision,
                plannedMode,
                activeCount,
                dirtyRangeCount,
                dirtyCount,
                true,
                dirtyCount,
                dirtyRangeCount);
        }

        private static void RequireGraphicsDevice()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics device is required for a live upload-plan token.");
        }

        private sealed class LiveDirtyPlan : IDisposable
        {
            private readonly NativeArray<GpuInstanceState> source;
            private readonly NativeArray<GpuInstanceDirtyRange> ranges;
            private readonly GpuInstanceStateUploader uploader;
            private readonly GraphicsBuffer destination;
            private readonly ulong sourceRevision;
            private readonly int activeCount;
            private GpuInstanceDirtyUploadPlan plan;

            public LiveDirtyPlan(
                int activeCount,
                ulong sourceRevision,
                ulong expectedResidentStateRevision = 1UL)
            {
                this.activeCount = activeCount;
                this.sourceRevision = sourceRevision;
                source = new NativeArray<GpuInstanceState>(
                    activeCount,
                    Allocator.TempJob,
                    NativeArrayOptions.ClearMemory);
                ranges = new NativeArray<GpuInstanceDirtyRange>(
                    2,
                    Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);
                ranges[0] = new GpuInstanceDirtyRange(0, 5);
                ranges[1] = new GpuInstanceDirtyRange(activeCount / 2, 5);
                uploader = new GpuInstanceStateUploader(2);
                destination = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    activeCount,
                    GpuInstanceState.Stride);
                plan = expectedResidentStateRevision != 0UL
                    ? uploader.PlanDirtyUpload(
                        destination,
                        source,
                        activeCount,
                        sourceRevision,
                        expectedResidentStateRevision,
                        ranges,
                        ranges.Length)
                    : uploader.PlanDirtyUpload(
                        destination,
                        source,
                        activeCount,
                        sourceRevision,
                        ranges,
                        ranges.Length);
            }

            public GpuDrivenInstanceUploadPlanFacts Facts =>
                GpuDrivenInstanceUploadPlanFacts.Capture(in plan);

            public void Consume()
            {
                using (var commands = new CommandBuffer())
                {
                    uploader.RecordPlanned(
                        commands,
                        destination,
                        source,
                        activeCount,
                        sourceRevision,
                        in plan);
                }
            }

            public void Dispose()
            {
                destination.Dispose();
                uploader.Dispose();
                ranges.Dispose();
                source.Dispose();
            }
        }
    }
}
