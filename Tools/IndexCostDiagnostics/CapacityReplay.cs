using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Summit.GpuSensorPipeline;
using Summit.PublicIntegration;

namespace Summit.IndexCostDiagnostics
{
    // Mechanism model only: no Unity, GPU timing, bundle loading or performance claim.
    public static class CapacityReplay
    {
        const int N = 262144, B = 262144, Frames = 384;
        const uint Invalid = uint.MaxValue, Seed = 927101;
        public sealed class Result
        {
            public string scenario, snapshotSequenceSha256;
            public int frames, oracleWords, baselineStateWords, baselineCsrWords, exactCsrChecks;
            public int baselineCapacityRebuilds, candidateCapacityRebuilds;
            public long baselineConsumerVisits, candidateConsumerVisits;
            public bool screeningPassed;
        }
        sealed class Model
        {
            public readonly int emptyReserve;
            public readonly uint[] state = new uint[16];
            readonly int[] capacity = new int[B], head = new int[B], offsets = new int[B + 1];
            readonly int[] members = new int[3 * N], position = new int[N], reservation = new int[N];
            readonly int[] seen = new int[N], overflowByCell = new int[B], incoming = new int[B];
            readonly int[] rebuildCounts = new int[B];
            public int zeroCapacityOverflow, nonzeroCapacityOverflow, zeroCapacityCells, nonzeroCapacityCells;
            public int emptyAtRebuildOverflow, emptyAtRebuildCells, maxIncoming;
            public long consumerVisits;
            public Model(int emptyReserve) { this.emptyReserve = emptyReserve; Array.Fill(position, -1); }

            public void Update(uint[] previous, uint[] next, int[] counts, int frame)
            {
                for (int i = 3; i <= 9; i++) state[i] = 0;
                state[13] = 0; state[14] = N;
                Array.Clear(overflowByCell); Array.Clear(incoming);
                zeroCapacityOverflow = nonzeroCapacityOverflow = zeroCapacityCells = nonzeroCapacityCells = 0;
                emptyAtRebuildOverflow = emptyAtRebuildCells = maxIncoming = 0;
                for (int id = 0; id < N; id++)
                {
                    uint old = previous[id], key = next[id];
                    if (key != Invalid) state[9]++;
                    if (old == key) { if (key != Invalid) state[13]++; continue; }
                    state[3]++;
                    if (old != Invalid) state[4]++;
                    if (key == Invalid) continue;
                    state[5]++;
                    int at = head[key]++; reservation[id] = at;
                    incoming[key]++;
                    if (at < capacity[key]) continue;
                    state[6]++; overflowByCell[key]++;
                    if (capacity[key] == 0) zeroCapacityOverflow++; else nonzeroCapacityOverflow++;
                    if (rebuildCounts[key] == 0) emptyAtRebuildOverflow++;
                }
                for (int key = 0; key < B; key++)
                {
                    maxIncoming = Math.Max(maxIncoming, incoming[key]);
                    if (overflowByCell[key] == 0) continue;
                    if (capacity[key] == 0) zeroCapacityCells++; else nonzeroCapacityCells++;
                    if (rebuildCounts[key] == 0) emptyAtRebuildCells++;
                }
                uint reason = state[0] == 0 ? 1u : 0u;
                if (state[3] > N * 200 / 1000) reason |= 4;
                if (state[2] + state[4] > N * 250 / 1000) reason |= 8;
                if (state[6] != 0) reason |= 16;
                state[8] = reason;
                if (reason != 0)
                {
                    int extent = 0;
                    for (int key = 0; key < B; key++)
                    {
                        int count = counts[key]; rebuildCounts[key] = count;
                        capacity[key] = count == 0 ? emptyReserve : count + (count + 1) / 2 + 1;
                        offsets[key] = extent; extent += capacity[key]; head[key] = 0;
                    }
                    offsets[B] = extent;
                    Require(extent <= members.Length, "Candidate exceeded fixed allocation bound");
                    Array.Fill(members, -1, 0, extent);
                    for (int id = 0; id < N; id++)
                    {
                        uint key = next[id];
                        if (key == Invalid) { position[id] = -1; continue; }
                        int at = offsets[key] + head[key]++;
                        members[at] = id; position[id] = at;
                    }
                    state[10] = (uint)extent; state[2] = 0; state[11]++;
                }
                else
                {
                    // Reservation happened before removals, exactly as the shader does.
                    for (int id = 0; id < N; id++)
                        if (previous[id] != next[id] && previous[id] != Invalid) members[position[id]] = -1;
                    for (int id = 0; id < N; id++)
                    {
                        if (previous[id] == next[id]) continue;
                        uint key = next[id]; position[id] = key == Invalid ? -1 : offsets[key] + reservation[id];
                        if (key != Invalid) members[position[id]] = id;
                    }
                    state[2] += state[4]; state[12]++;
                }
                state[0] = 1; state[1] = 0;
                state[15] = reason != 0 ? 11u : state[3] != 0 ? 6u : 3u;
                Validate(next, frame);
            }

