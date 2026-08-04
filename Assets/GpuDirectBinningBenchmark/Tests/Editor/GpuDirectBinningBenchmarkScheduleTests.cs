using System;
using System.Linq;
using NUnit.Framework;

namespace Summit.GpuDirectBinning.Benchmark.Tests
{
    public sealed class GpuDirectBinningBenchmarkScheduleTests
    {
        [Test]
        public void TwoSuperRoundsHaveExactAbbaBaabPositionsAndPairs()
        {
            var schedule = GpuDirectBinningBenchmarkSchedule
                .Build(2)
                .ToArray();

            Assert.That(schedule, Has.Length.EqualTo(10));
            Assert.That(
                schedule.Select(entry => entry.BlockIndex),
                Is.EqualTo(Enumerable.Range(1, 10)));
            Assert.That(
                schedule.Select(entry => entry.BlockType),
                Is.EqualTo(new[]
                {
                    "control-pre",
                    "measurement",
                    "measurement",
                    "measurement",
                    "measurement",
                    "measurement",
                    "measurement",
                    "measurement",
                    "measurement",
                    "control-post",
                }));
            Assert.That(
                schedule.Select(entry => entry.Variant),
                Is.EqualTo(new[]
                {
                    GpuDirectBinningBenchmarkVariant.Control,
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Control,
                }));

            var measurements = schedule
                .Where(entry =>
                    entry.Variant !=
                    GpuDirectBinningBenchmarkVariant.Control)
                .ToArray();
            Assert.That(
                measurements.Select(entry => entry.SuperRound),
                Is.EqualTo(new[] { 1, 1, 1, 1, 2, 2, 2, 2 }));
            Assert.That(
                measurements.Select(entry => entry.SequencePosition),
                Is.EqualTo(new[] { 1, 2, 3, 4, 1, 2, 3, 4 }));
            Assert.That(
                measurements.Select(entry => entry.PairIndex),
                Is.EqualTo(new[] { 1, 1, 2, 2, 3, 3, 4, 4 }));
            Assert.That(
                measurements.Select(entry => entry.PairOrder),
                Is.EqualTo(new[]
                {
                    "AB",
                    "AB",
                    "BA",
                    "BA",
                    "BA",
                    "BA",
                    "AB",
                    "AB",
                }));
            Assert.That(
                measurements.Select(entry => entry.WithinPairPosition),
                Is.EqualTo(new[] { 1, 2, 1, 2, 1, 2, 1, 2 }));

            Assert.That(schedule[0].SuperRound, Is.Zero);
            Assert.That(schedule[0].SequencePosition, Is.Zero);
            Assert.That(schedule[0].PairIndex, Is.Zero);
            Assert.That(schedule[0].PairOrder, Is.Empty);
            Assert.That(schedule[0].WithinPairPosition, Is.Zero);
            Assert.That(schedule[9].SuperRound, Is.Zero);
            Assert.That(schedule[9].SequencePosition, Is.Zero);
            Assert.That(schedule[9].PairIndex, Is.Zero);
            Assert.That(schedule[9].PairOrder, Is.Empty);
            Assert.That(schedule[9].WithinPairPosition, Is.Zero);
        }

        [Test]
        public void ScheduleRejectsNonPositiveSuperRoundCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDirectBinningBenchmarkSchedule.Build(0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDirectBinningBenchmarkSchedule.Build(-1));
        }

        [Test]
        public void InformationalLogSuppressionRequiresExplicitBenchmarkFlag()
        {
            Assert.That(
                GpuDirectBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(null),
                Is.False);
            Assert.That(
                GpuDirectBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(Array.Empty<string>()),
                Is.False);
            Assert.That(
                GpuDirectBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-batchmode",
                        "-gpu-direct-binning-benchmark"
                    }),
                Is.True);
            Assert.That(
                GpuDirectBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-GPU-DIRECT-BINNING-BENCHMARK"
                    }),
                Is.True);
        }
    }
}
