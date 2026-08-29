using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Summit.GpuDrivenInstances.Tests
{
    public sealed class GpuInstanceClusterBuilderTests
    {
        [Test]
        public void ClusterLayoutMatchesGpuContract()
        {
            Assert.That(
                Marshal.SizeOf<GpuInstanceCluster>(),
                Is.EqualTo(GpuInstanceCluster.Stride));
            Assert.That(
                Marshal.OffsetOf<GpuInstanceCluster>(
                    nameof(GpuInstanceCluster.PositionRadius)).ToInt32(),
                Is.Zero);
            Assert.That(
                Marshal.OffsetOf<GpuInstanceCluster>(
                    nameof(GpuInstanceCluster.FirstInstance)).ToInt32(),
                Is.EqualTo(16));
            Assert.That(
                Marshal.OffsetOf<GpuInstanceCluster>(
                    nameof(GpuInstanceCluster.InstanceCount)).ToInt32(),
                Is.EqualTo(20));
            Assert.That(
                Marshal.OffsetOf<GpuInstanceCluster>(
                    nameof(GpuInstanceCluster.UnionViewMask)).ToInt32(),
                Is.EqualTo(24));
            Assert.That(
                Marshal.OffsetOf<GpuInstanceCluster>(
                    nameof(GpuInstanceCluster.Reserved)).ToInt32(),
                Is.EqualTo(28));
            Assert.That(
                GpuInstanceCluster.MaximumInstanceCount,
                Is.EqualTo(64));
        }

        [Test]
        public void EmptyInputAllowsDefaultNativeArrays()
        {
            Assert.That(
                GpuInstanceClusterBuilder.GetRequiredClusterCount(0, 64),
                Is.Zero);
            Assert.That(
                GpuInstanceClusterBuilder.BuildContiguous(
                    default,
                    0,
                    64,
                    default),
                Is.Zero);
        }

        [TestCase(1, 1)]
        [TestCase(63, 1)]
        [TestCase(64, 1)]
        [TestCase(65, 2)]
        public void BoundaryCountsProduceContiguousMembership(
            int instanceCount,
            int expectedClusterCount)
        {
            using (var instances = LinearStates(instanceCount))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                expectedClusterCount,
                Allocator.Temp))
            {
                int written = GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    instanceCount,
                    64,
                    clusters);

                Assert.That(written, Is.EqualTo(expectedClusterCount));
                for (int clusterIndex = 0;
                    clusterIndex < written;
                    clusterIndex++)
                {
                    int expectedFirst = clusterIndex * 64;
                    int expectedCount = Math.Min(
                        64,
                        instanceCount - expectedFirst);
                    Assert.That(
                        clusters[clusterIndex].FirstInstance,
                        Is.EqualTo((uint)expectedFirst));
                    Assert.That(
                        clusters[clusterIndex].InstanceCount,
                        Is.EqualTo((uint)expectedCount));
                    Assert.That(
                        clusters[clusterIndex].Reserved,
                        Is.Zero);
                }
            }
        }

        [Test]
        public void SingleSphereRetainsCenterAndInflatesRadius()
        {
            using (var instances = States(
                State(new Vector3(3f, -2f, 5f), 2f, 0x20u)))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                1,
                Allocator.Temp))
            {
                GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    1,
                    64,
                    clusters);

                Vector4 bound = clusters[0].PositionRadius;
                Assert.That(
                    new Vector3(bound.x, bound.y, bound.z),
                    Is.EqualTo(new Vector3(3f, -2f, 5f)));
                Assert.That(bound.w, Is.GreaterThan(2f));
                Assert.That(bound.w, Is.LessThan(2.001f));
                Assert.That(clusters[0].UnionViewMask,
                    Is.EqualTo(0x20u));
            }
        }

        [Test]
        public void ClusterOrsEveryMemberViewMask()
        {
            using (var instances = States(
                State(Vector3.zero, 1f, 0x00000001u),
                State(Vector3.right, 1f, 0x00000004u),
                State(Vector3.up, 1f, 0x80000000u)))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                1,
                Allocator.Temp))
            {
                GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    instances.Length,
                    64,
                    clusters);

                Assert.That(
                    clusters[0].UnionViewMask,
                    Is.EqualTo(0x80000005u));
            }
        }

        [Test]
        public void ContainedSphereDoesNotShrinkContainingBound()
        {
            using (var instances = States(
                State(Vector3.zero, 10f, 1u),
                State(new Vector3(3f, 0f, 0f), 1f, 2u)))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                1,
                Allocator.Temp))
            {
                GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    instances.Length,
                    64,
                    clusters);

                Vector4 bound = clusters[0].PositionRadius;
                Assert.That(
                    new Vector3(bound.x, bound.y, bound.z),
                    Is.EqualTo(Vector3.zero));
                Assert.That(bound.w, Is.GreaterThan(10f));
                Assert.That(bound.w, Is.LessThan(10.001f));
            }
        }

        [Test]
        public void AabbCenterAndRadiusConservativelyContainEverySphere()
        {
            using (var instances = States(
                State(new Vector3(-3f, 1f, 2f), 2f, 1u),
                State(new Vector3(4f, -2f, 0f), 1f, 2u),
                State(new Vector3(0f, 5f, -4f), 0.5f, 4u)))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                1,
                Allocator.Temp))
            {
                GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    instances.Length,
                    64,
                    clusters);

                Vector4 bound = clusters[0].PositionRadius;
                var center = new Vector3(bound.x, bound.y, bound.z);
                Assert.That(
                    center,
                    Is.EqualTo(new Vector3(0f, 1.25f, -0.25f)));
                for (int index = 0; index < instances.Length; index++)
                {
                    Vector4 source = instances[index].PositionRadius;
                    var position = new Vector3(source.x, source.y, source.z);
                    Assert.That(
                        Vector3.Distance(center, position) + source.w,
                        Is.LessThan(bound.w),
                        $"Sphere {index} was not conservatively contained.");
                }
            }
        }

        [Test]
        public void ActivePrefixIgnoresInactiveTail()
        {
            using (var instances = States(
                State(Vector3.zero, 1f, 1u),
                State(new Vector3(10000f, 0f, 0f), 100f, 2u)))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                1,
                Allocator.Temp))
            {
                GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    1,
                    64,
                    clusters);

                Assert.That(clusters[0].PositionRadius.x, Is.Zero);
                Assert.That(clusters[0].PositionRadius.w,
                    Is.LessThan(1.001f));
                Assert.That(clusters[0].UnionViewMask, Is.EqualTo(1u));
            }
        }

        [Test]
        public void InvalidArgumentsFailBeforeWriting()
        {
            using (var instances = LinearStates(65))
            using (var tooSmall = new NativeArray<GpuInstanceCluster>(
                1,
                Allocator.Temp))
            using (var enough = new NativeArray<GpuInstanceCluster>(
                2,
                Allocator.Temp))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuInstanceClusterBuilder.GetRequiredClusterCount(-1, 64));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuInstanceClusterBuilder.GetRequiredClusterCount(1, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuInstanceClusterBuilder.GetRequiredClusterCount(1, 65));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuInstanceClusterBuilder.BuildContiguous(
                        instances,
                        66,
                        64,
                        enough));
                Assert.Throws<ArgumentException>(() =>
                    GpuInstanceClusterBuilder.BuildContiguous(
                        instances,
                        65,
                        64,
                        tooSmall));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuInstanceClusterBuilder.BuildContiguous(
                        default,
                        1,
                        64,
                        enough));
            }
        }

        [Test]
        public void WarmBuildDoesNotAllocateManagedMemory()
        {
            const int instanceCount = 257;
            int clusterCount =
                GpuInstanceClusterBuilder.GetRequiredClusterCount(
                    instanceCount,
                    64);
            using (var instances = LinearStates(instanceCount))
            using (var clusters = new NativeArray<GpuInstanceCluster>(
                clusterCount,
                Allocator.Temp))
            {
                GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    instanceCount,
                    64,
                    clusters);

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 64; iteration++)
                {
                    GpuInstanceClusterBuilder.BuildContiguous(
                        instances,
                        instanceCount,
                        64,
                        clusters);
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(allocated, Is.Zero);
            }
        }

        private static NativeArray<GpuInstanceState> LinearStates(int count)
        {
            var states = new NativeArray<GpuInstanceState>(
                count,
                Allocator.Temp);
            for (int index = 0; index < count; index++)
            {
                states[index] = State(
                    new Vector3(index, index % 3, -(index % 5)),
                    0.25f,
                    1u << (index % 31));
            }
            return states;
        }

        private static NativeArray<GpuInstanceState> States(
            params GpuInstanceState[] states)
        {
            return new NativeArray<GpuInstanceState>(
                states,
                Allocator.Temp);
        }

        private static GpuInstanceState State(
            Vector3 position,
            float radius,
            uint viewMask)
        {
            return new GpuInstanceState(
                position,
                radius,
                new Vector4(10f, 20f, 30f, 40f),
                0u,
                0u,
                1u,
                viewMask);
        }
    }
}
