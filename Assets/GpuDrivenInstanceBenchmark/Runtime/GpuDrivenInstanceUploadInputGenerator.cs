using System;
using Summit.GpuDrivenInstances;
using Unity.Collections;
using UnityEngine;

internal readonly struct GpuDrivenInstanceUploadPlan
{
    internal GpuDrivenInstanceUploadPlan(
        int instanceCount,
        int movingPercent,
        int seed,
        int changedInstanceCount,
        int rangeCount,
        ulong rangePlanHash)
    {
        InstanceCount = instanceCount;
        MovingPercent = movingPercent;
        Seed = seed;
        ChangedInstanceCount = changedInstanceCount;
        RangeCount = rangeCount;
        RangePlanHash = rangePlanHash;
    }

    internal int InstanceCount { get; }

    internal int MovingPercent { get; }

    internal int Seed { get; }

    internal int ChangedInstanceCount { get; }

    internal int RangeCount { get; }

    internal ulong RangePlanHash { get; }
}

/// <summary>
/// Builds the frozen, allocation-free dynamic-state input used by the
/// full-upload versus dirty-range macrobenchmark.
/// </summary>
/// <remarks>
/// The immutable base and every staging slot are caller-owned NativeArrays.
/// Dirty updates write only the returned ranges, so a fence-safe staging slot
/// does not need to contain a complete copy of the preceding logical state.
/// Positions are always derived from the immutable base and logical ordinal;
/// updates never accumulate across slot reuse.
/// </remarks>
internal static class GpuDrivenInstanceUploadInputGenerator
{
    internal const string LayoutId =
        "seeded-striped-contiguous-16-v1";
    internal const int MaximumRangeCount = 16;
    internal const float MaximumPositionDelta = 0.125f;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const uint LayoutHashSeed = 0xD1A7A16Bu;

    internal static int ChangedInstanceCount(
        int instanceCount,
        int movingPercent)
    {
        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        ValidateMovingPercent(movingPercent);
        return checked((int)(
            (long)instanceCount * movingPercent / 100L));
    }

    /// <summary>
    /// Writes at most sixteen sorted, non-overlapping ranges into caller-owned
    /// persistent storage. Membership is fixed by count, fraction, and seed;
    /// the logical ordinal changes values but not range topology.
    /// </summary>
    internal static GpuDrivenInstanceUploadPlan BuildDirtyRanges(
        int instanceCount,
        int movingPercent,
        int seed,
        NativeArray<GpuInstanceDirtyRange> outputRanges)
    {
        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        ValidateMovingPercent(movingPercent);
        if (!outputRanges.IsCreated ||
            outputRanges.Length < MaximumRangeCount)
        {
            throw new ArgumentException(
                "A created range buffer with at least sixteen entries is " +
                "required.",
                nameof(outputRanges));
        }

        for (int index = 0; index < MaximumRangeCount; index++)
        {
            outputRanges[index] = default;
        }

        int changedCount = ChangedInstanceCount(
            instanceCount,
            movingPercent);
        int stripeCount = Math.Min(MaximumRangeCount, instanceCount);
        int rangeCount = 0;
        int emittedCount = 0;

        for (int stripe = 0; stripe < stripeCount; stripe++)
        {
            int stripeStart = checked((int)(
                (long)stripe * instanceCount / stripeCount));
            int stripeEnd = checked((int)(
                (long)(stripe + 1) * instanceCount / stripeCount));
            int selectedStart = checked((int)(
                (long)stripe * changedCount / stripeCount));
            int selectedEnd = checked((int)(
                (long)(stripe + 1) * changedCount / stripeCount));
            int selectedCount = selectedEnd - selectedStart;
            if (selectedCount == 0)
            {
                continue;
            }

            int stripeLength = stripeEnd - stripeStart;
            int placementCount = stripeLength - selectedCount + 1;
            uint placementHash = Mix32(
                unchecked((uint)seed) ^
                unchecked((uint)stripe * 0x9E3779B9u) ^
                LayoutHashSeed);
            int rangeStart = stripeStart + checked((int)(
                placementHash % checked((uint)placementCount)));
            AppendRange(
                outputRanges,
                ref rangeCount,
                rangeStart,
                selectedCount);
            emittedCount = checked(emittedCount + selectedCount);
        }

        if (emittedCount != changedCount ||
            rangeCount > MaximumRangeCount)
        {
            throw new InvalidOperationException(
                "Striped upload layout violated its frozen range contract.");
        }

        ulong planHash = ComputePlanHashCore(
            instanceCount,
            movingPercent,
            seed,
            changedCount,
            outputRanges,
            rangeCount);
        return new GpuDrivenInstanceUploadPlan(
            instanceCount,
            movingPercent,
            seed,
            changedCount,
            rangeCount,
            planHash);
    }

