using NUnit.Framework;

namespace Summit.GpuResidencyManager.Tests
{
    public sealed class GpuPageResidencyPlannerTests
    {
        [Test]
        public void PersistentPlannerReusesResidentPagesAndUploadsOnlyMisses()
        {
            var planner = new GpuPageResidencyPlanner(
                16, 6, 4, GpuResidencyPolicy.PersistentLru);
            GpuResidencyFramePlan first = planner.PlanFrame(
                new[] { 0, 1, 2, 3 }, 0);
            Assert.That(first.UploadCount, Is.EqualTo(4));
            first.OwnerComplete();
            GpuResidencyFramePlan second = planner.PlanFrame(
                new[] { 1, 2, 3, 4 }, 1);

            Assert.That(second.HitCount, Is.EqualTo(3));
            Assert.That(second.UploadCount, Is.EqualTo(1));
            Assert.That(planner.PhysicalSlotForVirtualPage(4), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void RebuildPlannerUploadsEveryRequestedPage()
        {
            var planner = new GpuPageResidencyPlanner(
                16, 4, 4, GpuResidencyPolicy.RebuildVisibleSet);
            var first = planner.PlanFrame(new[] { 0, 1, 2, 3 }, 0);
            first.OwnerComplete();
            GpuResidencyFramePlan second = planner.PlanFrame(
                new[] { 1, 2, 3, 4 }, 1);

            Assert.That(second.HitCount, Is.Zero);
            Assert.That(second.UploadCount, Is.EqualTo(4));
        }

        [Test]
        public void PersistentPlannerNeverEvictsCurrentRequest()
        {
            var planner = new GpuPageResidencyPlanner(
                32, 4, 4, GpuResidencyPolicy.PersistentLru);
            var first = planner.PlanFrame(new[] { 0, 1, 2, 3 }, 0);
            first.OwnerComplete();
            GpuResidencyFramePlan second = planner.PlanFrame(
                new[] { 1, 2, 3, 4 }, 1);

            Assert.That(second.EvictionCount, Is.EqualTo(1));
            Assert.That(planner.PhysicalSlotForVirtualPage(1), Is.GreaterThanOrEqualTo(0));
            Assert.That(planner.PhysicalSlotForVirtualPage(2), Is.GreaterThanOrEqualTo(0));
            Assert.That(planner.PhysicalSlotForVirtualPage(3), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void DuplicateRequestsAreRejected()
        {
            var planner = new GpuPageResidencyPlanner(
                16, 4, 4, GpuResidencyPolicy.PersistentLru);
            Assert.That(
                () => planner.PlanFrame(new[] { 1, 1 }, 0),
                Throws.ArgumentException);
        }
    }
}
