using System.Collections.Generic;
using NUnit.Framework;

namespace Summit.GpuAutotuning.Tests
{
    public sealed class GpuAutotuneSelectorTests
    {
        [Test]
        public void SelectsFasterValidatedCandidateWithinP99Guardrail()
        {
            GpuAutotuneCandidateTiming baseline = Timing(
                "portable", 10.0, 10.2, 10.4);
            GpuAutotuneCandidateTiming wave = Timing(
                "wave-ops", 7.0, 7.2, 7.3);
            GpuAutotuneCandidateTiming selected = GpuAutotuneSelector.Select(
                "portable", new[] { baseline, wave });
            Assert.That(selected.CandidateId, Is.EqualTo("wave-ops"));
        }

        [Test]
        public void RejectsCandidateWithP99Regression()
        {
            GpuAutotuneCandidateTiming baseline = Timing(
                "portable", 10.0, 10.0, 10.0);
            GpuAutotuneCandidateTiming unstable = Timing(
                "wave-ops", 7.0, 7.1, 12.0);
            GpuAutotuneCandidateTiming selected = GpuAutotuneSelector.Select(
                "portable", new[] { baseline, unstable });
            Assert.That(selected.CandidateId, Is.EqualTo("portable"));
        }

        [Test]
        public void RejectsUnvalidatedCandidate()
        {
            GpuAutotuneCandidateTiming baseline = Timing(
                "portable", 10.0, 10.0, 10.0);
            GpuAutotuneCandidateTiming invalid = new GpuAutotuneCandidateTiming(
                "wave-ops", new[] { 5.0, 5.0, 5.0 }, false);
            Assert.That(
                GpuAutotuneSelector.Select(
                    "portable", new[] { baseline, invalid }).CandidateId,
                Is.EqualTo("portable"));
        }

        private static GpuAutotuneCandidateTiming Timing(
            string id,
            params double[] values)
        {
            return new GpuAutotuneCandidateTiming(
                id,
                new List<double>(values),
                true);
        }
    }
}
