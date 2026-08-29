using System;
using NUnit.Framework;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using UnityEngine;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceMacrobenchmarkCpuBackendTests
    {
        [Test]
        public void ZeroVisibleMatchesOracleAndProducesNoDraws()
        {
            GpuInstanceState[] instances = CreateInstances(
                37,
                1,
                1u,
                outsideFrustum: true);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 10f);
            Vector4[] views = CreateViews(1);
            GpuDrawTemplate[] draws = CreateDraws(1);

            AssertBackendMatchesOracle(
                instances,
                planes,
                views,
                draws,
                backend =>
                {
                    Assert.That(backend.VisiblePairCount, Is.Zero);
                    Assert.That(backend.DrawCallCount, Is.Zero);
                    Assert.That(backend.GetBatchCount(0), Is.Zero);
                    Assert.That(backend.GroupedCapacity, Is.Zero);
                });
        }

        [Test]
        public void AllVisiblePreservesStableOrderAcrossChunks()
        {
            const int instanceCount = 2500;
            GpuInstanceState[] instances = CreateInstances(
                instanceCount,
                1,
                1u,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 100f);
            Vector4[] views = CreateViews(1);
            GpuDrawTemplate[] draws = CreateDraws(1);

            AssertBackendMatchesOracle(
                instances,
                planes,
                views,
                draws,
                backend =>
                {
                    Assert.That(
                        backend.VisiblePairCount,
                        Is.EqualTo(instanceCount));
                    Assert.That(backend.DrawCallCount, Is.EqualTo(3));
                    for (int index = 0; index < instanceCount; index++)
                    {
                        Assert.That(
                            backend.GroupedIndices[index],
                            Is.EqualTo((uint)index));
                    }
                });
        }

        [TestCase(1, 1)]
        [TestCase(1, 8)]
        [TestCase(4, 1)]
        [TestCase(4, 8)]
        public void ViewAndGroupConfigurationsMatchOracle(
            int viewCount,
            int drawGroupCount)
        {
            const int instanceCount = 4097;
            uint allViews = (1u << viewCount) - 1u;
            var instances = new GpuInstanceState[instanceCount];
            for (int index = 0; index < instanceCount; index++)
            {
                uint mask = index % 5 == 0
                    ? 1u << (index % viewCount)
                    : allViews;
                uint group = checked((uint)(index % drawGroupCount));
                instances[index] = new GpuInstanceState(
                    new Vector3(
                        index % 17 - 8,
                        index % 13 - 6,
                        index % 11 - 5),
                    0.5f,
                    new Vector4(4096f, 0f, 0f, 0f),
                    checked((uint)index),
                    group,
                    1u,
                    mask);
            }

            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(
                    viewCount,
                    100f);
            Vector4[] views = CreateViews(viewCount);
            GpuDrawTemplate[] draws = CreateDraws(drawGroupCount);
            AssertBackendMatchesOracle(
                instances,
                planes,
                views,
                draws,
                null);
        }

        [Test]
        public void LodSelectionPopulatesAllEightGroupsExactly()
        {
            const int drawGroupCount = 8;
            var instances = new GpuInstanceState[10];
            for (int group = 0; group < drawGroupCount; group++)
            {
                int lod = group & 3;
                uint drawGroupBase = checked((uint)(group & ~3));
                instances[group] = new GpuInstanceState(
                    new Vector3(lod * 2f + 1f, 0f, 0f),
                    0.5f,
                    new Vector4(2f, 4f, 6f, 8f),
                    checked((uint)group),
                    drawGroupBase,
                    4u,
                    1u);
            }
            instances[8] = new GpuInstanceState(
                Vector3.zero,
                0.5f,
                new Vector4(2f, 4f, 6f, 8f),
                8u,
                0u,
                4u,
                0u);
            instances[9] = new GpuInstanceState(
                new Vector3(9f, 0f, 0f),
                0.5f,
                new Vector4(2f, 4f, 6f, 8f),
                9u,
                0u,
                4u,
                1u);

            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 100f);
            Vector4[] views = CreateViews(1);
            GpuDrawTemplate[] draws = CreateDraws(drawGroupCount);
            AssertBackendMatchesOracle(
                instances,
                planes,
                views,
                draws,
                backend =>
                {
                    Assert.That(backend.VisiblePairCount, Is.EqualTo(8));
                    Assert.That(backend.DrawCallCount, Is.EqualTo(8));
                    for (int bin = 0; bin < drawGroupCount; bin++)
                    {
                        Assert.That(backend.Counts[bin], Is.EqualTo(1));
                        Assert.That(
                            backend.GroupedIndices[backend.BinOffsets[bin]],
                            Is.EqualTo((uint)bin));
                    }
                });
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(1022, 1)]
        [TestCase(1023, 1)]
        [TestCase(1024, 2)]
        [TestCase(2046, 2)]
        [TestCase(2047, 3)]
        public void BatchHelpersRespectThe1023Boundary(
            int instanceCount,
            int expectedBatches)
        {
            GpuInstanceState[] instances = CreateInstances(
                instanceCount,
                1,
                1u,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 100f);
            Vector4[] views = CreateViews(1);
            GpuDrawTemplate[] draws = CreateDraws(1);

            AssertBackendMatchesOracle(
                instances,
                planes,
                views,
                draws,
                backend =>
                {
                    Assert.That(
                        backend.GetBatchCount(0),
                        Is.EqualTo(expectedBatches));
                    Assert.That(
                        backend.DrawCallCount,
                        Is.EqualTo(expectedBatches));
                    int covered = 0;
                    for (int batch = 0; batch < expectedBatches; batch++)
                    {
                        Assert.That(
                            backend.GetBatchStartInstance(0, batch),
                            Is.EqualTo(covered));
                        int count =
                            backend.GetBatchInstanceCount(0, batch);
                        Assert.That(
                            count,
                            Is.InRange(
                                1,
                                GpuDrivenInstanceMacrobenchmarkCpuBackend
                                    .MaxInstancesPerDraw));
                        covered += count;
                    }
                    Assert.That(covered, Is.EqualTo(instanceCount));
                    Assert.That(
                        backend.GroupedMatrices.Length,
                        Is.EqualTo(instanceCount));
                });
        }

        [Test]
        public void FrozenCapacityUnderestimateFailsClosed()
        {
            GpuInstanceState[] instances = CreateInstances(
                2,
                1,
                1u,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 100f);
            Vector4[] views = CreateViews(1);

            using (var backend =
                new GpuDrivenInstanceMacrobenchmarkCpuBackend(
                    instances,
                    planes,
                    views,
                    1,
                    new uint[] { 1u }))
            {
                InvalidOperationException exception = Assert.Throws<
                    InvalidOperationException>(() => backend.CullAndPack());
                StringAssert.Contains("frozen capacity", exception.Message);
            }
        }

        [Test]
        public void CullAndPackHasNoSteadyStateManagedAllocation()
        {
            GpuInstanceState[] instances = CreateInstances(
                2048,
                8,
                0xFu,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(4, 100f);
            Vector4[] views = CreateViews(4);
            GpuDrawTemplate[] draws = CreateDraws(8);
            GpuDrivenInstanceExpectedResult expected =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.VisibleOnly);

            using (var backend =
                new GpuDrivenInstanceMacrobenchmarkCpuBackend(
                    instances,
                    planes,
                    views,
                    draws.Length,
                    expected.Counts))
            {
                backend.CullAndPack();
                backend.CullAndPack();

                long before = GC.GetAllocatedBytesForCurrentThread();
                int observed = 0;
                for (int iteration = 0; iteration < 32; iteration++)
                {
                    backend.CullAndPack();
                    observed += backend.VisiblePairCount;
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(observed, Is.GreaterThan(0));
                Assert.That(allocated, Is.Zero);
            }
        }

        [Test]
        public void DisposeIsIdempotentAndRejectsFurtherUse()
        {
            GpuInstanceState[] instances = CreateInstances(
                1,
                1,
                1u,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 100f);
            Vector4[] views = CreateViews(1);
            var backend = new GpuDrivenInstanceMacrobenchmarkCpuBackend(
                instances,
                planes,
                views,
                1,
                new uint[] { 1u });
            backend.CullAndPack();

            backend.Dispose();
            Assert.DoesNotThrow(() => backend.Dispose());
            Assert.Throws<ObjectDisposedException>(
                () => backend.CullAndPack());
            Assert.Throws<ObjectDisposedException>(() =>
            {
                int ignored = backend.GroupedCapacity;
                GC.KeepAlive(ignored);
            });
            Assert.Throws<ObjectDisposedException>(() =>
            {
                int ignored = backend.Counts.Length;
                GC.KeepAlive(ignored);
            });
        }

        [Test]
        public void SingleViewResidentPayloadCountsEveryPersistentArray()
        {
            const int instanceCount = 10;
            GpuInstanceState[] instances = CreateInstances(
                instanceCount,
                1,
                1u,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(1, 100f);
            Vector4[] views = CreateViews(1);

            using (var backend =
                new GpuDrivenInstanceMacrobenchmarkCpuBackend(
                    instances,
                    planes,
                    views,
                    1,
                    new uint[] { 6u }))
            {
                long expectedNativePayload =
                    instanceCount * (long)GpuInstanceState.Stride +
                    6L * sizeof(float) * 4L +
                    sizeof(float) * 4L +
                    instanceCount * sizeof(float) * 16L +
                    instanceCount * sizeof(int) +
                    sizeof(int) +
                    sizeof(int) +
                    sizeof(int) +
                    2L * sizeof(int) +
                    6L * sizeof(uint) +
                    6L * sizeof(float) * 16L;
                Assert.That(expectedNativePayload, Is.EqualTo(1700L));
                Assert.That(
                    backend.PersistentNativeArrayPayloadBytes,
                    Is.EqualTo(expectedNativePayload));

                var batches = new[] { new Matrix4x4[6] };
                long managedPayload =
                    GpuDrivenInstanceMacrobenchmarkAdapter
                        .EstimateManagedBatchMatrixPayloadBytes(batches);
                long expectedManagedPayload =
                    IntPtr.Size + 6L * sizeof(float) * 16L;
                Assert.That(
                    managedPayload,
                    Is.EqualTo(expectedManagedPayload));
                Assert.That(
                    GpuDrivenInstanceMacrobenchmarkAdapter
                        .EstimateCpuResidentPayloadBytes(
                            backend.PersistentNativeArrayPayloadBytes,
                            managedPayload),
                    Is.EqualTo(
                        expectedNativePayload + expectedManagedPayload));
            }
        }

        [Test]
        public void FourViewEightGroupResidentPayloadCountsEveryArray()
        {
            const int instanceCount = 2048;
            const int viewCount = 4;
            const int drawGroupCount = 8;
            const int visibleBinCount = viewCount * drawGroupCount;
            const int pairsPerBin = instanceCount / drawGroupCount;
            const int pairCount = instanceCount * viewCount;
            const int chunkCount = 8;
            GpuInstanceState[] instances = CreateInstances(
                instanceCount,
                drawGroupCount,
                0xFu,
                outsideFrustum: false);
            Vector4[] planes =
                GpuDrivenInstanceBenchmarkCpuOracle.CreateBoxPlanes(
                    viewCount,
                    100f);
            Vector4[] views = CreateViews(viewCount);
            var maximumCounts = new uint[visibleBinCount];
            var batches = new Matrix4x4[visibleBinCount][];
            for (int bin = 0; bin < visibleBinCount; bin++)
            {
                maximumCounts[bin] = checked((uint)pairsPerBin);
                batches[bin] = new Matrix4x4[pairsPerBin];
            }

            using (var backend =
                new GpuDrivenInstanceMacrobenchmarkCpuBackend(
                    instances,
                    planes,
                    views,
                    drawGroupCount,
                    maximumCounts))
            {
                long expectedNativePayload =
                    instanceCount * (long)GpuInstanceState.Stride +
                    viewCount * 6L * sizeof(float) * 4L +
                    viewCount * sizeof(float) * 4L +
                    instanceCount * sizeof(float) * 16L +
                    pairCount * sizeof(int) +
                    chunkCount * visibleBinCount * (long)sizeof(int) +
                    visibleBinCount * (long)sizeof(int) +
                    visibleBinCount * (long)sizeof(int) +
                    (visibleBinCount + 1L) * sizeof(int) +
                    pairCount * (long)sizeof(uint) +
                    pairCount * sizeof(float) * 16L;
                Assert.That(expectedNativePayload, Is.EqualTo(821060L));
                Assert.That(
                    backend.PersistentNativeArrayPayloadBytes,
                    Is.EqualTo(expectedNativePayload));

                long managedPayload =
                    GpuDrivenInstanceMacrobenchmarkAdapter
                        .EstimateManagedBatchMatrixPayloadBytes(batches);
                long expectedManagedPayload =
                    visibleBinCount * (long)IntPtr.Size +
                    pairCount * sizeof(float) * 16L;
                Assert.That(
                    managedPayload,
                    Is.EqualTo(expectedManagedPayload));
                Assert.That(
                    GpuDrivenInstanceMacrobenchmarkAdapter
                        .EstimateCpuResidentPayloadBytes(
                            backend.PersistentNativeArrayPayloadBytes,
                            managedPayload),
                    Is.EqualTo(
                        expectedNativePayload + expectedManagedPayload));
            }
        }

        private static void AssertBackendMatchesOracle(
            GpuInstanceState[] instances,
            Vector4[] planes,
            Vector4[] views,
            GpuDrawTemplate[] draws,
            Action<GpuDrivenInstanceMacrobenchmarkCpuBackend> assertions)
        {
            GpuDrivenInstanceExpectedResult expected =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            using (var backend =
                new GpuDrivenInstanceMacrobenchmarkCpuBackend(
                    instances,
                    planes,
                    views,
                    draws.Length,
                    expected.Counts))
            {
                backend.CullAndPack();
                Assert.That(
                    backend.Validate(expected, out string message),
                    Is.True,
                    message);
                Assert.That(
                    backend.VisiblePairCount,
                    Is.EqualTo(expected.GroupedInstanceIndices.Length));
                for (int bin = 0; bin < expected.Counts.Length; bin++)
                {
                    Assert.That(
                        backend.Counts[bin],
                        Is.EqualTo(checked((int)expected.Counts[bin])));
                    Assert.That(
                        backend.BinOffsets[bin],
                        Is.EqualTo(checked((int)expected.Offsets[bin])));
                }
                Assert.That(
                    backend.BinOffsets[expected.Counts.Length],
                    Is.EqualTo(expected.GroupedInstanceIndices.Length));
                for (int index = 0;
                     index < expected.GroupedInstanceIndices.Length;
                     index++)
                {
                    Assert.That(
                        backend.GroupedIndices[index],
                        Is.EqualTo(expected.GroupedInstanceIndices[index]));
                    int instanceIndex = checked((int)backend.GroupedIndices[index]);
                    Matrix4x4 matrix = backend.GroupedMatrices[index];
                    Assert.That(
                        matrix.m03,
                        Is.EqualTo(
                            instances[instanceIndex].PositionRadius.x)
                            .Within(1.0e-6f));
                    Assert.That(
                        matrix.m13,
                        Is.EqualTo(
                            instances[instanceIndex].PositionRadius.y)
                            .Within(1.0e-6f));
                    Assert.That(
                        matrix.m23,
                        Is.EqualTo(
                            instances[instanceIndex].PositionRadius.z)
                            .Within(1.0e-6f));
                }
                assertions?.Invoke(backend);
            }
        }

        private static GpuInstanceState[] CreateInstances(
            int count,
            int drawGroupCount,
            uint viewMask,
            bool outsideFrustum)
        {
            var instances = new GpuInstanceState[count];
            for (int index = 0; index < count; index++)
            {
                float x = outsideFrustum
                    ? 1000f + index
                    : index % 17 - 8;
                instances[index] = new GpuInstanceState(
                    new Vector3(x, index % 13 - 6, index % 11 - 5),
                    0.5f,
                    new Vector4(4096f, 0f, 0f, 0f),
                    checked((uint)index),
                    checked((uint)(index % drawGroupCount)),
                    1u,
                    viewMask);
            }
            return instances;
        }

        private static Vector4[] CreateViews(int viewCount)
        {
            var result = new Vector4[viewCount];
            for (int view = 0; view < viewCount; view++)
            {
                result[view] = new Vector4(view * 2f, 0f, 0f, 1f);
            }
            return result;
        }

        private static GpuDrawTemplate[] CreateDraws(int count)
        {
            var draws = new GpuDrawTemplate[count];
            for (int group = 0; group < count; group++)
            {
                draws[group] = new GpuDrawTemplate(3u, 0u, 0u);
            }
            return draws;
        }
    }
}