            void Validate(uint[] keys, int frame)
            {
                int live = 0;
                for (int key = 0; key < B; key++)
                {
                    Require(head[key] <= capacity[key], "Invalid append head after commit");
                    for (int at = offsets[key]; at < offsets[key + 1]; at++)
                    {
                        int id = members[at]; if (id == -1) continue;
                        Require(id >= 0 && id < N && keys[id] == key && position[id] == at, "CSR member/cell/position mismatch");
                        Require(seen[id] != frame + 1, "Duplicate CSR member"); seen[id] = frame + 1; live++;
                    }
                }
                for (int id = 0; id < N; id++) Require((seen[id] == frame + 1) == (keys[id] != Invalid), "Exact active ID set mismatch");
                Require(live == state[9], "Live count mismatch");
            }

            public void Write(StreamWriter writer, string scenario, int frame, int[][] cells, int[] counts)
            {
                long[] visits = new long[9], compact = new long[9], worstLane = new long[9];
                for (int q = 0; q < 9; q++)
                {
                    var lanes = new long[256];
                    for (int i = 0; i < cells[q].Length; i++)
                    {
                        int key = cells[q][i]; visits[q] += capacity[key]; compact[q] += counts[key];
                        lanes[i % 256] += capacity[key];
                    }
                    worstLane[q] = lanes.Max();
                }
                consumerVisits = scenario == "hotspot-dynamic" ? visits.Sum() : state[10];
                writer.Write(scenario + "," + frame + "," + (emptyReserve == 0 ? "baseline" : "empty-one"));
                foreach (uint word in state) writer.Write("," + word);
                writer.Write("," + (state[10] - state[9]) + "," + zeroCapacityOverflow + "," + nonzeroCapacityOverflow + "," + zeroCapacityCells + "," + nonzeroCapacityCells);
                writer.Write("," + emptyAtRebuildOverflow + "," + emptyAtRebuildCells + "," + maxIncoming + "," + consumerVisits);
                foreach (long value in visits) writer.Write("," + value);
                foreach (long value in compact) writer.Write("," + value);
                foreach (long value in worstLane) writer.Write("," + value);
                writer.WriteLine();
            }
        }

