using System;
using System.Collections.Generic;
using System.Text;

internal enum GpuAdaptiveBinningBenchmarkVariant
{
    Control,
    Direct,
    Radix
}

internal readonly struct GpuAdaptiveBinningScheduleEntry
{
    public GpuAdaptiveBinningScheduleEntry(
        int blockIndex,
        string blockType,
        GpuAdaptiveBinningBenchmarkVariant variant,
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

    public GpuAdaptiveBinningBenchmarkVariant Variant { get; }

    public int SuperRound { get; }

    public int SequencePosition { get; }

    public int PairIndex { get; }

    public string PairOrder { get; }

    public int WithinPairPosition { get; }
}

internal static class GpuAdaptiveBinningBenchmarkSchedule
{
    public const int DiscoverySuperRoundCount = 2;
    public const int FormalSuperRoundCount = 4;

    public static string Contract(int superRoundCount)
    {
        ValidateSuperRoundCount(superRoundCount);

        var result = new StringBuilder(
            checked(24 + superRoundCount * 5));
        result.Append("control-pre");
        for (int superRound = 1;
             superRound <= superRoundCount;
             superRound++)
        {
            result.Append((superRound & 1) != 0
                ? ";ABBA"
                : ";BAAB");
        }
        result.Append(";control-post");
        return result.ToString();
    }

    public static IReadOnlyList<GpuAdaptiveBinningScheduleEntry> Build(
        int superRoundCount)
    {
        ValidateSuperRoundCount(superRoundCount);

        List<GpuAdaptiveBinningScheduleEntry> result =
            new List<GpuAdaptiveBinningScheduleEntry>(
                checked(superRoundCount * 4 + 2));
        int blockIndex = 1;
        int pairIndex = 1;
        result.Add(new GpuAdaptiveBinningScheduleEntry(
            blockIndex++,
            "control-pre",
            GpuAdaptiveBinningBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));

        for (int superRound = 1;
             superRound <= superRoundCount;
             superRound++)
        {
            bool abba = (superRound & 1) != 0;
            GpuAdaptiveBinningBenchmarkVariant[] variants = abba
                ? new[]
                {
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct
                }
                : new[]
                {
                    GpuAdaptiveBinningBenchmarkVariant.Radix,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Direct,
                    GpuAdaptiveBinningBenchmarkVariant.Radix
                };

            for (int sequencePosition = 1;
                 sequencePosition <= 4;
                 sequencePosition++)
            {
                int withinPairPosition =
                    ((sequencePosition - 1) & 1) + 1;
                GpuAdaptiveBinningBenchmarkVariant first =
                    variants[((sequencePosition - 1) / 2) * 2];
                string pairOrder =
                    first == GpuAdaptiveBinningBenchmarkVariant.Direct
                        ? "AB"
                        : "BA";
                result.Add(new GpuAdaptiveBinningScheduleEntry(
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

        result.Add(new GpuAdaptiveBinningScheduleEntry(
            blockIndex,
            "control-post",
            GpuAdaptiveBinningBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));
        return result;
    }

    private static void ValidateSuperRoundCount(int superRoundCount)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }
    }
}
