using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDeadlineScheduler.Tests
{
    public sealed class GpuDeadlineWorkloadIntegrationTests
    {
        [Test]
        public void RecordedJobMatchesPortableCpuDigest()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("A graphics-capable compute device is required.");
            }

            var job = new GpuDeadlineJob(
                7,
                0,
                GpuDeadlineClass.Critical,
                4000,
                1000,
                257,
                11,
                0x12345678u);
            const uint logicalState = 19u;
            using (var workload = new GpuDeadlineWorkload(2, 512, 512))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/GpuDeadlineWorkload"
                   })
            {
                workload.RecordCopyStage(commands);
                workload.RecordJob(commands, job, 1, logicalState);
                Graphics.ExecuteCommandBuffer(commands);

                var digests = new uint[4];
                workload.GetJobDigestBuffer(1).GetData(digests);
                uint[] expected = GpuDeadlineWorkload.ExpectedDigest(
                    job,
                    logicalState);
                Assert.That(
                    new[]
                    {
                        digests[0],
                        digests[1],
                        digests[2],
                        digests[3]
                    },
                    Is.EqualTo(expected));
            }
        }
    }
}
