using NUnit.Framework;

public sealed class GpuRuntimeSchedulerTraceTests
{
    [TestCase("bursts")]
    [TestCase("changing-costs")]
    [TestCase("dependencies")]
    [TestCase("overload")]
    [TestCase("background-progress")]
    public void ReplayCompletesAllWorkAndMatchesDeterministicTiming(string scenario)
    {
        var a = GpuRuntimeSchedulerTrace.Simulate(scenario, 64, false);
        var b = GpuRuntimeSchedulerTrace.Simulate(scenario, 64, false);
        Assert.That(a.completed, Is.EqualTo(a.offered));
        Assert.That(a.criticalP99Us, Is.EqualTo(b.criticalP99Us));
        Assert.That(a.acceptedCostSamples, Is.GreaterThan(0));
        Assert.That(a.backgroundCompleted, Is.GreaterThan(0));
        for (int i = 0; i < a.rows.Length; i++)
        {
            Assert.That(a.rows[i].submitUs, Is.EqualTo(b.rows[i].submitUs));
            Assert.That(a.rows[i].completeUs, Is.EqualTo(b.rows[i].completeUs));
            Assert.That(a.rows[i].completeUs - a.rows[i].submitUs, Is.EqualTo(a.rows[i].actualCostUs));
            if (scenario == "dependencies" && i % 4 != 0)
                Assert.That(a.rows[i].submitUs, Is.GreaterThanOrEqualTo(a.rows[i - 1].completeUs));
        }
        if (scenario == "overload") Assert.That(a.backpressureAttempts, Is.GreaterThan(0));
        if (scenario == "background-progress") Assert.That(a.rows[0].submitUs, Is.LessThanOrEqualTo(1000));
    }
}
