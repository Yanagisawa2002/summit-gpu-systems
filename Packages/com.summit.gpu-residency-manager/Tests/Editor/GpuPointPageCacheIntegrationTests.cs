using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuResidencyManager.Tests
{
    public sealed class GpuPointPageCacheIntegrationTests
    {
        [Test]
        public void UploadedPagesProduceCpuOracleDigests()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("A graphics-capable compute device is required.");
            }
            const int points = 64;
            const uint seed = 123u;
            var planner = new GpuPageResidencyPlanner(
                16, 4, 4, GpuResidencyPolicy.PersistentLru);
            GpuResidencyFramePlan plan = planner.PlanFrame(
                new[] { 2, 4, 6, 8 }, 0);
            var payload = new GpuPointPageValue[4 * points];
            for (int upload = 0; upload < plan.UploadCount; upload++)
            {
                uint page = plan.Uploads[upload].VirtualPage;
                for (uint point = 0; point < points; point++)
                {
                    payload[upload * points + point] =
                        GpuPointPageGenerator.Generate(page, point, seed);
                }
            }
            using (var cache = new GpuPointPageCache(16, 4, points, 4))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/GpuResidency"
                   })
            {
                cache.RecordReset(commands);
                cache.RecordFrame(commands, plan, payload);
                Graphics.ExecuteCommandBuffer(commands);
                var actual = new GpuPageDigest[4];
                cache.PageDigests.GetData(actual);
                for (int index = 0; index < actual.Length; index++)
                {
                    GpuPageDigest expected = GpuPointPageGenerator.Digest(
                        checked((uint)plan.RequestedPages[index]),
                        points,
                        seed);
                    Assert.That(actual[index].X, Is.EqualTo(expected.X));
                    Assert.That(actual[index].Y, Is.EqualTo(expected.Y));
                    Assert.That(actual[index].Z, Is.EqualTo(expected.Z));
                    Assert.That(actual[index].W, Is.EqualTo(expected.W));
                }
            }
        }
    }
}
