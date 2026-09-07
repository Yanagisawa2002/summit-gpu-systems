using System;
using NUnit.Framework;

namespace Summit.GpuDeadlineScheduler.Tests
{
    public sealed class GpuRuntimeSchedulerTests
    {
        private static readonly GpuJobDependency[] NoEdges = Array.Empty<GpuJobDependency>();
        private static GpuRuntimeCostEstimator Costs(int samples = 64)
        {
            var costs = new GpuRuntimeCostEstimator(2, samples, 1000);
            costs.Configure(0, 1, 100, 1, 10000); costs.Configure(1, 1, 200, 1, 10000); return costs;
        }
        private static GpuRuntimeJob Job(int id, GpuDeadlineClass kind = GpuDeadlineClass.Critical, int deadline = 1000, int key = 0)
            => new GpuRuntimeJob(new GpuDeadlineJob(id, id, kind, deadline, 100, 1, 1, 1), key);
        private static GpuRuntimeDispatch Take(GpuRuntimeScheduler scheduler, long now = 0, GpuAsyncAdmissionEvidence evidence = default)
        { Assert.That(scheduler.TryPrepare(now, true, evidence, out var result), Is.True); scheduler.MarkSubmitted(result); return result; }

        [Test]
        public void DelayedSamplesRejectInvalidStaleDuplicateAndOutOfOrder()
        {
            var costs = Costs();
            long first = costs.BeginSample(0, 10), second = costs.BeginSample(0, 20);
            Assert.That(costs.TryUpdate(second, 50, 200, true), Is.True);
            Assert.That(costs.Estimate(0), Is.EqualTo(125));
            Assert.That(costs.TryUpdate(first, 60, 10, true), Is.False);
            Assert.That(costs.TryUpdate(second, 60, 10, true), Is.False);
            Assert.That(costs.TryUpdate(costs.BeginSample(0, 0), 1001, 200, true), Is.False);
            Assert.That(costs.TryUpdate(costs.BeginSample(0, 100), 99, 200, true), Is.False);
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
                Assert.That(costs.TryUpdate(costs.BeginSample(0, 100), 101, value, true), Is.False);
            Assert.That(costs.TryUpdate(costs.BeginSample(0, 100), 101, 100, false), Is.False);
            Assert.That(costs.SampleCount(0), Is.EqualTo(1));
        }

        [Test]
        public void ColdStartOutliersConvergenceRevisionAndRingBounds()
        {
            var costs = Costs(2);
            Assert.That(costs.Estimate(0), Is.EqualTo(100));
            Assert.That(costs.TryUpdate(costs.BeginSample(0, 0), 1, 1e12, true), Is.True);
            Assert.That(costs.Estimate(0), Is.EqualTo(175));
            for (int i = 0; i < 40; i++) costs.TryUpdate(costs.BeginSample(0, i), i + 1, 1000, true);
            Assert.That(costs.Estimate(0), Is.InRange(990, 1000));
            long old = costs.BeginSample(0, 100); costs.BeginSample(0, 101); costs.BeginSample(0, 102);
            Assert.That(costs.TryUpdate(old, 103, 100, true), Is.False);
            old = costs.BeginSample(0, 104); costs.Configure(0, 2, 10, 1, 20);
            Assert.That(costs.TryUpdate(old, 105, 100, true), Is.False);
            Assert.That(costs.Estimate(0), Is.EqualTo(10));
        }

