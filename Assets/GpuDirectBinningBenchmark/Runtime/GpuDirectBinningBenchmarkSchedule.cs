using System;
using System.Collections.Generic;

internal enum GpuDirectBinningBenchmarkVariant
{
    Control,
    Reference,
    Direct
}

internal readonly struct GpuDirectBinningScheduleEntry
{
    public GpuDirectBinningScheduleEntry(
        int blockIndex,
        string blockType,
        GpuDirectBinningBenchmarkVariant variant,
        int superRound,
        int sequencePosition,
        int pairIndex,
        string pairOrder,
        int withinPairPosition)
    {
        BlockIndex = blockIndex;
        BlockType = blockType;
        Variant = variant;
        SuperRound = superRound;
        SequencePosition = sequencePosition;
        PairIndex = pairIndex;
        PairOrder = pairOrder;
        WithinPairPosition = withinPairPosition;
    }

    public int BlockIndex { get; }

    public string BlockType { get; }

    public GpuDirectBinningBenchmarkVariant Variant { get; }

    public int SuperRound { get; }

    public int SequencePosition { get; }

    public int PairIndex { get; }

    public string PairOrder { get; }

    public int WithinPairPosition { get; }
}

internal static class GpuDirectBinningBenchmarkSchedule
{
    public const int FormalSuperRoundCount = 2;

    public static IReadOnlyList<GpuDirectBinningScheduleEntry> Build(
        int superRoundCount)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }

        List<GpuDirectBinningScheduleEntry> result =
            new List<GpuDirectBinningScheduleEntry>(
                checked(superRoundCount * 4 + 2));
        int blockIndex = 1;
        int pairIndex = 1;
        result.Add(new GpuDirectBinningScheduleEntry(
            blockIndex++,
            "control-pre",
            GpuDirectBinningBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));

        for (int superRound = 1; superRound <= superRoundCount; superRound++)
        {
            bool abba = (superRound & 1) != 0;
            GpuDirectBinningBenchmarkVariant[] variants = abba
                ? new[]
                {
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Reference
                }
                : new[]
                {
                    GpuDirectBinningBenchmarkVariant.Direct,
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Reference,
                    GpuDirectBinningBenchmarkVariant.Direct
                };

            for (int sequencePosition = 1; sequencePosition <= 4; sequencePosition++)
            {
                int withinPairPosition = ((sequencePosition - 1) & 1) + 1;
                GpuDirectBinningBenchmarkVariant first =
                    variants[((sequencePosition - 1) / 2) * 2];
                string pairOrder =
                    first == GpuDirectBinningBenchmarkVariant.Reference
                        ? "AB"
                        : "BA";
                result.Add(new GpuDirectBinningScheduleEntry(
                    blockIndex++,
                    "measurement",
                    variants[sequencePosition - 1],
                    superRound,
                    sequencePosition,
                    pairIndex,
                    pairOrder,
                    withinPairPosition));
                if (withinPairPosition == 2)
                {
                    pairIndex++;
                }
            }
        }

        result.Add(new GpuDirectBinningScheduleEntry(
            blockIndex,
            "control-post",
            GpuDirectBinningBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));
        return result;
    }
}
