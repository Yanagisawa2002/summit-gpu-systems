using System;
using System.Collections.Generic;
using Summit.GpuSensorPipeline;

// Deterministic CPU models only. No graphics, clock, benchmark or process APIs.
internal static class ReferenceModels
{
    internal const int Bins = 262144;
    internal const uint Invalid = uint.MaxValue;
    internal static uint Key(GpuSensorSample p, uint active) =>
        active == 1 && p.X <= 65535 && p.Y <= 65535 && p.Z <= 65535 ?
        (p.X >> 10) | ((p.Y >> 10) << 6) | ((p.Z >> 10) << 12) : Invalid;

    internal static (uint[] Offsets, uint[] Ids) Reserved(GpuSensorSample[] samples, uint[] active)
    {
        var bins = new List<uint>[Bins];
        for (int id = 0; id < samples.Length; id++)
        {
            uint key = Key(samples[id], active[id]);
            if (key != Invalid) (bins[key] ??= new List<uint>()).Add((uint)id);
        }
        var offsets = new uint[Bins + 1];
        var ids = new List<uint>();
        for (int cell = 0; cell < Bins; cell++)
        {
            offsets[cell] = (uint)ids.Count;
            if (bins[cell] == null) continue;
            // Deliberate holes at both ends and reversed atomic-like member order.
            ids.Add(Invalid);
            for (int i = bins[cell].Count - 1; i >= 0; i--) ids.Add(bins[cell][i]);
            for (int i = 0; i < (bins[cell].Count + 1) / 2; i++) ids.Add(Invalid);
        }
        offsets[Bins] = (uint)ids.Count;
        return (offsets, ids.ToArray());
    }