        [Test]
        public void CyclesMissingDuplicateEdgesAndDuplicateIdsRejectAtomically()
        {
            var scheduler = new GpuRuntimeScheduler(4, 10000, 1000, 4, Costs());
            var jobs = new[] { Job(0), Job(1), Job(2) };
            var cycle = new[] { new GpuJobDependency(0, 1), new GpuJobDependency(1, 2), new GpuJobDependency(2, 0) };
            Assert.That(scheduler.TryAdmit(jobs, 3, cycle, 3, 0), Is.EqualTo(GpuAdmissionResult.Cycle));
            Assert.That(scheduler.PendingCount, Is.Zero);
            Assert.That(scheduler.TryAdmit(jobs, 3, new[] { new GpuJobDependency(0, 0) }, 1, 0), Is.EqualTo(GpuAdmissionResult.Cycle));
            Assert.That(scheduler.TryAdmit(jobs, 3, new[] { new GpuJobDependency(9, 0) }, 1, 0), Is.EqualTo(GpuAdmissionResult.InvalidDependency));
            Assert.That(scheduler.TryAdmit(jobs, 3, new[] { cycle[0], cycle[0] }, 2, 0), Is.EqualTo(GpuAdmissionResult.InvalidDependency));
            Assert.That(scheduler.TryAdmit(new[] { Job(0), Job(0) }, 2, NoEdges, 0, 0), Is.EqualTo(GpuAdmissionResult.DuplicateJob));
            Assert.That(scheduler.TryAdmit(jobs, 3, cycle, 2, 0), Is.EqualTo(GpuAdmissionResult.Accepted));
            Assert.That(Take(scheduler).Job.JobId, Is.EqualTo(0));
            Assert.That(Take(scheduler).Job.JobId, Is.EqualTo(1));
            Assert.That(Take(scheduler).Job.JobId, Is.EqualTo(2));
        }

        [Test]
        public void DagPreservesAlternatingQueuesAndRetainsCompletedPrerequisites()
        {
            var scheduler = new GpuRuntimeScheduler(4, 10000, 1000, 4, Costs());
            var jobs = new[] { Job(0, GpuDeadlineClass.Background), Job(1), Job(2, GpuDeadlineClass.Background), Job(3) };
            var edges = new[] { new GpuJobDependency(0, 1), new GpuJobDependency(1, 2), new GpuJobDependency(2, 3) };
            var evidence = new GpuAsyncAdmissionEvidence(32, 100, 90, 110, 100, 50, 40, 0.1, 0.05, 0, 1000);
            scheduler.TryAdmit(jobs, 4, edges, 3, 0);
            var a = Take(scheduler, 0, evidence);
            Assert.That(a.Queue, Is.EqualTo(GpuDeadlineQueue.MainGraphics));
            Assert.That(scheduler.TryComplete(a.Slot, a.Ticket), Is.True);
            Assert.That(scheduler.ReleaseCompleted(), Is.Zero);
            var b = Take(scheduler, 0, evidence);
            Assert.That(b.Queue, Is.EqualTo(GpuDeadlineQueue.ComputeUrgent));
            var c = Take(scheduler, 0, evidence); var d = Take(scheduler, 0, evidence);
            Assert.That(scheduler.DependsOn(c.Slot, b.Slot), Is.True);
            Assert.That(scheduler.DependsOn(d.Slot, c.Slot), Is.True);
            scheduler.TryComplete(b.Slot, b.Ticket); scheduler.TryComplete(c.Slot, c.Ticket); scheduler.TryComplete(d.Slot, d.Ticket);
            Assert.That(scheduler.ReleaseCompleted(), Is.EqualTo(4));
            Assert.That(scheduler.TryComplete(a.Slot, a.Ticket), Is.False);
        }

