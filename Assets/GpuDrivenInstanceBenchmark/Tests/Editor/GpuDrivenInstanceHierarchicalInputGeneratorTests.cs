using System.Collections;
using NUnit.Framework;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceHierarchicalInputGeneratorTests
    {
        [TestCase("visible5", 200)]
        [TestCase("visible25", 1000)]
        [TestCase("visible75", 3000)]
        [TestCase("visible100", 4000)]
        public void VisibilityCellsHaveExactViewSelectivePairCounts(
            string visibility,
            int expectedVisiblePairs)
        {
            Generate(
                1000,
                visibility,
                20260829,
                out GpuInstanceState[] instances,
                out Vector4[] planes,
                out Vector4[] views,
                out GpuDrawTemplate[] draws,
                out int returnedVisibleInstances);

            GpuDrivenInstanceExpectedResult expected =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            Assert.That(
                expected.GroupedInstanceIndices.Length,
                Is.EqualTo(expectedVisiblePairs));
            Assert.That(
                CountDistinct(expected.GroupedInstanceIndices, instances.Length),
                Is.EqualTo(returnedVisibleInstances));

            const uint allFourViews = 0xFu;
            if (visibility == "visible100")
            {
                foreach (GpuInstanceState instance in instances)
                {
                    Assert.That(instance.ViewMask, Is.EqualTo(allFourViews));
                }
            }
            else
            {
                bool hasSelectiveMask = false;
                bool hasNonzeroMask = false;
                foreach (GpuInstanceState instance in instances)
                {
                    hasSelectiveMask |= instance.ViewMask != allFourViews;
                    hasNonzeroMask |= instance.ViewMask != 0u;
                }
                Assert.That(hasSelectiveMask, Is.True);
                Assert.That(hasNonzeroMask, Is.True);
                for (int view = 0; view < 4; view++)
                {
                    uint viewBit = 1u << view;
                    bool hasMembership = false;
                    foreach (GpuInstanceState instance in instances)
                    {
                        hasMembership |= (instance.ViewMask & viewBit) != 0u;
                    }
                    Assert.That(
                        hasMembership,
                        Is.True,
                        $"View {view} has no selective membership.");
                }
            }
        }

        [Test]
        public void FrozenViewsUseDifferentFrustaAndParameters()
        {
            Generate(
                1000,
                "visible25",
                20260829,
                out _,
                out Vector4[] planes,
                out Vector4[] views,
                out _,
                out _);

            for (int view = 1; view < views.Length; view++)
            {
                Assert.That(views[view], Is.Not.EqualTo(views[0]));
                bool anyPlaneDiffers = false;
                for (int plane = 0;
                    plane < GpuDrivenInstancePipeline.FrustumPlaneCount;
                    plane++)
                {
                    if (planes[
                            view *
                            GpuDrivenInstancePipeline.FrustumPlaneCount +
                            plane] != planes[plane])
                    {
                        anyPlaneDiffers = true;
                        break;
                    }
                }
                Assert.That(
                    anyPlaneDiffers,
                    Is.True,
                    $"View {view} reused view zero's frustum.");
            }
        }

        [Test]
        public void EveryContiguousClusterIsSpatiallyCompact()
        {
            const int instanceCount = 1000;
            Generate(
                instanceCount,
                "visible25",
                20260829,
                out GpuInstanceState[] instances,
                out _,
                out _,
                out _,
                out _);

            for (int first = 0;
                first < instanceCount;
                first += GpuDrivenInstanceHierarchicalInputGenerator
                    .InstancesPerCluster)
            {
                int count = Mathf.Min(
                    GpuDrivenInstanceHierarchicalInputGenerator
                        .InstancesPerCluster,
                    instanceCount - first);
                Vector3 anchor = Position(instances[first]);
                for (int index = first + 1;
                    index < first + count;
                    index++)
                {
                    Assert.That(
                        Vector3.Distance(anchor, Position(instances[index])),
                        Is.LessThan(0.75f));
                }
            }
        }

        [Test]
        public void HierarchyStatisticsOracleIsExactAndIncludesSpatialRejects()
        {
            const int instanceCount = 1000;
            Generate(
                instanceCount,
                "visible25",
                20260829,
                out GpuInstanceState[] instances,
                out Vector4[] planes,
                out _,
                out _,
                out _);
            GpuInstanceCluster[] clusters = BuildClusters(instances);

            GpuDrivenInstanceHierarchyExpectedStatistics actual =
                GpuDrivenInstanceHierarchicalInputGenerator
                    .ComputeExpectedHierarchyStatistics(
                        instances,
                        clusters,
                        planes,
                        4);
            ComputeStatisticsIndependently(
                instances,
                clusters,
                planes,
                4,
                out uint expectedCoarse,
                out uint expectedCandidates,
                out uint unionMaskedClusterViews);

            Assert.That(
                actual.CoarseVisibleClusterViewCount,
                Is.EqualTo(expectedCoarse));
            Assert.That(
                actual.CandidateInstanceViewCount,
                Is.EqualTo(expectedCandidates));
            Assert.That(actual.CandidateInstanceViewCount, Is.EqualTo(1000u));
            Assert.That(
                actual.CoarseVisibleClusterViewCount,
                Is.LessThan(unionMaskedClusterViews),
                "At least one masked cluster-view must be spatially rejected.");
        }

        [Test]
        public void ClusterBuilderCoversContiguousPrefixConservatively()
        {
            const int instanceCount = 257;
            Generate(
                instanceCount,
                "visible75",
                20260829,
                out GpuInstanceState[] managed,
                out _,
                out _,
                out _,
                out _);
            int required = GpuInstanceClusterBuilder.GetRequiredClusterCount(
                instanceCount,
                GpuDrivenInstanceHierarchicalInputGenerator
                    .InstancesPerCluster);
            NativeArray<GpuInstanceState> instances =
                new NativeArray<GpuInstanceState>(managed, Allocator.Temp);
            NativeArray<GpuInstanceCluster> clusters =
                new NativeArray<GpuInstanceCluster>(required, Allocator.Temp);
            try
            {
                int built = GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    instanceCount,
                    GpuDrivenInstanceHierarchicalInputGenerator
                        .InstancesPerCluster,
                    clusters);
                Assert.That(built, Is.EqualTo(required));
                int nextFirst = 0;
                for (int clusterIndex = 0;
                    clusterIndex < built;
                    clusterIndex++)
                {
                    GpuInstanceCluster cluster = clusters[clusterIndex];
                    Assert.That(cluster.FirstInstance, Is.EqualTo((uint)nextFirst));
                    Assert.That(
                        cluster.InstanceCount,
                        Is.InRange(
                            1u,
                            (uint)GpuInstanceCluster.MaximumInstanceCount));
                    Vector3 center = new Vector3(
                        cluster.PositionRadius.x,
                        cluster.PositionRadius.y,
                        cluster.PositionRadius.z);
                    int end = nextFirst + checked((int)cluster.InstanceCount);
                    for (int index = nextFirst; index < end; index++)
                    {
                        Vector4 positionRadius =
                            instances[index].PositionRadius;
                        float requiredRadius = Vector3.Distance(
                            center,
                            new Vector3(
                                positionRadius.x,
                                positionRadius.y,
                                positionRadius.z)) + positionRadius.w;
                        Assert.That(
                            cluster.PositionRadius.w,
                            Is.GreaterThanOrEqualTo(requiredRadius));
                    }
                    nextFirst = end;
                }
                Assert.That(nextFirst, Is.EqualTo(instanceCount));
            }
            finally
            {
                clusters.Dispose();
                instances.Dispose();
            }
        }

        [Test]
        public void LayoutIsDeterministicButSeedSensitive()
        {
            GpuInstanceState[] first = GenerateStates(
                130,
                "visible25",
                20260829);
            GpuInstanceState[] repeat = GenerateStates(
                130,
                "visible25",
                20260829);
            GpuInstanceState[] changed = GenerateStates(
                130,
                "visible25",
                20260830);

            Assert.That(repeat, Is.EqualTo(first));
            Assert.That(changed, Is.Not.EqualTo(first));
            Assert.That(
                GpuDrivenInstanceHierarchicalInputGenerator.VisibilityLayoutId,
                Is.EqualTo("spatial-clustered-multiview-64-v2"));
        }

        [Test]
        public void BenchmarkModeParserRejectsImplicitFallback()
        {
            Assert.That(
                GpuDrivenInstanceBenchmarkModes.Parse("filtered-binning"),
                Is.EqualTo(GpuDrivenInstanceBenchmarkMode.FilteredBinning));
            Assert.That(
                GpuDrivenInstanceBenchmarkModes.Parse("hierarchical-culling"),
                Is.EqualTo(GpuDrivenInstanceBenchmarkMode.HierarchicalCulling));
            Assert.That(
                () => GpuDrivenInstanceBenchmarkModes.Parse("auto"),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [UnityTest]
        public IEnumerator FlatAndHierarchicalPathsMatchExactOracles()
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.supportsAsyncGPUReadback)
            {
                Assert.Ignore("Compute shaders and GPU readback are required.");
            }

            var adapter = new GpuDrivenInstanceBenchmarkAdapter(
                1024,
                4,
                "visible25",
                20260829,
                1,
                GpuDrivenInstanceBenchmarkModes.HierarchicalCullingId);
            try
            {
                Assert.That(adapter.BenchmarkMode, Is.EqualTo("hierarchical-culling"));
                Assert.That(
                    adapter.VisibilityLayout,
                    Is.EqualTo("spatial-clustered-multiview-64-v2"));
                Assert.That(adapter.VisiblePairCount, Is.EqualTo(1024));
                Assert.That(adapter.VisibleInstanceCount, Is.EqualTo(960));
                Assert.That(adapter.ClusterCount, Is.EqualTo(16));
                Assert.That(adapter.ClusterBytes, Is.EqualTo(512L));
                Assert.That(
                    adapter.ExpectedCoarseVisibleClusterViewCount,
                    Is.LessThan((uint)(adapter.ClusterCount * adapter.ViewCount)));
                Assert.That(
                    adapter.ExpectedCandidateInstanceViewCount,
                    Is.EqualTo(1024u));

                GpuDrivenInstanceValidationResult baseline = null;
                adapter.BeginValidation(
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    "test");
                for (int frame = 0; frame < 300 && baseline == null; frame++)
                {
                    adapter.TryCompleteValidation(out baseline);
                    if (baseline == null)
                    {
                        yield return null;
                    }
                }
                Assert.That(baseline, Is.Not.Null);
                Assert.That(baseline.Passed, Is.True, baseline.Message);

                GpuDrivenInstanceValidationResult hierarchical = null;
                adapter.BeginValidation(
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    "test");
                for (int frame = 0; frame < 300 && hierarchical == null; frame++)
                {
                    adapter.TryCompleteValidation(out hierarchical);
                    if (hierarchical == null)
                    {
                        yield return null;
                    }
                }
                Assert.That(hierarchical, Is.Not.Null);
                Assert.That(
                    hierarchical.Passed,
                    Is.True,
                    hierarchical.Message);
                Assert.That(
                    hierarchical.ResultHash,
                    Is.EqualTo(baseline.ResultHash));
                Assert.That(
                    hierarchical.HierarchicalVisiblePairCount,
                    Is.EqualTo((uint)adapter.VisiblePairCount));
                Assert.That(
                    hierarchical.HierarchyStatisticsAvailable,
                    Is.True);
                Assert.That(
                    hierarchical.CoarseVisibleClusterViewCount,
                    Is.EqualTo(
                        adapter.ExpectedCoarseVisibleClusterViewCount));
                Assert.That(
                    hierarchical.CandidateInstanceViewCount,
                    Is.EqualTo(adapter.ExpectedCandidateInstanceViewCount));
                Assert.That(
                    hierarchical.ExpectedCoarseVisibleClusterViewCount,
                    Is.EqualTo(
                        adapter.ExpectedCoarseVisibleClusterViewCount));
                Assert.That(
                    hierarchical.ExpectedCandidateInstanceViewCount,
                    Is.EqualTo(adapter.ExpectedCandidateInstanceViewCount));
            }
            finally
            {
                adapter.Dispose();
            }
        }

        private static GpuInstanceState[] GenerateStates(
            int instanceCount,
            string visibility,
            int seed)
        {
            Generate(
                instanceCount,
                visibility,
                seed,
                out GpuInstanceState[] instances,
                out _,
                out _,
                out _,
                out _);
            return instances;
        }

        private static void Generate(
            int instanceCount,
            string visibility,
            int seed,
            out GpuInstanceState[] instances,
            out Vector4[] planes,
            out Vector4[] views,
            out GpuDrawTemplate[] draws,
            out int visibleInstanceCount)
        {
            instances = new GpuInstanceState[instanceCount];
            planes = new Vector4[24];
            views = new Vector4[4];
            draws = new GpuDrawTemplate[8];
            visibleInstanceCount =
                GpuDrivenInstanceHierarchicalInputGenerator.Populate(
                    instances,
                    planes,
                    views,
                    draws,
                    visibility,
                    seed);
        }

        private static GpuInstanceCluster[] BuildClusters(
            GpuInstanceState[] managed)
        {
            int required = GpuInstanceClusterBuilder.GetRequiredClusterCount(
                managed.Length,
                GpuDrivenInstanceHierarchicalInputGenerator
                    .InstancesPerCluster);
            var result = new GpuInstanceCluster[required];
            NativeArray<GpuInstanceState> instances =
                new NativeArray<GpuInstanceState>(managed, Allocator.Temp);
            NativeArray<GpuInstanceCluster> clusters =
                new NativeArray<GpuInstanceCluster>(required, Allocator.Temp);
            try
            {
                int built = GpuInstanceClusterBuilder.BuildContiguous(
                    instances,
                    managed.Length,
                    GpuDrivenInstanceHierarchicalInputGenerator
                        .InstancesPerCluster,
                    clusters);
                Assert.That(built, Is.EqualTo(required));
                clusters.CopyTo(result);
                return result;
            }
            finally
            {
                clusters.Dispose();
                instances.Dispose();
            }
        }

        private static void ComputeStatisticsIndependently(
            GpuInstanceState[] instances,
            GpuInstanceCluster[] clusters,
            Vector4[] planes,
            int viewCount,
            out uint coarse,
            out uint candidates,
            out uint unionMaskedClusterViews)
        {
            coarse = 0u;
            candidates = 0u;
            unionMaskedClusterViews = 0u;
            foreach (GpuInstanceCluster cluster in clusters)
            {
                Vector3 center = new Vector3(
                    cluster.PositionRadius.x,
                    cluster.PositionRadius.y,
                    cluster.PositionRadius.z);
                for (int view = 0; view < viewCount; view++)
                {
                    uint bit = 1u << view;
                    if ((cluster.UnionViewMask & bit) == 0u)
                    {
                        continue;
                    }
                    unionMaskedClusterViews++;
                    if (!SphereVisible(
                            center,
                            cluster.PositionRadius.w,
                            planes,
                            view))
                    {
                        continue;
                    }
                    coarse++;
                    int first = checked((int)cluster.FirstInstance);
                    int end = first + checked((int)cluster.InstanceCount);
                    for (int index = first; index < end; index++)
                    {
                        if ((instances[index].ViewMask & bit) != 0u)
                        {
                            candidates++;
                        }
                    }
                }
            }
        }

        private static bool SphereVisible(
            Vector3 center,
            float radius,
            Vector4[] planes,
            int view)
        {
            int first =
                view * GpuDrivenInstancePipeline.FrustumPlaneCount;
            for (int index = 0;
                index < GpuDrivenInstancePipeline.FrustumPlaneCount;
                index++)
            {
                Vector4 plane = planes[first + index];
                if (plane.x * center.x +
                    plane.y * center.y +
                    plane.z * center.z +
                    plane.w < -radius)
                {
                    return false;
                }
            }
            return true;
        }

        private static int CountDistinct(uint[] values, int capacity)
        {
            var seen = new bool[capacity];
            int count = 0;
            foreach (uint value in values)
            {
                int index = checked((int)value);
                if (!seen[index])
                {
                    seen[index] = true;
                    count++;
                }
            }
            return count;
        }

        private static Vector3 Position(GpuInstanceState instance)
        {
            return new Vector3(
                instance.PositionRadius.x,
                instance.PositionRadius.y,
                instance.PositionRadius.z);
        }
    }
}
