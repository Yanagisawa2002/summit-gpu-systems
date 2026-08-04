using System;
using System.Collections.Generic;
using System.Linq;

namespace Summit.GpuAutotuning
{
    public readonly struct GpuAutotuneCandidateTiming
    {
        public GpuAutotuneCandidateTiming(
            string candidateId,
            IReadOnlyList<double> samples,
            bool validationPassed)
        {
            CandidateId = candidateId ?? throw new ArgumentNullException(
                nameof(candidateId));
            if (samples == null || samples.Count < 3)
            {
                throw new ArgumentException(
                    "At least three calibration samples are required.",
                    nameof(samples));
            }
            Samples = samples;
            ValidationPassed = validationPassed;
            MedianMs = Percentile(samples, 0.50);
            P99Ms = Percentile(samples, 0.99);
        }

        public string CandidateId { get; }
        public IReadOnlyList<double> Samples { get; }
        public bool ValidationPassed { get; }
        public double MedianMs { get; }
        public double P99Ms { get; }

        private static double Percentile(
            IReadOnlyList<double> values,
            double percentile)
        {
            double[] sorted = values.OrderBy(value => value).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                if (double.IsNaN(sorted[i]) || double.IsInfinity(sorted[i]) ||
                    sorted[i] < 0.0)
                {
                    throw new ArgumentException(
                        "Timing samples must be finite and non-negative.",
                        nameof(values));
                }
            }
            int index = Math.Max(0,
                (int)Math.Ceiling(sorted.Length * percentile) - 1);
            return sorted[index];
        }
    }

    public static class GpuAutotuneSelector
    {
        public static GpuAutotuneCandidateTiming Select(
            string baselineCandidateId,
            IReadOnlyList<GpuAutotuneCandidateTiming> candidates,
            double maximumP99RegressionPercent = 2.0,
            double minimumMedianImprovementPercent = 1.0)
        {
            if (string.IsNullOrWhiteSpace(baselineCandidateId))
            {
                throw new ArgumentException(
                    "A baseline candidate is required.",
                    nameof(baselineCandidateId));
            }
            if (candidates == null || candidates.Count == 0)
            {
                throw new ArgumentException(
                    "At least one candidate is required.",
                    nameof(candidates));
            }
            GpuAutotuneCandidateTiming baseline = candidates.FirstOrDefault(
                candidate => string.Equals(
                    candidate.CandidateId,
                    baselineCandidateId,
                    StringComparison.Ordinal));
            if (string.IsNullOrEmpty(baseline.CandidateId) ||
                !baseline.ValidationPassed)
            {
                throw new InvalidOperationException(
                    "The validated baseline candidate is missing.");
            }

            double maximumP99 = baseline.P99Ms *
                (1.0 + maximumP99RegressionPercent / 100.0);
            GpuAutotuneCandidateTiming selected = baseline;
            foreach (GpuAutotuneCandidateTiming candidate in candidates)
            {
                if (!candidate.ValidationPassed || candidate.P99Ms > maximumP99)
                {
                    continue;
                }
                double improvement = ImprovementPercent(
                    baseline.MedianMs,
                    candidate.MedianMs);
                if (improvement < minimumMedianImprovementPercent)
                {
                    continue;
                }
                if (candidate.MedianMs < selected.MedianMs ||
                    (candidate.MedianMs == selected.MedianMs &&
                     candidate.P99Ms < selected.P99Ms))
                {
                    selected = candidate;
                }
            }
            return selected;
        }

        public static double ImprovementPercent(double baseline, double value)
        {
            return baseline <= 0.0 ? 0.0 :
                ((baseline - value) / baseline) * 100.0;
        }
    }
}
