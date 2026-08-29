using System;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances.Tests
{
    public sealed class GpuDrivenInstancePipelineIntegrationTests
    {
        private const uint Sentinel = 0xDEADBEEFu;

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
        public void MultiViewVisibilityLodMasksAndArgumentsMatchCpuOracle()
        {
            var instances = new[]
            {
                CreateInstance(
                    new Vector3(0f, 0f, 0f),
                    1f,
                    new Vector4(5f, 15f, 0f, 0f),
                    0u,
                    2u,
                    0b11u),
                CreateInstance(
                    new Vector3(8f, 0f, 0f),
                    1f,
                    new Vector4(5f, 20f, 0f, 0f),
                    2u,
                    2u,
                    0b11u),
                CreateInstance(
                    new Vector3(20f, 0f, 0f),
                    0.5f,
                    new Vector4(100f, 0f, 0f, 0f),
                    0u,
                    1u,
                    0b11u),
                CreateInstance(
                    new Vector3(0f, 0f, 0f),
                    1f,
                    new Vector4(5f, 15f, 0f, 0f),
                    0u,
                    2u,
                    0b10u),
            };
            Vector4[] viewPlanes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(2, 10f);
            var viewParameters = new[]
            {
                new Vector4(0f, 0f, 0f, 1f),
                new Vector4(10f, 0f, 0f, 1f),
            };
            GpuDrawTemplate[] draws = CreateDrawTemplates(4);

            AssertGpuMatchesOracle(
                instances,
                viewPlanes,
                viewParameters,
                draws);
        }

        [Test]
        public void VisibleOnlyModeDiscardsRejectedPayloads()
        {
            var instances = new[]
            {
                CreateInstance(
                    Vector3.zero,
                    1f,
                    new Vector4(20f, 0f, 0f, 0f),
                    0u,
                    1u,
                    0b11u),
                CreateInstance(
                    new Vector3(100f, 0f, 0f),
                    1f,
                    new Vector4(200f, 0f, 0f, 0f),
                    0u,
                    1u,
                    0b11u),
            };
            Vector4[] views =
            {
                new Vector4(0f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
            };
            CpuGpuDrivenInstanceResult result = AssertGpuMatchesOracle(
                instances,
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(2, 10f),
                views,
                CreateDrawTemplates(1),
                GpuDrivenInstanceOutputMode.VisibleOnly);

            Assert.That(result.Counts, Has.Length.EqualTo(2));
            Assert.That(result.Offsets[result.Offsets.Length - 1],
                Is.EqualTo(2u));
            Assert.That(
                result.GroupedInstanceIndices,
                Has.Length.LessThan(instances.Length * views.Length));
        }

        [TestCase(1)]
        [TestCase(255)]
        [TestCase(256)]
        [TestCase(257)]
        [TestCase(4097)]
        public void DispatchBoundariesMatchCpuOracle(int instanceCount)
        {
            var instances = new GpuInstanceState[instanceCount];
            for (int index = 0; index < instances.Length; index++)
            {
                instances[index] = CreateInstance(
                    new Vector3(index % 17 - 8, 0f, 0f),
                    0.25f,
                    new Vector4(1000f, 0f, 0f, 0f),
                    checked((uint)(index % 4)),
                    1u,
                    1u);
            }

            AssertGpuMatchesOracle(
                instances,
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 1000f),
                new[] { new Vector4(0f, 0f, 0f, 1f) },
                CreateDrawTemplates(4));
        }

        [Test]
        public void InvalidContractsFailClosedIntoCulledBin()
        {
            var instances = new[]
            {
                CreateInstance(
                    Vector3.zero,
                    1f,
                    new Vector4(10f, 0f, 0f, 0f),
                    0u,
                    1u,
                    0b11u),
                CreateInstance(
                    Vector3.zero,
                    -1f,
                    new Vector4(10f, 0f, 0f, 0f),
                    0u,
                    1u,
                    0b11u),
                CreateInstance(
                    Vector3.zero,
                    1f,
                    new Vector4(10f, 5f, 0f, 0f),
                    0u,
                    2u,
                    0b11u),
                CreateInstance(
                    Vector3.zero,
                    1f,
                    new Vector4(10f, 20f, 0f, 0f),
                    3u,
                    2u,
                    0b11u),
            };
            Vector4[] viewParameters =
            {
                new Vector4(0f, 0f, 0f, 1f),
                new Vector4(0f, 0f, 0f, 0f),
            };

            CpuGpuDrivenInstanceResult result = AssertGpuMatchesOracle(
                instances,
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(2, 100f),
                viewParameters,
                CreateDrawTemplates(4));

            Assert.That(result.ContractViolationCount, Is.EqualTo(4u));
            Assert.That(result.ErrorFlags, Is.EqualTo(15u));
        }

        [Test]
        public void ZeroInstancesClearCountsOffsetsArgumentsAndDiagnostics()
        {
            var instances = Array.Empty<GpuInstanceState>();
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(2, 10f);
            Vector4[] views =
            {
                new Vector4(0f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
            };
            GpuDrawTemplate[] draws = CreateDrawTemplates(3);
            CpuGpuDrivenInstanceResult expected =
                CpuGpuDrivenInstanceOracle.Build(
                    instances,
                    planes,
                    views,
                    draws);

            using (var buffers = new BufferSet(
                       instances,
                       planes,
                       views,
                       draws))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       1,
                       views.Length,
                       draws.Length,
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                buffers.GroupedInstanceIndices.SetData(
                    new[] { Sentinel });
                Record(
                    pipeline,
                    commands,
                    buffers,
                    instances.Length,
                    views.Length,
                    draws.Length);
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(
                    ReadUint(buffers.GroupCounts, expected.Counts.Length),
                    Is.EqualTo(expected.Counts));
                Assert.That(
                    ReadUint(buffers.GroupOffsets, expected.Offsets.Length),
                    Is.EqualTo(expected.Offsets));
                Assert.That(
                    ReadUint(
                        buffers.IndirectArguments,
                        expected.IndirectArguments.Length),
                    Is.EqualTo(expected.IndirectArguments));
                Assert.That(
                    ReadUint(
                        buffers.Diagnostics,
                        GpuDrivenInstancePipeline.DiagnosticWordCount),
                    Is.EqualTo(new[] { 0u, 0u }));
                Assert.That(
                    ReadUint(buffers.GroupedInstanceIndices, 1)[0],
                    Is.EqualTo(Sentinel));
            }
        }

        [Test]
        public void WritableAliasesAreRejectedBeforeRecording()
        {
            GpuInstanceState[] instances =
            {
                CreateInstance(
                    Vector3.zero,
                    1f,
                    new Vector4(10f, 0f, 0f, 0f),
                    0u,
                    1u,
                    1u),
            };
            Vector4[] views =
            {
                new Vector4(0f, 0f, 0f, 1f),
            };
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 10f);
            GpuDrawTemplate[] draws = CreateDrawTemplates(1);
            using (var buffers = new BufferSet(
                       instances,
                       planes,
                       views,
                       draws))
            using (var pipeline = new GpuDrivenInstancePipeline(1, 1, 1))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentException>(
                    () => pipeline.Record(
                        commands,
                        buffers.Instances,
                        buffers.ViewPlanes,
                        buffers.ViewParameters,
                        buffers.DrawTemplates,
                        buffers.GroupCounts,
                        buffers.GroupOffsets,
                        buffers.GroupedInstanceIndices,
                        buffers.IndirectArguments,
                        buffers.GroupCounts,
                        1,
                        1,
                        1,
                        GpuPrimitiveBackend.Portable));
            }
        }

        [Test]
        public void DisposedPipelineRejectsRecord()
        {
            GpuInstanceState[] instances =
            {
                CreateInstance(
                    Vector3.zero,
                    1f,
                    new Vector4(10f, 0f, 0f, 0f),
                    0u,
                    1u,
                    1u),
            };
            Vector4[] views =
            {
                new Vector4(0f, 0f, 0f, 1f),
            };
            Vector4[] planes =
                CpuGpuDrivenInstanceOracle.CreateBoxPlanes(1, 10f);
            GpuDrawTemplate[] draws = CreateDrawTemplates(1);
            using (var buffers = new BufferSet(
                       instances,
                       planes,
                       views,
                       draws))
            using (var commands = new CommandBuffer())
            {
                var pipeline = new GpuDrivenInstancePipeline(1, 1, 1);
                pipeline.Dispose();
                Assert.Throws<ObjectDisposedException>(
                    () => Record(
                        pipeline,
                        commands,
                        buffers,
                        1,
                        1,
                        1));
                pipeline.Dispose();
            }
        }

        private static CpuGpuDrivenInstanceResult AssertGpuMatchesOracle(
            GpuInstanceState[] instances,
            Vector4[] planes,
            Vector4[] views,
            GpuDrawTemplate[] draws,
            GpuDrivenInstanceOutputMode outputMode =
                GpuDrivenInstanceOutputMode.CulledTail)
        {
            CpuGpuDrivenInstanceResult expected =
                CpuGpuDrivenInstanceOracle.Build(
                    instances,
                    planes,
                    views,
                    draws,
                    outputMode);
            using (var buffers = new BufferSet(
                       instances,
                       planes,
                       views,
                       draws,
                       outputMode))
            using (var pipeline = new GpuDrivenInstancePipeline(
                       Math.Max(1, instances.Length),
                       views.Length,
                       draws.Length,
                       emitProfilerMarkers: false))
            using (var commands = new CommandBuffer())
            {
                Record(
                    pipeline,
                    commands,
                    buffers,
                    instances.Length,
                    views.Length,
                    draws.Length,
                    outputMode);
                Graphics.ExecuteCommandBuffer(commands);

                uint[] actualCounts = ReadUint(
                    buffers.GroupCounts,
                    expected.Counts.Length);
                uint[] actualOffsets = ReadUint(
                    buffers.GroupOffsets,
                    expected.Offsets.Length);
                uint[] actualGrouped = ReadUint(
                    buffers.GroupedInstanceIndices,
                    expected.GroupedInstanceIndices.Length);
                uint[] actualArguments = ReadUint(
                    buffers.IndirectArguments,
                    expected.IndirectArguments.Length);
                uint[] actualDiagnostics = ReadUint(
                    buffers.Diagnostics,
                    GpuDrivenInstancePipeline.DiagnosticWordCount);

                Assert.That(actualCounts, Is.EqualTo(expected.Counts));
                Assert.That(actualOffsets, Is.EqualTo(expected.Offsets));
                Assert.That(
                    CpuGpuDrivenInstanceOracle.CanonicalizeBins(
                        actualGrouped,
                        actualOffsets,
                        actualCounts),
                    Is.EqualTo(
                        CpuGpuDrivenInstanceOracle.CanonicalizeBins(
                            expected.GroupedInstanceIndices,
                            expected.Offsets,
                            expected.Counts)));
                Assert.That(
                    actualArguments,
                    Is.EqualTo(expected.IndirectArguments));
                Assert.That(
                    actualDiagnostics[GpuDrivenInstancePipeline
                        .ContractViolationCountWord],
                    Is.EqualTo(expected.ContractViolationCount));
                Assert.That(
                    actualDiagnostics[GpuDrivenInstancePipeline
                        .ErrorFlagsWord],
                    Is.EqualTo(expected.ErrorFlags));
            }
            return expected;
        }

        private static void Record(
            GpuDrivenInstancePipeline pipeline,
            CommandBuffer commands,
            BufferSet buffers,
            int instanceCount,
            int viewCount,
            int drawGroupCount,
            GpuDrivenInstanceOutputMode outputMode =
                GpuDrivenInstanceOutputMode.CulledTail)
        {
            pipeline.Record(
                commands,
                buffers.Instances,
                buffers.ViewPlanes,
                buffers.ViewParameters,
                buffers.DrawTemplates,
                buffers.GroupCounts,
                buffers.GroupOffsets,
                buffers.GroupedInstanceIndices,
                buffers.IndirectArguments,
                buffers.Diagnostics,
                instanceCount,
                viewCount,
                drawGroupCount,
                GpuPrimitiveBackend.Portable,
                outputMode);
        }

        private static GpuInstanceState CreateInstance(
            Vector3 position,
            float radius,
            Vector4 lodDistances,
            uint drawGroupBase,
            uint lodCount,
            uint viewMask)
        {
            return new GpuInstanceState(
                position,
                radius,
                lodDistances,
                applicationId: drawGroupBase + 100u,
                drawGroupBase: drawGroupBase,
                lodCount: lodCount,
                viewMask: viewMask);
        }

        private static GpuDrawTemplate[] CreateDrawTemplates(int count)
        {
            var templates = new GpuDrawTemplate[count];
            for (int index = 0; index < templates.Length; index++)
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

        private sealed class BufferSet : IDisposable
        {
            internal BufferSet(
                GpuInstanceState[] instances,
                Vector4[] planes,
                Vector4[] views,
                GpuDrawTemplate[] draws,
                GpuDrivenInstanceOutputMode outputMode =
                    GpuDrivenInstanceOutputMode.CulledTail)
            {
                int pairCount = checked(instances.Length * views.Length);
                int visibleBinCount = checked(views.Length * draws.Length);
                int totalBinCount =
                    GpuDrivenInstancePipeline.GetOutputBinCount(
                        views.Length,
                        draws.Length,
                        outputMode);
                Instances = CreateStructured(
                    Math.Max(1, instances.Length),
                    GpuInstanceState.Stride);
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
                    totalBinCount,
                    sizeof(uint));
                GroupOffsets = CreateStructured(
                    totalBinCount + 1,
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

                if (instances.Length > 0)
                {
                    Instances.SetData(instances);
                }
                ViewPlanes.SetData(planes);
                ViewParameters.SetData(views);
                DrawTemplates.SetData(draws);
            }

            internal GraphicsBuffer Instances { get; }

            internal GraphicsBuffer ViewPlanes { get; }

            internal GraphicsBuffer ViewParameters { get; }

            internal GraphicsBuffer DrawTemplates { get; }

            internal GraphicsBuffer GroupCounts { get; }

            internal GraphicsBuffer GroupOffsets { get; }

            internal GraphicsBuffer GroupedInstanceIndices { get; }

            internal GraphicsBuffer IndirectArguments { get; }

            internal GraphicsBuffer Diagnostics { get; }

            public void Dispose()
            {
                Diagnostics.Dispose();
                IndirectArguments.Dispose();
                GroupedInstanceIndices.Dispose();
                GroupOffsets.Dispose();
                GroupCounts.Dispose();
                DrawTemplates.Dispose();
                ViewParameters.Dispose();
                ViewPlanes.Dispose();
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
