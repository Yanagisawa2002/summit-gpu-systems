using System.Linq;
using NUnit.Framework;

public sealed class GpuDeadlineSchedulerBenchmarkScheduleTests
{
    [Test]
    public void FourSuperRoundsAreBalancedAndCounterbalanced()
    {
        GpuDeadlineSchedulerScheduleEntry[] schedule =
            GpuDeadlineSchedulerBenchmarkSchedule.Build(4).ToArray();

        Assert.That(schedule.Length, Is.EqualTo(16));
        Assert.That(
            schedule.Count(entry => entry.Variant ==
                GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics),
            Is.EqualTo(8));
        Assert.That(
            schedule.Count(entry => entry.Variant ==
                GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive),
            Is.EqualTo(8));
        Assert.That(
            schedule.Select(entry => entry.PairIndex).Distinct().Count(),
            Is.EqualTo(8));
        Assert.That(
            schedule.Where(entry => entry.WithinPairPosition == 2).All(
                entry => entry.PairOrder == "AB" ||
                    entry.PairOrder == "BA"),
            Is.True);
    }

    [Test]
    public void LogicalStatesRepeatOverSixtyFourSamples()
    {
        Assert.That(
            GpuDeadlineSchedulerBenchmarkSchedule.LogicalState(0),
            Is.EqualTo(0));
        Assert.That(
            GpuDeadlineSchedulerBenchmarkSchedule.LogicalState(63),
            Is.EqualTo(63));
        Assert.That(
            GpuDeadlineSchedulerBenchmarkSchedule.LogicalState(64),
            Is.EqualTo(0));
    }
}
