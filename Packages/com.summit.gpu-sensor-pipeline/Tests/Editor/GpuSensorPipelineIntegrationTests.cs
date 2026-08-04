using System;
using System.Linq;
using NUnit.Framework;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorPipelineIntegrationTests
    {
        [SetUp]
        public void RequireComputeDevice()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore(
                    "A graphics-capable compute device is required for GPU integration tests.");
            }
        }

        [Test]
        public void CpuAndGpuProducersAreBitExactThroughTheSharedConsumer()
        {
            const int elementCount = 1025;
            const uint seed = 0x9e3779b9u;
            const uint logicalState = 37u;
            GpuSensorRangeQuery[] queries = CreateRepresentativeQueries();
            var samples = new GpuSensorSample[elementCount];
            var keys = new uint[elementCount];
            GpuSensorDeterministicGenerator.Populate(
                samples,
                keys,
                seed,
                logicalState);
            GpuSensorQueryDigest[] oracleDigests =
                GpuSensorPipelineTestOracle.QueryAll(
                    samples,
                    elementCount,
                    queries);
            GpuSensorQueryDigest oracleFrame =
                GpuSensorPipelineTestOracle.Frame(
                    oracleDigests,
                    logicalState);

            using (var pipeline = CreatePipeline(
                       elementCount,
                       queries,
                       GpuPrimitiveBackend.Portable))
            {
                using (var cpuCommands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/CpuProduced",
                       })
                {
                    pipeline.RecordCpuProduced(
                        cpuCommands,
                        samples,
                        keys,
                        elementCount,
                        queries.Length,
                        logicalState);
                    Execute(cpuCommands);
                }

                GpuSensorSample[] cpuSamples = ReadBuffer<GpuSensorSample>(
                    pipeline.Samples,
                    elementCount);
                uint[] cpuKeys = ReadBuffer<uint>(pipeline.Keys, elementCount);
                GpuSensorQueryDigest[] cpuDigests =
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.QueryDigests,
                        queries.Length);
                Assert.That(cpuSamples, Is.EqualTo(samples));
                Assert.That(cpuKeys, Is.EqualTo(keys));
                Assert.That(cpuDigests, Is.EqualTo(oracleDigests));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.FrameDigest,
                        GpuSensorDeterministicGenerator.StateCount)[logicalState],
                    Is.EqualTo(oracleFrame));
                Assert.That(
                    ReadBuffer<uint>(pipeline.Diagnostics, 2),
                    Is.EqualTo(new uint[2]));

                using (var cpuDigestBuffer = CreateDigestBuffer(cpuDigests))
                using (var gpuCommands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/GpuProduced",
                       })
                {
                    pipeline.RecordGpuProduced(
                        gpuCommands,
                        seed,
                        logicalState,
                        elementCount,
                        queries.Length);
                    pipeline.RecordClearComparison(gpuCommands);
                    pipeline.RecordCompareDigests(
                        gpuCommands,
                        cpuDigestBuffer,
                        pipeline.QueryDigests,
                        queries.Length);
                    Execute(gpuCommands);
                }

                Assert.That(
                    ReadBuffer<GpuSensorSample>(pipeline.Samples, elementCount),
                    Is.EqualTo(samples));
                Assert.That(
                    ReadBuffer<uint>(pipeline.Keys, elementCount),
                    Is.EqualTo(keys));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.QueryDigests,
                        queries.Length),
                    Is.EqualTo(oracleDigests));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(pipeline.ComparisonDigest, 1)[0],
                    Is.EqualTo(default(GpuSensorQueryDigest)));

                using (var validationCommands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/ValidateGeneratedKeys",
                       })
                {
                    pipeline.RecordValidateKeys(
                        validationCommands,
                        elementCount);
                    Execute(validationCommands);
                }
                Assert.That(
                    ReadBuffer<uint>(
                        pipeline.KeyValidationDiagnostics,
                        GpuSensorPipeline.KeyValidationWordCount),
                    Is.EqualTo(new uint[GpuSensorPipeline.KeyValidationWordCount]));
            }
        }

        [Test]
        public void SharedMultiSensorIndexMatchesPerSensorRebuildsAndCpuOracle()
        {
            const int elementCount = 4097;
            const int sensorCount = 4;
            const int queriesPerSensor = 3;
            const uint seed = 0x51ed270bu;
            const uint logicalState = 23u;
            GpuSensorRangeQuery[] queries = new GpuSensorRangeQuery[
                sensorCount * queriesPerSensor];
            for (int index = 0; index < queries.Length; index++)
            {
                uint ordinal = unchecked((uint)index);
                queries[index] = new GpuSensorRangeQuery(
                    (ordinal * 7919u + 17u) & 65535u,
                    (ordinal * 3571u + 29u) & 65535u,
                    (ordinal * 1543u + 43u) & 65535u,
                    3071u);
            }

            var samples = new GpuSensorSample[elementCount];
            var keys = new uint[elementCount];
            GpuSensorDeterministicGenerator.Populate(
                samples,
                keys,
                seed,
                logicalState);
            GpuSensorQueryDigest[] oracleDigests =
                GpuSensorPipelineTestOracle.QueryAll(
                    samples,
                    elementCount,
                    queries);
            GpuSensorQueryDigest oracleFrame =
                GpuSensorPipelineTestOracle.Frame(
                    oracleDigests,
                    logicalState);

            using (var pipeline = CreatePipeline(
                       elementCount,
                       queries,
                       GpuPrimitiveBackend.Portable))
            {
                GpuSensorQueryDigest[] rebuiltDigests;
                GpuSensorQueryDigest rebuiltFrame;
                using (var rebuiltCommands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/MultiSensorRebuilt",
                       })
                {
                    pipeline.RecordGpuProducedRebuiltPerSensor(
                        rebuiltCommands,
                        seed,
                        logicalState,
                        elementCount,
                        sensorCount,
                        queriesPerSensor);
                    Execute(rebuiltCommands);
                }
                rebuiltDigests = ReadBuffer<GpuSensorQueryDigest>(
                    pipeline.QueryDigests,
                    queries.Length);
                rebuiltFrame = ReadBuffer<GpuSensorQueryDigest>(
                    pipeline.FrameDigest,
                    GpuSensorDeterministicGenerator.StateCount)[logicalState];

                using (var sharedCommands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/MultiSensorShared",
                       })
                {
                    pipeline.RecordGpuProducedSharedSensorIndex(
                        sharedCommands,
                        seed,
                        logicalState,
                        elementCount,
                        sensorCount,
                        queriesPerSensor);
                    Execute(sharedCommands);
                }

                Assert.That(rebuiltDigests, Is.EqualTo(oracleDigests));
                Assert.That(rebuiltFrame, Is.EqualTo(oracleFrame));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.QueryDigests,
                        queries.Length),
                    Is.EqualTo(rebuiltDigests));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.FrameDigest,
                        GpuSensorDeterministicGenerator.StateCount)[logicalState],
                    Is.EqualTo(rebuiltFrame));
                Assert.That(
                    ReadBuffer<uint>(pipeline.Diagnostics, 2),
                    Is.EqualTo(new uint[2]));
            }
        }

        [Test]
        public void CpuUploadsAndConsumersRemainOrderedInsideOneCommandBuffer()
        {
            const int elementCount = 257;
            const uint firstSeed = 0x11111111u;
            const uint secondSeed = 0xeeeeeeeeu;
            const uint firstState = 7u;
            const uint secondState = 41u;
            GpuSensorRangeQuery[] queries = { FullDomainQuery() };
            var firstSamples = new GpuSensorSample[elementCount];
            var firstKeys = new uint[elementCount];
            var secondSamples = new GpuSensorSample[elementCount];
            var secondKeys = new uint[elementCount];
            GpuSensorDeterministicGenerator.Populate(
                firstSamples,
                firstKeys,
                firstSeed,
                firstState);
            GpuSensorDeterministicGenerator.Populate(
                secondSamples,
                secondKeys,
                secondSeed,
                secondState);
            GpuSensorQueryDigest[] expected =
                GpuSensorPipelineTestOracle.QueryAll(
                    secondSamples,
                    elementCount,
                    queries);

            using (var pipeline = CreatePipeline(
                       elementCount,
                       queries,
                       GpuPrimitiveBackend.Portable))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/SensorPipeline/CpuUploadOrdering",
                   })
            {
                pipeline.RecordCpuProduced(
                    commands,
                    firstSamples,
                    firstKeys,
                    elementCount,
                    queries.Length,
                    firstState);
                pipeline.RecordCpuProduced(
                    commands,
                    secondSamples,
                    secondKeys,
                    elementCount,
                    queries.Length,
                    secondState);
                Execute(commands);

                Assert.That(
                    ReadBuffer<GpuSensorSample>(pipeline.Samples, elementCount),
                    Is.EqualTo(secondSamples));
                Assert.That(
                    ReadBuffer<uint>(pipeline.Keys, elementCount),
                    Is.EqualTo(secondKeys));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(pipeline.QueryDigests, 1),
                    Is.EqualTo(expected));
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.FrameDigest,
                        GpuSensorDeterministicGenerator.StateCount)[secondState],
                    Is.EqualTo(
                        GpuSensorPipelineTestOracle.Frame(
                            expected,
                            secondState)));
            }
        }

        [Test]
        public void ValidationPassFindsInvalidKeysWithoutInvokingTrustedCsr()
        {
            const int elementCount = 9;
            var keys = Enumerable.Range(0, elementCount)
                .Select(index => (uint)index)
                .ToArray();
            keys[1] = (uint)GpuSensorPipeline.FixedBinCount;
            keys[4] = uint.MaxValue;
            keys[8] = (uint)GpuSensorPipeline.FixedBinCount + 17u;
            uint expectedHash =
                GpuSensorPipelineTestOracle.InvalidKeyHash(1u, keys[1]) ^
                GpuSensorPipelineTestOracle.InvalidKeyHash(4u, keys[4]) ^
                GpuSensorPipelineTestOracle.InvalidKeyHash(8u, keys[8]);

            using (var pipeline = new GpuSensorPipeline(
                       elementCount,
                       1,
                       GpuPrimitiveBackend.Portable,
                       false))
            {
                pipeline.Keys.SetData(keys);
                using (var commands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/InvalidKeyValidation",
                       })
                {
                    pipeline.RecordValidateKeys(commands, elementCount);
                    Execute(commands);
                }

                Assert.That(
                    ReadBuffer<uint>(pipeline.KeyValidationDiagnostics, 2),
                    Is.EqualTo(new[] { 3u, expectedHash }));

                for (var index = 0; index < elementCount; index++)
                {
                    keys[index] = (uint)index;
                }
                pipeline.Keys.SetData(keys);
                using (var commands = new CommandBuffer
                       {
                           name = "Test/SensorPipeline/ValidKeyValidation",
                       })
                {
                    pipeline.RecordValidateKeys(commands, elementCount);
                    Execute(commands);
                }

                Assert.That(
                    ReadBuffer<uint>(pipeline.KeyValidationDiagnostics, 2),
                    Is.EqualTo(new uint[2]));
            }
        }

        [Test]
        public void StateDigestRingIsStableAcrossZeroOneZeroSequence()
        {
            const int elementCount = 257;
            const uint seed = 0x12345678u;
            GpuSensorRangeQuery[] queries = { FullDomainQuery() };
            var stateZeroSamples = new GpuSensorSample[elementCount];
            var stateZeroKeys = new uint[elementCount];
            var stateOneSamples = new GpuSensorSample[elementCount];
            var stateOneKeys = new uint[elementCount];
            GpuSensorDeterministicGenerator.Populate(
                stateZeroSamples,
                stateZeroKeys,
                seed,
                0u);
            GpuSensorDeterministicGenerator.Populate(
                stateOneSamples,
                stateOneKeys,
                seed,
                1u);
            GpuSensorQueryDigest expectedZero =
                GpuSensorPipelineTestOracle.Frame(
                    GpuSensorPipelineTestOracle.QueryAll(
                        stateZeroSamples,
                        elementCount,
                        queries),
                    0u);
            GpuSensorQueryDigest expectedOne =
                GpuSensorPipelineTestOracle.Frame(
                    GpuSensorPipelineTestOracle.QueryAll(
                        stateOneSamples,
                        elementCount,
                        queries),
                    1u);

            using (var pipeline = CreatePipeline(
                       elementCount,
                       queries,
                       GpuPrimitiveBackend.Portable))
            {
                RecordGpuAndExecute(pipeline, seed, 0u, elementCount, 1);
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.FrameDigest,
                        GpuSensorDeterministicGenerator.StateCount)[0],
                    Is.EqualTo(expectedZero));

                RecordGpuAndExecute(pipeline, seed, 1u, elementCount, 1);
                GpuSensorQueryDigest[] afterOne =
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.FrameDigest,
                        GpuSensorDeterministicGenerator.StateCount);
                Assert.That(afterOne[0], Is.EqualTo(expectedZero));
                Assert.That(afterOne[1], Is.EqualTo(expectedOne));

                RecordGpuAndExecute(pipeline, seed, 0u, elementCount, 1);
                GpuSensorQueryDigest[] afterRepeatedZero =
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.FrameDigest,
                        GpuSensorDeterministicGenerator.StateCount);
                Assert.That(afterRepeatedZero[0], Is.EqualTo(expectedZero));
                Assert.That(afterRepeatedZero[1], Is.EqualTo(expectedOne));

                using (var commands = new CommandBuffer())
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() =>
                        pipeline.RecordGpuProduced(
                            commands,
                            seed,
                            GpuSensorDeterministicGenerator.StateCount,
                            elementCount,
                            1));
                }
            }
        }

        [Test]
        public void EdgeQueriesMatchIndependentCpuOracle()
        {
            GpuSensorSample[] samples =
            {
                new GpuSensorSample(0u, 0u, 0u, 11u),
                new GpuSensorSample(1u, 1u, 1u, 22u),
                new GpuSensorSample(1023u, 1023u, 1023u, 33u),
                new GpuSensorSample(1024u, 1024u, 1024u, 44u),
                new GpuSensorSample(32768u, 32768u, 32768u, 55u),
                new GpuSensorSample(65534u, 65534u, 65534u, 66u),
                new GpuSensorSample(65535u, 65535u, 65535u, 77u),
                new GpuSensorSample(0u, 65535u, 32768u, 88u),
            };
            uint[] keys = samples
                .Select(GpuSensorDeterministicGenerator.ComputeKey)
                .ToArray();
            GpuSensorRangeQuery[] queries =
            {
                new GpuSensorRangeQuery(0u, 0u, 0u, 0u),
                new GpuSensorRangeQuery(0u, 0u, 0u, 1u),
                new GpuSensorRangeQuery(1023u, 1023u, 1023u, 0u),
                new GpuSensorRangeQuery(1024u, 1024u, 1024u, 0u),
                new GpuSensorRangeQuery(1024u, 1024u, 1024u, 1u),
                new GpuSensorRangeQuery(65535u, 65535u, 65535u, 1u),
                new GpuSensorRangeQuery(32768u, 32768u, 32768u, 0u),
                FullDomainQuery(),
            };
            GpuSensorQueryDigest[] expected =
                GpuSensorPipelineTestOracle.QueryAll(
                    samples,
                    samples.Length,
                    queries);

            Assert.That(expected[0].Count, Is.EqualTo(1u));
            Assert.That(expected[1].Count, Is.EqualTo(2u));
            Assert.That(expected[4].Count, Is.EqualTo(2u));
            Assert.That(expected[5].Count, Is.EqualTo(2u));
            Assert.That(expected[7].Count, Is.EqualTo((uint)samples.Length));

            using (var pipeline = CreatePipeline(
                       samples.Length,
                       queries,
                       GpuPrimitiveBackend.Portable))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/SensorPipeline/EdgeQueries",
                   })
            {
                pipeline.RecordCpuProduced(
                    commands,
                    samples,
                    keys,
                    samples.Length,
                    queries.Length,
                    0u);
                Execute(commands);
                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(
                        pipeline.QueryDigests,
                        queries.Length),
                    Is.EqualTo(expected));
            }
        }

        [Test]
        public void ComparisonSupportsTheFullSixtyFourStateDigestRing()
        {
            var expected = new GpuSensorQueryDigest[
                GpuSensorDeterministicGenerator.StateCount];
            var actual = new GpuSensorQueryDigest[expected.Length];
            for (var index = 0; index < expected.Length; index++)
            {
                expected[index] = new GpuSensorQueryDigest(
                    (uint)index,
                    (uint)(index * 3),
                    (uint)(index * 5),
                    (uint)(index * 7));
                actual[index] = expected[index];
            }
            actual[0] = new GpuSensorQueryDigest(9u, 8u, 7u, 6u);
            actual[63] = new GpuSensorQueryDigest(1u, 2u, 3u, 4u);
            GpuSensorQueryDigest oracle =
                GpuSensorPipelineTestOracle.Comparison(expected, actual);

            using (var pipeline = new GpuSensorPipeline(
                       1,
                       1,
                       GpuPrimitiveBackend.Portable,
                       false))
            using (var expectedBuffer = CreateDigestBuffer(expected))
            using (var actualBuffer = CreateDigestBuffer(actual))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/SensorPipeline/CompareStateRing",
                   })
            {
                Assert.That(pipeline.FrameDigest.count, Is.EqualTo(
                    GpuSensorDeterministicGenerator.StateCount));
                pipeline.RecordClearComparison(commands);
                pipeline.RecordCompareDigests(
                    commands,
                    expectedBuffer,
                    actualBuffer,
                    expected.Length);
                Execute(commands);

                Assert.That(
                    ReadBuffer<GpuSensorQueryDigest>(pipeline.ComparisonDigest, 1)[0],
                    Is.EqualTo(oracle));
            }
        }

        [Test]
        public void FixedResourcesAndLogicalByteAccountingAreObservable()
        {
            const int elementCapacity = 17;
            const int queryCapacity = 3;
            using (var pipeline = new GpuSensorPipeline(
                       elementCapacity,
                       queryCapacity,
                       GpuPrimitiveBackend.Portable,
                       false))
            {
                Assert.That(pipeline.ElementCapacity, Is.EqualTo(elementCapacity));
                Assert.That(pipeline.QueryCapacity, Is.EqualTo(queryCapacity));
                Assert.That(pipeline.BinCount, Is.EqualTo(
                    GpuSensorDeterministicGenerator.BinCount));
                Assert.That(pipeline.Backend, Is.EqualTo(GpuPrimitiveBackend.Portable));
                Assert.That(pipeline.EmitsProfilerMarkers, Is.False);
                AssertBuffer(pipeline.Samples, elementCapacity, 16);
                AssertBuffer(pipeline.Keys, elementCapacity, sizeof(uint));
                AssertBuffer(pipeline.StableIds, elementCapacity, sizeof(uint));
                AssertBuffer(pipeline.BinnedIds, elementCapacity, sizeof(uint));
                AssertBuffer(pipeline.BinCounts, pipeline.BinCount, sizeof(uint));
                AssertBuffer(pipeline.BinOffsets, pipeline.BinCount + 1, sizeof(uint));
                AssertBuffer(pipeline.Queries, queryCapacity, 16);
                AssertBuffer(pipeline.QueryDigests, queryCapacity, 16);
                AssertBuffer(
                    pipeline.FrameDigest,
                    GpuSensorDeterministicGenerator.StateCount,
                    16);
                Assert.That(
                    pipeline.FrameDigest.target &
                    GraphicsBuffer.Target.CopySource,
                    Is.EqualTo(GraphicsBuffer.Target.CopySource));
                Assert.That(pipeline.CpuUploadLogicalBytes(17), Is.EqualTo(340L));
                Assert.That(
                    pipeline.GpuProducerLogicalWriteBytes(17),
                    Is.EqualTo(340L));
                Assert.That(
                    pipeline.RebuiltPerSensorProducerLogicalWriteBytes(17, 4),
                    Is.EqualTo(1360L));
                Assert.That(
                    pipeline.IndependentSensorSpatialIndexBytes(4),
                    Is.EqualTo(pipeline.SpatialIndexResidentBytes * 4L));
                Assert.That(
                    pipeline.SpatialIndexResidentBytes,
                    Is.GreaterThan(0L));
                Assert.That(pipeline.OwnedBufferBytes, Is.GreaterThan(0L));
                Assert.That(
                    pipeline.ResidentBytes,
                    Is.EqualTo(pipeline.OwnedBufferBytes + pipeline.BinnerScratchBytes));
            }
        }

        [Test]
        public void RecordingFailsClosedUntilInputsAndBoundsAreValid()
        {
            const int elementCapacity = 4;
            using (var pipeline = new GpuSensorPipeline(
                       elementCapacity,
                       2,
                       GpuPrimitiveBackend.Portable,
                       false))
            using (var commands = new CommandBuffer())
            {
                var samples = new GpuSensorSample[elementCapacity + 1];
                var keys = new uint[elementCapacity + 1];
                Assert.Throws<InvalidOperationException>(() =>
                    pipeline.RecordGpuProduced(commands, 1u, 0u, 1, 1));
                Assert.Throws<ArgumentNullException>(() =>
                    pipeline.SetStableIds(null));
                Assert.Throws<ArgumentException>(() =>
                    pipeline.SetStableIds(new uint[elementCapacity - 1]));
                Assert.Throws<ArgumentException>(() =>
                    pipeline.SetStableIds(new[] { 0u, 1u, 3u, 2u }));

                pipeline.SetStableIds(CreateIdentity(elementCapacity));
                Assert.Throws<InvalidOperationException>(() =>
                    pipeline.RecordGpuProduced(commands, 1u, 0u, 1, 1));
                Assert.Throws<ArgumentNullException>(() => pipeline.SetQueries(null));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.SetQueries(Array.Empty<GpuSensorRangeQuery>()));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.SetQueries(new[]
                    {
                        new GpuSensorRangeQuery(65536u, 0u, 0u, 0u),
                    }));

                pipeline.SetQueries(new[]
                {
                    FullDomainQuery(),
                    new GpuSensorRangeQuery(0u, 0u, 0u, 0u),
                });
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordCpuProduced(commands, samples, keys, 0, 1, 0u));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordCpuProduced(commands, samples, keys, 5, 1, 0u));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordCpuProduced(commands, samples, keys, 1, 0, 0u));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordCpuProduced(commands, samples, keys, 1, 3, 0u));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordGpuProduced(commands, 1u, 64u, 1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordGpuProducedSharedSensorIndex(
                        commands,
                        1u,
                        0u,
                        1,
                        1,
                        1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordGpuProducedSharedSensorIndex(
                        commands,
                        1u,
                        0u,
                        1,
                        2,
                        2));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordValidateKeys(commands, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    pipeline.RecordValidateKeys(commands, 5));
                Assert.Throws<ArgumentNullException>(() =>
                    pipeline.RecordCpuProduced(commands, null, keys, 1, 1, 0u));
                Assert.Throws<ArgumentNullException>(() =>
                    pipeline.RecordCpuProduced(commands, samples, null, 1, 1, 0u));
            }
        }

        [Test]
        public void DisposeIsIdempotentAndEveryMutatingEntryPointFailsAfterward()
        {
            var pipeline = new GpuSensorPipeline(
                1,
                1,
                GpuPrimitiveBackend.Portable,
                false);
            pipeline.Dispose();
            Assert.DoesNotThrow(pipeline.Dispose);

            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ObjectDisposedException>(() =>
                    pipeline.SetStableIds(new[] { 0u }));
                Assert.Throws<ObjectDisposedException>(() =>
                    pipeline.SetQueries(new[] { FullDomainQuery() }));
                Assert.Throws<ObjectDisposedException>(() =>
                    pipeline.RecordValidateKeys(commands, 1));
                Assert.Throws<ObjectDisposedException>(() =>
                    pipeline.RecordGpuProduced(commands, 1u, 0u, 1, 1));
                Assert.Throws<ObjectDisposedException>(() =>
                    pipeline.RecordClearComparison(commands));
                Assert.Throws<ObjectDisposedException>(() =>
                    pipeline.RecordCompareDigests(
                        commands,
                        null,
                        null,
                        1));
            }
        }
        [Test]
        public void WaveOpsRunsWhenSupportedAndOtherwiseFailsClosed()
        {
            if (!GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    new GpuSensorPipeline(
                        1,
                        1,
                        GpuPrimitiveBackend.WaveOps,
                        false));
                return;
            }

            const int elementCount = 257;
            const uint seed = 0xcafebabeu;
            const uint logicalState = 63u;
            GpuSensorRangeQuery[] queries = { FullDomainQuery() };
            var expectedSamples = new GpuSensorSample[elementCount];
            var expectedKeys = new uint[elementCount];
            GpuSensorDeterministicGenerator.Populate(
                expectedSamples,
                expectedKeys,
                seed,
                logicalState);

            using (var pipeline = CreatePipeline(
                       elementCount,
                       queries,
                       GpuPrimitiveBackend.WaveOps))
            {
                RecordGpuAndExecute(
                    pipeline,
                    seed,
                    logicalState,
                    elementCount,
                    queries.Length);
                Assert.That(pipeline.Backend, Is.EqualTo(GpuPrimitiveBackend.WaveOps));
                Assert.That(
                    ReadBuffer<GpuSensorSample>(pipeline.Samples, elementCount),
                    Is.EqualTo(expectedSamples));
                Assert.That(
                    ReadBuffer<uint>(pipeline.Keys, elementCount),
                    Is.EqualTo(expectedKeys));
            }
        }

        private static GpuSensorPipeline CreatePipeline(
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

        private static uint[] CreateIdentity(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => unchecked((uint)index))
                .ToArray();
        }

        private static GpuSensorRangeQuery[] CreateRepresentativeQueries()
        {
            return new[]
            {
                FullDomainQuery(),
                new GpuSensorRangeQuery(0u, 0u, 0u, 0u),
                new GpuSensorRangeQuery(65535u, 65535u, 65535u, 0u),
                new GpuSensorRangeQuery(32768u, 32768u, 32768u, 4096u),
                new GpuSensorRangeQuery(1024u, 64512u, 32768u, 1024u),
            };
        }

        private static GpuSensorRangeQuery FullDomainQuery()
        {
            return new GpuSensorRangeQuery(0u, 0u, 0u, 65535u);
        }

        private static void RecordGpuAndExecute(
            GpuSensorPipeline pipeline,
            uint seed,
            uint logicalState,
            int elementCount,
            int queryCount)
        {
            using (var commands = new CommandBuffer
                   {
                       name = $"Test/SensorPipeline/State{logicalState}",
                   })
            {
                pipeline.RecordGpuProduced(
                    commands,
                    seed,
                    logicalState,
                    elementCount,
                    queryCount);
                Execute(commands);
            }
        }

        private static GraphicsBuffer CreateDigestBuffer(
            GpuSensorQueryDigest[] values)
        {
            var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                values.Length,
                GpuSensorPipeline.DigestStride);
            buffer.SetData(values);
            return buffer;
        }

        private static T[] ReadBuffer<T>(GraphicsBuffer buffer, int count)
            where T : struct
        {
            var output = new T[count];
            buffer.GetData(output);
            return output;
        }

        private static void AssertBuffer(
            GraphicsBuffer buffer,
            int count,
            int stride)
        {
            Assert.That(
                buffer.target & GraphicsBuffer.Target.Structured,
                Is.Not.EqualTo((GraphicsBuffer.Target)0));
            Assert.That(buffer.count, Is.EqualTo(count));
            Assert.That(buffer.stride, Is.EqualTo(stride));
        }

        private static void Execute(CommandBuffer commands)
        {
            Graphics.ExecuteCommandBuffer(commands);
        }
    }
}
