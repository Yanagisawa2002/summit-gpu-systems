using System;
using NUnit.Framework;

namespace Summit.GpuResidencyManager.Tests
{
    public sealed class GpuResidencyStreamingTests
    {
        [TestCase(GpuResidencyPolicy.PersistentLru)]
        [TestCase(GpuResidencyPolicy.PersistentHeapLru)]
        [TestCase(GpuResidencyPolicy.RebuildVisibleSet)]
        public void OversubscribedDemandRespectsBudgetAndEventuallyServesEveryPage(GpuResidencyPolicy policy)
        {
            var planner = new GpuPageResidencyPlanner(16, 3, 8, policy);
            var requests = new GpuPageRequest[8]; var served = new bool[8];
            for (int i = 0; i < 8; i++) requests[i] = new GpuPageRequest(i);
            for (int frame = 0; frame < 32; frame++)
            {
                var plan = planner.PlanFrame(requests, frame, 1);
                Assert.That(plan.UploadCount, Is.LessThanOrEqualTo(1));
                for (int i = 0; i < plan.RequestedCount; i++)
                    if (plan.RequestedPhysicalSlots[i] >= 0) served[plan.RequestedPages[i]] = true;
                plan.OwnerComplete();
            }
            Assert.That(served, Is.All.True);
        }

        [Test]
        public void InvalidSuffixAndDuplicateDoNotCorruptRetryOrAdvanceFrameId()
        {
            var planner = new GpuPageResidencyPlanner(16, 3, 8, GpuResidencyPolicy.PersistentHeapLru);
            Assert.Throws<ArgumentOutOfRangeException>(() => planner.PlanFrame(new[] { 2, 16 }, 0));
            Assert.Throws<ArgumentException>(() => planner.PlanFrame(new[] { 2, 2 }, 0));
            var plan = planner.PlanFrame(new[] { 2, 3 }, 0);
            Assert.That(plan.UploadCount, Is.EqualTo(2));
            plan.OwnerComplete();
            Assert.Throws<ArgumentOutOfRangeException>(() => planner.PlanFrame(Array.Empty<int>(), 0));
            var empty = planner.PlanFrame(Array.Empty<int>(), 1);
            Assert.That(empty.RequestedCount, Is.Zero); empty.OwnerComplete();
        }

        [Test]
        public void ActiveLeasesPreserveSnapshotsAndProtectVictims()
        {
            var planner = new GpuPageResidencyPlanner(16, 2, 8, GpuResidencyPolicy.PersistentHeapLru, 2);
            var input = new[] { 0, 1 }; var first = planner.PlanFrame(input, 0); input[0] = 9;
            var second = planner.PlanFrame(new[] { 2, 3 }, 1);
            Assert.That(first.RequestedPages[0], Is.Zero);
            Assert.That(second.UploadCount, Is.Zero);
            Assert.That(second.DeferredDemandCount, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => planner.PlanFrame(new[] { 2 }, 2));
            first.OwnerComplete(); second.OwnerComplete();
            var retry = planner.PlanFrame(new[] { 2, 3 }, 2);
            Assert.That(retry.UploadCount, Is.EqualTo(2)); retry.OwnerComplete();
        }

        [Test]
        public void UploadDispatchBoundsRejectOverflowAndOneGroupPastDx12Limit()
        {
            Assert.That(GpuPointPageCache.UploadDispatchGroupCount(0, 512), Is.Zero);
            Assert.That(GpuPointPageCache.UploadDispatchGroupCount(65535, 256), Is.EqualTo(65535));
            Assert.That(GpuPointPageCache.UploadDispatchGroupCount(32767, 512), Is.EqualTo(65534));
            Assert.Throws<ArgumentOutOfRangeException>(() => GpuPointPageCache.UploadDispatchGroupCount(32768, 512));
            Assert.Throws<ArgumentOutOfRangeException>(() => GpuPointPageCache.UploadDispatchGroupCount(65536, 256));
            Assert.Throws<ArgumentOutOfRangeException>(() => GpuPointPageCache.UploadDispatchGroupCount(int.MaxValue, 16384));
        }

        [Test]
        public void SteadyStatePlansAllocateNoManagedMemory()
        {
            var planner = new GpuPageResidencyPlanner(16, 4, 4, GpuResidencyPolicy.PersistentHeapLru, 1);
            var requests = new[] { 0, 1, 2, 3 };
            for (int i = 0; i < 8; i++) planner.PlanFrame(requests, i).OwnerComplete();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 8; i < 108; i++) planner.PlanFrame(requests, i).OwnerComplete();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.Zero);
        }
    }
}
