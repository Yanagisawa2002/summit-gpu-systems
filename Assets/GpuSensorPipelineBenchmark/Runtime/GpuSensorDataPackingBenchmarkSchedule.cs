using System;
using System.Collections.Generic;

internal enum GpuSensorDataPackingBenchmarkVariant
{
    Control,
    ExpandedAosQuantizedView,
    PackedSoaFusedEndCursor
}

internal readonly struct GpuSensorDataPackingScheduleEntry
{
    public GpuSensorDataPackingScheduleEntry(
        int blockIndex,
        string blockType,
        GpuSensorDataPackingBenchmarkVariant variant,
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

    public GpuSensorDataPackingBenchmarkVariant Variant { get; }

    public int SuperRound { get; }

    public int SequencePosition { get; }

    public int PairIndex { get; }

    public string PairOrder { get; }

    public int WithinPairPosition { get; }
}

internal static class GpuSensorDataPackingBenchmarkSchedule
{
    public const int FormalSuperRoundCount = 4;
    public const int FormalStateCount = 64;
    public const int PreconditionSampleCount = 240;

    public static int LogicalStateForSample(
        int zeroBasedSampleIndex,
        int stateCount = FormalStateCount)
    {
        if (zeroBasedSampleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zeroBasedSampleIndex));
        }
        if (stateCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stateCount));
        }

        return zeroBasedSampleIndex % stateCount;
    }

    public static IReadOnlyList<GpuSensorDataPackingScheduleEntry> Build(
        int superRoundCount)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }

        List<GpuSensorDataPackingScheduleEntry> result =
            new List<GpuSensorDataPackingScheduleEntry>(
                checked(superRoundCount * 4 + 4));
        int blockIndex = 1;
        int pairIndex = 1;
        result.Add(new GpuSensorDataPackingScheduleEntry(
            blockIndex++,
            "control-pre",
            GpuSensorDataPackingBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));

        result.Add(new GpuSensorDataPackingScheduleEntry(
            blockIndex++,
            "precondition",
            GpuSensorDataPackingBenchmarkVariant.ExpandedAosQuantizedView,
            0,
            1,
            0,
            string.Empty,
            0));
        result.Add(new GpuSensorDataPackingScheduleEntry(
            blockIndex++,
            "precondition",
            GpuSensorDataPackingBenchmarkVariant.PackedSoaFusedEndCursor,
            0,
            2,
            0,
            string.Empty,
            0));

        for (int superRound = 1;
             superRound <= superRoundCount;
             superRound++)
        {
            bool abba = (superRound & 1) != 0;
            GpuSensorDataPackingBenchmarkVariant[] variants = abba
                ? new[]
                {
                    GpuSensorDataPackingBenchmarkVariant
                        .ExpandedAosQuantizedView,
                    GpuSensorDataPackingBenchmarkVariant
                        .PackedSoaFusedEndCursor,
                    GpuSensorDataPackingBenchmarkVariant
                        .PackedSoaFusedEndCursor,
                    GpuSensorDataPackingBenchmarkVariant
                        .ExpandedAosQuantizedView
                }
                : new[]
                {
                    GpuSensorDataPackingBenchmarkVariant
                        .PackedSoaFusedEndCursor,
                    GpuSensorDataPackingBenchmarkVariant
                        .ExpandedAosQuantizedView,
                    GpuSensorDataPackingBenchmarkVariant
                        .ExpandedAosQuantizedView,
                    GpuSensorDataPackingBenchmarkVariant
                        .PackedSoaFusedEndCursor
                };

            for (int sequencePosition = 1;
                 sequencePosition <= 4;
                 sequencePosition++)
            {
                int withinPairPosition =
                    ((sequencePosition - 1) & 1) + 1;
                GpuSensorDataPackingBenchmarkVariant first =
                    variants[((sequencePosition - 1) / 2) * 2];
                string pairOrder =
                    first ==
                    GpuSensorDataPackingBenchmarkVariant
                        .ExpandedAosQuantizedView
                        ? "AB"
                        : "BA";
                result.Add(new GpuSensorDataPackingScheduleEntry(
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

        result.Add(new GpuSensorDataPackingScheduleEntry(
            blockIndex,
            "control-post",
            GpuSensorDataPackingBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));
        return result;
    }
}
