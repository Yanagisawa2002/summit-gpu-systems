using System;
using System.Collections.Generic;

internal enum GpuDeadlineSchedulerBenchmarkVariant
{
    FifoGraphics = 0,
    DeadlineAwareAdaptive = 1
}

internal readonly struct GpuDeadlineSchedulerScheduleEntry
{
    public GpuDeadlineSchedulerScheduleEntry(
        int blockIndex,
        GpuDeadlineSchedulerBenchmarkVariant variant,
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

    public GpuDeadlineSchedulerBenchmarkVariant Variant { get; }

    public int SuperRound { get; }

    public int SequencePosition { get; }

    public int PairIndex { get; }

    public string PairOrder { get; }

    public int WithinPairPosition { get; }
}

internal static class GpuDeadlineSchedulerBenchmarkSchedule
{
    public const int StateCount = 64;

    public static IReadOnlyList<GpuDeadlineSchedulerScheduleEntry> Build(
        int superRoundCount)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }

        var result = new List<GpuDeadlineSchedulerScheduleEntry>(
            checked(superRoundCount * 4));
        int blockIndex = 1;
        int pairIndex = 1;
        for (int superRound = 1;
            superRound <= superRoundCount;
            superRound++)
        {
            bool abba = (superRound & 1) != 0;
            GpuDeadlineSchedulerBenchmarkVariant[] variants = abba
                ? new[]
                {
                    GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics,
                    GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive,
                    GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive,
                    GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics
                }
                : new[]
                {
                    GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive,
                    GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics,
                    GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics,
                    GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive
                };
            for (int position = 1; position <= variants.Length; position++)
            {
                int withinPairPosition = ((position - 1) & 1) + 1;
                string pairOrder = variants[position - withinPairPosition] ==
                    GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics
                    ? "AB"
                    : "BA";
                result.Add(new GpuDeadlineSchedulerScheduleEntry(
                    blockIndex++,
                    variants[position - 1],
                    superRound,
                    position,
                    pairIndex,
                    pairOrder,
                    withinPairPosition));
                if (withinPairPosition == 2)
                {
                    pairIndex++;
                }
            }
        }
        return result;
    }

    public static int LogicalState(int zeroBasedSample)
    {
        if (zeroBasedSample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedSample));
        }
        return zeroBasedSample % StateCount;
    }
}
