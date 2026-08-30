using NUnit.Framework;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning.Tests
{
    public sealed class GpuDrivenInstancePolicyCompositionTests
    {
        [Test]
        public void CompositionUsesOnlyInstanceUploadCullingAndExternalBackend()
        {
            GpuDrivenInstancePolicyDecision instancePolicy = Select(
                GpuDrivenInstanceOutputMode.VisibleOnly);

            GpuDrivenInstanceExecutionPolicy composed =
                GpuDrivenInstancePolicyComposition.Compose(
                    in instancePolicy,
                    GpuDrivenInstanceOutputMode.VisibleOnly,
                    GpuPrimitiveBackend.WaveOps,
                    supportsWaveOps: true);

            Assert.That(composed.UploadMode,
                Is.EqualTo(instancePolicy.UploadMode));
            Assert.That(composed.CullingMode,
                Is.EqualTo(instancePolicy.CullingMode));
            Assert.That(composed.OutputMode,
                Is.EqualTo(GpuDrivenInstanceOutputMode.VisibleOnly));
            Assert.That(composed.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.WaveOps));
            Assert.That(composed.OutputConstraintAccepted, Is.True);
            Assert.That(composed.PrimitiveProfileAccepted, Is.True);
        }

        [TestCase(GpuPrimitiveBackend.Auto, true)]
        [TestCase(GpuPrimitiveBackend.WaveOps, false)]
        public void MissingOrUnsupportedPrimitiveChoiceFallsBackPortable(
            GpuPrimitiveBackend backend,
            bool supportsWaveOps)
        {
            GpuDrivenInstancePolicyDecision instancePolicy = Select(
                GpuDrivenInstanceOutputMode.CulledTail);

            GpuDrivenInstanceExecutionPolicy composed =
                GpuDrivenInstancePolicyComposition.Compose(
                    in instancePolicy,
                    GpuDrivenInstanceOutputMode.CulledTail,
                    backend,
                    supportsWaveOps);

            Assert.That(composed.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            Assert.That(composed.PrimitiveProfileAccepted, Is.False);
            Assert.That(
                composed.Flags &
                    GpuDrivenInstancePolicyDecisionFlags.BackendGateFallback,
                Is.Not.EqualTo(GpuDrivenInstancePolicyDecisionFlags.None));
        }

        [Test]
        public void OutputMismatchFailsClosedInsteadOfBecomingTunable()
        {
            GpuDrivenInstancePolicyDecision instancePolicy = Select(
                GpuDrivenInstanceOutputMode.VisibleOnly);

            GpuDrivenInstanceExecutionPolicy composed =
                GpuDrivenInstancePolicyComposition.Compose(
                    in instancePolicy,
                    GpuDrivenInstanceOutputMode.CulledTail,
                    GpuPrimitiveBackend.WaveOps,
                    supportsWaveOps: true);

            Assert.That(composed.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(composed.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(composed.OutputMode,
                Is.EqualTo(GpuDrivenInstanceOutputMode.CulledTail));
            Assert.That(composed.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            Assert.That(composed.InstancePolicyRuleIndex, Is.EqualTo(-1));
            Assert.That(composed.OutputConstraintAccepted, Is.False);
        }

        [Test]
        public void ContractV2RejectsPrimitiveChoiceInsideInstanceProfile()
        {
            GpuDrivenInstancePolicyProfile profile =
                GpuDrivenInstancePolicyTestFactory.Profile(
                    GpuDrivenInstancePolicyTestFactory.Rule(
                        "forbidden-primitive-axis",
                        primitiveBackend: GpuPrimitiveBackend.WaveOps));
            GpuDrivenInstancePolicyEnvironment environment =
                GpuDrivenInstancePolicyTestFactory.Environment();

            Assert.That(GpuDrivenInstancePolicySelector.TryCreate(
                profile,
                in environment,
                out _,
                out GpuDrivenInstancePolicyValidationError error), Is.False);
            Assert.That(error,
                Is.EqualTo(GpuDrivenInstancePolicyValidationError.InvalidRule));
        }

        private static GpuDrivenInstancePolicyDecision Select(
            GpuDrivenInstanceOutputMode outputMode)
        {
            GpuDrivenInstancePolicySelector selector =
                GpuDrivenInstancePolicyTestFactory.Selector(
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule(
                            "composition",
                            outputMode: outputMode)));
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(outputMode);
            GpuDrivenInstancePolicyState state = default;
            return selector.Select(in observation, ref state);
        }
    }
}
