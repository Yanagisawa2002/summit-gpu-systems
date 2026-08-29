using System;
using System.Collections.Generic;

internal enum GpuDrivenInstanceBenchmarkVariant
{
    Control,
    Reference,
    Direct
}

internal readonly struct GpuDrivenInstanceScheduleEntry
{
    public GpuDrivenInstanceScheduleEntry(
        int blockIndex,
        string blockType,
        GpuDrivenInstanceBenchmarkVariant variant,
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

    public GpuDrivenInstanceBenchmarkVariant Variant { get; }

    public int SuperRound { get; }

    public int SequencePosition { get; }

    public int PairIndex { get; }

    public string PairOrder { get; }

    public int WithinPairPosition { get; }
}

internal static class GpuDrivenInstanceBenchmarkSchedule
{
    public const int FormalSuperRoundCount = 2;

    public static IReadOnlyList<GpuDrivenInstanceScheduleEntry> Build(
        int superRoundCount)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }

        List<GpuDrivenInstanceScheduleEntry> result =
            new List<GpuDrivenInstanceScheduleEntry>(
                checked(superRoundCount * 4 + 2));
        int blockIndex = 1;
        int pairIndex = 1;
        result.Add(new GpuDrivenInstanceScheduleEntry(
            blockIndex++,
            "control-pre",
            GpuDrivenInstanceBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));

        for (int superRound = 1; superRound <= superRoundCount; superRound++)
        {
            bool abba = (superRound & 1) != 0;
            GpuDrivenInstanceBenchmarkVariant[] variants = abba
                ? new[]
                {
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Reference
                }
                : new[]
                {
                    GpuDrivenInstanceBenchmarkVariant.Direct,
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Reference,
                    GpuDrivenInstanceBenchmarkVariant.Direct
                };

            for (int sequencePosition = 1; sequencePosition <= 4; sequencePosition++)
            {
                int withinPairPosition = ((sequencePosition - 1) & 1) + 1;
                GpuDrivenInstanceBenchmarkVariant first =
                    variants[((sequencePosition - 1) / 2) * 2];
                string pairOrder =
                    first == GpuDrivenInstanceBenchmarkVariant.Reference
                        ? "AB"
                        : "BA";
                result.Add(new GpuDrivenInstanceScheduleEntry(
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

        result.Add(new GpuDrivenInstanceScheduleEntry(
            blockIndex,
            "control-post",
            GpuDrivenInstanceBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));
        return result;
    }
}