        [Test]
        public void AdmissionBackpressureUsesUpdatedCostAndSubmittedBudget()
        {
            var costs = Costs(); var scheduler = new GpuRuntimeScheduler(2, 200, 1000, 1, costs);
            var jobs = new[] { Job(0), Job(1) };
            Assert.That(scheduler.TryAdmit(jobs, 2, NoEdges, 0, 0), Is.EqualTo(GpuAdmissionResult.Accepted));
            var first = Take(scheduler);
            Assert.That(scheduler.TryPrepare(1, true, default, out _), Is.False);
            Assert.That(scheduler.TryAdmit(new[] { Job(2) }, 1, NoEdges, 0, 1), Is.EqualTo(GpuAdmissionResult.Capacity));
            scheduler.TryComplete(first.Slot, first.Ticket); scheduler.ReleaseCompleted();
            costs.TryUpdate(costs.BeginSample(0, 1), 2, 400, true);
            Assert.That(scheduler.TryAdmit(new[] { Job(2) }, 1, NoEdges, 0, 2), Is.EqualTo(GpuAdmissionResult.CostBudget));
            Assert.That(scheduler.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void AgingGuaranteesBackgroundProgressUnderContinuousCriticalArrivals()
        {
            var scheduler = new GpuRuntimeScheduler(8, 10000, 500, 1, Costs());
            scheduler.TryAdmit(new[] { Job(0, GpuDeadlineClass.Background, 100000) }, 1, NoEdges, 0, 0);
            int backgroundAt = -1;
            for (int frame = 0; frame < 10; frame++)
            {
                scheduler.TryAdmit(new[] { Job(frame + 1, deadline: 1) }, 1, NoEdges, 0, frame * 100);
                var selected = Take(scheduler, frame * 100);
                if (selected.Job.JobId == 0) backgroundAt = frame;
                scheduler.TryComplete(selected.Slot, selected.Ticket); scheduler.ReleaseCompleted();
            }
            Assert.That(backgroundAt, Is.EqualTo(5));
        }

        [Test]
        public void EvidenceFailsClosedOnSaturationExpiryInvalidMetricsAndCriticalRegression()
        {
            Assert.That(default(GpuAsyncAdmissionEvidence).Allows(true, 0), Is.False);
            Assert.That(new GpuAsyncAdmissionEvidence(32, 100, 105, 110, 110, 50, 50, 0, 0, 0, 100).Allows(true, 1), Is.False);
            Assert.That(new GpuAsyncAdmissionEvidence(32, 100, 90, 110, 100, 50, 60, 0, 0, 0, 100).Allows(true, 1), Is.False);
            Assert.That(new GpuAsyncAdmissionEvidence(32, double.NaN, 90, 110, 100, 50, 40, 0, 0, 0, 100).Allows(true, 1), Is.False);
            var good = new GpuAsyncAdmissionEvidence(32, 100, 90, 110, 100, 50, 40, 0, 0, 0, 100);
            Assert.That(good.Allows(true, 99), Is.True); Assert.That(good.Allows(true, 100), Is.False);
            Assert.That(good.Allows(false, 1), Is.False);
        }

        [Test]
        public void ExistingPrerequisitesClockAndStaleTicketsAreValidated()
        {
            var scheduler = new GpuRuntimeScheduler(4, 10000, 1000, 1, Costs());
            scheduler.TryAdmit(new[] { Job(0) }, 1, NoEdges, 0, 10);
            var first = Take(scheduler, 10);
            scheduler.TryComplete(first.Slot, first.Ticket);
            Assert.That(scheduler.TryAdmit(new[] { Job(1) }, 1, new[] { new GpuJobDependency(0, 1) }, 1, 11), Is.EqualTo(GpuAdmissionResult.Accepted));
            Assert.That(scheduler.ReleaseCompleted(), Is.Zero);
            var second = Take(scheduler, 11); scheduler.TryComplete(second.Slot, second.Ticket); scheduler.ReleaseCompleted();
            scheduler.TryAdmit(new[] { Job(0) }, 1, NoEdges, 0, 12);
            Assert.Throws<InvalidOperationException>(() => scheduler.MarkSubmitted(first));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.TryPrepare(11, false, default, out _));
            Assert.That(Take(scheduler, 12).Queue, Is.EqualTo(GpuDeadlineQueue.MainGraphics));
        }

        [Test]
        public void UpdatedCostsReorderOnlyPendingJobsAndSubmittedDispatchIsNotPreempted()
        {
            var costs = Costs(); var scheduler = new GpuRuntimeScheduler(4, 10000, 1000, 1, costs);
            scheduler.TryAdmit(new[] { Job(0, deadline: 300), Job(1, deadline: 320, key: 1) }, 2, NoEdges, 0, 0);
            scheduler.TryPrepare(0, false, default, out var initial);
            Assert.That(initial.Job.JobId, Is.EqualTo(1));
            costs.TryUpdate(costs.BeginSample(0, 0), 1, 400, true);
            costs.TryUpdate(costs.BeginSample(0, 1), 2, 400, true);
            var dispatch = Take(scheduler, 2);
            Assert.That(dispatch.Job.JobId, Is.EqualTo(0));
            Assert.That(scheduler.TryPrepare(10000, false, default, out _), Is.False);
            Assert.That(scheduler.State(dispatch.Slot), Is.EqualTo(GpuRuntimeJobState.Submitted));
            Assert.That(scheduler.TryComplete(dispatch.Slot, dispatch.Ticket), Is.True);
            Assert.That(Take(scheduler, 10000).Job.JobId, Is.EqualTo(1));
        }

        [Test]
        public void RandomDagsMatchIndependentPredecessorOracle()
        {
            var random = new Random(719);
            for (int run = 0; run < 30; run++)
            {
                const int size = 24;
                var jobs = new GpuRuntimeJob[size]; var edges = new GpuJobDependency[size * size]; int edgeCount = 0;
                var required = new bool[size, size]; var seen = new bool[size];
                for (int i = 0; i < size; i++)
                {
                    jobs[i] = Job(i, (GpuDeadlineClass)(i % 3), random.Next(1, 10000));
                    for (int p = 0; p < i; p++) if (random.Next(4) == 0)
                    { required[i, p] = true; edges[edgeCount++] = new GpuJobDependency(p, i); }
                }
                var scheduler = new GpuRuntimeScheduler(size, 100000, 500, size, Costs());
                Assert.That(scheduler.TryAdmit(jobs, size, edges, edgeCount, 0), Is.EqualTo(GpuAdmissionResult.Accepted));
                for (int n = 0; n < size; n++)
                {
                    var selected = Take(scheduler, n * 50); int id = selected.Job.JobId;
                    Assert.That(seen[id], Is.False);
                    for (int p = 0; p < size; p++) if (required[id, p]) Assert.That(seen[p], Is.True);
                    seen[id] = true;
                }
                Assert.That(scheduler.PendingCount, Is.Zero);
            }
        }

        [Test]
        public void EmptyInvalidAndExtremeClockInputsRemainBounded()
        {
            var scheduler = new GpuRuntimeScheduler(4, 10000, long.MaxValue, 1, Costs());
            Assert.That(scheduler.TryAdmit(Array.Empty<GpuRuntimeJob>(), 0, NoEdges, 0, 0), Is.EqualTo(GpuAdmissionResult.Accepted));
            Assert.That(scheduler.TryPrepare(0, false, default, out _), Is.False);
            Assert.That(scheduler.TryAdmit(new GpuRuntimeJob[1], 1, NoEdges, 0, 0), Is.EqualTo(GpuAdmissionResult.InvalidJob));
            Assert.That(scheduler.TryAdmit(new[] { Job(0, key: 99) }, 1, NoEdges, 0, 0), Is.EqualTo(GpuAdmissionResult.InvalidJob));
            Assert.That(scheduler.TryAdmit(new[] { Job(0, deadline: 1), Job(1, deadline: 2) }, 2, NoEdges, 0, 0), Is.EqualTo(GpuAdmissionResult.Accepted));
            Assert.That(Take(scheduler, long.MaxValue - 1).Job.JobId, Is.EqualTo(0));
            Assert.That(scheduler.TryAdmit(new[] { Job(2) }, 1, NoEdges, 0, long.MaxValue), Is.EqualTo(GpuAdmissionResult.InvalidJob));
        }

        [Test]
        public void SteadyStateAdmissionPlanningCompletionAndSamplesAllocateZeroBytes()
        {
            var costs = Costs(); var scheduler = new GpuRuntimeScheduler(4, 10000, 1000, 1, costs);
            var jobs = new[] { Job(0) };
            for (int i = 0; i < 110; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                scheduler.TryAdmit(jobs, 1, NoEdges, 0, i);
                scheduler.TryPrepare(i, false, default, out var dispatch); scheduler.MarkSubmitted(dispatch);
                long sample = scheduler.BeginCostSample(dispatch, i);
                scheduler.TryComplete(dispatch.Slot, dispatch.Ticket); scheduler.ReleaseCompleted();
                costs.TryUpdate(sample, i, 100, true);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (i >= 10) Assert.That(allocated, Is.Zero);
            }
        }
    }
}
