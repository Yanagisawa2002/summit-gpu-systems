using System.Linq;
using NUnit.Framework;

namespace Summit.GpuDeadlineScheduler.Tests
{
    public sealed class GpuDeadlinePlannerTests
    {
        [Test]
        public void FifoPlanPreservesArrivalOrderAndUsesGraphicsQueue()
        {
            GpuDeadlineJob[] jobs = Jobs();
            GpuDeadlineDispatch[] plan = GpuDeadlinePlanner.BuildPlan(
                jobs,
                GpuDeadlinePolicy.FifoGraphics,
                supportsAsyncCompute: true).ToArray();

            Assert.That(
                plan.Select(entry => entry.Job.JobId),
                Is.EqualTo(new[] { 20, 30, 10 }));
            Assert.That(
                plan.All(entry =>
                    entry.Queue == GpuDeadlineQueue.MainGraphics),
                Is.True);
        }

        [Test]
        public void DeadlinePlanOrdersLeastSlackFirstAndMapsQueueClasses()
        {
            GpuDeadlineDispatch[] plan = GpuDeadlinePlanner.BuildPlan(
                Jobs(),
                GpuDeadlinePolicy.LeastSlackAsync,
                supportsAsyncCompute: true).ToArray();

            Assert.That(
                plan.Select(entry => entry.Job.JobId),
                Is.EqualTo(new[] { 10, 30, 20 }));
            Assert.That(
                plan.Select(entry => entry.Queue),
                Is.EqualTo(new[]
                {
                    GpuDeadlineQueue.ComputeUrgent,
                    GpuDeadlineQueue.ComputeDefault,
                    GpuDeadlineQueue.ComputeBackground
                }));
        }

        [Test]
        public void DeadlinePlanFailsClosedToGraphicsWithoutAsyncCompute()
        {
            GpuDeadlineDispatch[] plan = GpuDeadlinePlanner.BuildPlan(
                Jobs(),
                GpuDeadlinePolicy.LeastSlackAsync,
                supportsAsyncCompute: false).ToArray();

            Assert.That(
                plan.All(entry =>
                    entry.Queue == GpuDeadlineQueue.MainGraphics),
                Is.True);
        }

        [Test]
        public void EqualSlackAndDeadlineRemainStableBySequence()
        {
            var first = Job(1, 4, GpuDeadlineClass.Normal, 4000, 1000);
            var second = Job(2, 2, GpuDeadlineClass.Normal, 4000, 1000);
            GpuDeadlineDispatch[] plan = GpuDeadlinePlanner.BuildPlan(
                new[] { first, second },
                GpuDeadlinePolicy.LeastSlackAsync,
                supportsAsyncCompute: true).ToArray();

            Assert.That(
                plan.Select(entry => entry.Job.JobId),
                Is.EqualTo(new[] { 2, 1 }));
        }

        private static GpuDeadlineJob[] Jobs()
        {
            return new[]
            {
                Job(20, 0, GpuDeadlineClass.Background, 12000, 3000),
                Job(30, 1, GpuDeadlineClass.Normal, 6000, 2500),
                Job(10, 2, GpuDeadlineClass.Critical, 2500, 1800)
            };
        }

        private static GpuDeadlineJob Job(
            int id,
            int sequence,
            GpuDeadlineClass deadlineClass,
            int deadline,
            int cost)
        {
            return new GpuDeadlineJob(
                id,
                sequence,
                deadlineClass,
                deadline,
                cost,
                1024,
                8,
                unchecked((uint)(id * 17)));
        }
    }
}
