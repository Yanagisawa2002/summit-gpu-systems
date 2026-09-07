using System;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorIncrementalIndexTests
    {
        [SetUp]
        public void RequireCompute()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Real compute device required.");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(5)] [TestCase(20)] [TestCase(100)]
        public void DeterministicRatesMatchIndependentOracleAndFullDirectRebuild(int changeRate)
        {
            foreach (int crossingRate in GpuSensorIndexUpdateTrace.Rates)
            using (var f = new Fixture(257))
            {
                f.Check();
                for (int step = 0; step < 4; step++)
                {
                    GpuSensorIndexUpdateTrace.Advance(f.Samples, f.Active, 0,
                        changeRate, crossingRate, step, step == 3);
                    f.Check(force: step == 2);
                    if (crossingRate == 0 && step != 2)
                        Assert.That(f.State[GpuSensorIncrementalIndex.RebuildReasonWord], Is.Zero);
                }
            }
        }

        [Test]
        public void MembershipIsByteIdenticalForSameCellPayloadUpdates()
        {
            using (var f = new Fixture(259))
            {
                f.Check();
                uint[] offsets = Read<uint>(f.Index.BinOffsets);
                uint[] members = Read<uint>(f.Index.BinnedIds, (int)offsets[offsets.Length - 1]);
                GpuSensorQueryDigest before = Read<GpuSensorQueryDigest>(f.Consumer.QueryDigests)[0];
                GpuSensorIndexUpdateTrace.Advance(f.Samples, f.Active, 0, 100, 0, 0);
                f.Check();
                Assert.That(Read<uint>(f.Index.BinOffsets), Is.EqualTo(offsets));
                Assert.That(Read<uint>(f.Index.BinnedIds, members.Length), Is.EqualTo(members));
                Assert.That(f.State[GpuSensorIncrementalIndex.ChangedWord], Is.Zero);
                Assert.That(f.State[GpuSensorIncrementalIndex.ReusedWord], Is.EqualTo(259));
                Assert.That(Read<GpuSensorQueryDigest>(f.Consumer.QueryDigests)[0], Is.Not.EqualTo(before));
            }
        }

        [Test]
        public void SparseStableIdsAddRemoveReuseAndEmptySceneRemainCorrect()
        {
            using (var f = new Fixture(257))
            {
                f.Check();
                for (int i = 0; i < 256; i++) f.Active[i] = 0;
                f.Check();
                Assert.That(f.State[GpuSensorIncrementalIndex.ActiveCountWord], Is.EqualTo(1));
                f.Active[256] = 0; f.Check();
                Assert.That(f.State[GpuSensorIncrementalIndex.ActiveCountWord], Is.Zero);
                f.Samples[256] = new GpuSensorSample(65535, 65535, 65535, 42);
                f.Active[256] = 1; f.Check();
                f.Active[0] = 1; f.Check();
                f.Active[256] = 0; f.Check();
                f.Active[256] = 1; f.Check(force: true);
            }
        }

        [Test]
        public void StaticHeavyRevisionAndForcedRefreshRespectSnapshotContract()
        {
            using (var f = new Fixture(1000, 900))
            {
                f.Check();
                for (int step = 0; step < 5; step++)
                {
                    GpuSensorIndexUpdateTrace.Advance(f.Samples, f.Active, 900, 100, 20, step);
                    f.Check();
                    Assert.That(f.State[GpuSensorIncrementalIndex.InspectedWord], Is.EqualTo(100));
                }
                f.Samples[0].Payload = 123; f.Samples[1].X = 65000; f.Active[2] = 0;
                f.Check(revision: 1);
                Assert.That(f.State[GpuSensorIncrementalIndex.InspectedWord], Is.EqualTo(1000));
                f.Samples[0].Payload = 321;
                f.Check(revision: 1, force: true);
            }
        }

        [Test]
        public void ConcentratedCellsCapacityChurnAndFragmentationTriggerSafeRebuilds()
        {
            using (var f = new Fixture(100, concentrated: true, churn: 1000, fragmentation: 0))
            {
                f.Check();
                f.Active[0] = 0; f.Check();
                Assert.That(f.State[8] & (uint)GpuSensorIndexRebuildReason.Fragmentation, Is.Not.Zero);
                f.Active[0] = 1; f.Samples[0].X = 65535; f.Check();
                Assert.That(f.State[8] & (uint)GpuSensorIndexRebuildReason.CellCapacity, Is.Not.Zero);
            }
            using (var f = new Fixture(257, concentrated: true))
            {
                f.Check();
                for (int step = 0; step < 6; step++)
                {
                    GpuSensorIndexUpdateTrace.Advance(f.Samples, f.Active, 0,
                        step % 2 == 0 ? 100 : 0, 100, step, true);
                    f.Check();
                    Assert.That(f.State[8] != 0, Is.EqualTo(step % 2 == 0));
                }
            }
        }

        [Test]
        public void DistributedCellsCrossScanBlocksAndIncludeBoundaryCoordinates()
        {
            using (var f = new Fixture(4099))
            {
                for (uint id = 0; id < f.Samples.Length; id++)
                    f.Samples[id] = new GpuSensorSample(
                        GpuSensorPipelineTestOracle.Mix(id + 1) & 65535,
                        GpuSensorPipelineTestOracle.Mix(id + 10001) & 65535,
                        GpuSensorPipelineTestOracle.Mix(id + 20001) & 65535, id);
                f.Samples[0] = new GpuSensorSample(0, 0, 0, 0);
                f.Samples[4098] = new GpuSensorSample(65535, 65535, 65535, 9);
                f.Check();
                GpuSensorIndexUpdateTrace.Advance(f.Samples, f.Active, 0, 20, 100, 1, true);
                f.Check(); f.Check(); f.Check(force: true);
            }
        }

        [Test]
        public void CellReservationExhaustionAndExactThresholdBoundaryAreSafe()
        {
            using (var f = new Fixture(100, concentrated: true, churn: 1000, fragmentation: 1000))
            {
                for (int i = 50; i < 100; i++) f.Samples[i].X = 1536;
                f.Check();
                // Cell 1 starts with 50 members and 26 reserved words.
                for (int i = 0; i < 27; i++)
                {
                    f.Samples[i].X = 1536; f.Check();
                    Assert.That((f.State[8] & 16) != 0, Is.EqualTo(i == 26));
                }
            }
            using (var f = new Fixture(10, concentrated: true, churn: 200, fragmentation: 1000))
            {
                f.Check(); f.Active[0] = 0; f.Active[1] = 0; f.Check();
                Assert.That(f.State[8], Is.Zero, "Equality does not exceed the 20% threshold.");
                f.Active[2] = 0; f.Active[3] = 0; f.Active[4] = 0; f.Check();
                Assert.That(f.State[8] & 4, Is.Not.Zero);
            }
        }

        [Test]
        public void InvalidGpuSamplesAreExcludedAndCountedWithoutOutOfBoundsAccess()
        {
            using (var f = new Fixture(17))
            {
                f.Check(); f.Samples[0].X = 65536; f.Active[1] = 2; f.Check();
                Assert.That(f.State[7], Is.EqualTo(2));
                f.Samples[0].X = 0; f.Active[1] = 1; f.Check();
                Assert.That(f.State[7], Is.Zero);
            }
        }

        [Test]
        public void RecordingContractRejectsMalformedBuffersAndSupportsCommandReplay()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSensorIncrementalIndex(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSensorIncrementalIndex(2, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSensorIncrementalIndex(2, churnPermille: 1001));
            using (var f = new Fixture(17))
            using (var c = new CommandBuffer())
            {
                Assert.Throws<ArgumentNullException>(() => f.Index.RecordUpdate(null, f.Input, f.Flags));
                Assert.Throws<ArgumentException>(() => f.Index.RecordUpdate(c, f.Flags, f.Flags));
                Assert.Throws<ArgumentException>(() => f.Index.RecordUpdate(c, f.Index.Samples, f.Flags));
                f.Input.SetData(f.Samples); f.Flags.SetData(f.Active);
                f.Index.RecordUpdate(c, f.Input, f.Flags);
                Graphics.ExecuteCommandBuffer(c); Read<uint>(f.Index.Diagnostics);
                Graphics.ExecuteCommandBuffer(c);
                var state = Read<uint>(f.Index.Diagnostics);
                Assert.That(state[11], Is.EqualTo(1)); Assert.That(state[12], Is.EqualTo(1));
                f.Check();
                long bytes = (long)17 * 44 + (3L * GpuSensorPipeline.FixedBinCount + 1 + 1024 + 16) * 4;
                Assert.That(f.Index.ResidentBytes, Is.EqualTo(bytes));
                f.Index.Dispose();
                Assert.DoesNotThrow(f.Index.Dispose);
                Assert.Throws<ObjectDisposedException>(() => f.Index.RecordUpdate(c, f.Input, f.Flags));
            }
        }

        private static T[] Read<T>(GraphicsBuffer buffer, int count = -1) where T : struct
        {
            var data = new T[count < 0 ? buffer.count : count]; if (data.Length > 0) buffer.GetData(data); return data;
        }

        private sealed class Fixture : IDisposable
        {
            public readonly GpuSensorSample[] Samples;
            public readonly uint[] Active;
            public readonly GpuSensorIncrementalIndex Index;
            public readonly GpuSensorPipeline Consumer, Reference;
            public readonly GraphicsBuffer Input, Flags;
            public uint[] State;
            private readonly GpuSensorFullRebuildIndex full;
            private readonly GpuSensorRangeQuery[] queries = {
                new GpuSensorRangeQuery(32768, 32768, 32768, 65535),
                new GpuSensorRangeQuery(512, 512, 512, 511),
                new GpuSensorRangeQuery(1024, 512, 512, 1),
                new GpuSensorRangeQuery(65535, 65535, 65535, 0),
                new GpuSensorRangeQuery(40000, 40000, 40000, 2) };
            public Fixture(int capacity, int staticCount = 0, bool concentrated = false,
                int churn = 200, int fragmentation = 250)
            {
                Samples = new GpuSensorSample[capacity]; Active = new uint[capacity];
                GpuSensorIndexUpdateTrace.Initialize(Samples, Active, concentrated);
                Index = new GpuSensorIncrementalIndex(capacity, staticCount, churn, fragmentation);
                Consumer = new GpuSensorPipeline(capacity, queries.Length, GpuPrimitiveBackend.Portable, false);
                Reference = new GpuSensorPipeline(capacity, queries.Length, GpuPrimitiveBackend.Portable, false);
                Consumer.SetQueries(queries); Reference.SetQueries(queries);
                uint[] ids = new uint[capacity]; for (int i = 0; i < capacity; i++) ids[i] = (uint)i;
                Reference.SetStableIds(ids);
                Input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16);
                Flags = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
                full = new GpuSensorFullRebuildIndex(capacity);
            }
            public void Check(uint revision = 0, bool force = false)
            {
                int n = Samples.Length;
                var keys = new uint[n]; var oracleSamples = new GpuSensorSample[n];
                for (int i = 0; i < n; i++)
                {
                    bool valid = Active[i] == 1 && Samples[i].X <= 65535 && Samples[i].Y <= 65535 && Samples[i].Z <= 65535;
                    keys[i] = valid ? GpuSensorDeterministicGenerator.ComputeKey(Samples[i]) : uint.MaxValue;
                    // A sentinel outside the query domain excludes inactive slots while retaining IDs.
                    oracleSamples[i] = valid ? Samples[i] : new GpuSensorSample(uint.MaxValue, 0, 0, 0);
                }
                Input.SetData(Samples); Flags.SetData(Active); Reference.Keys.SetData(keys);
                Reference.Samples.SetData(Samples);
                using (var c = new CommandBuffer())
                {
                    Index.RecordUpdate(c, Input, Flags, revision, force);
                    Consumer.RecordExternalIndexQueries(c, Index.Samples, Index.BinOffsets, Index.BinnedIds, n, queries.Length, 0);
                    full.RecordUpdate(c, Input, Flags);
                    Reference.RecordExternalIndexQueries(c, Input, full.BinOffsets, full.BinnedIds, n, queries.Length, 0);
                    Graphics.ExecuteCommandBuffer(c);
                }
                State = Read<uint>(Index.Diagnostics);
                var expected = GpuSensorPipelineTestOracle.QueryAll(oracleSamples, n, queries);
                Assert.That(Read<GpuSensorQueryDigest>(Consumer.QueryDigests), Is.EqualTo(expected));
                Assert.That(Read<GpuSensorQueryDigest>(Reference.QueryDigests), Is.EqualTo(expected));
                var offsets = Read<uint>(Index.BinOffsets);
                Assert.That(offsets[0], Is.Zero);
                Assert.That(offsets[offsets.Length - 1], Is.LessThanOrEqualTo(Index.BinnedIds.count));
                var members = Read<uint>(Index.BinnedIds, (int)offsets[offsets.Length - 1]);
                var seen = new bool[n]; int live = 0;
                for (int cell = 0; cell < offsets.Length - 1; cell++)
                {
                    if (offsets[cell + 1] < offsets[cell]) Assert.Fail("Non-monotonic CSR offsets.");
                    for (uint pos = offsets[cell]; pos < offsets[cell + 1]; pos++)
                    {
                        uint id = members[pos]; if (id == uint.MaxValue) continue;
                        Assert.That(id, Is.LessThan(n)); Assert.That(seen[id], Is.False);
                        Assert.That(keys[id], Is.EqualTo(cell)); seen[id] = true; live++;
                    }
                }
                Assert.That(State[9], Is.EqualTo(live));
            }
            public void Dispose()
            {
                full.Dispose(); Flags.Dispose(); Input.Dispose(); Reference.Dispose(); Consumer.Dispose(); Index.Dispose();
            }
        }
    }
}
