using System;
using Summit.GpuDrivenInstances;
using Unity.Collections;

internal readonly struct GpuDrivenInstancePolicyUpdatePlan
{
    internal GpuDrivenInstancePolicyUpdatePlan(
        int instanceCount,
        int dirtyBasisPoints,
        int seed,
        int changedInstanceCount,
        int rangeCount,
        ulong rangePlanHash)
    {
        InstanceCount = instanceCount;
        DirtyBasisPoints = dirtyBasisPoints;
        Seed = seed;
        ChangedInstanceCount = changedInstanceCount;
        RangeCount = rangeCount;
        RangePlanHash = rangePlanHash;
    }

    internal int InstanceCount { get; }

    internal int DirtyBasisPoints { get; }

    internal int Seed { get; }

    internal int ChangedInstanceCount { get; }

    internal int RangeCount { get; }

    internal ulong RangePlanHash { get; }
}

/// <summary>
/// Builds deterministic, allocation-free metadata updates for the automatic
/// GPU-driven instance policy benchmark.
/// </summary>
/// <remarks>
/// Dirty membership is expressed in basis points and written into caller-owned
/// NativeArray storage. Updates change only ApplicationId. PositionRadius,
/// LodDistances, DrawGroupBase, LodCount, and ViewMask remain byte-for-byte
/// equal to the immutable base, preserving visibility, LOD, enabled state,
/// draw-group routing, and fixed hierarchy membership.
/// </remarks>
internal static class GpuDrivenInstancePolicyInputGenerator
{
    internal const string LayoutId =
        "seeded-striped-contiguous-basis-points-16-metadata-v1";
    internal const int MaximumRangeCount = 16;
    internal const int BasisPointScale = 10000;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const uint LayoutHashSeed = 0xA17F2D4Bu;
    private const uint UpdateHashSeed = 0xC011AB1Eu;

    internal static int ChangedInstanceCount(
        int instanceCount,
        int dirtyBasisPoints)
    {
        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        ValidateDirtyBasisPoints(dirtyBasisPoints);
        return checked((int)(
            (long)instanceCount * dirtyBasisPoints / BasisPointScale));
    }

