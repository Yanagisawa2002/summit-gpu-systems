using System;
using System.Collections.Generic;
using System.Linq;
using Summit.GpuSensorPipeline;

internal static class Program
{
    private static void Main()
    {
        LayoutContracts(); DirectoryBoundaries(); ScanProperties(); QueryProperties(); LiveCountTransitions(); PlannerContracts();
        Console.WriteLine("PASS: six deterministic CPU functional suites (layout, directory, scan, queries, live counts, planner). No GPU, clocks, counters, Player or benchmark execution.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid input was accepted.");
    }
    private static void LayoutContracts()
    {
        Reject(() => GpuSensorCellSpanLayout.MaximumChunkCount(0));
        Reject(() => GpuSensorCellSpanLayout.MaximumChunkCount(int.MaxValue));
        Reject(() => GpuSensorCellSpanLayout.GetSpanCount(new GpuSensorRangeQuery(65536, 0, 0, 0)));
        Reject(() => GpuSensorCellSpanLayout.GetSpanCount(new GpuSensorRangeQuery(0, 0, 0, 65536)));
        var queries = new List<GpuSensorRangeQuery>();
        foreach (uint center in new uint[] { 0, 1, 1023, 1024, 32768, 65534, 65535 })
            foreach (uint radius in new uint[] { 0, 1, 1023, 1024, 65535 })
                queries.Add(new GpuSensorRangeQuery(center, center, center, radius));
        foreach (var q in queries)
        {
            int rows = GpuSensorCellSpanLayout.GetSpanCount(q);
            int previous = -1, count = 0;
            for (int row = 0; row < rows; row++)
            {
                GpuSensorCellSpanLayout.GetSpanCells(q, row, out int first, out int end);
                Check(first > previous && end > first && end <= 262144, "Overlapping/out-of-bounds row.");
                Check(first / 64 == (end - 1) / 64, "Span crosses a y/z row.");
                for (int cell = first; cell < end; cell++)
                {
                    long x = (cell % 64) * 1024, y = (cell / 64 % 64) * 1024, z = (cell / 4096) * 1024;
                    Check(x <= (long)q.CenterX + q.Radius && x + 1023 >= (long)q.CenterX - q.Radius &&
                        y <= (long)q.CenterY + q.Radius && y + 1023 >= (long)q.CenterY - q.Radius &&
                        z <= (long)q.CenterZ + q.Radius && z + 1023 >= (long)q.CenterZ - q.Radius, "Noncandidate cell visited.");
                    count++;
                }
                previous = end - 1;
            }
            long extent = Math.Min(65535L, q.CenterX + q.Radius) / 1024 - Math.Max(0L, (long)q.CenterX - q.Radius) / 1024 + 1;
            Check(count == extent * extent * extent, "Candidate cell missing.");
            Reject(() => GpuSensorCellSpanLayout.GetSpanCells(q, rows, out _, out _));
        }
    }
    private static void DirectoryBoundaries()
    {
        var ranges = new (uint, uint)[4096];
        uint cursor = 0;
        var expected = new List<(uint, uint)>();
        for (int row = 0; row < ranges.Length; row++)
        {
            // Empty blocks and repeated prefix values surround single-entry/tail chunks.
            uint length = row < 512 || row % 5 != 0 ? 0u : (uint)(row % 3 == 0 ? 257 : 1);
            ranges[row] = (cursor, cursor + length);
            for (uint start = cursor; start < cursor + length; start += 256)
                expected.Add((start, Math.Min(cursor + length, start + 256)));
            cursor += length;
        }
        var directory = new ReferenceModels.Directory(ranges);
        Check(directory.Count == expected.Count, "Directory total mismatch.");
        for (uint chunk = 0; chunk < directory.Count; chunk++)
            Check(directory.Resolve(chunk) == expected[(int)chunk], "Directory upper_bound lost/repeated a chunk.");
        Check(directory.Resolve(directory.Count) == (0, 0), "Padded chunk contributes.");
        Check(directory.Count <= GpuSensorCellSpanLayout.MaximumChunkCount((int)cursor), "Chunk bound insufficient.");
        var empty = new ReferenceModels.Directory(Array.Empty<(uint, uint)>());
        Check(empty.Count == 0 && empty.Resolve(0) == (0, 0), "Empty directory not cleared.");

        // Exercise the two-dimensional group boundary without allocating a large payload.
        uint max = GpuSensorCellSpanLayout.MaxEntryCapacity;
        cursor = 0;
        for (int row = 0; row < ranges.Length; row++)
        {
            uint length = max / 4096 + (row < max % 4096 ? 1u : 0u);
            ranges[row] = (cursor, cursor + length); cursor += length;
        }
        directory = new ReferenceModels.Directory(ranges);
        Check(directory.Count > 65535 && directory.Count <= GpuSensorCellSpanLayout.MaximumChunkCount((int)max), "2D boundary fixture invalid.");
        Check(directory.Resolve(65535).Item1 < max && directory.Resolve(directory.Count - 1).Item2 == max, "Last real 2D chunk missing.");
        Check(directory.Resolve(65535u * 2u - 1u) == (0, 0), "2D padding contributes.");
    }
    private static void QueryProperties()
    {
        var random = new Random(71903);
        foreach (int capacity in new[] { 1, 255, 256, 257, 1025, 4097 })
        foreach (bool hotspot in new[] { false, true })
        {
            var samples = new GpuSensorSample[capacity]; var active = new uint[capacity];
            for (int id = 0; id < capacity; id++)
            {
                samples[id] = new GpuSensorSample((uint)random.Next(65536), (uint)random.Next(65536),
                    (uint)random.Next(65536), unchecked((uint)random.NextInt64(0, 1L << 32)));
                if (hotspot && id % 101 != 0) samples[id] = new GpuSensorSample(32768, 32768, 32768, samples[id].Payload);
                active[id] = id % 7 != 3 ? 1u : 0u;
            }
            samples[0] = new GpuSensorSample(65535, 65535, 65535, uint.MaxValue); active[0] = 1;
            var csr = ReferenceModels.Reserved(samples, active);
            var queries = new List<GpuSensorRangeQuery> {
                new(0, 0, 0, 65535), new(32768, 32768, 32768, 0), new(1024, 1024, 1024, 1),
                new(65535, 65535, 65535, 0), new(0, 0, 0, 0), new(65535, 0, 32768, 1024) };
            for (int q = 0; q < 8; q++) queries.Add(new GpuSensorRangeQuery((uint)random.Next(65536),
                (uint)random.Next(65536), (uint)random.Next(65536), (uint)random.Next(20000)));
            foreach (var query in queries)
            {
                var actual = ReferenceModels.QueryIds(samples, capacity, csr.Offsets, csr.Ids, query);
                var expectedIds = Enumerable.Range(0, capacity).Where(id => active[id] == 1 &&
                    Math.Abs((long)samples[id].X - query.CenterX) <= query.Radius &&
                    Math.Abs((long)samples[id].Y - query.CenterY) <= query.Radius &&
                    Math.Abs((long)samples[id].Z - query.CenterZ) <= query.Radius).Select(id => (uint)id);
                Check(actual.SequenceEqual(expectedIds), "Candidate enumeration omitted/duplicated an active ID.");
                Check(ReferenceModels.Digest(samples, actual, false) == GpuSensorBenchmarkOracle.Query(samples, capacity, query, active), "Digest mismatch.");
                var quantized = (GpuSensorSample[])samples.Clone();
                for (int id = 0; id < capacity; id++) quantized[id].Payload = GpuSensorIntensityQuantizer.QuantizeToUInt(samples[id].Payload);
                Check(ReferenceModels.Digest(samples, actual, true) == GpuSensorBenchmarkOracle.Query(quantized, capacity, query, active), "Quantized digest mismatch.");
                Check(ReferenceModels.QueryIds(samples, 0, csr.Offsets, csr.Ids, query).Count == 0, "Zero element bound is not empty.");
            }
            // Non-max out-of-range IDs must be treated like tombstones too.
            for (int i = 0; i < csr.Ids.Length; i++) if (csr.Ids[i] == uint.MaxValue) csr.Ids[i] = (uint)capacity;
            var all = new GpuSensorRangeQuery(0, 0, 0, 65535);
            Check(ReferenceModels.Digest(samples, ReferenceModels.QueryIds(samples, capacity, csr.Offsets, csr.Ids, all), false) ==
                GpuSensorBenchmarkOracle.Query(samples, capacity, all, active), "Invalid ID was counted.");
        }
    }
    private static void ScanProperties()
    {
        foreach (int pattern in new[] { 0, 1, 2, 3, 4 })
        {
            var values = new uint[1024]; var expected = new uint[1024]; uint total = 0;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = pattern == 0 ? 0u : pattern == 1 ? 1u : pattern == 2 ? (i == 1023 ? 65535u : 0u) :
                    pattern == 3 ? (i == 0 ? 65535u : 0u) : (uint)(i % 7 * 13);
                expected[i] = total; total += values[i];
            }
            Check(ReferenceModels.ScanBlocks(values) == total && values.SequenceEqual(expected), "Block scan disagrees with serial prefix oracle.");
        }
    }
    private static void LiveCountTransitions()
    {
        const int capacity = 513, staticSlots = 128;
        var input = new GpuSensorSample[capacity]; var active = new uint[capacity];
        for (int id = 0; id < capacity; id++) { input[id] = new GpuSensorSample(32768, 32768, 32768, (uint)id); active[id] = 1; }
        var index = new ReferenceModels.LiveIndex(capacity, staticSlots);
        Check(index.Compact().Ids.Length == 0, "Never-updated view should be empty.");
        uint revision = 0;
        for (int frame = 0; frame < 18; frame++)
        {
            // Payload-only change, movement, removal, reactivation, invalid input,
            // revision refresh, forced/capacity rebuild, and total empty transitions.
            input[130].Payload = (uint)(frame * 65537);
            input[131].X = frame % 2 == 0 ? 0u : 65535u;
            active[132] = (uint)(frame % 2);
            active[133] = frame % 3 == 0 ? 2u : 1u;
            input[134].Z = frame % 3 == 0 ? 65536u : 1023u;
            input[0].X = (uint)(frame * 1024);
            if (frame == 4 || frame == 9) revision++;
            if (frame == 16) { Array.Clear(active); revision++; }
            bool rebuild = frame == 7 || frame == 12;
            index.Update(input, active, revision, rebuild);
            var compact = index.Compact(); var seen = new HashSet<uint>();
            var freshCounts = new uint[ReferenceModels.Bins];
            for (int id = 0; id < capacity; id++) if (index.Keys[id] != ReferenceModels.Invalid) freshCounts[index.Keys[id]]++;
            Check(index.Counts.SequenceEqual(freshCounts), "Maintained counts became stale.");
            for (int cell = 0; cell < ReferenceModels.Bins; cell++)
            {
                Check(compact.Offsets[cell + 1] - compact.Offsets[cell] == freshCounts[cell], "Compact interval count mismatch.");
                for (uint p = compact.Offsets[cell]; p < compact.Offsets[cell + 1]; p++)
                {
                    uint id = compact.Ids[p];
                    Check(id < capacity && index.Keys[id] == cell && seen.Add(id), "Compact membership invalid or duplicate.");
                }
            }
            Check(seen.SetEquals(Enumerable.Range(0, capacity).Where(id => index.Keys[id] != ReferenceModels.Invalid).Select(id => (uint)id)), "Compact membership incomplete.");
            var flags = index.Keys.Select(k => k == ReferenceModels.Invalid ? 0u : 1u).ToArray();
            foreach (var query in new[] { new GpuSensorRangeQuery(0, 0, 0, 65535), new GpuSensorRangeQuery(32768, 32768, 32768, 0) })
                Check(ReferenceModels.Digest(index.Samples, ReferenceModels.QueryIds(index.Samples, capacity, compact.Offsets, compact.Ids, query), false) ==
                    GpuSensorBenchmarkOracle.Query(index.Samples, capacity, query, flags), "Compact consumer digest mismatch.");
            if (frame == 1) Check(index.Samples[0].X == 0, "Static slot refreshed without a revision.");
            if (frame == 4 || frame == 7) Check(index.Samples[0].X == input[0].X, "Static slot not refreshed.");
        }
    }
    private static void PlannerContracts()
    {
        var w = new GpuSensorIndexQueryWorkload {
            Capacity = 262144, ActiveCount = 200000, InspectedSlotCount = 256, MembershipChanges = 8,
            ReservedExtent = 600000, IndexEntryCapacity = 786432, QueryCount = 64, ConsumerPasses = 1,
            CandidateReservedVisits = 64L * 600000, CandidateLiveVisits = 64L * 200000,
            CandidateSpanCount = 4096 * 64, HasExactQueryStructure = true, IncrementalStateValid = true };
        var disabled = GpuSensorIndexQueryPlanner.Select(w);
        Check(disabled.IndexMode == GpuSensorIndexPlanMode.FullRebuild && disabled.QueryBackend == GpuSensorQueryBackend.CellSerial &&
            disabled.TotalWorkUnits == -1 && disabled.EvidenceStatus == "Unmeasured", "Default promoted a candidate or invented a zero cost.");
        var compact = GpuSensorIndexQueryPlanner.Select(w, true, false, true, true, 8 * 1024 * 1024, 0);
        Check(compact.IndexMode == GpuSensorIndexPlanMode.IncrementalCompactView && compact.ConversionWorkUnits > 0 &&
            compact.AdditionalResidentBytes > GpuSensorCellSpanLayout.ScratchBytes, "Compact conversion was not charged/selected.");
        var expensiveConversion = w; expensiveConversion.QueryCount = 1; expensiveConversion.CandidateSpanCount = 1;
        expensiveConversion.CandidateReservedVisits = 10; expensiveConversion.CandidateLiveVisits = 5;
        var incremental = GpuSensorIndexQueryPlanner.Select(expensiveConversion, true, false, true, true, 8 * 1024 * 1024, 0);
        Check(incremental.IndexMode == GpuSensorIndexPlanMode.Incremental, "Small query incorrectly paid a compact conversion.");
        var noBudget = GpuSensorIndexQueryPlanner.Select(w, true, false, true, true, 0, 0);
        Check(noBudget.AdditionalResidentBytes == 0 && noBudget.QueryBackend == GpuSensorQueryBackend.CellSerial &&
            noBudget.IndexMode != GpuSensorIndexPlanMode.IncrementalCompactView, "Memory budget ignored.");
        var knownFailure = w; knownFailure.CapacityFallbackKnown = true;
        var recovery = GpuSensorIndexQueryPlanner.Select(knownFailure, true, true, true, true, 8 * 1024 * 1024, 0);
        Check(recovery.IndexMode == GpuSensorIndexPlanMode.FullRebuild && recovery.Reason == GpuSensorIndexPlanReason.CapacityFallbackKnown,
            "Capacity failure retried reserved maintenance.");
        var force = w; force.ForceFullRebuild = true;
        Check(GpuSensorIndexQueryPlanner.Select(force, true, true, true, true, 8 * 1024 * 1024).IndexMode == GpuSensorIndexPlanMode.FullRebuild, "Forced full rebuild ignored.");
        var unknown = w; unknown.HasExactQueryStructure = false;
        Check(GpuSensorIndexQueryPlanner.Select(unknown, true, true, true, true, 8 * 1024 * 1024).Reason == GpuSensorIndexPlanReason.QueryStructureUnknown,
            "Unknown structure guessed a candidate.");
        var stale = w; stale.IncrementalStateValid = false;
        Check(GpuSensorIndexQueryPlanner.Select(stale, true, true, true, true, 8 * 1024 * 1024).IndexMode == GpuSensorIndexPlanMode.FullRebuild,
            "Stale incremental index reused.");
        var repeated = w; repeated.ConsumerPasses = 7;
        var repeatedPlan = GpuSensorIndexQueryPlanner.Select(repeated, true, false, true, true, 8 * 1024 * 1024, 0);
        Check(repeatedPlan.QueryWorkUnits == compact.QueryWorkUnits * 7 && repeatedPlan.ConversionWorkUnits == compact.ConversionWorkUnits,
            "Repeated consumers do not amortize a single conversion correctly.");
        var broad = GpuSensorIndexQueryPlanner.Select(w, true, true, true, true, 8 * 1024 * 1024, 0);
        Check(broad.QueryBackend == GpuSensorQueryBackend.BatchedPointScanWave, "Full-domain batch predicate work not considered.");
        var narrow = GpuSensorIndexQueryPlanner.Select(expensiveConversion, true, true, true, true, 8 * 1024 * 1024, 0);
        Check(narrow.QueryBackend == GpuSensorQueryBackend.CellSpansWave, "Sparse range pruning not considered.");
        var huge = w; huge.QueryCount = 65535; huge.ConsumerPasses = 65535;
        huge.CandidateReservedVisits = (long)huge.QueryCount * huge.ReservedExtent;
        huge.CandidateLiveVisits = (long)huge.QueryCount * huge.ActiveCount; huge.CandidateSpanCount = 4096L * huge.QueryCount;
        Check(GpuSensorIndexQueryPlanner.Select(huge, true, true, true, true, long.MaxValue).TotalWorkUnits > 0, "Large theory count overflow.");
        var invalid = w; invalid.CandidateLiveVisits = long.MaxValue;
        Reject(() => GpuSensorIndexQueryPlanner.Select(invalid, true));
        invalid = w; invalid.ReservedExtent = 1;
        Reject(() => GpuSensorIndexQueryPlanner.Select(invalid));
        invalid = w; invalid.InspectedSlotCount = 0;
        Reject(() => GpuSensorIndexQueryPlanner.Select(invalid));
        invalid = w; invalid.CandidateLiveVisits = 0;
        Reject(() => GpuSensorIndexQueryPlanner.Select(invalid));
        invalid = w; invalid.IndexEntryCapacity = 0;
        Reject(() => GpuSensorIndexQueryPlanner.Select(invalid));
        Reject(() => GpuSensorIndexQueryPlanner.Select(w, additionalMemoryBudgetBytes: -1));
        Reject(() => GpuSensorIndexQueryPlanner.Select(w, minimumWorkReductionPermille: 1001));
        Check(GpuSensorIndexQueryPlanner.Select(w, true, false, true, true, 8 * 1024 * 1024, 1000).IndexMode == GpuSensorIndexPlanMode.FullRebuild,
            "100-percent reduction gate must retain the full plan.");
    }
}
