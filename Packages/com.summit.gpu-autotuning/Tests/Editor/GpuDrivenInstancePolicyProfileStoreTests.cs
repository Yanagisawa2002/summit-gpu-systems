using System;
using System.IO;
using NUnit.Framework;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning.Tests
{
    public sealed class GpuDrivenInstancePolicyProfileStoreTests
    {
        [Test]
        public void SaveAndLoadRoundTripCompilesExactProfile()
        {
            string directory = NewTemporaryDirectory();
            try
            {
                string path = Path.Combine(directory, "profile.json");
                GpuDrivenInstancePolicyProfile expected =
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule("roundtrip"));
                GpuDrivenInstancePolicyProfileStore.Save(path, expected);
                GpuDrivenInstancePolicyEnvironment environment =
                    GpuDrivenInstancePolicyTestFactory.Environment();

                Assert.That(GpuDrivenInstancePolicyProfileStore.TryLoad(
                    path,
                    in environment,
                    out GpuDrivenInstancePolicyProfile actual,
                    out GpuDrivenInstancePolicySelector selector,
                    out GpuDrivenInstancePolicyValidationError error),
                    Is.True);
                Assert.That(error,
                    Is.EqualTo(GpuDrivenInstancePolicyValidationError.None));
                Assert.That(selector.ProfileAccepted, Is.True);
                Assert.That(actual, Is.Not.Null);
                Assert.That(actual.sourceCommit,
                    Is.EqualTo(expected.sourceCommit));
                Assert.That(actual.measurementContractFingerprint,
                    Is.EqualTo(expected.measurementContractFingerprint));
                Assert.That(actual.rules, Has.Length.EqualTo(1));
                Assert.That(actual.rules[0].ruleId, Is.EqualTo("roundtrip"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void CorruptJsonReturnsUsableSafeSelector()
        {
            string directory = NewTemporaryDirectory();
            try
            {
                string path = Path.Combine(directory, "corrupt.json");
                File.WriteAllText(path, "{not-json");
                GpuDrivenInstancePolicyEnvironment environment =
                    GpuDrivenInstancePolicyTestFactory.Environment();

                Assert.That(GpuDrivenInstancePolicyProfileStore.TryLoad(
                    path,
                    in environment,
                    out GpuDrivenInstancePolicyProfile profile,
                    out GpuDrivenInstancePolicySelector selector,
                    out GpuDrivenInstancePolicyValidationError error),
                    Is.False);
                Assert.That(profile, Is.Null);
                Assert.That(error,
                    Is.EqualTo(
                        GpuDrivenInstancePolicyValidationError.MissingProfile));
                AssertSafeSelector(selector);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void MeasurementMismatchReturnsUsableSafeSelector()
        {
            string directory = NewTemporaryDirectory();
            try
            {
                string path = Path.Combine(directory, "profile.json");
                GpuDrivenInstancePolicyProfileStore.Save(
                    path,
                    GpuDrivenInstancePolicyTestFactory.Profile(
                        GpuDrivenInstancePolicyTestFactory.Rule("mismatch")));
                GpuDrivenInstancePolicyEnvironment environment =
                    GpuDrivenInstancePolicyTestFactory.Environment(
                        measurementFingerprint: new string('f', 64));

                Assert.That(GpuDrivenInstancePolicyProfileStore.TryLoad(
                    path,
                    in environment,
                    out GpuDrivenInstancePolicyProfile rejectedProfile,
                    out GpuDrivenInstancePolicySelector selector,
                    out GpuDrivenInstancePolicyValidationError error),
                    Is.False);
                Assert.That(rejectedProfile, Is.Null);
                Assert.That(error,
                    Is.EqualTo(
                        GpuDrivenInstancePolicyValidationError
                            .EnvironmentMismatch));
                AssertSafeSelector(selector);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static string NewTemporaryDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "summit-gpu-policy-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void AssertSafeSelector(
            GpuDrivenInstancePolicySelector selector)
        {
            GpuDrivenInstancePolicyObservation observation =
                GpuDrivenInstancePolicyTestFactory.Observation(
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            GpuDrivenInstancePolicyState state = default;
            GpuDrivenInstancePolicyDecision decision = selector.Select(
                in observation,
                ref state);
            Assert.That(decision.UploadMode,
                Is.EqualTo(GpuDrivenInstanceUploadMode.Full));
            Assert.That(decision.OutputMode,
                Is.EqualTo(GpuDrivenInstanceOutputMode.VisibleOnly));
            Assert.That(decision.CullingMode,
                Is.EqualTo(GpuDrivenInstanceCullingMode.Flat));
            Assert.That(decision.PrimitiveBackend,
                Is.EqualTo(GpuPrimitiveBackend.Portable));
        }
    }
}
