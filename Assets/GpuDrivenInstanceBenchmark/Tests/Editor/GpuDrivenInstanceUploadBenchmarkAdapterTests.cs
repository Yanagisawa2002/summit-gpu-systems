using System.Collections;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceUploadBenchmarkAdapterTests
    {
        [UnityTest]
        public IEnumerator FullAndDirtyWorkloadsProduceExactStateParity()
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.supportsInstancing ||
                !SystemInfo.supportsIndirectArgumentsBuffer ||
                !SystemInfo.supportsGraphicsFence ||
                !SystemInfo.supportsAsyncCompute)
            {
                Assert.Ignore(
                    "Compute, instancing, indirect arguments, graphics " +
                    "fences, and CPU-queryable async-compute fences are " +
                    "required.");
            }

            const int instanceCount = 1024;
            const int movingPercent = 10;
            const int seed = 20260830;
            const uint logicalOrdinal = 17u;
            var adapter = new GpuDrivenInstanceUploadBenchmarkAdapter(
                instanceCount,
                1,
                "visible25",
                seed,
                drawGroupCount: 1,
                stagingSlotCount: 2);
            try
            {
                ulong fullHash = 0UL;
                ulong dirtyHash = 0UL;
                GpuDrivenInstanceUploadBenchmarkVariant[] variants =
                {
                    GpuDrivenInstanceUploadBenchmarkVariant.FullUpload,
                    GpuDrivenInstanceUploadBenchmarkVariant.DirtyRangeUpload,
                };

                foreach (GpuDrivenInstanceUploadBenchmarkVariant variant in
                         variants)
                {
                    int slotIndex;
                    int waitFrames;
                    while (!adapter.TryAcquireWorkSlot(
                               out slotIndex,
                               out waitFrames))
                    {
                        yield return null;
                    }
                    Assert.That(waitFrames, Is.Zero);

                    GpuDrivenInstanceUploadPreparationReceipt preparation =
                        adapter.PrepareSlot(
                            slotIndex,
                            variant,
                            movingPercent,
                            seed,
                            logicalOrdinal);
                    ulong expectedStateHash =
                        adapter.ComputeSlotStateHash(slotIndex);
                    GpuDrivenInstanceUploadRecordReceipt recorded =
                        adapter.RecordWorkload(slotIndex, variant);
                    GraphicsFence fence =
                        adapter.AppendLifetimeFence(slotIndex);
                    Graphics.ExecuteCommandBuffer(adapter.Commands(slotIndex));
                    adapter.MarkWorkSlotSubmitted(slotIndex, fence);

                    while (!adapter.AllCompletionFencesPassed)
                    {
                        yield return null;
                    }

                    var managedReadback = new Summit.GpuDrivenInstances
                        .GpuInstanceState[instanceCount];
                    adapter.InstanceStateBuffer.GetData(managedReadback);
                    using (var readback = new NativeArray<
                               Summit.GpuDrivenInstances.GpuInstanceState>(
                               managedReadback,
                               Allocator.Temp))
                    {
                        Assert.That(
                            GpuDrivenInstanceUploadBenchmarkAdapter
                                .ValidateStateHash(
                                    readback,
                                    instanceCount,
                                    expectedStateHash,
                                    out ulong actualHash),
                            Is.True);
                        if (variant ==
                            GpuDrivenInstanceUploadBenchmarkVariant.FullUpload)
                        {
                            fullHash = actualHash;
                            Assert.That(recorded.Upload.UploadCallCount,
                                Is.EqualTo(1));
                            Assert.That(recorded.Upload.UploadedRecordCount,
                                Is.EqualTo(instanceCount));
                        }
                        else
                        {
                            dirtyHash = actualHash;
                            Assert.That(recorded.Upload.UploadCallCount,
                                Is.InRange(1, 16));
                            Assert.That(recorded.Upload.DirtyRecordCount,
                                Is.EqualTo(
                                    preparation.Plan.ChangedInstanceCount));
                            Assert.That(recorded.Upload.UploadedRecordCount,
                                Is.EqualTo(
                                    preparation.Plan.ChangedInstanceCount));
                        }
                    }
                }

                Assert.That(dirtyHash, Is.EqualTo(fullHash));
            }
            finally
            {
                if (adapter.AllCompletionFencesPassed)
                {
                    adapter.Dispose();
                }
            }
        }
    }
}
