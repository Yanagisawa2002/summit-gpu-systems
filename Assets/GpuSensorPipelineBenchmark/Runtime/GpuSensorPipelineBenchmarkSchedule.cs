using System;
using System.Collections.Generic;

internal enum GpuSensorPipelineBenchmarkVariant
{
    Control,
    CpuProducedUploaded,
    GpuProducedResident,
    RebuiltPerSensor,
    SharedSensorIndex
}

internal readonly struct GpuSensorPipelineScheduleEntry
{
    public GpuSensorPipelineScheduleEntry(
        int blockIndex,
        string blockType,
        GpuSensorPipelineBenchmarkVariant variant,
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

    public GpuSensorPipelineBenchmarkVariant Variant { get; }

    public int SuperRound { get; }

    public int SequencePosition { get; }

    public int PairIndex { get; }

    public string PairOrder { get; }

    public int WithinPairPosition { get; }
}

internal static class GpuSensorPipelineBenchmarkSchedule
{
    public const int FormalSuperRoundCount = 4;
    public const int FormalStateCount = 64;

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

    public static IReadOnlyList<GpuSensorPipelineScheduleEntry> Build(
        int superRoundCount)
    {
        return Build(
            superRoundCount,
            GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded,
            GpuSensorPipelineBenchmarkVariant.GpuProducedResident);
    }

    public static IReadOnlyList<GpuSensorPipelineScheduleEntry> Build(
        int superRoundCount,
        GpuSensorPipelineBenchmarkVariant variantA,
        GpuSensorPipelineBenchmarkVariant variantB)
    {
        if (superRoundCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(superRoundCount));
        }
        if (variantA == GpuSensorPipelineBenchmarkVariant.Control ||
            variantB == GpuSensorPipelineBenchmarkVariant.Control ||
            variantA == variantB)
        {
            throw new ArgumentException(
                "Measurement variants must be distinct non-control cases.");
        }

        List<GpuSensorPipelineScheduleEntry> result =
            new List<GpuSensorPipelineScheduleEntry>(
                checked(superRoundCount * 4 + 2));
        int blockIndex = 1;
        int pairIndex = 1;
        result.Add(new GpuSensorPipelineScheduleEntry(
            blockIndex++,
            "control-pre",
            GpuSensorPipelineBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));

        for (int superRound = 1; superRound <= superRoundCount; superRound++)
        {
            bool abba = (superRound & 1) != 0;
            GpuSensorPipelineBenchmarkVariant[] variants = abba
                ? new[]
                {
                    variantA,
                    variantB,
                    variantB,
                    variantA
                }
                : new[]
                {
                    variantB,
                    variantA,
                    variantA,
                    variantB
                };

            for (int sequencePosition = 1; sequencePosition <= 4; sequencePosition++)
            {
                int withinPairPosition = ((sequencePosition - 1) & 1) + 1;
                GpuSensorPipelineBenchmarkVariant first =
                    variants[((sequencePosition - 1) / 2) * 2];
                string pairOrder =
                    first == variantA
                        ? "AB"
                        : "BA";
                result.Add(new GpuSensorPipelineScheduleEntry(
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

        result.Add(new GpuSensorPipelineScheduleEntry(
            blockIndex,
            "control-post",
            GpuSensorPipelineBenchmarkVariant.Control,
            0,
            0,
            0,
            string.Empty,
            0));
        return result;
    }
}
