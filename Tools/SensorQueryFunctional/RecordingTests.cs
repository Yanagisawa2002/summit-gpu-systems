using System;
using Summit.GpuSensorPipeline;
using UnityEngine;
using UnityEngine.Rendering;

internal static class RecordingTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static GraphicsBuffer Buffer(int count, int stride = 4) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
    private static void Main()
    {
        QueryRecording(); CompactRecording();
        Require(GraphicsBuffer.LiveBuffers == 0, "Owned resources leaked.");
        Console.WriteLine("PASS: CPU command-recording contracts, validation, bindings, lifetimes and opt-in gates. No Unity/native libraries loaded.");
    }
    private static void QueryRecording()
    {
        using var samples = Buffer(257, 16); using var offsets = Buffer(262145); using var ids = Buffer(771);
        using var queries = Buffer(5, 16); using var digests = Buffer(5, 16);
        using var query = new GpuSensorCellSpanQuery(257, indexEntryCapacity: 771);
        var commands = new CommandBuffer();
        query.Record(commands, samples, offsets, ids, queries, digests, 257, 1, 0);
        Require(commands.Records.Count == 0, "Zero queries recorded work.");
        query.Record(commands, samples, offsets, ids, queries, digests, 257, 1, 2, true);
        Require(commands.Records.Count == 6, "Segment should have three commands per query.");
        for (int i = 0; i < commands.Records.Count; i++)
        {
            var r = commands.Records[i];
            Require(r.Integers["_QueryIndex"] == 1 + i / 3 && r.Integers["_QuantizeIntensity"] == 1 &&
                r.Integers["_ElementCount"] == 257, "Segment/quantization parameters lost.");
            if (i % 3 == 0) Require(r.X == 16 && r.Buffers["_QueryDigests"] == digests && r.Buffers["_BinOffsets"] == offsets, "Build binding lost.");
            if (i % 3 == 2) Require(r.Indirect != null && r.Offset == 0 && r.Buffers["_BinnedIds"] == ids && r.Buffers["_Samples"] == samples, "Consume binding lost.");
        }
        int before = commands.Records.Count;
        Reject<ArgumentException>(() => query.Record(commands, samples, offsets, ids, queries, queries, 257, 0, 1));
        Reject<ArgumentOutOfRangeException>(() => query.Record(commands, samples, offsets, ids, queries, digests, 258, 0, 1));
        Reject<ArgumentException>(() => query.Record(commands, samples, offsets, ids, queries, digests, 257, 4, 2));
        using var tooMany = Buffer(772);
        Reject<ArgumentException>(() => query.Record(commands, samples, offsets, tooMany, queries, digests, 257, 0, 1));
        Require(commands.Records.Count == before, "Validation failed after partial recording.");
        query.Record(commands, samples, offsets, ids, queries, digests, 0, 0, 1);
        Require(commands.Records[before].Integers["_ElementCount"] == 0 && commands.Records[before].Integers["_QuantizeIntensity"] == 0,
            "Scratch reuse retained stale parameters.");
        query.Dispose(); query.Dispose();
        Reject<ObjectDisposedException>(() => query.Record(commands, samples, offsets, ids, queries, digests, 0, 0, 0));
        GpuSensorChunkedRangeQuery.SupportsWaveOperations = false;
        Reject<NotSupportedException>(() => new GpuSensorCellSpanQuery(257, GpuSensorQueryBackend.CellSpansWave));
        GpuSensorChunkedRangeQuery.SupportsWaveOperations = true;
        using var wave = new GpuSensorCellSpanQuery(257, GpuSensorQueryBackend.CellSpansWave);
        Resources.Available = false;
        Reject<InvalidOperationException>(() => new GpuSensorCellSpanQuery(257));
        Resources.Available = true;
    }
    private static void CompactRecording()
    {
        using var input = Buffer(257, 16); using var active = Buffer(257);
        using var disabled = new GpuSensorIncrementalIndex(257);
        using var compact = new GpuSensorCompactIndexView(257);
        var commands = new CommandBuffer();
        Reject<InvalidOperationException>(() => compact.Record(commands, disabled));
        Require(commands.Records.Count == 0, "Disabled live counts were used.");
        foreach (var mode in new[] { GpuSensorIndexExecutionMode.Original, GpuSensorIndexExecutionMode.GpuDriven })
        {
            using var index = new GpuSensorIncrementalIndex(257, 128, executionMode: mode, maintainLiveCounts: true);
            commands = new CommandBuffer();
            index.RecordUpdate(commands, input, active);
            Require(commands.Records.Count == 13, "Index update contract changed.");
            Require(commands.Records[0].Integers["_MaintainLiveCounts"] == 1, "Live count flag not recorded.");
            var producerCounts = commands.Records[3].Buffers["_Counts"];
            compact.Record(commands, index);
            Require(commands.Records.Count == 17 && commands.Records[13].Buffers["_LiveCounts"] == producerCounts,
                "Compact view did not consume the maintained counts.");
            Require(commands.Records[16].Buffers["_CurrentKeys"] == commands.Records[1].Buffers["_PreviousKeys"] &&
                commands.Records[16].Buffers["_Members"] == compact.BinnedIds, "Compact stable-slot scatter binding lost.");
            Require(compact.BinnedIds.count == index.Capacity && compact.BinOffsets.count == 262145, "Compact allocation shape mismatch.");
            index.Dispose();
            Reject<ObjectDisposedException>(() => compact.Record(commands, index));
        }
        using var wrongCapacity = new GpuSensorIncrementalIndex(256, maintainLiveCounts: true);
        Reject<ArgumentException>(() => compact.Record(commands, wrongCapacity));
        compact.Dispose(); compact.Dispose();
        Reject<ObjectDisposedException>(() => compact.Record(commands, disabled));
        Require(GpuSensorCompactIndexView.CalculateResidentBytes(257) == 4L * (257 + 2 * 262144 + 1 + 1024), "Compact allocation accounting mismatch.");
    }
}
