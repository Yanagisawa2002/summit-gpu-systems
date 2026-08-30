using NUnit.Framework;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning.Tests
{
    public sealed class GpuDrivenInstancePolicyProfileTests
    {
        [Test]
        public void AcceptsExactEnvironmentAndHoldoutProfile()
        {
            GpuDrivenInstancePolicyProfile profile =
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule("exact"));
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();

            Assert.That(GpuDrivenInstancePolicySelector.TryCreate(
                profile,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out GpuDrivenInstancePolicyValidationError error), Is.True);
            Assert.That(selector.ProfileAccepted, Is.True);
            Assert.That(error,
                Is.EqualTo(GpuDrivenInstancePolicyValidationError.None));
        }

        [Test]
        public void MissingProfileReturnsUsableSafeSelector()
        {
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();
            Assert.That(GpuDrivenInstancePolicySelector.TryCreate(
                null,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out GpuDrivenInstancePolicyValidationError error), Is.False);
            Assert.That(error,
                Is.EqualTo(
                    GpuDrivenInstancePolicyValidationError.MissingProfile));

            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            GpuDrivenInstancePolicyState state = default;
            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);
            AssertSafeFallback(
                decision,
                GpuDrivenInstanceOutputMode.VisibleOnly);
        }

        [Test]
        public void RejectsEveryExactEnvironmentMismatch()
        {
            GpuDrivenInstancePolicyProfile profile =
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule("exact"));

            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    unityVersion: "6000.5.3f1"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    packageVersion: "0.2.1"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    processorType: "different-cpu"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    operatingSystem: "different-os"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    pipelineFingerprint: "different-pipeline"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    shaderFingerprint: "different-shader"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    calibrationProtocol: "different-protocol"));
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(
                    measurementFingerprint: new string('f', 64)));

            GpuDeviceFingerprint device =
                GpuDrivenInstancePolicyTestFactory.Device();
            device.graphicsVersion = "different-driver-contract";
            AssertEnvironmentMismatch(
                profile,
                GpuDrivenInstancePolicyTestFactory.Environment(device));
        }

        [Test]
        public void RejectsSchemaAndPolicyContractVersionMismatch()
        {
            GpuDrivenInstancePolicyProfile profile =
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule("version"));
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();

            profile.schemaVersion++;
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.SchemaMismatch);

            profile.schemaVersion =
                GpuDrivenInstancePolicyProfile.CurrentSchemaVersion;
            profile.policyContractVersion++;
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.ContractMismatch);
        }

        [Test]
        public void RejectsProfileOrEntryWithoutHoldoutAcceptance()
        {
            GpuDrivenInstancePolicyRule rule =
                GpuDrivenInstancePolicyTestFactory.Rule("holdout");
            GpuDrivenInstancePolicyProfile profile =
                GpuDrivenInstancePolicyTestFactory.Profile(rule);
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();

            profile.holdoutAccepted = false;
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.HoldoutNotAccepted);

            profile.holdoutAccepted = true;
            rule.holdoutAccepted = false;
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.InvalidRule);
        }

        [Test]
        public void RejectsPlaceholderOrMalformedProvenance()
        {
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();
            GpuDrivenInstancePolicyProfile profile =
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule("provenance"));

            profile.sourceCommit = "x";
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.HoldoutNotAccepted);

            profile = GpuDrivenInstancePolicyTestFactory.Profile(
                GpuDrivenInstancePolicyTestFactory.Rule("provenance"));
            profile.holdoutEvidenceSetId = "x";
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.HoldoutNotAccepted);

            profile = GpuDrivenInstancePolicyTestFactory.Profile(
                GpuDrivenInstancePolicyTestFactory.Rule("provenance"));
            profile.rules[0].holdoutEvidenceId = "x";
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.InvalidRule);

            profile = GpuDrivenInstancePolicyTestFactory.Profile(
                GpuDrivenInstancePolicyTestFactory.Rule("provenance"));
            profile.generatedUtc =
                "2026-08-30T08:00:00.0000000+08:00";
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.HoldoutNotAccepted);

            profile = GpuDrivenInstancePolicyTestFactory.Profile(
                GpuDrivenInstancePolicyTestFactory.Rule("provenance"));
            profile.generatedUtc = "2026-08-30";
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.HoldoutNotAccepted);
        }

        [Test]
        public void RejectsInvalidOrNonHystereticRanges()
        {
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();
            GpuDrivenInstancePolicyRule invalidBasisPoints =
                GpuDrivenInstancePolicyTestFactory.Rule("invalid-range");
            invalidBasisPoints.enter.maxVisibleBasisPoints = 10001;
            AssertRejected(
                GpuDrivenInstancePolicyTestFactory.Profile(
                    invalidBasisPoints),
                in environment,
                GpuDrivenInstancePolicyValidationError.InvalidRule);

            GpuDrivenInstancePolicyRule noExitMargin =
                GpuDrivenInstancePolicyTestFactory.Rule("no-margin");
            noExitMargin.exit = noExitMargin.enter;
            AssertRejected(
                GpuDrivenInstancePolicyTestFactory.Profile(noExitMargin),
                in environment,
                GpuDrivenInstancePolicyValidationError.InvalidRule);
        }

        [Test]
        public void RejectsOverlappingIntegerRuleRanges()
        {
            GpuDrivenInstancePolicyRule first =
                GpuDrivenInstancePolicyTestFactory.Rule(
                    "first",
                    minDirtyBasisPoints: 0,
                    maxDirtyBasisPoints: 2000);
            GpuDrivenInstancePolicyRule second =
                GpuDrivenInstancePolicyTestFactory.Rule(
                    "second",
                    minDirtyBasisPoints: 2000,
                    maxDirtyBasisPoints: 4000);
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();

            AssertRejected(
                GpuDrivenInstancePolicyTestFactory.Profile(first, second),
                in environment,
                GpuDrivenInstancePolicyValidationError.OverlappingRules);
        }

        [Test]
        public void RejectsHierarchyRuleForCulledTailContract()
        {
            GpuDrivenInstancePolicyRule rule =
                GpuDrivenInstancePolicyTestFactory.Rule(
                    "invalid-hierarchy",
                    cullingMode: GpuDrivenInstanceCullingMode.Hierarchy,
                    outputMode: GpuDrivenInstanceOutputMode.CulledTail);
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();

            AssertRejected(
                GpuDrivenInstancePolicyTestFactory.Profile(rule),
                in environment,
                GpuDrivenInstancePolicyValidationError.InvalidRule);
        }

        private static void AssertEnvironmentMismatch(
            GpuDrivenInstancePolicyProfile profile,
            GpuDrivenInstancePolicyEnvironment environment)
        {
            AssertRejected(
                profile,
                in environment,
                GpuDrivenInstancePolicyValidationError.EnvironmentMismatch);
        }

        private static void AssertRejected(
            GpuDrivenInstancePolicyProfile profile,
            in GpuDrivenInstancePolicyEnvironment environment,
            GpuDrivenInstancePolicyValidationError expected)
        {
            Assert.That(GpuDrivenInstancePolicySelector.TryCreate(
                profile,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out GpuDrivenInstancePolicyValidationError actual), Is.False);
            Assert.That(selector, Is.Not.Null);
            Assert.That(selector.ProfileAccepted, Is.False);
            Assert.That(actual, Is.EqualTo(expected));
        }

        private static void AssertSafeFallback(
            GpuDrivenInstancePolicyDecision decision,
            GpuDrivenInstanceOutputMode outputMode)
        {
            Assert.That(decision.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(decision.OutputMode, Is.EqualTo(outputMode));
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(decision.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            Assert.That(decision.UsesProfileRule, Is.False);
        }
    }
}
