using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Summit.GpuSensorPipeline.Benchmark.Tests
{
    public sealed class GpuSensorPipelineBenchmarkScheduleTests
    {
        [Test]
        public void FourSuperRoundsHaveEightBalancedPairs()
        {
            GpuSensorPipelineScheduleEntry[] schedule =
                GpuSensorPipelineBenchmarkSchedule
                    .Build(GpuSensorPipelineBenchmarkSchedule.FormalSuperRoundCount)
                    .ToArray();

            Assert.That(schedule, Has.Length.EqualTo(18));
            Assert.That(
                schedule.Select(entry => entry.BlockIndex),
                Is.EqualTo(Enumerable.Range(1, 18)));
            Assert.That(schedule[0].BlockType, Is.EqualTo("control-pre"));
            Assert.That(schedule[0].Variant,
                Is.EqualTo(GpuSensorPipelineBenchmarkVariant.Control));
            Assert.That(schedule[17].BlockType, Is.EqualTo("control-post"));
            Assert.That(schedule[17].Variant,
                Is.EqualTo(GpuSensorPipelineBenchmarkVariant.Control));

            GpuSensorPipelineScheduleEntry[] measured = schedule
                .Where(entry =>
                    entry.Variant != GpuSensorPipelineBenchmarkVariant.Control)
                .ToArray();
            Assert.That(measured, Has.Length.EqualTo(16));
            Assert.That(
                measured.Select(entry => entry.PairIndex).Distinct(),
                Is.EqualTo(Enumerable.Range(1, 8)));

            for (int superRound = 1; superRound <= 4; superRound++)
            {
                GpuSensorPipelineScheduleEntry[] group = measured
                    .Where(entry => entry.SuperRound == superRound)
                    .OrderBy(entry => entry.SequencePosition)
                    .ToArray();
                Assert.That(group, Has.Length.EqualTo(4));
                GpuSensorPipelineBenchmarkVariant a =
                    GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded;
                GpuSensorPipelineBenchmarkVariant b =
                    GpuSensorPipelineBenchmarkVariant.GpuProducedResident;
                Assert.That(
                    group.Select(entry => entry.Variant),
                    Is.EqualTo((superRound & 1) != 0
                        ? new[] { a, b, b, a }
                        : new[] { b, a, a, b }));
            }

            foreach (IGrouping<int, GpuSensorPipelineScheduleEntry> pair in
                measured.GroupBy(entry => entry.PairIndex))
            {
                GpuSensorPipelineScheduleEntry[] entries =
                    pair.OrderBy(entry => entry.WithinPairPosition).ToArray();
                Assert.That(entries, Has.Length.EqualTo(2));
                Assert.That(
                    entries.Select(entry => entry.WithinPairPosition),
                    Is.EqualTo(new[] { 1, 2 }));
                Assert.That(
                    entries[0].Variant ==
                        GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded
                        ? entries[0].PairOrder
                        : entries[0].PairOrder,
                    Is.EqualTo(
                        entries[0].Variant ==
                            GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded
                            ? "AB"
                            : "BA"));
            }

            foreach (int position in Enumerable.Range(1, 4))
            {
                Assert.That(
                    measured.Count(entry =>
                        entry.SequencePosition == position &&
                        entry.Variant ==
                            GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded),
                    Is.EqualTo(2));
                Assert.That(
                    measured.Count(entry =>
                        entry.SequencePosition == position &&
                        entry.Variant ==
                            GpuSensorPipelineBenchmarkVariant.GpuProducedResident),
                    Is.EqualTo(2));
            }
        }

        [Test]
        public void LogicalStatesResetAndWrapAtSixtyFour()
        {
            Assert.That(
                GpuSensorPipelineBenchmarkSchedule.LogicalStateForSample(0),
                Is.Zero);
            Assert.That(
                GpuSensorPipelineBenchmarkSchedule.LogicalStateForSample(63),
                Is.EqualTo(63));
            Assert.That(
                GpuSensorPipelineBenchmarkSchedule.LogicalStateForSample(64),
                Is.Zero);
            Assert.That(
                GpuSensorPipelineBenchmarkSchedule.LogicalStateForSample(129),
                Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuSensorPipelineBenchmarkSchedule.LogicalStateForSample(-1));
        }

        [Test]
        public void MultiSensorVariantsUseTheSameBalancedScheduleContract()
        {
            GpuSensorPipelineScheduleEntry[] measured =
                GpuSensorPipelineBenchmarkSchedule.Build(
                        4,
                        GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor,
                        GpuSensorPipelineBenchmarkVariant.SharedSensorIndex)
                    .Where(entry =>
                        entry.Variant !=
                        GpuSensorPipelineBenchmarkVariant.Control)
                    .ToArray();

            Assert.That(measured, Has.Length.EqualTo(16));
            Assert.That(
                measured.Count(entry =>
                    entry.Variant ==
                    GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor),
                Is.EqualTo(8));
            Assert.That(
                measured.Count(entry =>
                    entry.Variant ==
                    GpuSensorPipelineBenchmarkVariant.SharedSensorIndex),
                Is.EqualTo(8));
            Assert.That(
                measured.Take(4).Select(entry => entry.Variant),
                Is.EqualTo(new[]
                {
                    GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor,
                    GpuSensorPipelineBenchmarkVariant.SharedSensorIndex,
                    GpuSensorPipelineBenchmarkVariant.SharedSensorIndex,
                    GpuSensorPipelineBenchmarkVariant.RebuiltPerSensor,
                }));
        }

        [Test]
        public void BlockDigestBuffersAreCopyDestinations()
        {
            Assert.That(
                GpuSensorPipelineBenchmarkAdapter.BlockDigestBufferTarget,
                Is.EqualTo(
                    GraphicsBuffer.Target.Structured |
                    GraphicsBuffer.Target.CopyDestination));
        }

        [Test]
        public void InformationalSuppressionRequiresUniqueFlag()
        {
            Assert.That(
                GpuSensorPipelineBenchmarkController
                    .ShouldSuppressInformationalLogs(Array.Empty<string>()),
                Is.False);
            Assert.That(
                GpuSensorPipelineBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-gpu-sensor-pipeline-benchmark"
                    }),
                Is.True);
            Assert.That(
                GpuSensorPipelineBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-GPU-SENSOR-PIPELINE-BENCHMARK"
                    }),
                Is.True);
        }
    }
}
