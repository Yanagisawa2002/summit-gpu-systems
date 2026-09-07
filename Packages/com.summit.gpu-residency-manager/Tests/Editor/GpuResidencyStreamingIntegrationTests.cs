using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuResidencyManager.Tests
{
    public sealed class GpuResidencyStreamingIntegrationTests
    {
        private static GpuPointPageValue[] Payload(GpuResidencyFramePlan plan)
        {
            var payload = new GpuPointPageValue[plan.UploadCount * 16];
            for (int i = 0; i < plan.UploadCount; i++)
                for (uint j = 0; j < 16; j++) payload[i * 16 + j] = GpuPointPageGenerator.Generate(plan.Uploads[i].VirtualPage, j, 42);
            return payload;
        }
        private static void Validate(GpuResidencyFramePlan plan, GraphicsBuffer buffer)
        {
            var digests = new GpuPageDigest[plan.RequestedCount]; buffer.GetData(digests);
            for (int i = 0; i < digests.Length; i++)
            {
                var expected = plan.RequestedPhysicalSlots[i] < 0 ? new GpuPageDigest(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue)
                    : GpuPointPageGenerator.Digest((uint)plan.RequestedPages[i], 16, 42);
                Assert.That(digests[i], Is.EqualTo(expected), "Request " + i);
            }
        }
        [Test]
        public void TwoQueuedPlansKeepDistinctGpuSnapshotsAndUnavailablePagesReturnSentinel()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsGraphicsFence || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Graphics compute and fences required.");
            var planner = new GpuPageResidencyPlanner(16, 4, 4, GpuResidencyPolicy.PersistentHeapLru);
            var first = planner.PlanFrame(new[] { 0, 1, 2, 3 }, 0);
            var second = planner.PlanFrame(new[] { 0, 1, 6, 7 }, 1);
            using (var cache = new GpuPointPageCache(16, 4, 16, 4))
            using (var a = new CommandBuffer())
            using (var b = new CommandBuffer())
            using (var saved = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, 4, 16))
            {
                cache.RecordReset(a);
                Assert.Throws<System.InvalidOperationException>(() => cache.RecordFrame(b, second, Payload(second)));
                Assert.Throws<System.ArgumentException>(() => cache.RecordFrame(a, first, new GpuPointPageValue[0]));
                cache.RecordFrame(a, first, Payload(first));
                a.CopyBuffer(cache.PageDigests, saved); cache.RecordConsumerFence(a);
                cache.RecordFrame(b, second, Payload(second));
                // Both command buffers are recorded before either executes.
                Graphics.ExecuteCommandBuffer(a); Graphics.ExecuteCommandBuffer(b);
                Validate(first, saved); Validate(second, cache.PageDigests);
                Assert.That(second.DeferredDemandCount, Is.EqualTo(2));
                first.OwnerComplete(); second.OwnerComplete();
                var third = planner.PlanFrame(new[] { 4, 5, 6, 7 }, 2);
                a.Clear(); cache.RecordFrame(a, third, Payload(third)); Graphics.ExecuteCommandBuffer(a);
                Validate(third, cache.PageDigests); third.OwnerComplete();
                var empty = planner.PlanFrame(System.Array.Empty<int>(), 3);
                a.Clear(); cache.RecordFrame(a, empty, Payload(empty)); Graphics.ExecuteCommandBuffer(a);
                // Synchronous readback also retires the empty frame before disposal.
                cache.PageDigests.GetData(new GpuPageDigest[4]); empty.OwnerComplete();
            }
        }

        [Test]
        public void BudgetedRebuildProducesUniqueDeltasAndCorrectDigests()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsGraphicsFence || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Graphics compute and fences required.");
            var planner = new GpuPageResidencyPlanner(16, 2, 4, GpuResidencyPolicy.RebuildVisibleSet);
            var interest = new[] { new GpuPageRequest(0), new GpuPageRequest(1), new GpuPageRequest(2), new GpuPageRequest(3) };
            using (var cache = new GpuPointPageCache(16, 2, 16, 4))
            using (var commands = new CommandBuffer())
            {
                cache.RecordReset(commands);
                for (int frame = 0; frame < 6; frame++)
                {
                    var plan = planner.PlanFrame(interest, frame, 1);
                    cache.RecordFrame(commands, plan, Payload(plan)); Graphics.ExecuteCommandBuffer(commands);
                    Validate(plan, cache.PageDigests); plan.OwnerComplete(); commands.Clear();
                }
            }
        }
    }
}