    /// <summary>
    /// Writes at most sixteen sorted, non-overlapping ranges into caller-owned
    /// storage. The logical ordinal changes values but not range topology.
    /// </summary>
    internal static GpuDrivenInstancePolicyUpdatePlan BuildDirtyRanges(
        int instanceCount,
        int dirtyBasisPoints,
        int seed,
        NativeArray<GpuInstanceDirtyRange> outputRanges)
    {
        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        ValidateDirtyBasisPoints(dirtyBasisPoints);
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
            dirtyBasisPoints);
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
                (long)stripeStart * dirtyBasisPoints / BasisPointScale));
            int selectedEnd = checked((int)(
                (long)stripeEnd * dirtyBasisPoints / BasisPointScale));
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
                "Policy update layout violated its bounded range contract.");
        }

        ulong planHash = ComputePlanHashCore(
            instanceCount,
            dirtyBasisPoints,
            seed,
            changedCount,
            outputRanges,
            rangeCount);
        return new GpuDrivenInstancePolicyUpdatePlan(
            instanceCount,
            dirtyBasisPoints,
            seed,
            changedCount,
            rangeCount,
            planHash);
    }

    /// <summary>
    /// Applies only the planned ranges to a reusable staging slot.
    /// </summary>
    internal static void ApplyDirtyUpdates(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstancePolicyUpdatePlan plan,
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

    /// <summary>
    /// Produces the same logical state as applying the dirty plan to a complete
    /// copy of the immutable base.
    /// </summary>
    internal static void PopulateFullState(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstancePolicyUpdatePlan plan,
        uint logicalOrdinal)
    {
        ValidateApplyInputs(immutableBase, staging, ranges, plan);
        if (plan.InstanceCount > 0)
        {
            NativeArray<GpuInstanceState>.Copy(
                immutableBase,
                staging,
                plan.InstanceCount);
        }
        ApplyDirtyUpdatesUnchecked(
            immutableBase,
            staging,
            ranges,
            plan,
            logicalOrdinal);
    }

    internal static ulong ComputePlanHash(
        GpuDrivenInstancePolicyUpdatePlan plan,
        NativeArray<GpuInstanceDirtyRange> ranges)
    {
        if (plan.RangeCount < 0 ||
            plan.RangeCount > MaximumRangeCount ||
            !ranges.IsCreated ||
            ranges.Length < plan.RangeCount)
        {
            throw new ArgumentException(
                "Range buffer does not cover the policy update plan.",
                nameof(ranges));
        }
        return ComputePlanHashCore(
            plan.InstanceCount,
            plan.DirtyBasisPoints,
            plan.Seed,
            plan.ChangedInstanceCount,
            ranges,
            plan.RangeCount);
    }

    internal static ulong ComputeUpdateHash(
        GpuDrivenInstancePolicyUpdatePlan plan,
        uint logicalOrdinal)
    {
        ulong result = HashUInt32(FnvOffsetBasis, UpdateHashSeed);
        result = HashUInt64(result, plan.RangePlanHash);
        return HashUInt32(result, logicalOrdinal);
    }

    private static void ApplyDirtyUpdatesUnchecked(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstancePolicyUpdatePlan plan,
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
                state.ApplicationId ^= ApplicationIdDelta(
                    index,
                    plan.Seed,
                    logicalOrdinal);
                staging[index] = state;
            }
        }
    }

    private static void ValidateDirtyBasisPoints(int dirtyBasisPoints)
    {
        if (dirtyBasisPoints < 0 || dirtyBasisPoints > BasisPointScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dirtyBasisPoints),
                dirtyBasisPoints,
                "Dirty basis points must be in [0, 10000].");
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
                "Policy update layout exceeded its maximum range count.");
        }
        outputRanges[rangeCount++] =
            new GpuInstanceDirtyRange(startIndex, count);
    }

    private static void ValidateApplyInputs(
        NativeArray<GpuInstanceState>.ReadOnly immutableBase,
        NativeArray<GpuInstanceState> staging,
        NativeArray<GpuInstanceDirtyRange> ranges,
        GpuDrivenInstancePolicyUpdatePlan plan)
    {
        ValidatePlanAndRanges(plan, ranges);
        if (!immutableBase.IsCreated ||
            immutableBase.Length < plan.InstanceCount)
        {
            throw new ArgumentException(
                "Immutable base does not cover the policy update plan.",
                nameof(immutableBase));
        }
        if (!staging.IsCreated || staging.Length < plan.InstanceCount)
        {
            throw new ArgumentException(
                "Staging slot does not cover the policy update plan.",
                nameof(staging));
        }
    }

    private static void ValidatePlanAndRanges(
        GpuDrivenInstancePolicyUpdatePlan plan,
        NativeArray<GpuInstanceDirtyRange> ranges)
    {
        if (plan.InstanceCount < 0 ||
            plan.DirtyBasisPoints < 0 ||
            plan.DirtyBasisPoints > BasisPointScale ||
            plan.RangeCount < 0 ||
            plan.RangeCount > MaximumRangeCount ||
            !ranges.IsCreated ||
            ranges.Length < plan.RangeCount)
        {
            throw new ArgumentException(
                "Policy update plan shape is invalid.",
                nameof(plan));
        }

        int expectedChanged = ChangedInstanceCount(
            plan.InstanceCount,
            plan.DirtyBasisPoints);
        if (plan.ChangedInstanceCount != expectedChanged)
        {
            throw new ArgumentException(
                "Policy update plan changed-count is inconsistent.",
                nameof(plan));
        }

        int covered = 0;
        int previousEnd = 0;
        for (int index = 0; index < plan.RangeCount; index++)
        {
            GpuInstanceDirtyRange range = ranges[index];
            if (range.StartIndex < 0 ||
                range.Count <= 0 ||
                range.Count > plan.InstanceCount ||
                range.StartIndex > plan.InstanceCount - range.Count ||
                (index > 0 && range.StartIndex < previousEnd))
            {
                throw new ArgumentException(
                    "Policy update ranges must be sorted, non-overlapping, " +
                    "positive, and within the active instance prefix.",
                    nameof(ranges));
            }
            previousEnd = range.StartIndex + range.Count;
            covered = checked(covered + range.Count);
        }
        if (covered != plan.ChangedInstanceCount ||
            ComputePlanHash(plan, ranges) != plan.RangePlanHash)
        {
            throw new ArgumentException(
                "Policy update plan or range hash is inconsistent.",
                nameof(plan));
        }
    }

    private static uint ApplicationIdDelta(
        int instanceIndex,
        int seed,
        uint logicalOrdinal)
    {
        uint basis =
            unchecked((uint)seed) ^
            unchecked((uint)instanceIndex * 0x85EBCA6Bu) ^
            unchecked(logicalOrdinal * 0xC2B2AE35u) ^
            UpdateHashSeed;
        return Mix32(basis) | 1u;
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
        int dirtyBasisPoints,
        int seed,
        int changedInstanceCount,
        NativeArray<GpuInstanceDirtyRange> ranges,
        int rangeCount)
    {
        ulong result = HashUInt32(FnvOffsetBasis, LayoutHashSeed);
        result = HashInt32(result, instanceCount);
        result = HashInt32(result, dirtyBasisPoints);
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