    /// <summary>
    /// Populates only dirty records in a reusable staging slot.
    /// </summary>
    internal static void ApplyDirtyUpdates(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstanceUploadPlan plan,
        uint logicalOrdinal)
    {
        ValidateApplyInputs(immutableBase, staging, ranges, plan);
        ApplyDirtyUpdatesUnchecked(
            immutableBase,
            staging,
            ranges,
            plan,
            logicalOrdinal);
    }

    private static void ApplyDirtyUpdatesUnchecked(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstanceUploadPlan plan,
        uint logicalOrdinal)
    {
        for (int rangeIndex = 0;
             rangeIndex < plan.RangeCount;
             rangeIndex++)
        {
            GpuInstanceDirtyRange range = ranges[rangeIndex];
            for (int index = range.StartIndex;
                 index < range.EndIndex;
                 index++)
            {
                GpuInstanceState state = immutableBase[index];
                Vector4 positionRadius = state.PositionRadius;
                Vector3 delta = PositionDelta(
                    index,
                    plan.Seed,
                    logicalOrdinal);
                positionRadius.x += delta.x;
                positionRadius.y += delta.y;
                positionRadius.z += delta.z;
                state.PositionRadius = positionRadius;
                staging[index] = state;
            }
        }
    }

    /// <summary>
    /// Produces the identical logical state in a full staging mirror.
    /// </summary>
    internal static void PopulateFullState(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstanceUploadPlan plan,
        uint logicalOrdinal)
    {
        ValidateApplyInputs(immutableBase, staging, ranges, plan);
        NativeArray<GpuInstanceState>.Copy(
            immutableBase,
            staging,
            plan.InstanceCount);
        ApplyDirtyUpdatesUnchecked(
            immutableBase,
            staging,
            ranges,
            plan,
            logicalOrdinal);
    }

    internal static ulong ComputePlanHash(
        GpuDrivenInstanceUploadPlan plan,
        NativeArray<GpuInstanceDirtyRange> ranges)
    {
        if (!ranges.IsCreated || ranges.Length < plan.RangeCount)
        {
            throw new ArgumentException(
                "Range buffer does not cover the plan.",
                nameof(ranges));
        }
        return ComputePlanHashCore(
            plan.InstanceCount,
            plan.MovingPercent,
            plan.Seed,
            plan.ChangedInstanceCount,
            ranges,
            plan.RangeCount);
    }

    internal static ulong ComputeUpdateHash(
        GpuDrivenInstanceUploadPlan plan,
        uint logicalOrdinal)
    {
        ulong result = HashUInt64(FnvOffsetBasis, plan.RangePlanHash);
        return HashUInt32(result, logicalOrdinal);
    }

