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
        [Test]
        public void RestorePreviousMinusCurrentWritesOnlySetDifference()
        {
            const int count = 16;
            var immutableBase = new NativeArray<
                Summit.GpuDrivenInstances.GpuInstanceState>(
                count,
                Allocator.Temp);
            var staging = new NativeArray<
                Summit.GpuDrivenInstances.GpuInstanceState>(
                count,
                Allocator.Temp);
            var previous = new NativeArray<
                Summit.GpuDrivenInstances.GpuInstanceDirtyRange>(
                2,
                Allocator.Temp);
            var current = new NativeArray<
                Summit.GpuDrivenInstances.GpuInstanceDirtyRange>(
                1,
                Allocator.Temp);
            try
            {
                for (int index = 0; index < count; index++)
                {
                    var state = new Summit.GpuDrivenInstances
                        .GpuInstanceState(
                            new Vector3(index, 0f, 0f),
                            1f,
                            new Vector4(100f, 0f, 0f, 0f),
                            checked((uint)index),
                            0u,
                            1u,
                            1u);
                    immutableBase[index] = state;
                    staging[index] = state;
                }
                previous[0] = new Summit.GpuDrivenInstances
                    .GpuInstanceDirtyRange(2, 3);
                previous[1] = new Summit.GpuDrivenInstances
                    .GpuInstanceDirtyRange(8, 4);
                current[0] = new Summit.GpuDrivenInstances
                    .GpuInstanceDirtyRange(4, 5);

                foreach (int index in new[] { 2, 3, 4, 8, 9, 10, 11 })
                {
                    var changed = staging[index];
                    changed.ApplicationId += 1000u;
                    staging[index] = changed;
                }

                int restored = GpuDrivenInstanceUploadBenchmarkAdapter
                    .RestorePreviousMinusCurrent(
                        immutableBase,
                        staging,
                        previous,
                        previous.Length,
                        current,
                        current.Length);

                Assert.That(restored, Is.EqualTo(5));
                foreach (int index in new[] { 2, 3, 9, 10, 11 })
                {
                    Assert.That(
                        staging[index].ApplicationId,
                        Is.EqualTo(immutableBase[index].ApplicationId));
                }
                foreach (int index in new[] { 4, 8 })
                {
                    Assert.That(
                        staging[index].ApplicationId,
                        Is.EqualTo(
                            immutableBase[index].ApplicationId + 1000u));
                }
            }
            finally
            {
                current.Dispose();
                previous.Dispose();
                staging.Dispose();
                immutableBase.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReusedDirtySlotSkipsSameTopologyRestore()
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
            var adapter = new GpuDrivenInstanceUploadBenchmarkAdapter(
                instanceCount,
                1,
                "visible25",
                seed,
                drawGroupCount: 1,
                stagingSlotCount: 1);
            try
            {
                for (uint logicalOrdinal = 1u;
                     logicalOrdinal <= 2u;
                     logicalOrdinal++)
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
                            GpuDrivenInstanceUploadBenchmarkVariant
                                .DirtyRangeUpload,
                            movingPercent,
                            seed,
                            logicalOrdinal);
                    Assert.That(
                        preparation.StateRecordsWritten,
                        Is.EqualTo(preparation.Plan.ChangedInstanceCount));
                    ulong expectedStateHash =
                        adapter.ComputeSlotStateHash(slotIndex);

                    adapter.RecordWorkload(
                        slotIndex,
                        GpuDrivenInstanceUploadBenchmarkVariant
                            .DirtyRangeUpload);
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
                        Assert.That(actualHash, Is.EqualTo(expectedStateHash));
                    }
                }
            }
            finally
            {
                if (adapter.AllCompletionFencesPassed)
                {
                    adapter.Dispose();
                }
            }
        }

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
