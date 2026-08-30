using System;
using System.Collections;
using NUnit.Framework;
using Summit.GpuAutotuning;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstancePolicyBenchmarkAdapterTests
    {
        [Test]
        public void LifetimeFencesUseUnitySupportedAsyncQueueType()
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkAdapter.LifetimeFenceType,
                Is.EqualTo(
                    GraphicsFenceType.AsyncQueueSynchronisation));
            Assert.That(
                GpuDrivenInstanceUploadBenchmarkAdapter.LifetimeFenceType,
                Is.EqualTo(
                    GraphicsFenceType.AsyncQueueSynchronisation));
        }

        [Test]
        public void CaseContractExposesSelectorIsolationAndCalibrationMatrix()
        {
            GpuDrivenInstancePolicyBenchmarkCase safe =
                GpuDrivenInstancePolicyBenchmarkCase.SafeBaseline();
            GpuDrivenInstancePolicyBenchmarkCase actual =
                GpuDrivenInstancePolicyBenchmarkCase.ActualAuto();
            Assert.That(safe.InvokesSelector, Is.False);
            Assert.That(actual.InvokesSelector, Is.True);
            Assert.That(safe.CaseId, Does.Contain("safe-baseline"));
            Assert.That(actual.CaseId, Does.Contain("actual-auto"));

            GpuDrivenInstancePolicyBenchmarkCase[] cases =
            {
                GpuDrivenInstancePolicyBenchmarkCase.ForcedFullFlat(),
                GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedFullHierarchy(),
                GpuDrivenInstancePolicyBenchmarkCase.ForcedDirtyFlat(),
                GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedDirtyHierarchy(),
                GpuDrivenInstancePolicyBenchmarkCase.ForcedNoneFlat(),
                GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedNoneHierarchy(),
            };
            GpuDrivenInstanceUploadMode[] uploads =
            {
                GpuDrivenInstanceUploadMode.Full,
                GpuDrivenInstanceUploadMode.Full,
                GpuDrivenInstanceUploadMode.Dirty,
                GpuDrivenInstanceUploadMode.Dirty,
                GpuDrivenInstanceUploadMode.None,
                GpuDrivenInstanceUploadMode.None,
            };
            GpuDrivenInstanceCullingMode[] culling =
            {
                GpuDrivenInstanceCullingMode.Flat,
                GpuDrivenInstanceCullingMode.Hierarchy,
                GpuDrivenInstanceCullingMode.Flat,
                GpuDrivenInstanceCullingMode.Hierarchy,
                GpuDrivenInstanceCullingMode.Flat,
                GpuDrivenInstanceCullingMode.Hierarchy,
            };

            for (int index = 0; index < cases.Length; index++)
            {
                GpuDrivenInstancePolicyResolvedDecision decision =
                    cases[index].ResolveCalibration(
                        GpuDrivenInstanceOutputMode.VisibleOnly);
                Assert.That(cases[index].InvokesSelector, Is.False);
                Assert.That(
                    decision.UploadMode,
                    Is.EqualTo(uploads[index]));
                Assert.That(
                    decision.CullingMode,
                    Is.EqualTo(culling[index]));
                Assert.That(
                    decision.OutputMode,
                    Is.EqualTo(GpuDrivenInstanceOutputMode.VisibleOnly));
                Assert.That(
                    decision.PrimitiveBackend,
                    Is.EqualTo(GpuPrimitiveBackend.Portable));
                Assert.That(
                    decision.Source,
                    Is.EqualTo(
                        GpuDrivenInstancePolicyDecisionSource
                            .ForcedCalibration));
            }
        }

        [Test]
        public void SafeBaselineUsesNoneOnlyForExactZeroChangeResidency()
        {
            var observation = new GpuDrivenInstancePolicyObservation
            {
                RequiredOutputMode =
                    GpuDrivenInstanceOutputMode.CulledTail,
                ActiveInstanceCount = 1000,
                DirtyInstanceCount = 0,
                HasResidentState = true,
                ResidentInstanceCount = 1000,
                StateRevision = 17UL,
                ResidentStateRevision = 17UL,
            };

            GpuDrivenInstancePolicyResolvedDecision none =
                GpuDrivenInstancePolicyBenchmarkAdapter
                    .ResolveSafeBaseline(in observation);
            Assert.That(
                none.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.None));
            Assert.That(
                none.OutputMode,
                Is.EqualTo(GpuDrivenInstanceOutputMode.CulledTail));
            Assert.That(
                none.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(
                none.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));

            observation.DirtyInstanceCount = 1;
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkAdapter
                    .ResolveSafeBaseline(in observation).UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            observation.DirtyInstanceCount = 0;
            observation.ResidentStateRevision = 18UL;
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkAdapter
                    .ResolveSafeBaseline(in observation).UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            observation.ResidentStateRevision = 17UL;
            observation.HasResidentState = false;
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkAdapter
                    .ResolveSafeBaseline(in observation).UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
        }

        [Test]
        public void StateHashCoversEveryGpuInstanceField()
        {
            var states = new NativeArray<GpuInstanceState>(
                2,
                Allocator.Persistent);
            try
            {
                states[0] = State(0);
                states[1] = State(1);
                ulong baseline =
                    GpuDrivenInstancePolicyBenchmarkAdapter.ComputeStateHash(
                        states,
                        states.Length);
                Assert.That(
                    GpuDrivenInstancePolicyBenchmarkAdapter.ValidateStateHash(
                        states,
                        states.Length,
                        baseline,
                        out ulong repeated),
                    Is.True);
                Assert.That(repeated, Is.EqualTo(baseline));

                GpuInstanceState changed = states[1];
                changed.ApplicationId ^= 0x100u;
                states[1] = changed;
                Assert.That(
                    GpuDrivenInstancePolicyBenchmarkAdapter.ComputeStateHash(
                        states,
                        states.Length),
                    Is.Not.EqualTo(baseline));
            }
            finally
            {
                states.Dispose();
            }
        }

        [Test]
        public void PolicyEvidenceGateRejectsEveryFallbackOrInvalidFlag()
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .DecisionFlagsPassEvidenceGate(
                        GpuDrivenInstancePolicyDecisionFlags.None),
                Is.True);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .DecisionFlagsPassEvidenceGate(
                        GpuDrivenInstancePolicyDecisionFlags
                            .OverrideApplied),
                Is.True);

            GpuDrivenInstancePolicyDecisionFlags[] rejected =
            {
                GpuDrivenInstancePolicyDecisionFlags.ProfileFallback,
                GpuDrivenInstancePolicyDecisionFlags.InvalidObservation,
                GpuDrivenInstancePolicyDecisionFlags.NoMatchingRule,
                GpuDrivenInstancePolicyDecisionFlags.HysteresisPending,
                GpuDrivenInstancePolicyDecisionFlags.InvalidOverride,
                GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback,
                GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback,
                GpuDrivenInstancePolicyDecisionFlags.BackendGateFallback,
            };
            foreach (GpuDrivenInstancePolicyDecisionFlags flag in rejected)
            {
                Assert.That(
                    GpuDrivenInstancePolicyBenchmarkController
                        .DecisionFlagsPassEvidenceGate(flag),
                    Is.False,
                    flag.ToString());
            }
        }

        [Test]
        public void PolicyEvidenceHexValidationIsExact()
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController.IsHex(
                    new string('a', 40),
                    40),
                Is.True);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController.IsHex(null, 40),
                Is.False);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController.IsHex(
                    new string('a', 39),
                    40),
                Is.False);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController.IsHex(
                    new string('z', 40),
                    40),
                Is.False);
        }

        [TestCase("safe-baseline", "dirty-flat", false)]
        [TestCase("none-flat", "none-hierarchy", false)]
        [TestCase("actual-auto", "forced-selected", true)]
        [TestCase("safe-baseline", "actual_auto", true)]
        [TestCase("full-flat", "forced-selected", true)]
        public void AcceptedSelectorEvidenceIsRequiredOnlyForProfileCases(
            string leftCase,
            string rightCase,
            bool expected)
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .CasesRequireAcceptedSelectorEvidence(
                        leftCase,
                        rightCase),
                Is.EqualTo(expected));
        }

        [Test]
        public void MeasuredOrdinalsArePairLocalAndDisjointFromWarmup()
        {
            uint pairOneSampleOne =
                GpuDrivenInstancePolicyBenchmarkController
                    .MeasuredLogicalOrdinal(1, 1, 240);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .MeasuredLogicalOrdinal(1, 1, 240),
                Is.EqualTo(pairOneSampleOne));
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .MeasuredLogicalOrdinal(1, 2, 240),
                Is.Not.EqualTo(pairOneSampleOne));
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .MeasuredLogicalOrdinal(2, 1, 240),
                Is.EqualTo(241u));
            uint warmup = GpuDrivenInstancePolicyBenchmarkController
                .UnmeasuredLogicalOrdinal(1u);
            Assert.That(pairOneSampleOne & 0x80000000u, Is.Zero);
            Assert.That(warmup & 0x80000000u, Is.Not.Zero);
            Assert.That(warmup, Is.Not.EqualTo(pairOneSampleOne));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuDrivenInstancePolicyBenchmarkController
                    .MeasuredLogicalOrdinal(0, 1, 240));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuDrivenInstancePolicyBenchmarkController
                    .MeasuredLogicalOrdinal(1, 241, 240));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuDrivenInstancePolicyBenchmarkController
                    .UnmeasuredLogicalOrdinal(0u));
        }

        [Test]
        public void PairedInputEvidenceRequiresAllThreeExactFields()
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .PairedInputFieldsMatch(
                        7u,
                        11UL,
                        13UL,
                        7u,
                        11UL,
                        13UL),
                Is.True);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .PairedInputFieldsMatch(
                        7u, 11UL, 13UL, 8u, 11UL, 13UL),
                Is.False);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .PairedInputFieldsMatch(
                        7u, 11UL, 13UL, 7u, 12UL, 13UL),
                Is.False);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .PairedInputFieldsMatch(
                        7u, 11UL, 13UL, 7u, 11UL, 14UL),
                Is.False);
        }

        [Test]
        public void AcquisitionAndValidationDeadlinesAreInclusiveAndSafe()
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .DeadlineHasExpired(9.99, 10.0),
                Is.False);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .DeadlineHasExpired(10.0, 10.0),
                Is.True);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .DeadlineHasExpired(10.01, 10.0),
                Is.True);
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .DeadlineHasExpired(double.NaN, 10.0),
                Is.True);
        }

        [Test]
        public void EngineIndirectEvidenceRequiresExactCopiedWords()
        {
            uint[] expected = { 36u, 9u, 0u, 0u, 0u };
            uint[] exact = { 36u, 9u, 0u, 0u, 0u };
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateExactEngineIndirectWords(
                        expected,
                        exact,
                        out ulong expectedHash,
                        out ulong exactHash,
                        out int exactMismatchCount),
                Is.True);
            Assert.That(exactHash, Is.EqualTo(expectedHash));
            Assert.That(exactMismatchCount, Is.Zero);

            uint[] changed = { 36u, 0u, 0u, 0u, 0u };
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateExactEngineIndirectWords(
                        expected,
                        changed,
                        out _,
                        out _,
                        out int changedMismatchCount),
                Is.False);
            Assert.That(changedMismatchCount, Is.EqualTo(1));
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateExactEngineIndirectWords(
                        expected,
                        new uint[4],
                        out _,
                        out _,
                        out int lengthMismatchCount),
                Is.False);
            Assert.That(lengthMismatchCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void RenderTargetEvidenceRejectsBlackAndHashesNonBlackRgba()
        {
            byte[] black =
            {
                0, 0, 0, 255,
                0, 0, 0, 255,
            };
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateRenderTargetRgba32(
                        black,
                        2,
                        1,
                        out ulong blackHash,
                        out ulong blackReferenceHash,
                        out int blackPixelCount),
                Is.False);
            Assert.That(blackHash, Is.EqualTo(blackReferenceHash));
            Assert.That(blackPixelCount, Is.Zero);

            byte[] colored = (byte[])black.Clone();
            colored[0] = 242;
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateRenderTargetRgba32(
                        colored,
                        2,
                        1,
                        out ulong coloredHash,
                        out ulong coloredBlackReferenceHash,
                        out int coloredPixelCount),
                Is.True);
            Assert.That(coloredHash, Is.Not.EqualTo(blackHash));
            Assert.That(
                coloredBlackReferenceHash,
                Is.EqualTo(blackReferenceHash));
            Assert.That(coloredPixelCount, Is.EqualTo(1));
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateRenderTargetRgba32(
                        colored,
                        2,
                        1,
                        out ulong repeatedColoredHash,
                        out _,
                        out _),
                Is.True);
            Assert.That(repeatedColoredHash, Is.EqualTo(coloredHash));
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .ValidateRenderTargetRgba32(
                        new byte[7],
                        2,
                        1,
                        out _,
                        out _,
                        out _),
                Is.False);
        }

        [Test]
        public void AdapterPresentationResourcesRemainDiscoverable()
        {
            Assert.That(
                GpuDrivenInstancePolicyBenchmarkController
                    .AdapterPresentationResourceContractIsAvailable(),
                Is.True);
        }

        [UnityTest]
        public IEnumerator SafeNoneAndForcedSelectedDoNotInvokeSelector()
        {
            RequirePolicyGpuSupport();

            var safeAdapter = new GpuDrivenInstancePolicyBenchmarkAdapter(
                512,
                1,
                "visible25",
                20260830,
                0,
                GpuDrivenInstanceOutputMode.VisibleOnly,
                selector: null,
                drawGroupCount: 1,
                stagingSlotCount: 1);
            try
            {
                Assert.That(
                    safeAdapter.TryAcquireWorkSlot(
                        out int safeSlot,
                        out int safeWait),
                    Is.True);
                Assert.That(safeWait, Is.Zero);
                safeAdapter.PrepareSlot(
                    safeSlot,
                    GpuDrivenInstancePolicyBenchmarkCase.SafeBaseline(),
                    1u);
                GpuDrivenInstancePolicyRecordReceipt safe =
                    safeAdapter.RecordWorkload(safeSlot);
                Assert.That(safe.SelectorInvoked, Is.False);
                Assert.That(safe.SelectorCpuTicks, Is.Zero);
                Assert.That(
                    safe.Decision.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.None));
                Assert.That(
                    safe.PlannedUpload.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.None));
                Assert.That(
                    safe.RecordedUpload.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.None));
                safeAdapter.AbandonUnsubmittedWorkSlot(safeSlot);
            }
            finally
            {
                safeAdapter.Dispose();
            }

            GpuDrivenInstancePolicyDecision supplied =
                CreateFallbackDecision(
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            var forcedAdapter =
                new GpuDrivenInstancePolicyBenchmarkAdapter(
                    512,
                    1,
                    "visible25",
                    20260830,
                    1000,
                    GpuDrivenInstanceOutputMode.VisibleOnly,
                    selector: null,
                    drawGroupCount: 1,
                    stagingSlotCount: 1);
            try
            {
                Assert.That(
                    forcedAdapter.TryAcquireWorkSlot(
                        out int forcedSlot,
                        out int forcedWait),
                    Is.True);
                Assert.That(forcedWait, Is.Zero);
                GpuDrivenInstancePolicyBenchmarkCase forced =
                    GpuDrivenInstancePolicyBenchmarkCase.ForcedSelected(
                        in supplied);
                forcedAdapter.PrepareSlot(forcedSlot, forced, 3u);
                GpuDrivenInstancePolicyRecordReceipt recorded =
                    forcedAdapter.RecordWorkload(forcedSlot);
                Assert.That(recorded.SelectorInvoked, Is.False);
                Assert.That(recorded.SelectorCpuTicks, Is.Zero);
                Assert.That(
                    recorded.Decision.Source,
                    Is.EqualTo(
                        GpuDrivenInstancePolicyDecisionSource
                            .ForcedSelected));
                Assert.That(
                    recorded.Decision.UploadMode,
                    Is.EqualTo(supplied.UploadMode));
                Assert.That(
                    recorded.Decision.CullingMode,
                    Is.EqualTo(supplied.CullingMode));
                forcedAdapter.AbandonUnsubmittedWorkSlot(forcedSlot);
            }
            finally
            {
                forcedAdapter.Dispose();
            }
            yield break;
        }

        [UnityTest]
        public IEnumerator WarmActualAutoRecordAllocatesNoManagedMemory()
        {
            RequirePolicyGpuSupport();
            GpuDrivenInstancePolicySelector selector =
                CreateDirtySelector(
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            var adapter = new GpuDrivenInstancePolicyBenchmarkAdapter(
                1024,
                1,
                "visible25",
                20260830,
                1000,
                GpuDrivenInstanceOutputMode.VisibleOnly,
                selector,
                drawGroupCount: 1,
                stagingSlotCount: 1);
            try
            {
                GpuDrivenInstancePolicyObservation overhead =
                    adapter.CreateSelectorOverheadObservation();
                Assert.That(overhead.UploadPlan.HasPlan, Is.True);
                Assert.That(
                    overhead.UploadPlan.IsLiveTokenBound,
                    Is.True);
                Assert.That(overhead.UploadPlan.TokenIsValid, Is.True);
                Assert.That(
                    overhead.UploadPlan.PlannedMode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(
                    overhead.DirtyInstanceCount,
                    Is.EqualTo(102));
                Assert.That(
                    overhead.UploadPlan.DirtyRecordCount,
                    Is.EqualTo(overhead.DirtyInstanceCount));
                Assert.That(
                    overhead.HierarchyCandidateEstimateValid,
                    Is.True);
                Assert.That(
                    overhead.HierarchyCandidatePairCount,
                    Is.EqualTo(
                        adapter.ExpectedCandidateInstanceViewCount));
                Assert.That(
                    overhead.HierarchyCandidateEstimateRevision,
                    Is.EqualTo(adapter.VisibilityInputRevision));
                Assert.That(
                    overhead.HierarchyCandidateLayoutRevision,
                    Is.EqualTo(adapter.InstanceLayoutRevision));

                GpuDrivenInstancePolicyState overheadState = default;
                GpuDrivenInstancePolicyDecision overheadDecision = default;
                for (int index = 0;
                     index < GpuDrivenInstancePolicyContract
                         .MaximumConsecutiveFrames;
                     index++)
                {
                    overheadDecision = selector.Select(
                        in overhead,
                        ref overheadState);
                }
                Assert.That(
                    overheadDecision.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Dirty));
                Assert.That(overheadDecision.UsesProfileRule, Is.True);
                Assert.That(
                    overheadDecision.Flags &
                    (GpuDrivenInstancePolicyDecisionFlags
                        .UploadGateFallback |
                     GpuDrivenInstancePolicyDecisionFlags.ProfileFallback |
                     GpuDrivenInstancePolicyDecisionFlags
                         .InvalidObservation |
                     GpuDrivenInstancePolicyDecisionFlags.NoMatchingRule |
                     GpuDrivenInstancePolicyDecisionFlags
                         .HysteresisPending),
                    Is.EqualTo(GpuDrivenInstancePolicyDecisionFlags.None));
                Assert.That(overhead.UploadPlan.TokenIsValid, Is.True);

                Assert.That(
                    adapter.TryAcquireWorkSlot(
                        out int warmSlot,
                        out _),
                    Is.True);
                adapter.PrepareSlot(
                    warmSlot,
                    GpuDrivenInstancePolicyBenchmarkCase.ActualAuto(),
                    1u);
                adapter.RecordWorkload(warmSlot);
                Assert.That(overhead.UploadPlan.TokenIsValid, Is.False);
                Assert.Throws<InvalidOperationException>(() =>
                    adapter.CreateSelectorOverheadObservation());
                adapter.AbandonUnsubmittedWorkSlot(warmSlot);

                Assert.That(
                    adapter.TryAcquireWorkSlot(
                        out int measuredSlot,
                        out _),
                    Is.True);
                adapter.PrepareSlot(
                    measuredSlot,
                    GpuDrivenInstancePolicyBenchmarkCase.ActualAuto(),
                    2u);
                long before = GC.GetAllocatedBytesForCurrentThread();
                GpuDrivenInstancePolicyRecordReceipt receipt =
                    adapter.RecordWorkload(measuredSlot);
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(allocated, Is.Zero);
                Assert.That(receipt.SelectorInvoked, Is.True);
                Assert.That(
                    receipt.Decision.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Dirty));
                Assert.That(
                    receipt.Decision.Flags &
                    GpuDrivenInstancePolicyDecisionFlags
                        .UploadGateFallback,
                    Is.EqualTo(GpuDrivenInstancePolicyDecisionFlags.None));
                Assert.That(receipt.PlanCpuTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    receipt.SelectorCpuTicks,
                    Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    receipt.RecordCpuTicks,
                    Is.GreaterThanOrEqualTo(0));
                adapter.AbandonUnsubmittedWorkSlot(measuredSlot);
            }
            finally
            {
                adapter.Dispose();
            }
            yield break;
        }

        [UnityTest]
        public IEnumerator FlatFullAndHierarchicalDirtyValidateExactly()
        {
            RequirePolicyGpuSupport();
            var adapter = new GpuDrivenInstancePolicyBenchmarkAdapter(
                1024,
                2,
                "visible25",
                20260830,
                1000,
                GpuDrivenInstanceOutputMode.VisibleOnly,
                selector: null,
                drawGroupCount: 2,
                stagingSlotCount: 2);
            try
            {
                Assert.That(adapter.InstanceLayoutRevision, Is.Not.Zero);
                Assert.That(
                    adapter.ClusterLayoutRevision,
                    Is.EqualTo(adapter.InstanceLayoutRevision));
                Assert.That(adapter.VisibilityInputRevision, Is.Not.Zero);
                Assert.That(
                    adapter.VisibilityEstimateRevision,
                    Is.EqualTo(adapter.VisibilityInputRevision));

                Assert.That(
                    adapter.TryAcquireWorkSlot(
                        out int fullSlot,
                        out _),
                    Is.True);
                GpuDrivenInstancePolicyPreparationReceipt fullPreparation =
                    adapter.PrepareSlot(
                        fullSlot,
                        GpuDrivenInstancePolicyBenchmarkCase
                            .ForcedFullFlat(),
                        17u);
                GpuDrivenInstancePolicyRecordReceipt full =
                    adapter.RecordWorkload(fullSlot);
                Assert.That(
                    full.Decision.UploadMode,
                    Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
                Assert.That(
                    full.Decision.CullingMode,
                    Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
                Assert.That(
                    full.RecordedUpload.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.Full));
                Assert.Throws<InvalidOperationException>(() =>
                    adapter.MarkWorkSlotSubmitted(fullSlot));
                adapter.AppendLifetimeFence(fullSlot);
                Graphics.ExecuteCommandBuffer(adapter.Commands(fullSlot));
                adapter.MarkWorkSlotSubmitted(fullSlot);
                adapter.BeginValidation(fullSlot, "full-flat");

                GpuDrivenInstancePolicyValidationResult fullValidation;
                while (!adapter.TryCompleteValidation(out fullValidation))
                {
                    yield return null;
                }
                Assert.That(
                    fullValidation.Passed,
                    Is.True,
                    fullValidation.Message);
                Assert.That(
                    fullValidation.ActualStateHash,
                    Is.EqualTo(fullPreparation.ExpectedStateHash));
                Assert.That(
                    fullValidation.ActualOutputHash,
                    Is.EqualTo(adapter.ExpectedOutputHash));
                Assert.That(
                    fullValidation.HierarchyStatisticsAvailable,
                    Is.False);
                while (!adapter.AllCompletionFencesPassed)
                {
                    yield return null;
                }

                Assert.That(
                    adapter.TryAcquireWorkSlot(
                        out int dirtySlot,
                        out _),
                    Is.True);
                GpuDrivenInstancePolicyPreparationReceipt dirtyPreparation =
                    adapter.PrepareSlot(
                        dirtySlot,
                        GpuDrivenInstancePolicyBenchmarkCase
                            .ForcedDirtyHierarchy(),
                        18u);
                GpuDrivenInstancePolicyRecordReceipt dirty =
                    adapter.RecordWorkload(dirtySlot);
                Assert.That(
                    dirty.PlannedUpload.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(
                    dirty.RecordedUpload.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(
                    dirty.RecordedUpload.DirtyRecordCount,
                    Is.EqualTo(
                        dirtyPreparation.UpdatePlan.ChangedInstanceCount));
                Assert.That(
                    dirty.Decision.CullingMode,
                    Is.EqualTo(GpuDrivenInstanceCullingMode.Hierarchy));
                Assert.That(
                    dirty.Decision.OutputMode,
                    Is.EqualTo(GpuDrivenInstanceOutputMode.VisibleOnly));
                adapter.AppendLifetimeFence(dirtySlot);
                Graphics.ExecuteCommandBuffer(adapter.Commands(dirtySlot));
                adapter.MarkWorkSlotSubmitted(dirtySlot);
                adapter.BeginValidation(dirtySlot, "dirty-hierarchy");

                GpuDrivenInstancePolicyValidationResult dirtyValidation;
                while (!adapter.TryCompleteValidation(out dirtyValidation))
                {
                    yield return null;
                }
                Assert.That(
                    dirtyValidation.Passed,
                    Is.True,
                    dirtyValidation.Message);
                Assert.That(
                    dirtyValidation.ActualStateHash,
                    Is.EqualTo(dirtyPreparation.ExpectedStateHash));
                Assert.That(
                    dirtyValidation.ActualOutputHash,
                    Is.EqualTo(adapter.ExpectedOutputHash));
                Assert.That(
                    dirtyValidation.HierarchyStatisticsAvailable,
                    Is.True);
                Assert.That(
                    dirtyValidation.CoarseVisibleClusterViewCount,
                    Is.EqualTo(
                        dirtyValidation
                            .ExpectedCoarseVisibleClusterViewCount));
                Assert.That(
                    dirtyValidation.CandidateInstanceViewCount,
                    Is.EqualTo(
                        dirtyValidation
                            .ExpectedCandidateInstanceViewCount));
                Assert.That(
                    dirtyValidation.HierarchicalVisiblePairCount,
                    Is.EqualTo(
                        dirtyValidation
                            .ExpectedHierarchicalVisiblePairCount));
                while (!adapter.AllCompletionFencesPassed)
                {
                    yield return null;
                }
            }
            finally
            {
                if (adapter.AllCompletionFencesPassed)
                {
                    adapter.Dispose();
                }
            }
        }

        [UnityTest]
        public IEnumerator ReusedAndAbandonedSlotRejectsStalePreparedState()
        {
            RequirePolicyGpuSupport();
            var adapter = new GpuDrivenInstancePolicyBenchmarkAdapter(
                512,
                1,
                "visible25",
                20260830,
                0,
                GpuDrivenInstanceOutputMode.VisibleOnly,
                selector: null,
                drawGroupCount: 1,
                stagingSlotCount: 1);
            bool submitted = false;
            try
            {
                Assert.That(
                    adapter.TryAcquireWorkSlot(out int slotIndex, out _),
                    Is.True);
                adapter.PrepareSlot(
                    slotIndex,
                    GpuDrivenInstancePolicyBenchmarkCase.ForcedFullFlat(),
                    1u);
                adapter.RecordWorkload(slotIndex);
                adapter.AppendLifetimeFence(slotIndex);
                Graphics.ExecuteCommandBuffer(adapter.Commands(slotIndex));
                adapter.MarkWorkSlotSubmitted(slotIndex);
                submitted = true;

                while (!adapter.AllCompletionFencesPassed)
                {
                    yield return null;
                }

                Assert.That(
                    adapter.TryAcquireWorkSlot(
                        out int reusedSlot,
                        out _),
                    Is.True);
                Assert.That(reusedSlot, Is.EqualTo(slotIndex));
                Assert.Throws<InvalidOperationException>(() =>
                    adapter.ComputeSlotStateHash(reusedSlot));
                adapter.AbandonUnsubmittedWorkSlot(reusedSlot);
                Assert.Throws<InvalidOperationException>(() =>
                    adapter.ComputeSlotStateHash(reusedSlot));
            }
            finally
            {
                if (!submitted || adapter.AllCompletionFencesPassed)
                {
                    adapter.Dispose();
                }
            }
        }

        private static GpuDrivenInstancePolicySelector
            CreateFallbackSelector()
        {
            GpuDrivenInstancePolicyEnvironment environment = default;
            bool created = GpuDrivenInstancePolicySelector.TryCreate(
                null,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out GpuDrivenInstancePolicyValidationError error);
            Assert.That(created, Is.False);
            Assert.That(
                error,
                Is.EqualTo(
                    GpuDrivenInstancePolicyValidationError.MissingProfile));
            Assert.That(selector, Is.Not.Null);
            return selector;
        }

        private static GpuDrivenInstancePolicySelector CreateDirtySelector(
            GpuDrivenInstanceOutputMode outputMode)
        {
            const string pipelineFingerprint =
                "policy-adapter-test-pipeline";
            const string shaderFingerprint =
                "policy-adapter-test-shader";
            const string calibrationProtocol =
                "policy-adapter-test-calibration-v1";
            string measurementFingerprint = new string('a', 64);
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyEnvironment.Capture(
                    pipelineFingerprint,
                    shaderFingerprint,
                    calibrationProtocol,
                    measurementFingerprint);
            var rule = new GpuDrivenInstancePolicyRule
            {
                ruleId = "dirty-flat",
                holdoutAccepted = true,
                holdoutEvidenceId = new string('d', 64),
                holdoutSampleCount = 100,
                requiredOutputMode = outputMode,
                uploadMode = GpuDrivenInstanceUploadMode.Dirty,
                cullingMode = GpuDrivenInstanceCullingMode.Flat,
                primitiveBackend = GpuPrimitiveBackend.Portable,
                requiredConsecutiveFrames = 1,
                enter = new GpuDrivenInstancePolicyRuleRange
                {
                    minActiveInstanceCount = 1,
                    maxActiveInstanceCount = int.MaxValue - 1,
                },
                exit = new GpuDrivenInstancePolicyRuleRange(),
            };
            var profile = new GpuDrivenInstancePolicyProfile
            {
                schemaVersion =
                    GpuDrivenInstancePolicyProfile.CurrentSchemaVersion,
                policyContractVersion = environment.PolicyContractVersion,
                profileRevision = 1,
                generatedUtc = "2026-08-30T00:00:00.0000000Z",
                sourceCommit = new string('b', 40),
                calibrationProtocol = environment.CalibrationProtocol,
                measurementContractFingerprint =
                    environment.MeasurementContractFingerprint,
                holdoutEvidenceSetId = new string('c', 64),
                holdoutAccepted = true,
                unityVersion = environment.UnityVersion,
                autotuningPackageVersion =
                    environment.AutotuningPackageVersion,
                gpuDrivenInstancesPackageVersion =
                    environment.GpuDrivenInstancesPackageVersion,
                processorType = environment.ProcessorType,
                operatingSystem = environment.OperatingSystem,
                pipelineContractFingerprint =
                    environment.PipelineContractFingerprint,
                shaderContractFingerprint =
                    environment.ShaderContractFingerprint,
                device = environment.Device,
                rules = new[] { rule },
            };

            bool accepted = GpuDrivenInstancePolicySelector.TryCreate(
                profile,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out GpuDrivenInstancePolicyValidationError error);
            Assert.That(accepted, Is.True, error.ToString());
            Assert.That(
                error,
                Is.EqualTo(GpuDrivenInstancePolicyValidationError.None));
            return selector;
        }

        private static GpuDrivenInstancePolicyDecision
            CreateFallbackDecision(GpuDrivenInstanceOutputMode outputMode)
        {
            GpuDrivenInstancePolicySelector selector =
                CreateFallbackSelector();
            var observation = new GpuDrivenInstancePolicyObservation
            {
                RequiredOutputMode = outputMode,
            };
            GpuDrivenInstancePolicyState state = default;
            return selector.Select(in observation, ref state);
        }

        private static GpuInstanceState State(int index)
        {
            return new GpuInstanceState(
                new Vector3(index, index + 1f, index + 2f),
                0.5f,
                new Vector4(10f, 20f, 30f, 40f),
                checked((uint)(1000 + index)),
                checked((uint)(index % 2)),
                1u,
                1u);
        }

        private static void RequirePolicyGpuSupport()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice ||
                !SystemInfo.supportsInstancing ||
                !SystemInfo.supportsIndirectArgumentsBuffer ||
                !SystemInfo.supportsGraphicsFence ||
                !SystemInfo.supportsAsyncCompute)
            {
                Assert.Ignore(
                    "Compute, instancing, indirect arguments, graphics " +
                    "fences, and CPU-queryable async-compute fences are " +
                    "required.");
            }
        }
    }
}