    private static void ValidateMovingPercent(int movingPercent)
    {
        if (movingPercent != 0 &&
            movingPercent != 1 &&
            movingPercent != 10 &&
            movingPercent != 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movingPercent),
                movingPercent,
                "Supported moving fractions are 0, 1, 10, and 100 percent.");
        }
    }

    private static void AppendRange(
        NativeArray<GpuInstanceDirtyRange> outputRanges,
        ref int rangeCount,
        int startIndex,
        int count)
    {
        if (rangeCount > 0)
        {
            GpuInstanceDirtyRange previous = outputRanges[rangeCount - 1];
            if (previous.EndIndex == startIndex)
            {
                outputRanges[rangeCount - 1] =
                    new GpuInstanceDirtyRange(
                        previous.StartIndex,
                        checked(previous.Count + count));
                return;
            }
        }

        if (rangeCount >= MaximumRangeCount)
        {
            throw new InvalidOperationException(
                "Striped layout exceeded its maximum range count.");
        }
        outputRanges[rangeCount++] =
            new GpuInstanceDirtyRange(startIndex, count);
    }

    private static void ValidateApplyInputs(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstanceUploadPlan plan)
    {
        if (!immutableBase.IsCreated ||
            immutableBase.Length < plan.InstanceCount)
        {
            throw new ArgumentException(
                "Immutable base does not cover the plan.",
                nameof(immutableBase));
        }
        if (!staging.IsCreated || staging.Length < plan.InstanceCount)
        {
            throw new ArgumentException(
                "Staging slot does not cover the plan.",
                nameof(staging));
        }
        if (!ranges.IsCreated || ranges.Length < plan.RangeCount)
        {
            throw new ArgumentException(
                "Range buffer does not cover the plan.",
                nameof(ranges));
        }
        if (plan.RangeCount < 0 ||
            plan.RangeCount > MaximumRangeCount ||
            plan.InstanceCount < 0 ||
            plan.ChangedInstanceCount != ChangedInstanceCount(
                plan.InstanceCount,
                plan.MovingPercent) ||
            ComputePlanHash(plan, ranges) != plan.RangePlanHash)
        {
            throw new ArgumentException(
                "Upload plan or range buffer is inconsistent.",
                nameof(plan));
        }
    }

    private static Vector3 PositionDelta(
        int instanceIndex,
        int seed,
        uint logicalOrdinal)
    {
        uint basis =
            unchecked((uint)seed) ^
            unchecked((uint)instanceIndex * 0x85EBCA6Bu) ^
            unchecked(logicalOrdinal * 0xC2B2AE35u);
        float x = SignedUnit(Mix32(basis ^ 0xA511E9B3u));
        float y = SignedUnit(Mix32(basis ^ 0x63D83595u));
        float z = SignedUnit(Mix32(basis ^ 0xB5297A4Du));
        return new Vector3(x, y, z) * MaximumPositionDelta;
    }

    private static float SignedUnit(uint value)
    {
        return ((value & 0x0000FFFFu) / 32767.5f) - 1f;
    }

    private static uint Mix32(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }

    private static ulong ComputePlanHashCore(
        int instanceCount,
        int movingPercent,
        int seed,
        int changedInstanceCount,
        NativeArray<GpuInstanceDirtyRange> ranges,
        int rangeCount)
    {
        ulong result = HashUInt32(FnvOffsetBasis, LayoutHashSeed);
        result = HashInt32(result, instanceCount);
        result = HashInt32(result, movingPercent);
        result = HashInt32(result, seed);
        result = HashInt32(result, changedInstanceCount);
        result = HashInt32(result, rangeCount);
        for (int index = 0; index < rangeCount; index++)
        {
            result = HashInt32(result, ranges[index].StartIndex);
            result = HashInt32(result, ranges[index].Count);
        }
        return result;
    }

    private static ulong HashInt32(ulong hash, int value)
    {
        return HashUInt32(hash, unchecked((uint)value));
    }

    private static ulong HashUInt32(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= FnvPrime;
        }
        return hash;
    }

    private static ulong HashUInt64(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= FnvPrime;
        }
        return hash;
    }
}
