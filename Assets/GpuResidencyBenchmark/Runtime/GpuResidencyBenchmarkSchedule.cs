using System;
using System.Collections.Generic;

internal enum GpuResidencyBenchmarkVariant
{
    RebuildVisibleSet = 0,
    PersistentLru = 1
}

internal readonly struct GpuResidencyScheduleEntry
{
    public GpuResidencyScheduleEntry(
        int blockIndex,
        GpuResidencyBenchmarkVariant variant,
        int superRound,
        int sequencePosition,
        int pairIndex,
        string pairOrder,
        int withinPairPosition)
    {
        BlockIndex = blockIndex;
        Variant = variant;
        SuperRound = superRound;
        SequencePosition = sequencePosition;
        PairIndex = pairIndex;
        PairOrder = pairOrder;
        WithinPairPosition = withinPairPosition;
    }

    public int BlockIndex { get; }
    public GpuResidencyBenchmarkVariant Variant { get; }
    public int SuperRound { get; }
    public int SequencePosition { get; }
    public int PairIndex { get; }
    public string PairOrder { get; }
    public int WithinPairPosition { get; }
}

internal static class GpuResidencyBenchmarkSchedule
{
    public static IReadOnlyList<GpuResidencyScheduleEntry> Build(
        int superRoundCount)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }
        var result = new List<GpuResidencyScheduleEntry>(
            superRoundCount * 4);
        int block = 1;
        int pair = 1;
        for (int round = 1; round <= superRoundCount; round++)
        {
            bool abba = (round & 1) != 0;
            GpuResidencyBenchmarkVariant[] variants = abba
                ? new[]
                {
                    GpuResidencyBenchmarkVariant.RebuildVisibleSet,
                    GpuResidencyBenchmarkVariant.PersistentLru,
                    GpuResidencyBenchmarkVariant.PersistentLru,
                    GpuResidencyBenchmarkVariant.RebuildVisibleSet
                }
                : new[]
                {
                    GpuResidencyBenchmarkVariant.PersistentLru,
                    GpuResidencyBenchmarkVariant.RebuildVisibleSet,
                    GpuResidencyBenchmarkVariant.RebuildVisibleSet,
                    GpuResidencyBenchmarkVariant.PersistentLru
                };
            for (int position = 1; position <= 4; position++)
            {
                int within = ((position - 1) & 1) + 1;
                string order = variants[position - within] ==
                    GpuResidencyBenchmarkVariant.RebuildVisibleSet
                    ? "AB"
                    : "BA";
                result.Add(new GpuResidencyScheduleEntry(
                    block++,
                    variants[position - 1],
                    round,
                    position,
                    pair,
                    order,
                    within));
                if (within == 2)
                {
                    pair++;
                }
            }
        }
        return result;
    }
}
