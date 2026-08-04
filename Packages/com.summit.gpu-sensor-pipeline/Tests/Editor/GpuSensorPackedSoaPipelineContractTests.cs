using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorPackedSoaPipelineContractTests
    {
        [Test]
        public void TypeAndConstructorExposeAnExplicitBackendContract()
        {
            Type type = typeof(GpuSensorPackedSoaPipeline);
            ConstructorInfo constructor = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Single();
            ParameterInfo[] parameters = constructor.GetParameters();

            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.That(type.IsSealed, Is.True);
                Assert.That(
                    typeof(IDisposable).IsAssignableFrom(type),
                    Is.True);
                Assert.That(
                    parameters.Select(parameter => parameter.ParameterType),
                    Is.EqualTo(new[]
                    {
                        typeof(int),
                        typeof(int),
                        typeof(GpuPrimitiveBackend),
                        typeof(bool),
                        typeof(ComputeShader),
                    }));
                Assert.That(parameters[0].IsOptional, Is.False);
                Assert.That(parameters[1].IsOptional, Is.False);
                Assert.That(
                    parameters[2].DefaultValue,
                    Is.EqualTo(GpuPrimitiveBackend.WaveOps));
                Assert.That(parameters[3].DefaultValue, Is.True);
                Assert.That(parameters[4].DefaultValue, Is.Null);
            });

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new GpuSensorPackedSoaPipeline(
                    1,
                    1,
                    GpuPrimitiveBackend.Auto,
                    false));
        }

        [Test]
        public void ConstructorRejectsInvalidCapacitiesBeforeGpuUse()
        {
            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new GpuSensorPackedSoaPipeline(0, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new GpuSensorPackedSoaPipeline(-1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new GpuSensorPackedSoaPipeline(1, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new GpuSensorPackedSoaPipeline(1, -1));
            });
        }

        [Test]
        public void RecordContractsDoNotHideMeasurementAffectingDefaults()
        {
            AssertMethod(
                "SetQueries",
                typeof(void),
                typeof(GpuSensorRangeQuery[]));
            AssertMethod(
                "RecordGpuProduced",
                typeof(void),
                typeof(CommandBuffer),
                typeof(uint),
                typeof(uint),
                typeof(int),
                typeof(int));
            AssertMethod(
                "RecordValidate",
                typeof(void),
                typeof(CommandBuffer),
                typeof(uint),
                typeof(uint),
                typeof(int));
        }

        [Test]
        public void PackedWordCountCoversZeroEvenOddAndInvalidCounts()
        {
            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.That(
                    GpuSensorPackedSoaPipeline.PackedWordCount(0),
                    Is.EqualTo(0));
                Assert.That(
                    GpuSensorPackedSoaPipeline.PackedWordCount(16),
                    Is.EqualTo(8));
                Assert.That(
                    GpuSensorPackedSoaPipeline.PackedWordCount(17),
                    Is.EqualTo(9));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorPackedSoaPipeline.PackedWordCount(-1));
            });
        }

        [Test]
        public void EvenAndOddLogicalByteAccountingIsExact()
        {
            RequireComputeDevice();
            using (var pipeline = new GpuSensorPackedSoaPipeline(
                       17,
                       3,
                       GpuPrimitiveBackend.Portable,
                       false))
            {
                GpuSensorTestAssert.Multiple(() =>
                {
                    Assert.That(
                        pipeline.GpuProducerLogicalWriteBytes(16),
                        Is.EqualTo(128L));
                    Assert.That(
                        pipeline.GpuProducerLogicalWriteBytes(17),
                        Is.EqualTo(144L));
                    Assert.That(
                        pipeline.ReferenceExpandedProducerLogicalWriteBytes(
                            16),
                        Is.EqualTo(320L));
                    Assert.That(
                        pipeline.ReferenceExpandedProducerLogicalWriteBytes(
                            17),
                        Is.EqualTo(340L));
                    Assert.That(
                        pipeline.PipelineElementMaterializedWriteBytes(16),
                        Is.EqualTo(192L));
                    Assert.That(
                        pipeline.PipelineElementMaterializedWriteBytes(17),
                        Is.EqualTo(212L));
                    Assert.That(
                        pipeline
                            .ReferencePipelineElementMaterializedWriteBytes(
                                16),
                        Is.EqualTo(384L));
                    Assert.That(
                        pipeline
                            .ReferencePipelineElementMaterializedWriteBytes(
                                17),
                        Is.EqualTo(408L));
                    Assert.That(
                        pipeline.SpatialBuildAddressedReadBytes(16),
                        Is.EqualTo(96L));
                    Assert.That(
                        pipeline.SpatialBuildAddressedReadBytes(17),
                        Is.EqualTo(108L));
                    Assert.That(
                        pipeline.ReferenceSpatialBuildAddressedReadBytes(16),
                        Is.EqualTo(192L));
                    Assert.That(
                        pipeline.ReferenceSpatialBuildAddressedReadBytes(17),
                        Is.EqualTo(204L));
                    Assert.That(
                        pipeline.MaterializedKeyBytesAvoided(17),
                        Is.EqualTo(68L));
                    Assert.That(
                        pipeline.StableIdBytesAvoided(17),
                        Is.EqualTo(68L));
                    Assert.That(
                        pipeline.FusedCountAtomicOperations(17),
                        Is.EqualTo(17L));
                    Assert.That(
                        pipeline.ScatterReservationAtomicOperations(17),
                        Is.EqualTo(17L));
                    Assert.That(
                        GpuSensorPackedSoaPipeline.LogicalRangeQueryReadBytes(
                            20,
                            7),
                        Is.EqualTo(268L));
                    Assert.That(
                        GpuSensorPackedSoaPipeline
                            .ReferenceExpandedRangeQueryReadBytes(20),
                        Is.EqualTo(320L));
                });

                GpuSensorTestAssert.Multiple(() =>
                {
                    Assert.That(pipeline.PackedAttributeBytes, Is.EqualTo(144L));
                    Assert.That(pipeline.PackedDynamicBytes, Is.EqualTo(212L));
                    Assert.That(
                        pipeline.ReferenceExpandedDynamicBytes,
                        Is.EqualTo(476L));
                    Assert.That(
                        pipeline.DynamicBytesSavedAgainstReference,
                        Is.EqualTo(264L));
                    Assert.That(
                        pipeline.OwnedBufferBytes,
                        Is.EqualTo(
                            144L +
                            17L * sizeof(uint) +
                            (long)pipeline.BinCount * sizeof(uint) +
                            (long)(pipeline.BinCount + 1) * sizeof(uint) +
                            3L *
                                GpuSensorPackedSoaPipeline.DigestStride *
                                2L +
                            (long)GpuSensorPackedSoaPipeline
                                .FrameDigestCount *
                                GpuSensorPackedSoaPipeline.DigestStride +
                            (long)GpuSensorPackedSoaPipeline
                                .ValidationWordCount *
                                sizeof(uint)));
                    Assert.That(
                        pipeline.ResidentBytes,
                        Is.EqualTo(
                            pipeline.OwnedBufferBytes +
                            pipeline.PrimitiveScratchBytes));
                });
            }

            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorPackedSoaPipeline.LogicalRangeQueryReadBytes(
                        -1,
                        0));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorPackedSoaPipeline.LogicalRangeQueryReadBytes(
                        1,
                        2));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorPackedSoaPipeline
                        .ReferenceExpandedRangeQueryReadBytes(-1));
            });
        }

        [Test]
        public void BuffersExposeAuditableCountsStridesAndTargets()
        {
            RequireComputeDevice();
            const int elementCapacity = 17;
            const int queryCapacity = 3;
            using (var pipeline = new GpuSensorPackedSoaPipeline(
                       elementCapacity,
                       queryCapacity,
                       GpuPrimitiveBackend.Portable,
                       false))
            {
                GpuSensorTestAssert.Multiple(() =>
                {
                    Assert.That(
                        pipeline.ElementCapacity,
                        Is.EqualTo(elementCapacity));
                    Assert.That(
                        pipeline.QueryCapacity,
                        Is.EqualTo(queryCapacity));
                    Assert.That(pipeline.PackedWordCapacity, Is.EqualTo(9));
                    Assert.That(
                        pipeline.BinCount,
                        Is.EqualTo(
                            GpuSensorDeterministicGenerator.BinCount));
                    Assert.That(
                        pipeline.Backend,
                        Is.EqualTo(GpuPrimitiveBackend.Portable));
                    Assert.That(pipeline.EmitsProfilerMarkers, Is.False);
                });

                AssertStructuredBuffer(pipeline.PackedX, 9, sizeof(uint));
                AssertStructuredBuffer(pipeline.PackedY, 9, sizeof(uint));
                AssertStructuredBuffer(pipeline.PackedZ, 9, sizeof(uint));
                AssertStructuredBuffer(
                    pipeline.PackedIntensity,
                    9,
                    sizeof(uint));
                AssertStructuredBuffer(
                    pipeline.BinCounts,
                    pipeline.BinCount,
                    sizeof(uint));
                AssertStructuredBuffer(
                    pipeline.BinOffsets,
                    pipeline.BinCount + 1,
                    sizeof(uint));
                AssertStructuredBuffer(
                    pipeline.BinnedIds,
                    elementCapacity,
                    sizeof(uint));
                Assert.That(
                    typeof(GpuSensorPackedSoaPipeline).GetProperty(
                        "WriteHeads",
                        BindingFlags.Instance | BindingFlags.Public),
                    Is.Null);
                AssertStructuredBuffer(
                    pipeline.Queries,
                    queryCapacity,
                    GpuSensorPackedSoaPipeline.DigestStride);
                AssertStructuredBuffer(
                    pipeline.QueryDigests,
                    queryCapacity,
                    GpuSensorPackedSoaPipeline.DigestStride);
                AssertStructuredBuffer(
                    pipeline.FrameDigest,
                    GpuSensorPackedSoaPipeline.FrameDigestCount,
                    GpuSensorPackedSoaPipeline.DigestStride,
                    GraphicsBuffer.Target.CopySource);
                AssertStructuredBuffer(
                    pipeline.ValidationDiagnostics,
                    GpuSensorPackedSoaPipeline.ValidationWordCount,
                    sizeof(uint));
            }
        }

        [Test]
        public void WaveBackendRunsWhenSupportedAndOtherwiseFailsClosed()
        {
            RequireComputeDevice();
            if (!GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    new GpuSensorPackedSoaPipeline(
                        1,
                        1,
                        GpuPrimitiveBackend.WaveOps,
                        false));
                return;
            }

            using (var pipeline = new GpuSensorPackedSoaPipeline(
                       1,
                       1,
                       GpuPrimitiveBackend.WaveOps,
                       false))
            {
                Assert.That(
                    pipeline.Backend,
                    Is.EqualTo(GpuPrimitiveBackend.WaveOps));
            }
        }

        [Test]
        public void DisposeIsIdempotentAndMutatingEntryPointsFailAfterward()
        {
            RequireComputeDevice();
            var pipeline = new GpuSensorPackedSoaPipeline(
                1,
                1,
                GpuPrimitiveBackend.Portable,
                false);
            pipeline.Dispose();
            Assert.DoesNotThrow(pipeline.Dispose);

            using (var commands = new CommandBuffer())
            {
                GpuSensorTestAssert.Multiple(() =>
                {
                    Assert.Throws<ObjectDisposedException>(() =>
                        pipeline.SetQueries(new[]
                        {
                            FullDomainQuery(),
                        }));
                    Assert.Throws<ObjectDisposedException>(() =>
                        pipeline.RecordGpuProduced(
                            commands,
                            1u,
                            0u,
                            1,
                            1));
                    Assert.Throws<ObjectDisposedException>(() =>
                        pipeline.RecordValidate(
                            commands,
                            1u,
                            0u,
                            1));
                });
            }
        }

        private static void AssertStructuredBuffer(
            GraphicsBuffer buffer,
            int count,
            int stride,
            GraphicsBuffer.Target additionalTarget =
                (GraphicsBuffer.Target)0)
        {
            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.That(
                    buffer.target & GraphicsBuffer.Target.Structured,
                    Is.EqualTo(GraphicsBuffer.Target.Structured));
                Assert.That(
                    buffer.target & additionalTarget,
                    Is.EqualTo(additionalTarget));
                Assert.That(buffer.count, Is.EqualTo(count));
                Assert.That(buffer.stride, Is.EqualTo(stride));
            });
        }

        private static void AssertMethod(
            string name,
            Type returnType,
            params Type[] parameterTypes)
        {
            MethodInfo method = typeof(GpuSensorPackedSoaPipeline).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                parameterTypes,
                null);
            Assert.That(method, Is.Not.Null, $"Missing public {name}.");
            Assert.That(method.ReturnType, Is.EqualTo(returnType));
            Assert.That(
                method.GetParameters().All(parameter => !parameter.IsOptional),
                Is.True,
                $"{name} must not conceal measurement-affecting defaults.");
        }

        private static void RequireComputeDevice()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore(
                    "A graphics-capable compute device is required for " +
                    "GPU resource contract tests.");
            }
        }

        private static GpuSensorRangeQuery FullDomainQuery()
        {
            return new GpuSensorRangeQuery(0u, 0u, 0u, 65535u);
        }
    }
}
