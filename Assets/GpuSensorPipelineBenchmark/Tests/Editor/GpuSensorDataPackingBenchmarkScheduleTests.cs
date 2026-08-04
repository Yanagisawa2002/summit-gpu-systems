using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Summit.GpuSensorPipeline.Benchmark.Tests
{
    public sealed class GpuSensorDataPackingBenchmarkScheduleTests
    {
        [Test]
        public void FourSuperRoundsHaveEightBalancedAdjacentPairs()
        {
            GpuSensorDataPackingScheduleEntry[] schedule =
                GpuSensorDataPackingBenchmarkSchedule
                    .Build(
                        GpuSensorDataPackingBenchmarkSchedule
                            .FormalSuperRoundCount)
                    .ToArray();

            Assert.That(schedule, Has.Length.EqualTo(20));
            Assert.That(
                schedule.Select(entry => entry.BlockIndex),
                Is.EqualTo(Enumerable.Range(1, 20)));
            Assert.That(schedule[0].BlockType, Is.EqualTo("control-pre"));
            Assert.That(
                schedule[0].Variant,
                Is.EqualTo(
                    GpuSensorDataPackingBenchmarkVariant.Control));
            Assert.That(
                schedule[19].BlockType,
                Is.EqualTo("control-post"));
            Assert.That(
                schedule[19].Variant,
                Is.EqualTo(
                    GpuSensorDataPackingBenchmarkVariant.Control));

            GpuSensorDataPackingScheduleEntry[] precondition = schedule
                .Where(entry => entry.BlockType == "precondition")
                .ToArray();
            Assert.That(precondition, Has.Length.EqualTo(2));
            Assert.That(
                precondition.Select(entry => entry.BlockIndex),
                Is.EqualTo(new[] { 2, 3 }));
            Assert.That(
                precondition.Select(entry => entry.Variant),
                Is.EqualTo(new[]
                {
                    GpuSensorDataPackingBenchmarkVariant
                        .ExpandedAosQuantizedView,
                    GpuSensorDataPackingBenchmarkVariant
                        .PackedSoaFusedEndCursor,
                }));
            Assert.That(
                precondition.Select(entry => entry.SequencePosition),
                Is.EqualTo(new[] { 1, 2 }));
            Assert.That(
                precondition.All(entry =>
                    entry.SuperRound == 0 &&
                    entry.PairIndex == 0 &&
                    entry.PairOrder == string.Empty &&
                    entry.WithinPairPosition == 0),
                Is.True);
            Assert.That(
                GpuSensorDataPackingBenchmarkSchedule.PreconditionSampleCount,
                Is.EqualTo(240));

            GpuSensorDataPackingScheduleEntry[] measured = schedule
                .Where(entry => entry.BlockType == "measurement")
                .ToArray();
            Assert.That(measured, Has.Length.EqualTo(16));
            Assert.That(
                measured.Select(entry => entry.PairIndex).Distinct(),
                Is.EqualTo(Enumerable.Range(1, 8)));

            GpuSensorDataPackingBenchmarkVariant a =
                GpuSensorDataPackingBenchmarkVariant
                    .ExpandedAosQuantizedView;
            GpuSensorDataPackingBenchmarkVariant b =
                GpuSensorDataPackingBenchmarkVariant
                    .PackedSoaFusedEndCursor;
            for (int superRound = 1;
                 superRound <=
                    GpuSensorDataPackingBenchmarkSchedule
                        .FormalSuperRoundCount;
                 superRound++)
            {
                GpuSensorDataPackingScheduleEntry[] group = measured
                    .Where(entry =>
                        entry.SuperRound == superRound)
                    .OrderBy(entry => entry.SequencePosition)
                    .ToArray();
                Assert.That(group, Has.Length.EqualTo(4));
                Assert.That(
                    group.Select(entry => entry.Variant),
                    Is.EqualTo((superRound & 1) != 0
                        ? new[] { a, b, b, a }
                        : new[] { b, a, a, b }));
            }

            foreach (IGrouping<
                         int,
                         GpuSensorDataPackingScheduleEntry> pair in
                measured.GroupBy(entry => entry.PairIndex))
            {
                GpuSensorDataPackingScheduleEntry[] entries =
                    pair.OrderBy(
                            entry => entry.WithinPairPosition)
                        .ToArray();
                Assert.That(entries, Has.Length.EqualTo(2));
                Assert.That(
                    entries.Select(
                        entry => entry.WithinPairPosition),
                    Is.EqualTo(new[] { 1, 2 }));
                Assert.That(
                    entries.Select(entry => entry.BlockIndex),
                    Is.EqualTo(new[]
                    {
                        entries[0].BlockIndex,
                        entries[0].BlockIndex + 1
                    }));
                Assert.That(
                    entries[0].Variant,
                    Is.Not.EqualTo(entries[1].Variant));
                Assert.That(
                    entries[0].PairOrder,
                    Is.EqualTo(entries[0].Variant == a
                        ? "AB"
                        : "BA"));
                Assert.That(
                    entries[1].PairOrder,
                    Is.EqualTo(entries[0].PairOrder));
            }

            Assert.That(
                measured
                    .GroupBy(entry => entry.PairIndex)
                    .Count(group => group.First().PairOrder == "AB"),
                Is.EqualTo(4));
            Assert.That(
                measured
                    .GroupBy(entry => entry.PairIndex)
                    .Count(group => group.First().PairOrder == "BA"),
                Is.EqualTo(4));

            foreach (int position in Enumerable.Range(1, 4))
            {
                Assert.That(
                    measured.Count(entry =>
                        entry.SequencePosition == position &&
                        entry.Variant == a),
                    Is.EqualTo(2));
                Assert.That(
                    measured.Count(entry =>
                        entry.SequencePosition == position &&
                        entry.Variant == b),
                    Is.EqualTo(2));
            }
        }

        [Test]
        public void LogicalStatesResetAndWrapAtSixtyFour()
        {
            Assert.That(
                GpuSensorDataPackingBenchmarkSchedule
                    .LogicalStateForSample(0),
                Is.Zero);
            Assert.That(
                GpuSensorDataPackingBenchmarkSchedule
                    .LogicalStateForSample(63),
                Is.EqualTo(63));
            Assert.That(
                GpuSensorDataPackingBenchmarkSchedule
                    .LogicalStateForSample(64),
                Is.Zero);
            Assert.That(
                GpuSensorDataPackingBenchmarkSchedule
                    .LogicalStateForSample(129),
                Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuSensorDataPackingBenchmarkSchedule
                    .LogicalStateForSample(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuSensorDataPackingBenchmarkSchedule
                    .LogicalStateForSample(0, 0));
        }

        [Test]
        public void FormalScheduleRejectsInvalidSuperRoundCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuSensorDataPackingBenchmarkSchedule.Build(0));
        }

        [Test]
        public void BlockDigestBuffersAreCopyDestinations()
        {
            Assert.That(
                GpuSensorDataPackingBenchmarkAdapter
                    .BlockDigestBufferTarget,
                Is.EqualTo(
                    GraphicsBuffer.Target.Structured |
                    GraphicsBuffer.Target.CopyDestination));
        }

        [Test]
        public void ValidationReadsOnlyFiftySixBytesPerComparison()
        {
            Assert.That(
                GpuSensorDataPackingBenchmarkAdapter
                    .ValidationReadbackBytesPerComparison,
                Is.EqualTo(56L));
            Assert.That(
                GpuSensorDataPackingBenchmarkAdapter
                    .WorkloadReadbackBytesPerFrame,
                Is.Zero);
        }
    }
}