        public static Result Run(string scenario, string oraclePath, string gpuHistoryPath, string output)
        {
            Require(scenario == "hotspot-dynamic" || scenario == "streaming-switch", "Frozen scenario only");
            uint[] oracle = Words(oraclePath), gpu = Words(gpuHistoryPath);
            Require(oracle.Length == Frames * 40 && gpu.Length == Frames * 112, "History length mismatch");
            var result = new Result { scenario = scenario };
            var samples = IntegrationFixture.Create(scenario, Seed); var active = new uint[N];
            Array.Fill(active, 1u, 0, scenario == "streaming-switch" ? N - 32768 : N);
            var previous = new uint[N]; Array.Fill(previous, Invalid);
            var baseline = new Model(0); var candidate = new Model(1);
            var queries = IntegrationFixture.Queries(); var cells = QueryCells(queries);
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var writer = new StreamWriter(output))
            {
                writer.Write("scenario,frame,policy"); for (int i = 0; i < 16; i++) writer.Write(",state" + i);
                writer.Write(",physicalInvalid,zeroCapacityOverflow,nonzeroCapacityOverflow,zeroCapacityCells,nonzeroCapacityCells,emptyAtRebuildOverflow,emptyAtRebuildCells,maxIncoming,consumerVisits");
                foreach (string name in new[] { "rangeVisits", "compactVisits", "worstCellSerialLane" }) for (int i = 0; i < 9; i++) writer.Write("," + name + i);
                writer.WriteLine();
                for (int frame = 0; frame < Frames; frame++)
                {
                    int changed = IntegrationFixture.Advance(scenario, samples, active, frame);
                    if (scenario == "streaming-switch") changed += ApplyContent(frame, samples, active);
                    sha.AppendData(MemoryMarshal.AsBytes(samples.AsSpan())); sha.AppendData(MemoryMarshal.AsBytes(active.AsSpan()));
                    var digests = IntegrationFixture.Oracle(samples, active);
                    for (int q = 0; q < 9; q++)
                    {
                        var d = digests[q]; uint[] words = { d.Count, d.XorHash, d.SumHash0, d.SumHash1 };
                        for (int w = 0; w < 4; w++) { Require(words[w] == oracle[frame * 40 + q * 4 + w], "Original oracle mismatch: " + scenario + "/" + frame + "/" + q); result.oracleWords++; }
                    }
                    var next = new uint[N]; var counts = new int[B]; uint activeCount = 0;
                    for (int id = 0; id < N; id++) { next[id] = active[id] == 1 ? GpuSensorDeterministicGenerator.ComputeKey(samples[id]) : Invalid; if (next[id] != Invalid) { counts[next[id]]++; activeCount++; } }
                    uint[] meta = { (uint)frame, activeCount, N, (uint)changed };
                    for (int w = 0; w < 4; w++) { Require(meta[w] == oracle[frame * 40 + 36 + w], "Original metadata mismatch"); result.oracleWords++; }
                    baseline.Update(previous, next, counts, frame);
                    for (int w = 0; w < 16; w++) { Require(baseline.state[w] == gpu[frame * 112 + 80 + w], "Original GPU state mismatch: " + scenario + "/" + frame + "/word" + w + " cpu=" + baseline.state[w] + " gpu=" + gpu[frame * 112 + 80 + w]); result.baselineStateWords++; }
                    uint[] stats = { activeCount, baseline.state[10] - activeCount, 0, 0, 0, 0, 0, baseline.state[10] };
                    for (int w = 0; w < 8; w++) { Require(stats[w] == gpu[frame * 112 + 104 + w], "Original GPU CSR mismatch"); result.baselineCsrWords++; }
                    // Establish the baseline correspondence before accepting this frame's intervention.
                    candidate.Update(previous, next, counts, frame); result.exactCsrChecks += 2;
                    baseline.Write(writer, scenario, frame, cells, counts); candidate.Write(writer, scenario, frame, cells, counts);
                    if (frame >= 64)
                    {
                        if ((baseline.state[8] & 16) != 0) result.baselineCapacityRebuilds++;
                        if ((candidate.state[8] & 16) != 0) result.candidateCapacityRebuilds++;
                        result.baselineConsumerVisits += baseline.consumerVisits; result.candidateConsumerVisits += candidate.consumerVisits;
                    }
                    previous = next; result.frames++;
                }
                result.snapshotSequenceSha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            result.screeningPassed = result.candidateConsumerVisits <= result.baselineConsumerVisits &&
                (scenario != "streaming-switch" || result.candidateCapacityRebuilds * 2 <= result.baselineCapacityRebuilds);
            return result;
        }

        static int ApplyContent(int frame, GpuSensorSample[] samples, uint[] active)
        {
            if (frame == 128 || frame == 240)
            {
                int bundle = frame == 128 ? 0 : 1, begin = N - 32768 + bundle * 16384;
                for (int i = 0; i < 16384; i++) { var s = IntegrationFixture.ContentSample(bundle, i); s.Payload ^= Seed; samples[begin + i] = s; active[begin + i] = 1; }
                return 16384;
            }
            if (frame == 224 || frame == 320)
            {
                int bundle = frame == 224 ? 0 : 1, begin = N - 32768 + bundle * 16384;
                Array.Clear(active, begin, 16384); return 16384;
            }
            return 0;
        }
        static int[][] QueryCells(GpuSensorRangeQuery[] queries)
        {
            var result = new int[queries.Length][];
            for (int q = 0; q < queries.Length; q++)
            {
                var a = queries[q]; uint r = Math.Min(a.Radius, 65535u);
                int x0 = (int)((a.CenterX - Math.Min(a.CenterX, r)) >> 10), x1 = (int)(Math.Min(a.CenterX + r, 65535u) >> 10);
                int y0 = (int)((a.CenterY - Math.Min(a.CenterY, r)) >> 10), y1 = (int)(Math.Min(a.CenterY + r, 65535u) >> 10);
                int z0 = (int)((a.CenterZ - Math.Min(a.CenterZ, r)) >> 10), z1 = (int)(Math.Min(a.CenterZ + r, 65535u) >> 10);
                var keys = new int[(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1)]; int at = 0;
                for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) keys[at++] = x | y << 6 | z << 12;
                result[q] = keys;
            }
            return result;
        }
        static uint[] Words(string path) { byte[] bytes = File.ReadAllBytes(path); var words = new uint[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length); return words; }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
