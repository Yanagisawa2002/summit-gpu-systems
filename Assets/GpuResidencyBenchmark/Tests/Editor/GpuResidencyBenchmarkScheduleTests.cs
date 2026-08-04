using System.Linq;
using NUnit.Framework;

public sealed class GpuResidencyBenchmarkScheduleTests
{
    [Test]
    public void FormalScheduleIsBalanced()
    {
        GpuResidencyScheduleEntry[] schedule =
            GpuResidencyBenchmarkSchedule.Build(4).ToArray();
        Assert.That(schedule.Length, Is.EqualTo(16));
        Assert.That(schedule.Count(entry => entry.Variant ==
            GpuResidencyBenchmarkVariant.RebuildVisibleSet), Is.EqualTo(8));
        Assert.That(schedule.Count(entry => entry.Variant ==
            GpuResidencyBenchmarkVariant.PersistentLru), Is.EqualTo(8));
        Assert.That(schedule.Select(entry => entry.PairIndex).Distinct().Count(),
            Is.EqualTo(8));
    }
}
