using System;
using System.Linq;
using NUnit.Framework;

namespace Summit.GpuAdaptiveBinning.Benchmark.Tests
{
    public sealed class GpuAdaptiveBinningBenchmarkScheduleTests
    {
        [Test]
        public void FormalScheduleHasEightBalancedAdjacentPairs()
        {
            Assert.That(
                GpuAdaptiveBinningBenchmarkSchedule
                    .FormalSuperRoundCount,
                Is.EqualTo(4));
            Assert.That(
                GpuAdaptiveBinningBenchmarkSchedule.Contract(4),
                Is.EqualTo(
                    "control-pre;ABBA;BAAB;ABBA;BAAB;control-post"));

            GpuAdaptiveBinningScheduleEntry[] schedule =
                GpuAdaptiveBinningBenchmarkSchedule
                    .Build(
                        GpuAdaptiveBinningBenchmarkSchedule
                            .FormalSuperRoundCount)
                    .ToArray();
            Assert.That(schedule, Has.Length.EqualTo(18));
            Assert.That(
                schedule.Select(entry => entry.BlockIndex),
                Is.EqualTo(Enumerable.Range(1, 18)));
            Assert.That(
                schedule.Select(entry => entry.Variant),
                Is.EqualTo(new[]
                {
                    GpuAdaptiveBinningBenchmarkVariant.Control,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Control,
                }));

            AssertControlEntry(schedule[0], "control-pre");
            AssertControlEntry(schedule[schedule.Length - 1], "control-post");

            GpuAdaptiveBinningScheduleEntry[] measurements =
                schedule
                    .Where(entry =>
                        entry.Variant !=
                        GpuAdaptiveBinningBenchmarkVariant.Control)
                    .ToArray();
            Assert.That(measurements, Has.Length.EqualTo(16));
            Assert.That(
                measurements.Count(entry =>
                    entry.Variant ==
                    GpuAdaptiveBinningBenchmarkVariant.Direct),
                Is.EqualTo(8));
            Assert.That(
                measurements.Count(entry =>
                    entry.Variant ==
                    GpuAdaptiveBinningBenchmarkVariant.Radix),
                Is.EqualTo(8));

            for (int pairIndex = 1; pairIndex <= 8; pairIndex++)
            {
                GpuAdaptiveBinningScheduleEntry[] pair =
                    measurements
                        .Where(entry => entry.PairIndex == pairIndex)
                        .OrderBy(entry => entry.WithinPairPosition)
                        .ToArray();
                Assert.That(pair, Has.Length.EqualTo(2));
                Assert.That(
                    pair.Select(entry => entry.WithinPairPosition),
                    Is.EqualTo(new[] { 1, 2 }));
                Assert.That(
                    pair.Select(entry => entry.BlockIndex),
                    Is.EqualTo(new[]
                    {
                        pair[0].BlockIndex,
                        pair[0].BlockIndex + 1,
                    }));

                if (pair[0].PairOrder == "AB")
                {
                    Assert.That(
                        pair.Select(entry => entry.Variant),
                        Is.EqualTo(new[]
                        {
                            GpuAdaptiveBinningBenchmarkVariant.Direct,
                            GpuAdaptiveBinningBenchmarkVariant.Radix,
                        }));
                }
                else
                {
                    Assert.That(pair[0].PairOrder, Is.EqualTo("BA"));
                    Assert.That(
                        pair.Select(entry => entry.Variant),
                        Is.EqualTo(new[]
                        {
                            GpuAdaptiveBinningBenchmarkVariant.Radix,
                            GpuAdaptiveBinningBenchmarkVariant.Direct,
                        }));
                }
                Assert.That(
                    pair[1].PairOrder,
                    Is.EqualTo(pair[0].PairOrder));
            }

            Assert.That(
                measurements
                    .Where(entry => entry.WithinPairPosition == 1)
                    .Count(entry => entry.PairOrder == "AB"),
                Is.EqualTo(4));
            Assert.That(
                measurements
                    .Where(entry => entry.WithinPairPosition == 1)
                    .Count(entry => entry.PairOrder == "BA"),
                Is.EqualTo(4));

            foreach (int sequencePosition in Enumerable.Range(1, 4))
            {
                Assert.That(
                    measurements.Count(entry =>
                        entry.SequencePosition == sequencePosition &&
                        entry.Variant ==
                        GpuAdaptiveBinningBenchmarkVariant.Direct),
                    Is.EqualTo(2));
                Assert.That(
                    measurements.Count(entry =>
                        entry.SequencePosition == sequencePosition &&
                        entry.Variant ==
                        GpuAdaptiveBinningBenchmarkVariant.Radix),
                    Is.EqualTo(2));
            }
        }

