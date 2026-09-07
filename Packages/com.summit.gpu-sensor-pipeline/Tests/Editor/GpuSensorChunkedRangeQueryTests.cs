using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorChunkedRangeQueryTests
    {
        [TestCase("sparse", 65541)] // >65535 chunks exercises the indirect second dimension.
        [TestCase("uniform", 257)]
        [TestCase("hotspot", 4097)]
        [TestCase("single-cell", 1025)]
        [TestCase("uniform", 1)]
        [TestCase("single-cell", 255)]
        [TestCase("single-cell", 256)]
        public void EveryBackendMatchesIndependentOracleAndSegments(string distribution, int count)
        {
            RequireGpu();
            var samples = GpuSensorQueryFixtures.Samples(distribution, count);
            var queries = GpuSensorQueryFixtures.Queries();
            var expected = GpuSensorPipelineTestOracle.QueryAll(samples, count, queries);
            var quantized = samples.Select(s => new GpuSensorSample(s.X, s.Y, s.Z,
                (uint)(((ulong)s.Payload + 32768ul) / 65537ul))).ToArray();
            var expectedQuantized = GpuSensorPipelineTestOracle.QueryAll(quantized, count, queries);
            foreach (GpuSensorQueryBackend backend in Enum.GetValues(typeof(GpuSensorQueryBackend)))
            {
                if (backend == GpuSensorQueryBackend.PointChunksWave && !GpuSensorChunkedRangeQuery.SupportsWaveOperations)
                    continue;
                using (var pipeline = GpuSensorQueryFixtures.Create(samples, backend))
                using (var commands = new CommandBuffer())
                {
                    Assert.That(GpuSensorQueryFixtures.Read(pipeline), Is.EqualTo(expected), backend.ToString());
                    var frame = new GpuSensorQueryDigest[64];
                    pipeline.FrameDigest.GetData(frame);
                    Assert.That(frame[0], Is.EqualTo(GpuSensorPipelineTestOracle.Frame(expected, 0)));
                    // Same scratch reused twice in a single submission, disjoint segments.
                    pipeline.RecordQueries(commands, count, 1, 3, true);
                    pipeline.RecordQueries(commands, count, 4, queries.Length - 4, true);
                    pipeline.RecordQueries(commands, count, queries.Length, 0);
                    Graphics.ExecuteCommandBuffer(commands);
                    var actual = GpuSensorQueryFixtures.Read(pipeline);
                    Assert.That(actual[0], Is.EqualTo(expected[0]));
                    Assert.That(actual.Skip(1), Is.EqualTo(expectedQuantized.Skip(1)), backend.ToString());
                    commands.Clear();
                    pipeline.RecordQueries(commands, count, 0, queries.Length);
                    Graphics.ExecuteCommandBuffer(commands);
                    Assert.That(GpuSensorQueryFixtures.Read(pipeline), Is.EqualTo(expected));
                }
            }
        }

        [TestCase(GpuSensorQueryBackend.PointChunks)]
        [TestCase(GpuSensorQueryBackend.PointChunksWave)]
        public void ExternalReservedCsrSkipsTombstonesAndSupportsEmptyInputs(GpuSensorQueryBackend backend)
        {
            RequireGpu();
            if (backend == GpuSensorQueryBackend.PointChunksWave && !GpuSensorChunkedRangeQuery.SupportsWaveOperations)
                Assert.Ignore("Wave operations unavailable.");
            const int entries = 4097;
            var samples = new[] { new GpuSensorSample(0, 0, 0, 7), new GpuSensorSample(1, 1, 1, 9) };
            var query = new[] { new GpuSensorRangeQuery(0, 0, 0, 1) };
            var offsets = Enumerable.Repeat((uint)entries, 262145).ToArray(); offsets[0] = 0;
            var ids = Enumerable.Repeat(uint.MaxValue, entries).ToArray(); ids[0] = 0; ids[4096] = 1;
            using (var consumer = new GpuSensorChunkedRangeQuery(2, backend, entries))
            using (var sb = Buffer(samples, 16))
            using (var ob = Buffer(offsets, 4))
            using (var ib = Buffer(ids, 4))
            using (var qb = Buffer(query, 16))
            using (var output = Buffer(new GpuSensorQueryDigest[1], 16))
            using (var commands = new CommandBuffer())
            {
                consumer.Record(commands, sb, ob, ib, qb, output, 2, 0, 1);
                Graphics.ExecuteCommandBuffer(commands);
                var actual = new GpuSensorQueryDigest[1]; output.GetData(actual);
                Assert.That(actual, Is.EqualTo(GpuSensorPipelineTestOracle.QueryAll(samples, 2, query)));
                commands.Clear();
                consumer.Record(commands, sb, ob, ib, qb, output, 0, 0, 1);
                Graphics.ExecuteCommandBuffer(commands); output.GetData(actual);
                Assert.That(actual[0], Is.EqualTo(default(GpuSensorQueryDigest)));
                commands.Clear();
                consumer.Record(commands, sb, ob, ib, qb, output, 2, 1, 0);
                Assert.That(commands.sizeInBytes, Is.Zero);
                Assert.Throws<ArgumentOutOfRangeException>(() => consumer.Record(commands, sb, ob, ib, qb, output, 3, 0, 1));
                Assert.Throws<ArgumentException>(() => consumer.Record(commands, sb, ob, ib, sb, output, 2, 0, 1));
                Assert.That(commands.sizeInBytes, Is.Zero, "Rejected inputs must not partially record commands.");
                using (var undersized = new GpuSensorChunkedRangeQuery(2))
                    Assert.Throws<ArgumentException>(() => undersized.Record(commands, sb, ob, ib, qb, output, 2, 0, 1));
                consumer.Dispose();
                Assert.Throws<ObjectDisposedException>(() => consumer.Record(commands, sb, ob, ib, qb, output, 2, 0, 1));
            }
        }

        [Test]
        public void CapacityBoundCoversSkewAndRejectsInvalidCapacities()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GpuSensorChunkedRangeQuery.CalculateChunkCapacity(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => GpuSensorChunkedRangeQuery.CalculateChunkCapacity(int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSensorChunkedRangeQuery(1, GpuSensorQueryBackend.CellSerial));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSensorPipeline(1, 1, queryBackend: (GpuSensorQueryBackend)999));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSensorPipeline(2, 1, queryIndexEntryCapacity: 1));
            var random = new System.Random(73);
            for (int trial = 0; trial < 100; trial++)
            {
                int n = random.Next(1, 100000), remaining = n, chunks = 0;
                while (remaining > 0)
                {
                    int occupancy = random.Next(1, remaining + 1);
                    chunks += (occupancy + 255) / 256; remaining -= occupancy;
                }
                Assert.That(chunks, Is.LessThanOrEqualTo(GpuSensorChunkedRangeQuery.CalculateChunkCapacity(n)));
            }
        }

        private static GraphicsBuffer Buffer<T>(T[] data, int stride) where T : struct
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Length, stride);
            buffer.SetData(data); return buffer;
        }

        private static void RequireGpu()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("A compute device is required.");
        }
    }
}
