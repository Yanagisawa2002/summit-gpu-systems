using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives.Tests
{
    public class GpuPrimitiveCandidateTests
    {
        public static string[] Ids => GpuPrimitiveCandidates.All.Select(c => c.Id).ToArray();
        [Test]
        public void CatalogIsBoundedVersionedAndAccountsForHierarchy()
        {
            Assert.AreEqual(6, Ids.Distinct().Count());
            Assert.Throws<ArgumentException>(() => GpuPrimitiveCandidates.Get("unknown"));
            foreach (var c in GpuPrimitiveCandidates.All)
            {
                Assert.AreEqual(0, c.ScanDispatches(0));
                Assert.AreEqual(1, c.ScanDispatches(c.TileSize));
                Assert.AreEqual(3, c.ScanDispatches(c.TileSize + 1));
                Assert.AreEqual(1, c.ReductionDispatches(0));
                Assert.AreEqual(2, c.ReductionDispatches(c.TileSize + 1));
                Assert.AreEqual(0, c.RadixDispatches(0));
                Assert.AreEqual(3 * c.RadixPasses(32), c.RadixDispatches(1));
                Assert.Throws<ArgumentOutOfRangeException>(() => c.RadixPasses(0));
            }
        }

        [Test]
        public void ExplicitWaveRequestsFailClosedUntilToolchainIsValidated()
        {
            foreach (var c in GpuPrimitiveCandidates.All.Where(c => c.WaveSize > 0))
            {
                Assert.IsFalse(GpuPrimitiveCandidates.IsSupported(c.Id, out string reason));
                Assert.IsNotEmpty(reason);
                Assert.Throws<NotSupportedException>(() => new GpuPrimitives(1, candidateId: c.Id));
            }
        }

        [Test]
        public void CandidatePreservesArgumentAndBackendContracts()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("GPU compute device required.");
            using (var p = new GpuPrimitives(1, candidateId: Ids[0]))
            using (var a = Buffer(new[] { 1u })) using (var b = Buffer(new[] { 0u }))
            using (var cmd = new CommandBuffer())
            {
                Assert.Throws<ArgumentException>(() => p.RecordReduceSum(cmd, a, a, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => p.RecordReduceSum(cmd, a, b, 2));
                Assert.Throws<ArgumentNullException>(() => p.RecordReduceSum(null, a, b, 1));
                Assert.Throws<ArgumentException>(() => p.RecordRadixSortKeyBits(cmd, a, a, b, b, 1, 8));
                Assert.Throws<ArgumentOutOfRangeException>(() => p.RecordRadixSortKeyBits(cmd, a, a, b, a, 1, 33));
                p.RecordExclusiveScan(cmd, a, b, 1, GpuPrimitiveBackend.Portable);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.AreEqual(0u, Read(b, 1)[0]);
            }
        }

        [TestCaseSource(nameof(Ids))]
        public void CandidateMatchesIndependentOracles(string id)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("GPU compute device required.");
            var c = GpuPrimitiveCandidates.Get(id);
            bool supported = GpuPrimitiveCandidates.TryProbeWaveSize(id, out int observed, out string reason);
            TestContext.WriteLine($"{id}: supported={supported}; observedWave={observed}; reason={reason}; device={SystemInfo.graphicsDeviceName}");
            if (!supported && c.WaveSize > 0) Assert.Ignore("Explicit wave candidate unavailable: " + reason);
            Assert.IsTrue(supported, reason);
            int[] sizes = { 0, 1, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 512, 513, 4097 };
            using (var p = new GpuPrimitives(1, candidateId: id))
            {
                p.EnsureCapacity(4097);
                Assert.Greater(p.CandidateScratchBytes, 0);
                Assert.Greater(p.ScratchBytes, p.CandidateScratchBytes);
                foreach (int n in sizes) CheckScanCompactReduce(p, n);
                foreach (int bits in new[] { 1, 4, 5, 7, 8, 9, 12, 13, 16, 17, 24, 25, 31, 32 })
                    foreach (int n in new[] { 0, 1, 513, 4097 }) CheckSort(p, n, bits);
                // Same instance retains explicitly requested Portable behavior.
                CheckSort(p, 513, 13, GpuPrimitiveBackend.Portable);
                // Three-level scan and histogram hierarchy, still a short correctness run.
                p.EnsureCapacity(262145);
                CheckScanCompactReduce(p, 262145);
            }
        }
        private static GraphicsBuffer Buffer(uint[] data)
        {
            var b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, data.Length), 4);
            if (data.Length > 0) b.SetData(data); else b.SetData(new[] { 0xdeadbeefu });
            return b;
        }
        private static uint[] Read(GraphicsBuffer b, int n)
        { var data = new uint[n]; if (n > 0) b.GetData(data); return data; }
        private static void CheckScanCompactReduce(GpuPrimitives p, int n)
        {
            var values = Enumerable.Range(0, n).Select(i => unchecked((uint)i * 1664525u + 0xfffffff0u)).ToArray();
            var flags = Enumerable.Range(0, n).Select(i => i % 5 == 0 ? 0u : (uint)(i % 3 + 1)).ToArray();
            var expected = new uint[n]; uint sum = 0;
            for (int i = 0; i < n; i++) { expected[i] = sum; sum = unchecked(sum + values[i]); }
            using (var input = Buffer(values)) using (var predicates = Buffer(flags))
            using (var output = Buffer(new uint[Math.Max(1, n)])) using (var count = Buffer(new[] { 0xdeadbeefu }))
            using (var cmd = new CommandBuffer())
            {
                p.RecordExclusiveScan(cmd, input, output, n); Graphics.ExecuteCommandBuffer(cmd); cmd.Clear();
                CollectionAssert.AreEqual(expected, Read(output, n), "scan n=" + n);
                p.RecordReduceSum(cmd, input, count, n); Graphics.ExecuteCommandBuffer(cmd); cmd.Clear();
                Assert.AreEqual(sum, Read(count, 1)[0], "reduce n=" + n);
                p.RecordStableCompaction(cmd, input, predicates, output, count, n); Graphics.ExecuteCommandBuffer(cmd);
                uint[] compact = values.Where((_, i) => flags[i] != 0).ToArray();
                Assert.AreEqual(compact.Length, Read(count, 1)[0]);
                CollectionAssert.AreEqual(compact, Read(output, compact.Length), "compact n=" + n);
            }
        }
        private static void CheckSort(GpuPrimitives p, int n, int bits, GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto)
        {
            uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
            // Duplicates, hot bin, maximal key, descending runs, high-bit boundaries.
            uint[] keys = Enumerable.Range(0, n).Select(i => (i % 7 == 0 ? mask : i % 3 == 0 ? 0u : unchecked((uint)(n - i) * 2654435761u) % 37u) & mask).ToArray();
            uint[] values = Enumerable.Range(0, n).Select(i => (uint)i).ToArray();
            int[] order = Enumerable.Range(0, n).OrderBy(i => keys[i]).ToArray();
            uint[] sentinels = Enumerable.Repeat(0xdeadbeefu, n + 1).ToArray();
            using (var ki = Buffer(keys)) using (var vi = Buffer(values))
            using (var ko = Buffer(sentinels)) using (var vo = Buffer(sentinels)) using (var cmd = new CommandBuffer())
            {
                p.RecordRadixSortKeyBits(cmd, ki, vi, ko, vo, n, bits, backend);
                Graphics.ExecuteCommandBuffer(cmd);
                CollectionAssert.AreEqual(order.Select(i => keys[i]), Read(ko, n), $"keys n={n} bits={bits}");
                CollectionAssert.AreEqual(order.Select(i => values[i]), Read(vo, n), $"stable payload n={n} bits={bits}");
                Assert.AreEqual(0xdeadbeefu, Read(ko, n + 1)[n]);
                Assert.AreEqual(0xdeadbeefu, Read(vo, n + 1)[n]);
                CollectionAssert.AreEqual(keys, Read(ki, n));
            }
        }
    }
}