        [Test]
        public void DiscoveryScheduleRetainsBalancedAbbaBaabOrder()
        {
            Assert.That(
                GpuAdaptiveBinningBenchmarkSchedule
                    .DiscoverySuperRoundCount,
                Is.EqualTo(2));
            Assert.That(
                GpuAdaptiveBinningBenchmarkSchedule.Contract(2),
                Is.EqualTo(
                    "control-pre;ABBA;BAAB;control-post"));

            GpuAdaptiveBinningScheduleEntry[] measurements =
                GpuAdaptiveBinningBenchmarkSchedule
                    .Build(2)
                    .Where(entry =>
                        entry.Variant !=
                        GpuAdaptiveBinningBenchmarkVariant.Control)
                    .ToArray();
            Assert.That(measurements, Has.Length.EqualTo(8));
            Assert.That(
                measurements
                    .Where(entry => entry.WithinPairPosition == 1)
                    .Select(entry => entry.PairOrder),
                Is.EqualTo(new[] { "AB", "BA", "BA", "AB" }));

            foreach (int sequencePosition in Enumerable.Range(1, 4))
            {
                Assert.That(
                    measurements.Count(entry =>
                        entry.SequencePosition == sequencePosition &&
                        entry.Variant ==
                        GpuAdaptiveBinningBenchmarkVariant.Direct),
                    Is.EqualTo(1));
                Assert.That(
                    measurements.Count(entry =>
                        entry.SequencePosition == sequencePosition &&
                        entry.Variant ==
                        GpuAdaptiveBinningBenchmarkVariant.Radix),
                    Is.EqualTo(1));
            }
        }

        [Test]
        public void ScheduleAndContractRejectNonPositiveSuperRoundCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningBenchmarkSchedule.Build(0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningBenchmarkSchedule.Build(-1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningBenchmarkSchedule.Contract(0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningBenchmarkSchedule.Contract(-1));
        }

        [Test]
        public void InformationalLogSuppressionRequiresExplicitBenchmarkFlag()
        {
            Assert.That(
                GpuAdaptiveBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(null),
                Is.False);
            Assert.That(
                GpuAdaptiveBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(
                        Array.Empty<string>()),
                Is.False);
            Assert.That(
                GpuAdaptiveBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-batchmode",
                        "-gpu-adaptive-binning-benchmark",
                    }),
                Is.True);
            Assert.That(
                GpuAdaptiveBinningBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-GPU-ADAPTIVE-BINNING-BENCHMARK",
                    }),
                Is.True);
        }

        private static void AssertControlEntry(
            GpuAdaptiveBinningScheduleEntry entry,
            string expectedBlockType)
        {
            Assert.That(entry.BlockType, Is.EqualTo(expectedBlockType));
            Assert.That(
                entry.Variant,
                Is.EqualTo(
                    GpuAdaptiveBinningBenchmarkVariant.Control));
            Assert.That(entry.SuperRound, Is.Zero);
            Assert.That(entry.SequencePosition, Is.Zero);
            Assert.That(entry.PairIndex, Is.Zero);
            Assert.That(entry.PairOrder, Is.Empty);
            Assert.That(entry.WithinPairPosition, Is.Zero);
        }
    }
}
