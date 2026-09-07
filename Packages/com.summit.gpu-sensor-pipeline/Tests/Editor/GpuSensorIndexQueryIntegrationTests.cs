using System;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    [TestFixture(GpuSensorIndexExecutionMode.Original)]
    [TestFixture(GpuSensorIndexExecutionMode.GpuDriven)]
    public sealed class GpuSensorIndexQueryIntegrationTests
    {
        private readonly GpuSensorIndexExecutionMode mode;
        public GpuSensorIndexQueryIntegrationTests(GpuSensorIndexExecutionMode mode) { this.mode = mode; }
        [TestCase(GpuSensorQueryBackend.BatchedPointScanWave)]
        [TestCase(GpuSensorQueryBackend.CellSerial)]
        [TestCase(GpuSensorQueryBackend.PointChunks)]
        [TestCase(GpuSensorQueryBackend.PointChunksWave)]
        public void ExternalIndexTransitionsPreserveSparseStableIdsAndPayloads(GpuSensorQueryBackend backend)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("A compute device is required.");
            if ((backend == GpuSensorQueryBackend.PointChunksWave || backend == GpuSensorQueryBackend.BatchedPointScanWave) && !GpuSensorChunkedRangeQuery.SupportsWaveOperations)
                Assert.Ignore("Wave operations unavailable.");
            const int capacity = 513, staticSlots = 384;
            var samples = new GpuSensorSample[capacity];
            var active = new uint[capacity];
            for (int i = 0; i < capacity; i++)
            {
                samples[i] = new GpuSensorSample(32768, 32768, 32768, (uint)i + 19);
                active[i] = 1;
            }
            var queries = new[] {
                new GpuSensorRangeQuery(0, 0, 0, 65535),
                new GpuSensorRangeQuery(32768, 32768, 32768, 0),
                new GpuSensorRangeQuery(1024, 1024, 1024, 1),
                new GpuSensorRangeQuery(65535, 65535, 65535, 0),
                new GpuSensorRangeQuery(1, 2, 3, 0)
            };
            using (var index = new GpuSensorIncrementalIndex(capacity, staticSlots, executionMode: mode))
            using (var rebuilt = new GpuSensorFullRebuildIndex(capacity))
            using (var pipeline = new GpuSensorPipeline(capacity, queries.Length,
                GpuPrimitiveBackend.Portable, false, queryBackend: backend,
                queryIndexEntryCapacity: capacity * 3))
            using (var reference = new GpuSensorPipeline(capacity, queries.Length,
                GpuPrimitiveBackend.Portable, false))
            using (var input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16))
            using (var flags = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4))
            using (var commands = new CommandBuffer())
            {
                pipeline.SetQueries(queries);
                reference.SetQueries(queries);
                uint revision = 0;
                for (int frame = 0; frame < 18; frame++)
                {
                    if (frame == 1) { active[0] = 0; revision++; }
                    if (frame == 2) samples[capacity - 1].Payload = uint.MaxValue;
                    if (frame == 3) samples[capacity - 1] = new GpuSensorSample(1024, 1024, 1024, 81);
                    if (frame == 4) { samples[1].Payload++; revision++; }
                    if (frame == 5) { active[0] = 1; samples[0] = new GpuSensorSample(65535, 65535, 65535, 83); revision++; }
                    if (frame == 6 || frame == 12)
                    {
                        for (int i = staticSlots; i < capacity; i++)
                            samples[i] = new GpuSensorSample((uint)(i * 79 % 65536), 1024, 1024, (uint)i);
                    }
                    if (frame == 7 || frame == 13)
                    {
                        for (int i = staticSlots; i < capacity; i++)
                            samples[i] = new GpuSensorSample(32768, 32768, 32768, (uint)i);
                    }
                    if (frame == 8) { Array.Clear(active, 0, capacity - 1); revision++; }
                    if (frame == 9) { active[capacity - 1] = 0; }
                    if (frame == 10)
                    {
                        for (int i = 0; i < capacity; i++) active[i] = 1;
                        revision++;
                    }
                    if (frame >= 14) samples[capacity - 1].Payload++;
                    input.SetData(samples); flags.SetData(active); commands.Clear();
                    index.RecordUpdate(commands, input, flags, revision, frame == 11);
                    rebuilt.RecordUpdate(commands, input, flags);
                    pipeline.RecordExternalIndexQueries(commands, index.Samples, index.BinOffsets,
                        index.BinnedIds, capacity, queries.Length, (uint)frame);
                    reference.RecordExternalIndexQueries(commands, input, rebuilt.BinOffsets,
                        rebuilt.BinnedIds, capacity, queries.Length, (uint)frame);
                    Graphics.ExecuteCommandBuffer(commands);
                    var actual = new GpuSensorQueryDigest[queries.Length];
                    var full = new GpuSensorQueryDigest[queries.Length];
                    pipeline.QueryDigests.GetData(actual); reference.QueryDigests.GetData(full);
                    // Preserve original stable IDs in the independent brute-force
                    // oracle while excluding inactive slots by an out-of-domain X.
                    var oracleSamples = (GpuSensorSample[])samples.Clone();
                    uint activeCount = 0;
                    for (int i = 0; i < capacity; i++)
                        if (active[i] == 0) oracleSamples[i].X = uint.MaxValue;
                        else activeCount++;
                    var expected = GpuSensorPipelineTestOracle.QueryAll(oracleSamples, capacity, queries);
                    Assert.That(actual, Is.EqualTo(expected), $"{backend} frame {frame}");
                    Assert.That(full, Is.EqualTo(expected), $"full rebuild frame {frame}");
                    var diagnostics = new uint[GpuSensorIncrementalIndex.DiagnosticWordCount];
                    index.Diagnostics.GetData(diagnostics);
                    Assert.That(diagnostics[GpuSensorIncrementalIndex.ActiveCountWord], Is.EqualTo(activeCount));
                    Assert.That(diagnostics[GpuSensorIncrementalIndex.InvalidInputWord], Is.Zero);
                    if (frame == 8) Assert.That(actual[0].Count, Is.EqualTo(1), "highest stable slot survives lower removals");
                    if (frame == 17)
                    {
                        Assert.That(diagnostics[GpuSensorIncrementalIndex.RebuildCountWord], Is.GreaterThan(1));
                        Assert.That(diagnostics[GpuSensorIncrementalIndex.IncrementalCountWord], Is.GreaterThan(1));
                    }
                }
            }
        }

        [Test]
        public void OversizedExternalCsrIsRejectedBeforeProfilerOrGpuCommands()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("A compute device is required.");
            using (var index = new GpuSensorIncrementalIndex(2))
            using (var pipeline = new GpuSensorPipeline(2, 1, GpuPrimitiveBackend.Portable,
                true, queryBackend: GpuSensorQueryBackend.PointChunks))
            using (var commands = new CommandBuffer())
            {
                pipeline.SetQueries(new[] { new GpuSensorRangeQuery(0, 0, 0, 65535) });
                Assert.Throws<ArgumentException>(() => pipeline.RecordExternalIndexQueries(commands,
                    index.Samples, index.BinOffsets, index.BinnedIds, index.Capacity, 1, 0));
                Assert.That(commands.sizeInBytes, Is.Zero);
            }
        }
    }
}
