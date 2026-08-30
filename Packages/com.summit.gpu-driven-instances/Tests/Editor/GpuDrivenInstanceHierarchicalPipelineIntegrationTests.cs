using System;
using NUnit.Framework;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances.Tests
{
    public sealed class GpuDrivenInstanceHierarchicalPipelineIntegrationTests
    {
        [SetUp]
        public void RequireComputeDevice()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore(
                    "A graphics-capable compute device is required.");
            }
        }

        [Test]
        public void HierarchicalMultiViewMatchesFlatAndIndependentCpuOracle()
        {
            GpuInstanceState[] instances = CreateStates(197, 4, 0xFu);
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(4, 18f);
            var views = new[]
            {
                new Vector4(-6f, 0f, 0f, 1f),
                new Vector4(6f, 0f, 0f, 0.75f),
                new Vector4(0f, 4f, 0f, 1.25f),
                new Vector4(0f, -4f, 0f, 0.5f),
            };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);

            AssertHierarchicalFlatAndOracleParity(
                instances,
                BuildClusters(instances, 64),
                planes,
                views,
                draws);
        }

        [Test]
        public void GlobalBinCountFallbackAboveSharedCapacityMatchesOracle()
        {
            const int viewCount = 32;
            GpuInstanceState[] instances =
                CreateStates(129, viewCount, uint.MaxValue);
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(viewCount, 1000f);
            var views = new Vector4[viewCount];
            for (int viewIndex = 0; viewIndex < viewCount; viewIndex++)
            {
                views[viewIndex] = new Vector4(
                    viewIndex - viewCount / 2,
                    0f,
                    0f,
                    1f);
            }

            AssertHierarchicalFlatAndOracleParity(
                instances,
                BuildClusters(instances, 64),
                planes,
                views,
                CreateDrawTemplates(4));
        }

        [TestCase(1, 1)]
        [TestCase(64, 1)]
        [TestCase(65, 2)]
        [TestCase(257, 5)]
        public void ClusterBoundariesMatchFlatAndCpuOracle(
            int instanceCount,
            int expectedClusterCount)
        {
            GpuInstanceState[] instances =
                CreateStates(instanceCount, 2, 0x3u);
            GpuInstanceCluster[] clusters = BuildClusters(instances, 64);
            Assert.That(clusters, Has.Length.EqualTo(expectedClusterCount));

            AssertHierarchicalFlatAndOracleParity(
                instances,
                clusters,
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(2, 1000f),
                new[]
                {
                    new Vector4(0f, 0f, 0f, 1f),
                    new Vector4(5f, 0f, 0f, 1f),
                },
                CreateDrawTemplates(4));
        }

        [TestCase(0u, 1u, 2)]
        [TestCase(0u, 65u, 64)]
        [TestCase(1u, 1u, 2)]
        public void InvalidClusterCoverFailsClosedWithDiagnostics(
            uint firstInstance,
            uint clusterInstanceCount,
            int activeCount)
        {
            GpuInstanceState[] instances =
                CreateStates(activeCount, 1, 1u);
            GpuInstanceCluster[] validClusters =
                BuildClusters(instances, 64);
            var invalidClusters = new[]
            {
                new GpuInstanceCluster(
                    new Vector4(0f, 0f, 0f, 1000f),
                    firstInstance,
                    clusterInstanceCount,
                    1u),
            };
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 1000f);
            var views = new[] { new Vector4(0f, 0f, 0f, 1f) };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);

            using (var buffers = new BufferSet(
                       instances,
                       validClusters,
                       planes,
                       views,
                       draws))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       Math.Max(1, instances.Length),
                       1,
                       draws.Length,
                       1,
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                RecordHierarchical(
                    pipeline,
                    commands,
                    buffers,
                    instances.Length,
                    validClusters.Length,
                    views.Length,
                    draws.Length);
                Graphics.ExecuteCommandBuffer(commands);
                Assert.That(
                    ReadUint(buffers.GroupCounts, 4),
                    Has.Some.GreaterThan(0u),
                    "The setup frame must populate state before fail-closed " +
                    "reset is tested.");

                buffers.Clusters.SetData(invalidClusters);
                commands.Clear();
                RecordHierarchical(
                    pipeline,
                    commands,
                    buffers,
                    instances.Length,
                    invalidClusters.Length,
                    views.Length,
                    draws.Length);
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(ReadUint(buffers.GroupCounts, 4),
                    Is.EqualTo(new uint[4]));
                Assert.That(ReadUint(buffers.GroupOffsets, 5),
                    Is.EqualTo(new uint[5]));
                Assert.That(
                    ReadUint(
                        buffers.HierarchyStatistics,
                        GpuDrivenInstancePipeline
                            .HierarchyStatisticWordCount),
                    Is.EqualTo(new uint[3]));
                uint[] arguments = ReadUint(buffers.IndirectArguments, 20);
                for (int draw = 0; draw < draws.Length; draw++)
                {
                    int argumentBase = draw * 5;
                    Assert.That(
                        arguments[argumentBase + 0],
                        Is.EqualTo(draws[draw].IndexCountPerInstance));
                    Assert.That(arguments[argumentBase + 1], Is.Zero);
                    Assert.That(
                        arguments[argumentBase + 2],
                        Is.EqualTo(draws[draw].StartIndex));
                    Assert.That(
                        arguments[argumentBase + 3],
                        Is.EqualTo(draws[draw].BaseVertex));
                    Assert.That(arguments[argumentBase + 4], Is.Zero);
                }

                uint[] diagnostics = ReadUint(
                    buffers.Diagnostics,
                    GpuDrivenInstancePipeline.DiagnosticWordCount);
                Assert.That(
                    diagnostics[GpuDrivenInstancePipeline
                        .ContractViolationCountWord],
                    Is.GreaterThanOrEqualTo(1u));
                Assert.That(
                    diagnostics[GpuDrivenInstancePipeline.ErrorFlagsWord],
                    Is.EqualTo(
                        (uint)GpuDrivenInstanceErrorFlags
                            .InvalidClusterContract));
            }
        }

        [Test]
        public void NonFiniteClusterBoundsFailClosedWithDiagnostics()
        {
            GpuInstanceState[] instances = CreateStates(1, 1, 1u);
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 1000f);
            var views = new[] { new Vector4(0f, 0f, 0f, 1f) };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);
            var invalidBounds = new[]
            {
                new Vector4(float.NaN, 0f, 0f, 1f),
                new Vector4(float.PositiveInfinity, 0f, 0f, 1f),
                new Vector4(0f, 0f, 0f, float.PositiveInfinity),
            };
            var initialClusters = new[]
            {
                new GpuInstanceCluster(
                    invalidBounds[0],
                    0u,
                    1u,
                    1u),
            };

            using (var buffers = new BufferSet(
                       instances,
                       initialClusters,
                       planes,
                       views,
                       draws))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       1,
                       1,
                       draws.Length,
                       1,
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                foreach (Vector4 invalidBound in invalidBounds)
                {
                    buffers.Clusters.SetData(new[]
                    {
                        new GpuInstanceCluster(
                            invalidBound,
                            0u,
                            1u,
                            1u),
                    });
                    commands.Clear();
                    RecordHierarchical(
                        pipeline,
                        commands,
                        buffers,
                        1,
                        1,
                        1,
                        draws.Length);
                    Graphics.ExecuteCommandBuffer(commands);

                    Assert.That(
                        ReadUint(buffers.GroupCounts, draws.Length),
                        Is.EqualTo(new uint[draws.Length]));
                    Assert.That(
                        ReadUint(
                            buffers.HierarchyStatistics,
                            GpuDrivenInstancePipeline
                                .HierarchyStatisticWordCount),
                        Is.EqualTo(new uint[3]));
                    uint[] arguments = ReadUint(
                        buffers.IndirectArguments,
                        draws.Length *
                            GpuDrivenInstancePipeline
                                .IndirectArgumentWordCount);
                    for (int draw = 0; draw < draws.Length; draw++)
                    {
                        Assert.That(
                            arguments[
                                draw *
                                    GpuDrivenInstancePipeline
                                        .IndirectArgumentWordCount +
                                1],
                            Is.Zero);
                    }
                    uint[] diagnostics = ReadUint(
                        buffers.Diagnostics,
                        GpuDrivenInstancePipeline.DiagnosticWordCount);
                    Assert.That(
                        diagnostics[GpuDrivenInstancePipeline
                            .ContractViolationCountWord],
                        Is.GreaterThanOrEqualTo(1u));
                    Assert.That(
                        diagnostics[GpuDrivenInstancePipeline.ErrorFlagsWord],
                        Is.EqualTo(
                            (uint)GpuDrivenInstanceErrorFlags
                                .InvalidClusterContract));
                }
            }
        }

        [Test]
        public void HierarchyDiagnosticsSuppressEveryIndirectDraw()
        {
            GpuInstanceState[] instances = CreateStates(2, 1, 1u);
            instances[0].LodCount = 0u;
            GpuInstanceCluster[] clusters = BuildClusters(instances, 64);
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 1000f);
            var views = new[] { new Vector4(0f, 0f, 0f, 1f) };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);

            using (var buffers = new BufferSet(
                       instances,
                       clusters,
                       planes,
                       views,
                       draws))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       instances.Length,
                       1,
                       draws.Length,
                       clusters.Length,
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                RecordHierarchical(
                    pipeline,
                    commands,
                    buffers,
                    instances.Length,
                    clusters.Length,
                    views.Length,
                    draws.Length);
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(
                    ReadUint(buffers.GroupCounts, draws.Length),
                    Has.Some.GreaterThan(0u),
                    "A valid peer must populate CSR counts before the " +
                    "draw-level fail-closed gate is checked.");
                uint[] arguments = ReadUint(
                    buffers.IndirectArguments,
                    draws.Length *
                        GpuDrivenInstancePipeline.IndirectArgumentWordCount);
                for (int draw = 0; draw < draws.Length; draw++)
                {
                    int argumentBase =
                        draw *
                        GpuDrivenInstancePipeline.IndirectArgumentWordCount;
                    Assert.That(arguments[argumentBase + 1], Is.Zero);
                    Assert.That(arguments[argumentBase + 4], Is.Zero);
                }
                uint[] diagnostics = ReadUint(
                    buffers.Diagnostics,
                    GpuDrivenInstancePipeline.DiagnosticWordCount);
                Assert.That(
                    diagnostics[GpuDrivenInstancePipeline
                        .ContractViolationCountWord],
                    Is.EqualTo(1u));
                Assert.That(
                    diagnostics[GpuDrivenInstancePipeline.ErrorFlagsWord],
                    Is.EqualTo(
                        (uint)GpuDrivenInstanceErrorFlags.InvalidLodContract));
            }
        }

        [Test]
        public void HierarchicalEntryRequiresOptInAndFeasibleClusterCount()
        {
            GpuInstanceState[] instances = CreateStates(65, 1, 1u);
            GpuInstanceCluster[] clusters = BuildClusters(instances, 64);
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 1000f);
            var views = new[] { new Vector4(0f, 0f, 0f, 1f) };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);

            using (var buffers = new BufferSet(
                       instances,
                       clusters,
                       planes,
                       views,
                       draws))
            using (var commands = new CommandBuffer())
            using (var flatOnly = new GpuDrivenInstancePipeline(
                       instances.Length,
                       1,
                       draws.Length,
                       emitProfilerMarkers: false))
            using (var insufficient = new GpuDrivenInstancePipeline(
                       instances.Length,
                       1,
                       draws.Length,
                       1,
                       emitProfilerMarkers: false))
            {
                Assert.That(flatOnly.SupportsHierarchicalVisibleOnly,
                    Is.False);
                Assert.That(flatOnly.HierarchicalScratchBytes, Is.Zero);
                Assert.Throws<InvalidOperationException>(() =>
                    RecordHierarchical(
                        flatOnly,
                        commands,
                        buffers,
                        instances.Length,
                        clusters.Length,
                        1,
                        draws.Length));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    RecordHierarchical(
                        insufficient,
                        commands,
                        buffers,
                        instances.Length,
                        clusters.Length,
                        1,
                        draws.Length));
            }
        }

        [Test]
        public void HierarchyStatisticsRequireCapacityAndNoOutputAlias()
        {
            GpuInstanceState[] instances = CreateStates(1, 1, 1u);
            GpuInstanceCluster[] clusters = BuildClusters(instances, 64);
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 1000f);
            var views = new[] { new Vector4(0f, 0f, 0f, 1f) };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);

            using (var buffers = new BufferSet(
                       instances,
                       clusters,
                       planes,
                       views,
                       draws))
            using (var tooSmall = new GraphicsBuffer(
                       GraphicsBuffer.Target.Structured,
                       2,
                       sizeof(uint)))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       1,
                       1,
                       draws.Length,
                       1,
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentException>(() =>
                    pipeline.RecordHierarchicalVisibleOnly(
                        commands,
                        buffers.Instances,
                        buffers.Clusters,
                        buffers.ViewPlanes,
                        buffers.ViewParameters,
                        buffers.DrawTemplates,
                        buffers.GroupCounts,
                        buffers.GroupOffsets,
                        buffers.GroupedInstanceIndices,
                        buffers.IndirectArguments,
                        tooSmall,
                        buffers.Diagnostics,
                        1,
                        1,
                        1,
                        draws.Length,
                        GpuPrimitiveBackend.Portable));
                Assert.Throws<ArgumentException>(() =>
                    pipeline.RecordHierarchicalVisibleOnly(
                        commands,
                        buffers.Instances,
                        buffers.Clusters,
                        buffers.ViewPlanes,
                        buffers.ViewParameters,
                        buffers.DrawTemplates,
                        buffers.GroupCounts,
                        buffers.GroupOffsets,
                        buffers.GroupedInstanceIndices,
                        buffers.IndirectArguments,
                        buffers.GroupCounts,
                        buffers.Diagnostics,
                        1,
                        1,
                        1,
                        draws.Length,
                        GpuPrimitiveBackend.Portable));
            }
        }

        private static void AssertHierarchicalFlatAndOracleParity(
            GpuInstanceState[] instances,
            GpuInstanceCluster[] clusters,
            Vector4[] planes,
            Vector4[] views,
            GpuDrawTemplate[] draws)
        {
            CpuGpuDrivenInstanceResult expected =
                CpuGpuDrivenInstanceOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    GpuDrivenInstanceOutputMode.VisibleOnly);

            using (var flat = new BufferSet(
                       instances,
                       clusters,
                       planes,
                       views,
                       draws))
            using (var hierarchical = new BufferSet(
                       instances,
                       clusters,
                       planes,
                       views,
                       draws))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       Math.Max(1, instances.Length),
                       views.Length,
                       draws.Length,
                       Math.Max(1, clusters.Length),
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                pipeline.Record(
                    commands,
                    flat.Instances,
                    flat.ViewPlanes,
                    flat.ViewParameters,
                    flat.DrawTemplates,
                    flat.GroupCounts,
                    flat.GroupOffsets,
                    flat.GroupedInstanceIndices,
                    flat.IndirectArguments,
                    flat.Diagnostics,
                    instances.Length,
                    views.Length,
                    draws.Length,
                    GpuPrimitiveBackend.Portable,
                    GpuDrivenInstanceOutputMode.VisibleOnly);
                RecordHierarchical(
                    pipeline,
                    commands,
                    hierarchical,
                    instances.Length,
                    clusters.Length,
                    views.Length,
                    draws.Length);
                Graphics.ExecuteCommandBuffer(commands);

                Snapshot flatResult = ReadSnapshot(flat, expected);
                Snapshot hierarchicalResult =
                    ReadSnapshot(hierarchical, expected);
                AssertSnapshotMatchesExpected(flatResult, expected);
                AssertSnapshotMatchesExpected(
                    hierarchicalResult,
                    expected);
                Assert.That(
                    hierarchicalResult.Counts,
                    Is.EqualTo(flatResult.Counts));
                Assert.That(
                    hierarchicalResult.Offsets,
                    Is.EqualTo(flatResult.Offsets));
                Assert.That(
                    hierarchicalResult.CanonicalGrouped,
                    Is.EqualTo(flatResult.CanonicalGrouped));
                Assert.That(
                    hierarchicalResult.Arguments,
                    Is.EqualTo(flatResult.Arguments));
                Assert.That(
                    hierarchicalResult.Diagnostics,
                    Is.EqualTo(flatResult.Diagnostics));

                uint[] statistics = ReadUint(
                    hierarchical.HierarchyStatistics,
                    GpuDrivenInstancePipeline.HierarchyStatisticWordCount);
                Assert.That(
                    statistics[GpuDrivenInstancePipeline
                        .HierarchicalVisiblePairCountWord],
                    Is.EqualTo(
                        checked((uint)expected.GroupedInstanceIndices.Length)));
                Assert.That(
                    statistics[GpuDrivenInstancePipeline
                        .CandidateInstanceViewCountWord],
                    Is.GreaterThanOrEqualTo(
                        statistics[GpuDrivenInstancePipeline
                            .HierarchicalVisiblePairCountWord]));
                Assert.That(
                    statistics[GpuDrivenInstancePipeline
                        .CoarseVisibleClusterViewCountWord],
                    Is.LessThanOrEqualTo(
                        checked((uint)(clusters.Length * views.Length))));
            }
        }

        private static void AssertSnapshotMatchesExpected(
            Snapshot actual,
            CpuGpuDrivenInstanceResult expected)
        {
            Assert.That(actual.Counts, Is.EqualTo(expected.Counts));
            Assert.That(actual.Offsets, Is.EqualTo(expected.Offsets));
            Assert.That(
                actual.CanonicalGrouped,
                Is.EqualTo(
                    CpuGpuDrivenInstanceOracle.CanonicalizeBins(
                        expected.GroupedInstanceIndices,
                        expected.Offsets,
                        expected.Counts)));
            Assert.That(
                actual.Arguments,
                Is.EqualTo(expected.IndirectArguments));
            Assert.That(actual.Diagnostics, Is.EqualTo(new[] { 0u, 0u }));
        }

        private static Snapshot ReadSnapshot(
            BufferSet buffers,
            CpuGpuDrivenInstanceResult expected)
        {
            uint[] counts = ReadUint(
                buffers.GroupCounts,
                expected.Counts.Length);
            uint[] offsets = ReadUint(
                buffers.GroupOffsets,
                expected.Offsets.Length);
            uint[] grouped = ReadUint(
                buffers.GroupedInstanceIndices,
                expected.GroupedInstanceIndices.Length);
            return new Snapshot(
                counts,
                offsets,
                CpuGpuDrivenInstanceOracle.CanonicalizeBins(
                    grouped,
                    offsets,
                    counts),
                ReadUint(
                    buffers.IndirectArguments,
                    expected.IndirectArguments.Length),
                ReadUint(
                    buffers.Diagnostics,
                    GpuDrivenInstancePipeline.DiagnosticWordCount));
        }

        private static void RecordHierarchical(
            GpuDrivenInstancePipeline pipeline,
            CommandBuffer commands,
            BufferSet buffers,
            int instanceCount,
            int clusterCount,
            int viewCount,
            int drawGroupCount)
        {
            pipeline.RecordHierarchicalVisibleOnly(
                commands,
                buffers.Instances,
                buffers.Clusters,
                buffers.ViewPlanes,
                buffers.ViewParameters,
                buffers.DrawTemplates,
                buffers.GroupCounts,
                buffers.GroupOffsets,
                buffers.GroupedInstanceIndices,
                buffers.IndirectArguments,
                buffers.HierarchyStatistics,
                buffers.Diagnostics,
                instanceCount,
                clusterCount,
                viewCount,
                drawGroupCount,
                GpuPrimitiveBackend.Portable);
        }

        private static GpuInstanceState[] CreateStates(
            int count,
            int viewCount,
            uint activeViewMask)
        {
            var states = new GpuInstanceState[count];
            for (int index = 0; index < count; index++)
            {
                uint mask = index % 7 == 0
                    ? activeViewMask & ~(1u << (index % viewCount))
                    : activeViewMask;
                states[index] = new GpuInstanceState(
                    new Vector3(
                        index % 31 - 15,
                        index % 9 - 4,
                        index % 13 - 6),
                    0.35f + (index % 3) * 0.1f,
                    new Vector4(8f, 80f, 0f, 0f),
                    checked((uint)(1000 + index)),
                    checked((uint)((index % 2) * 2)),
                    2u,
                    mask);
            }
            return states;
        }

        private static GpuInstanceCluster[] BuildClusters(
            GpuInstanceState[] states,
            int instancesPerCluster)
        {
            int clusterCount =
                GpuInstanceClusterBuilder.GetRequiredClusterCount(
                    states.Length,
                    instancesPerCluster);
            using (var nativeStates = new NativeArray<GpuInstanceState>(
                       states,
                       Allocator.Temp))
            using (var nativeClusters = new NativeArray<GpuInstanceCluster>(
                       clusterCount,
                       Allocator.Temp))
            {
                int written = GpuInstanceClusterBuilder.BuildContiguous(
                    nativeStates,
                    states.Length,
                    instancesPerCluster,
                    nativeClusters);
                var clusters = new GpuInstanceCluster[written];
                nativeClusters.CopyTo(clusters);
                return clusters;
            }
        }

        private static GpuDrawTemplate[] CreateDrawTemplates(int count)
        {
            var templates = new GpuDrawTemplate[count];
            for (int index = 0; index < count; index++)
            {
                templates[index] = new GpuDrawTemplate(
                    checked((uint)(36 + index)),
                    checked((uint)(index * 7)),
                    checked((uint)(index * 3)));
            }
            return templates;
        }

        private static uint[] ReadUint(GraphicsBuffer buffer, int count)
        {
            var values = new uint[count];
            if (count > 0)
            {
                buffer.GetData(values);
            }
            return values;
        }

        private sealed class Snapshot
        {
            internal Snapshot(
                uint[] counts,
                uint[] offsets,
                uint[] canonicalGrouped,
                uint[] arguments,
                uint[] diagnostics)
            {
                Counts = counts;
                Offsets = offsets;
                CanonicalGrouped = canonicalGrouped;
                Arguments = arguments;
                Diagnostics = diagnostics;
            }

            internal uint[] Counts { get; }
            internal uint[] Offsets { get; }
            internal uint[] CanonicalGrouped { get; }
            internal uint[] Arguments { get; }
            internal uint[] Diagnostics { get; }
        }

        private sealed class BufferSet : IDisposable
        {
            internal BufferSet(
                GpuInstanceState[] instances,
                GpuInstanceCluster[] clusters,
                Vector4[] planes,
                Vector4[] views,
                GpuDrawTemplate[] draws)
            {
                int pairCount = checked(instances.Length * views.Length);
                int visibleBinCount = checked(views.Length * draws.Length);
                Instances = CreateStructured(
                    Math.Max(1, instances.Length),
                    GpuInstanceState.Stride);
                Clusters = CreateStructured(
                    Math.Max(1, clusters.Length),
                    GpuInstanceCluster.Stride);
                ViewPlanes = CreateStructured(
                    planes.Length,
                    sizeof(float) * 4);
                ViewParameters = CreateStructured(
                    views.Length,
                    sizeof(float) * 4);
                DrawTemplates = CreateStructured(
                    draws.Length,
                    GpuDrawTemplate.Stride);
                GroupCounts = CreateStructured(
                    visibleBinCount,
                    sizeof(uint));
                GroupOffsets = CreateStructured(
                    visibleBinCount + 1,
                    sizeof(uint));
                GroupedInstanceIndices = CreateStructured(
                    Math.Max(1, pairCount),
                    sizeof(uint));
                IndirectArguments = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured |
                    GraphicsBuffer.Target.IndirectArguments,
                    visibleBinCount *
                    GpuDrivenInstancePipeline.IndirectArgumentWordCount,
                    sizeof(uint));
                Diagnostics = CreateStructured(
                    GpuDrivenInstancePipeline.DiagnosticWordCount,
                    sizeof(uint));
                HierarchyStatistics = CreateStructured(
                    GpuDrivenInstancePipeline.HierarchyStatisticWordCount,
                    sizeof(uint));

                if (instances.Length > 0)
                {
                    Instances.SetData(instances);
                }
                if (clusters.Length > 0)
                {
                    Clusters.SetData(clusters);
                }
                ViewPlanes.SetData(planes);
                ViewParameters.SetData(views);
                DrawTemplates.SetData(draws);
            }

            internal GraphicsBuffer Instances { get; }
            internal GraphicsBuffer Clusters { get; }
            internal GraphicsBuffer ViewPlanes { get; }
            internal GraphicsBuffer ViewParameters { get; }
            internal GraphicsBuffer DrawTemplates { get; }
            internal GraphicsBuffer GroupCounts { get; }
            internal GraphicsBuffer GroupOffsets { get; }
            internal GraphicsBuffer GroupedInstanceIndices { get; }
            internal GraphicsBuffer IndirectArguments { get; }
            internal GraphicsBuffer HierarchyStatistics { get; }
            internal GraphicsBuffer Diagnostics { get; }

            public void Dispose()
            {
                Diagnostics.Dispose();
                HierarchyStatistics.Dispose();
                IndirectArguments.Dispose();
                GroupedInstanceIndices.Dispose();
                GroupOffsets.Dispose();
                GroupCounts.Dispose();
                DrawTemplates.Dispose();
                ViewParameters.Dispose();
                ViewPlanes.Dispose();
                Clusters.Dispose();
                Instances.Dispose();
            }

            private static GraphicsBuffer CreateStructured(
                int count,
                int stride)
            {
                return new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    count,
                    stride);
            }
        }
    }
}