    internal sealed class Directory
    {
        internal readonly (uint Begin, uint End, uint PrefixBegin, uint PrefixEnd)[] Spans =
            new (uint, uint, uint, uint)[4096];
        internal readonly uint[] BlockEnds = new uint[17];
        internal uint Count => BlockEnds[16];
        internal Directory((uint Begin, uint End)[] ranges)
        {
            for (int block = 0; block < 16; block++)
            {
                uint prefix = 0;
                for (int lane = 0; lane < 256; lane++)
                {
                    int row = block * 256 + lane;
                    var range = row < ranges.Length ? ranges[row] : (0u, 0u);
                    uint chunks = (range.Item2 - range.Item1 + 255u) / 256u;
                    Spans[row] = (range.Item1, range.Item2, prefix, prefix + chunks);
                    prefix += chunks;
                }
                BlockEnds[block] = prefix;
            }
            uint sum = 0;
            for (int block = 0; block < 16; block++)
            {
                sum += BlockEnds[block]; BlockEnds[block] = sum;
            }
            BlockEnds[16] = sum;
        }
        internal (uint Begin, uint End) Resolve(uint chunk)
        {
            if (chunk >= Count) return (0, 0);
            int lo = 0, hi = 16;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (BlockEnds[mid] <= chunk) lo = mid + 1; else hi = mid;
            }
            int block = lo;
            uint local = chunk - (block == 0 ? 0 : BlockEnds[block - 1]);
            lo = block * 256; hi = lo + 256;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Spans[mid].PrefixEnd <= local) lo = mid + 1; else hi = mid;
            }
            var span = Spans[lo];
            uint begin = span.Begin + (local - span.PrefixBegin) * 256;
            return (begin, Math.Min(span.End, begin + 256));
        }
    }

    internal static List<uint> QueryIds(GpuSensorSample[] samples, int elementCount,
        uint[] offsets, uint[] ids, GpuSensorRangeQuery query)
    {
        int rows = GpuSensorCellSpanLayout.GetSpanCount(query);
        var ranges = new (uint, uint)[rows];
        for (int row = 0; row < rows; row++)
        {
            GpuSensorCellSpanLayout.GetSpanCells(query, row, out int first, out int end);
            ranges[row] = (offsets[first], offsets[end]);
        }
        var directory = new Directory(ranges);
        var hits = new List<uint>();
        for (uint chunk = 0; chunk < directory.Count; chunk++)
        {
            var range = directory.Resolve(chunk);
            for (uint cursor = range.Begin; cursor < range.End; cursor++)
            {
                uint id = ids[cursor];
                if (id >= elementCount) continue;
                var p = samples[id];
                // Signed difference is independent of the shader's clamped bounds.
                if (Math.Abs((long)p.X - query.CenterX) <= query.Radius &&
                    Math.Abs((long)p.Y - query.CenterY) <= query.Radius &&
                    Math.Abs((long)p.Z - query.CenterZ) <= query.Radius) hits.Add(id);
            }
        }
        hits.Sort();
        return hits;
    }

    internal static GpuSensorQueryDigest Digest(GpuSensorSample[] samples, List<uint> ids, bool quantize)
    {
        uint count = 0, xor = 0, sum0 = 0, sum1 = 0;
        unchecked
        {
            foreach (uint id in ids)
            {
                var p = samples[id];
                if (quantize)
                {
                    uint q = p.Payload / 65537u;
                    p.Payload = Math.Min(q + ((p.Payload - q * 65537u) > 32768u ? 1u : 0u), 65535u);
                }
                uint h = Mix(id ^ 0x85ebca6bu);
                h = Mix(h ^ p.X); h = Mix(h ^ p.Y); h = Mix(h ^ p.Z); h = Mix(h ^ p.Payload);
                count++; xor ^= h; sum0 += h; sum1 += Mix(h ^ id ^ 0x27d4eb2fu);
            }
        }
        return new GpuSensorQueryDigest(count, xor, sum0, sum1);
    }
    private static uint Mix(uint v)
    {
        unchecked { v ^= v >> 16; v *= 0x7feb352d; v ^= v >> 15; v *= 0x846ca68b; return v ^ (v >> 16); }
    }

    internal static uint ScanBlocks(uint[] values)
    {
        if (values.Length != 1024) throw new ArgumentException("Block directory must contain 1024 entries.");
        for (int stride = 1; stride < 1024; stride *= 2)
            for (int node = 0; node < 1024 / (2 * stride); node++)
            {
                int i = (node + 1) * stride * 2 - 1;
                values[i] += values[i - stride];
            }
        uint total = values[1023]; values[1023] = 0;
        for (int stride = 512; stride > 0; stride /= 2)
            for (int node = 0; node < 1024 / (2 * stride); node++)
            {
                int i = (node + 1) * stride * 2 - 1;
                uint left = values[i - stride]; values[i - stride] = values[i]; values[i] += left;
            }
        return total;
    }

    internal sealed class LiveIndex
    {
        internal readonly uint[] Keys, Counts = new uint[Bins];
        internal readonly GpuSensorSample[] Samples;
        private readonly int staticSlots;
        private bool initialized;
        private uint revision;
        internal LiveIndex(int capacity, int staticSlots)
        {
            Keys = new uint[capacity]; Array.Fill(Keys, Invalid);
            Samples = new GpuSensorSample[capacity]; this.staticSlots = staticSlots;
        }
        internal void Update(GpuSensorSample[] input, uint[] active, uint staticRevision, bool rebuild)
        {
            var next = (uint[])Keys.Clone();
            for (int id = 0; id < Keys.Length; id++)
            {
                if (id < staticSlots && initialized && revision == staticRevision && !rebuild) continue;
                Samples[id] = input[id]; next[id] = Key(input[id], active[id]);
            }
            if (!initialized || rebuild)
            {
                Array.Clear(Counts);
                for (int id = 0; id < Keys.Length; id++) if (next[id] != Invalid) Counts[next[id]]++;
            }
            else
            {
                // All removals happen before insertions, like separate dispatches.
                for (int id = 0; id < Keys.Length; id++)
                    if (Keys[id] != next[id] && Keys[id] != Invalid) Counts[Keys[id]]--;
                for (int id = 0; id < Keys.Length; id++)
                    if (Keys[id] != next[id] && next[id] != Invalid) Counts[next[id]]++;
            }
            Array.Copy(next, Keys, Keys.Length); initialized = true; revision = staticRevision;
        }
        internal (uint[] Offsets, uint[] Ids) Compact()
        {
            var offsets = new uint[Bins + 1];
            var blocks = new uint[Bins / 256];
            for (int block = 0; block < blocks.Length; block++)
            {
                uint sum = 0;
                for (int lane = 0; lane < 256; lane++)
                {
                    int cell = block * 256 + lane;
                    offsets[cell] = sum; sum += Counts[cell];
                }
                blocks[block] = sum;
            }
            uint total = ScanBlocks(blocks);
            for (int cell = 0; cell < Bins; cell++) offsets[cell] += blocks[cell / 256];
            offsets[Bins] = total;
            var ids = new uint[total]; var heads = (uint[])offsets.Clone();
            for (int id = Keys.Length - 1; id >= 0; id--)
                if (Keys[id] != Invalid) ids[heads[Keys[id]]++] = (uint)id;
            return (offsets, ids);
        }
    }
}
