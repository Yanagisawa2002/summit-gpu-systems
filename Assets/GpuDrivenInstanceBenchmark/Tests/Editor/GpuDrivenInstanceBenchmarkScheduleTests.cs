using System;
using System.Linq;
using NUnit.Framework;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceBenchmarkScheduleTests
    {
        [Test]
        public void TwoSuperRoundsUseBalancedAbbaBaabPairs()
        {
            GpuDrivenInstanceScheduleEntry[] schedule =
                GpuDrivenInstanceBenchmarkSchedule.Build(2).ToArray();

            Assert.That(schedule, Has.Length.EqualTo(10));
            Assert.That(
                schedule.Select(entry => entry.Variant),
                Is.EqualTo(new[]
                {
                    GpuDrivenInstanceBenchmarkVariant.Control,
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Control,
                }));
            Assert.That(
                schedule.Where(entry =>
                        entry.Variant !=
                        GpuDrivenInstanceBenchmarkVariant.Control)
                    .Select(entry => entry.PairOrder),
                Is.EqualTo(new[]
                {
                    "AB", "AB", "BA", "BA",
                    "BA", "BA", "AB", "AB",
                }));
            Assert.That(
                schedule.Select(entry => entry.BlockIndex),
                Is.EqualTo(Enumerable.Range(1, 10)));
        }

        [Test]
        public void ScheduleRejectsNonPositiveSuperRoundCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuDrivenInstanceBenchmarkSchedule.Build(0));
        }

        [Test]
        public void LogSuppressionRequiresBenchmarkFlag()
        {
            Assert.That(
                GpuDrivenInstanceBenchmarkController
                    .ShouldSuppressInformationalLogs(Array.Empty<string>()),
                Is.False);
            Assert.That(
                GpuDrivenInstanceBenchmarkController
                    .ShouldSuppressInformationalLogs(new[]
                    {
                        "-gpu-driven-instance-benchmark",
                    }),
                Is.True);
        }
    }
}
