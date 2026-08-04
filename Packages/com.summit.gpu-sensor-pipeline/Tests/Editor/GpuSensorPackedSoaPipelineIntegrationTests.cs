using System;
using NUnit.Framework;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorPackedSoaPipelineIntegrationTests
    {
        [SetUp]
        public void RequireComputeDevice()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore(
                    "A graphics-capable compute device is required for " +
                    "GPU integration tests.");
            }
        }

        [TestCase(256, GpuPrimitiveBackend.Portable)]
        [TestCase(257, GpuPrimitiveBackend.Portable)]
        [TestCase(256, GpuPrimitiveBackend.WaveOps)]
        [TestCase(257, GpuPrimitiveBackend.WaveOps)]
        public void PackedAndExpandedQuantizedPathsHaveExactDigests(
            int elementCount,
            GpuPrimitiveBackend backend)
        {
            RequireBackend(backend);
            const uint seed = 0xA341316Cu;
            const uint logicalState = 37u;
            GpuSensorSample[] samples =
                CreateQuantizedSamples(
                    elementCount,
                    seed,
                    logicalState);
            GpuSensorRangeQuery[] queries =
                CreateRepresentativeQueries(samples);
            GpuSensorQueryDigest[] expectedQueries =
                GpuSensorPipelineTestOracle.QueryAll(
                    samples,
                    elementCount,
                    queries);
            GpuSensorQueryDigest expectedFrame =
                GpuSensorPipelineTestOracle.Frame(
                    expectedQueries,
                    logicalState);

            using (GpuSensorPipeline expanded =
                   CreateExpandedPipeline(
                       elementCount,
                       queries,
                       backend))
            using (GpuSensorPackedSoaPipeline packed =
                   CreatePackedPipeline(
                       elementCount,
                       queries,
                       backend))
            {
                uint[] diagnosticSentinel =
                {
                    0x13579BDFu,
                    0x2468ACE0u,
                };
                expanded.Diagnostics.SetData(diagnosticSentinel);
                using (var commands = new CommandBuffer
                       {
                           name =
                               "Test/SensorPipeline/S5/ExpandedQuantized",
                       })
                {
                    expanded.RecordGpuProducedQuantizedView(
                        commands,
                        seed,
                        logicalState,
                        elementCount,
                        queries.Length);
                    Execute(commands);
                }

                uint[] expandedDiagnostics = ReadBuffer<uint>(
                    expanded.Diagnostics,
                    diagnosticSentinel.Length);
                GpuSensorQueryDigest[] expandedQueries =
                    ReadBuffer<GpuSensorQueryDigest>(
                        expanded.QueryDigests,
                        queries.Length);
                GpuSensorQueryDigest expandedFrame =
                    ReadBuffer<GpuSensorQueryDigest>(
                        expanded.FrameDigest,
                        GpuSensorPipeline.FrameDigestCount)[logicalState];

                using (var commands = new CommandBuffer
                       {
                           name =
                               "Test/SensorPipeline/S5/PackedQuantized",
                       })
                {
                    packed.RecordGpuProduced(
                        commands,
                        seed,
                        logicalState,
                        elementCount,
                        queries.Length);
                    Execute(commands);
                }

                GpuSensorQueryDigest[] packedQueries =
                    ReadBuffer<GpuSensorQueryDigest>(
                        packed.QueryDigests,
                        queries.Length);
                GpuSensorQueryDigest packedFrame =
                    ReadBuffer<GpuSensorQueryDigest>(
                        packed.FrameDigest,
                        GpuSensorPackedSoaPipeline.FrameDigestCount)[
                            logicalState];

                GpuSensorTestAssert.Multiple(() =>
                {
                    Assert.That(
                        expandedDiagnostics,
                        Is.EqualTo(diagnosticSentinel));
                    Assert.That(
                        expandedQueries,
                        Is.EqualTo(expectedQueries));
                    Assert.That(
                        packedQueries,
                        Is.EqualTo(expectedQueries));
                    Assert.That(
                        packedQueries,
                        Is.EqualTo(expandedQueries));
                    Assert.That(expandedFrame, Is.EqualTo(expectedFrame));
                    Assert.That(packedFrame, Is.EqualTo(expectedFrame));
                    Assert.That(packedFrame, Is.EqualTo(expandedFrame));
                });
            }
        }

        [TestCase(256, GpuPrimitiveBackend.Portable)]
        [TestCase(257, GpuPrimitiveBackend.Portable)]
        [TestCase(256, GpuPrimitiveBackend.WaveOps)]
        [TestCase(257, GpuPrimitiveBackend.WaveOps)]
        public void ValidationDiagnosticsProvePackedDataAndCsrPermutation(
            int elementCount,
            GpuPrimitiveBackend backend)
        {
            RequireBackend(backend);
            const uint seed = 0xC8013EA4u;
            const uint logicalState = 63u;
            GpuSensorRangeQuery[] queries =
            {
                FullDomainQuery(),
            };
            uint[] expected = CreateValidationOracle(elementCount);

            using (GpuSensorPackedSoaPipeline pipeline =
                   CreatePackedPipeline(
                       elementCount,
                       queries,
                       backend))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/SensorPipeline/S5/ValidatePackedAndCsr",
                   })
            {
                pipeline.RecordGpuProduced(
                    commands,
                    seed,
                    logicalState,
                    elementCount,
                    queries.Length);
                pipeline.RecordValidate(
                    commands,
                    seed,
                    logicalState,
                    elementCount);
                Execute(commands);

                uint[] actual = ReadBuffer<uint>(
                    pipeline.ValidationDiagnostics,
                    GpuSensorPackedSoaPipeline.ValidationWordCount);
                GpuSensorTestAssert.Multiple(() =>
                {
                    Assert.That(
                        actual[
                            GpuSensorPackedSoaPipeline
                                .PackedSampleMismatchCountWord],
                        Is.Zero);
                    Assert.That(
                        actual[
                            GpuSensorPackedSoaPipeline
                                .PackedSampleMismatchHashWord],
                        Is.Zero);
                    Assert.That(
                        actual[
                            GpuSensorPackedSoaPipeline
                                .CsrMismatchCountWord],
                        Is.Zero);
                    Assert.That(
                        actual[
                            GpuSensorPackedSoaPipeline
                                .CsrMismatchHashWord],
                        Is.Zero);
                    Assert.That(actual, Is.EqualTo(expected));
                });
            }
        }

        private static GpuSensorPipeline CreateExpandedPipeline(
            int elementCapacity,
            GpuSensorRangeQuery[] queries,
            GpuPrimitiveBackend backend)
        {
            var pipeline = new GpuSensorPipeline(
                elementCapacity,
                queries.Length,
                backend,
                false);
            try
            {
                pipeline.SetStableIds(CreateIdentity(elementCapacity));
                pipeline.SetQueries(queries);
                return pipeline;
            }
            catch
            {
                pipeline.Dispose();
                throw;
            }
        }

        private static GpuSensorPackedSoaPipeline CreatePackedPipeline(
            int elementCapacity,
            GpuSensorRangeQuery[] queries,
            GpuPrimitiveBackend backend)
        {
            var pipeline = new GpuSensorPackedSoaPipeline(
                elementCapacity,
                queries.Length,
                backend,
                false);
            try
            {
                pipeline.SetQueries(queries);
                return pipeline;
            }
            catch
            {
                pipeline.Dispose();
                throw;
            }
        }

        private static GpuSensorSample[] CreateQuantizedSamples(
            int elementCount,
            uint seed,
            uint logicalState)
        {
            var samples = new GpuSensorSample[elementCount];
            var keys = new uint[elementCount];
            GpuSensorDeterministicGenerator.Populate(
                samples,
                keys,
                seed,
                logicalState);
            for (var index = 0; index < samples.Length; index++)
            {
                GpuSensorSample sample = samples[index];
                samples[index] = new GpuSensorSample(
                    sample.X,
                    sample.Y,
                    sample.Z,
                    GpuSensorIntensityQuantizer.QuantizeToUInt(
                        sample.Payload));
            }
            return samples;
        }

        private static GpuSensorRangeQuery[] CreateRepresentativeQueries(
            GpuSensorSample[] samples)
        {
            GpuSensorSample first = samples[0];
            GpuSensorSample middle = samples[samples.Length / 2];
            GpuSensorSample last = samples[samples.Length - 1];
            return new[]
            {
                FullDomainQuery(),
                new GpuSensorRangeQuery(
                    first.X,
                    first.Y,
                    first.Z,
                    0u),
                new GpuSensorRangeQuery(
                    middle.X,
                    middle.Y,
                    middle.Z,
                    1024u),
                new GpuSensorRangeQuery(
                    last.X,
                    last.Y,
                    last.Z,
                    3u),
                new GpuSensorRangeQuery(
                    32768u,
                    32768u,
                    32768u,
                    4096u),
                new GpuSensorRangeQuery(0u, 0u, 0u, 4096u),
                new GpuSensorRangeQuery(
                    65535u,
                    65535u,
                    65535u,
                    4096u),
            };
        }

        private static uint[] CreateIdentity(int count)
        {
            var identity = new uint[count];
            for (var index = 0; index < count; index++)
            {
                identity[index] = unchecked((uint)index);
            }
            return identity;
        }

        private static uint[] CreateValidationOracle(int elementCount)
        {
            uint idXor = 0u;
            uint idSum = 0u;
            uint mixedSum = 0u;
            unchecked
            {
                for (uint id = 0u; id < (uint)elementCount; id++)
                {
                    idXor ^= id;
                    idSum += id;
                    mixedSum += GpuSensorPipelineTestOracle.Mix(
                        id ^ 0x27D4EB2Fu);
                }
            }

            var expected = new uint[
                GpuSensorPackedSoaPipeline.ValidationWordCount];
            expected[
                GpuSensorPackedSoaPipeline.CsrElementCountWord] =
                unchecked((uint)elementCount);
            expected[
                GpuSensorPackedSoaPipeline.CsrIdXorWord] = idXor;
            expected[
                GpuSensorPackedSoaPipeline.CsrIdSumWord] = idSum;
            expected[
                GpuSensorPackedSoaPipeline.CsrIdMixedSumWord] = mixedSum;
            return expected;
        }

        private static void RequireBackend(GpuPrimitiveBackend backend)
        {
            if (backend == GpuPrimitiveBackend.WaveOps &&
                !GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Ignore(
                    "The active graphics device does not support the " +
                    "explicit WaveOps backend.");
            }
        }

        private static GpuSensorRangeQuery FullDomainQuery()
        {
            return new GpuSensorRangeQuery(0u, 0u, 0u, 65535u);
        }

        private static T[] ReadBuffer<T>(
            GraphicsBuffer buffer,
            int count)
            where T : struct
        {
            var output = new T[count];
            buffer.GetData(output);
            return output;
        }

        private static void Execute(CommandBuffer commands)
        {
            Graphics.ExecuteCommandBuffer(commands);
        }
    }
}
