using System;
using Summit.GpuDrivenInstances;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstancePolicyInputGeneratorTests
    {
        [TestCase(0, 0, 0)]
        [TestCase(0, 10000, 0)]
        [TestCase(7, 1, 0)]
        [TestCase(7, 10000, 7)]
        [TestCase(19, 9509, 18)]
        [TestCase(10000, 1, 1)]
        [TestCase(10000, 333, 333)]
        [TestCase(100000, 1000, 10000)]
        [TestCase(100000, 9999, 99990)]
        [TestCase(100000, 10000, 100000)]
        public void BasisPointsProduceExactSortedCoverage(
            int instanceCount,
            int dirtyBasisPoints,
            int expectedChanged)
        {
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstancePolicyUpdatePlan plan =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        instanceCount,
                        dirtyBasisPoints,
                        20260830,
                        ranges);

                Assert.That(plan.InstanceCount, Is.EqualTo(instanceCount));
                Assert.That(
                    plan.DirtyBasisPoints,
                    Is.EqualTo(dirtyBasisPoints));
                Assert.That(
                    plan.ChangedInstanceCount,
                    Is.EqualTo(expectedChanged));
                Assert.That(
                    plan.RangeCount,
                    Is.InRange(
                        expectedChanged == 0 ? 0 : 1,
                        GpuDrivenInstancePolicyInputGenerator
                            .MaximumRangeCount));
                Assert.That(
                    SumAndValidateRanges(
                        ranges,
                        plan.RangeCount,
                        instanceCount),
                    Is.EqualTo(expectedChanged));
                Assert.That(
                    GpuDrivenInstancePolicyInputGenerator.ComputePlanHash(
                        plan,
                        ranges),
                    Is.EqualTo(plan.RangePlanHash));

                if (expectedChanged == 0)
                {
                    Assert.That(plan.RangeCount, Is.Zero);
                }
                if (dirtyBasisPoints == 10000 && instanceCount > 0)
                {
                    Assert.That(plan.RangeCount, Is.EqualTo(1));
                    Assert.That(ranges[0].StartIndex, Is.Zero);
                    Assert.That(ranges[0].Count, Is.EqualTo(instanceCount));
                }
                for (int index = plan.RangeCount;
                     index < GpuDrivenInstancePolicyInputGenerator
                         .MaximumRangeCount;
                     index++)
                {
                    Assert.That(ranges[index].StartIndex, Is.Zero);
                    Assert.That(ranges[index].Count, Is.Zero);
                }
            }
        }

        [Test]
        public void RangeTopologyIsDeterministicAndSeeded()
        {
            using (var firstRanges = CreateRanges())
            using (var repeatedRanges = CreateRanges())
            using (var otherRanges = CreateRanges())
            {
                GpuDrivenInstancePolicyUpdatePlan first =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        100000,
                        1250,
                        12345,
                        firstRanges);
                GpuDrivenInstancePolicyUpdatePlan repeated =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        100000,
                        1250,
                        12345,
                        repeatedRanges);
                GpuDrivenInstancePolicyUpdatePlan other =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        100000,
                        1250,
                        54321,
                        otherRanges);

                Assert.That(
                    repeated.RangePlanHash,
                    Is.EqualTo(first.RangePlanHash));
                Assert.That(repeated.RangeCount, Is.EqualTo(first.RangeCount));
                for (int index = 0; index < first.RangeCount; index++)
                {
                    Assert.That(
                        repeatedRanges[index].StartIndex,
                        Is.EqualTo(firstRanges[index].StartIndex));
                    Assert.That(
                        repeatedRanges[index].Count,
                        Is.EqualTo(firstRanges[index].Count));
                }
                Assert.That(
                    other.RangePlanHash,
                    Is.Not.EqualTo(first.RangePlanHash));
            }
        }

        [Test]
        public void PlanAndUpdateHashesHaveFrozenFnvValues()
        {
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstancePolicyUpdatePlan plan =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        100,
                        2500,
                        12345,
                        ranges);

                Assert.That(
                    plan.RangePlanHash,
                    Is.EqualTo(783755314353279542UL));
                Assert.That(
                    GpuDrivenInstancePolicyInputGenerator.ComputeUpdateHash(
                        plan,
                        17u),
                    Is.EqualTo(17438577846149966933UL));
                Assert.That(
                    GpuDrivenInstancePolicyInputGenerator.ComputeUpdateHash(
                        plan,
                        18u),
                    Is.Not.EqualTo(
                        GpuDrivenInstancePolicyInputGenerator
                            .ComputeUpdateHash(plan, 17u)));
            }
        }

        [Test]
        public void DirtyApplyChangesOnlyApplicationIdInsideRanges()
        {
            const int count = 1600;
            using (var baseStates = CreateBaseStates(count))
            using (var firstSlot = CreateSentinelStates(count))
            using (var secondSlot = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstancePolicyUpdatePlan plan =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        count,
                        1375,
                        991,
                        ranges);
                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    firstSlot,
                    ranges,
                    plan,
                    17u);
                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    secondSlot,
                    ranges,
                    plan,
                    17u);

                for (int index = 0; index < count; index++)
                {
                    bool dirty = Contains(ranges, plan.RangeCount, index);
                    if (!dirty)
                    {
                        AssertStatesEqual(Sentinel(), firstSlot[index]);
                        AssertStatesEqual(Sentinel(), secondSlot[index]);
                        continue;
                    }

                    GpuInstanceState baseline = baseStates[index];
                    GpuInstanceState actual = firstSlot[index];
                    Assert.That(
                        actual.ApplicationId,
                        Is.EqualTo(secondSlot[index].ApplicationId));
                    Assert.That(
                        actual.ApplicationId,
                        Is.Not.EqualTo(baseline.ApplicationId));
                    AssertCullingAndHierarchyFieldsEqual(baseline, actual);
                }
            }
        }

        [Test]
        public void FullPopulateMatchesDirtyApplyForTheSameLogicalState()
        {
            const int count = 1600;
            using (var baseStates = CreateBaseStates(count))
            using (var dirtySlot = CreateSentinelStates(count))
            using (var fullSlot = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                NativeArray<GpuInstanceState>.Copy(baseStates, dirtySlot);
                GpuDrivenInstancePolicyUpdatePlan plan =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        count,
                        731,
                        809,
                        ranges);
                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    dirtySlot,
                    ranges,
                    plan,
                    29u);
                GpuDrivenInstancePolicyInputGenerator.PopulateFullState(
                    baseStates.AsReadOnly(),
                    fullSlot,
                    ranges,
                    plan,
                    29u);

                for (int index = 0; index < count; index++)
                {
                    AssertStatesEqual(dirtySlot[index], fullSlot[index]);
                    AssertCullingAndHierarchyFieldsEqual(
                        baseStates[index],
                        fullSlot[index]);
                }
            }
        }

        [Test]
        public void UpdatesAreAbsoluteAcrossStagingSlotReuse()
        {
            const int count = 1600;
            using (var baseStates = CreateBaseStates(count))
            using (var reusedSlot = CreateSentinelStates(count))
            using (var freshSlot = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                NativeArray<GpuInstanceState>.Copy(baseStates, reusedSlot);
                NativeArray<GpuInstanceState>.Copy(baseStates, freshSlot);
                GpuDrivenInstancePolicyUpdatePlan plan =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        count,
                        1000,
                        77,
                        ranges);
                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    reusedSlot,
                    ranges,
                    plan,
                    1u);
                uint ordinalOne =
                    reusedSlot[ranges[0].StartIndex].ApplicationId;

                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    reusedSlot,
                    ranges,
                    plan,
                    2u);
                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    freshSlot,
                    ranges,
                    plan,
                    2u);

                for (int rangeIndex = 0;
                     rangeIndex < plan.RangeCount;
                     rangeIndex++)
                {
                    GpuInstanceDirtyRange range = ranges[rangeIndex];
                    for (int index = range.StartIndex;
                         index < range.EndIndex;
                         index++)
                    {
                        AssertStatesEqual(reusedSlot[index], freshSlot[index]);
                    }
                }
                Assert.That(
                    reusedSlot[ranges[0].StartIndex].ApplicationId,
                    Is.Not.EqualTo(ordinalOne));
            }
        }

        [Test]
        public void MalformedPlansAndRangesFailClosed()
        {
            const int count = 100;
            NativeArray<GpuInstanceState> baseStates =
                CreateBaseStates(count);
            NativeArray<GpuInstanceState> staging =
                CreateSentinelStates(count);
            NativeArray<GpuInstanceDirtyRange> ranges = CreateRanges();
            try
            {
                ranges[0] = new GpuInstanceDirtyRange(10, 15);
                ranges[1] = new GpuInstanceDirtyRange(20, 5);
                var unhashedOverlap =
                    new GpuDrivenInstancePolicyUpdatePlan(
                        count,
                        2000,
                        1,
                        20,
                        2,
                        0UL);
                ulong overlapHash =
                    GpuDrivenInstancePolicyInputGenerator.ComputePlanHash(
                        unhashedOverlap,
                        ranges);
                var overlappingPlan =
                    new GpuDrivenInstancePolicyUpdatePlan(
                        count,
                        2000,
                        1,
                        20,
                        2,
                        overlapHash);
                Assert.Throws<ArgumentException>(() =>
                    GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                        baseStates.AsReadOnly(),
                        staging,
                        ranges,
                        overlappingPlan,
                        1u));

                ranges[0] = new GpuInstanceDirtyRange(90, 20);
                var unhashedOutOfBounds =
                    new GpuDrivenInstancePolicyUpdatePlan(
                        count,
                        2000,
                        1,
                        20,
                        1,
                        0UL);
                ulong outOfBoundsHash =
                    GpuDrivenInstancePolicyInputGenerator.ComputePlanHash(
                        unhashedOutOfBounds,
                        ranges);
                var outOfBoundsPlan =
                    new GpuDrivenInstancePolicyUpdatePlan(
                        count,
                        2000,
                        1,
                        20,
                        1,
                        outOfBoundsHash);
                Assert.Throws<ArgumentException>(() =>
                    GpuDrivenInstancePolicyInputGenerator.PopulateFullState(
                        baseStates.AsReadOnly(),
                        staging,
                        ranges,
                        outOfBoundsPlan,
                        1u));

                GpuDrivenInstancePolicyUpdatePlan valid =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        count,
                        2000,
                        1,
                        ranges);
                var wrongHash = new GpuDrivenInstancePolicyUpdatePlan(
                    valid.InstanceCount,
                    valid.DirtyBasisPoints,
                    valid.Seed,
                    valid.ChangedInstanceCount,
                    valid.RangeCount,
                    valid.RangePlanHash ^ 1UL);
                Assert.Throws<ArgumentException>(() =>
                    GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                        baseStates.AsReadOnly(),
                        staging,
                        ranges,
                        wrongHash,
                        1u));
            }
            finally
            {
                ranges.Dispose();
                staging.Dispose();
                baseStates.Dispose();
            }
        }

        [Test]
        public void InvalidBasisPointsAndUndersizedStorageFailClosed()
        {
            using (var ranges = new NativeArray<GpuInstanceDirtyRange>(
                       GpuDrivenInstancePolicyInputGenerator
                           .MaximumRangeCount - 1,
                       Allocator.Persistent))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuDrivenInstancePolicyInputGenerator
                        .ChangedInstanceCount(1000, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuDrivenInstancePolicyInputGenerator
                        .ChangedInstanceCount(1000, 10001));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuDrivenInstancePolicyInputGenerator
                        .ChangedInstanceCount(-1, 0));
                Assert.Throws<ArgumentException>(() =>
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        1000,
                        1000,
                        1,
                        ranges));
            }
        }

        [Test]
        public void HundredThousandInstanceWarmPathAllocatesNoManagedMemory()
        {
            const int count = 100000;
            using (var baseStates = CreateBaseStates(count))
            using (var staging = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstancePolicyUpdatePlan warmup =
                    GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                        count,
                        1000,
                        42,
                        ranges);
                GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    staging,
                    ranges,
                    warmup,
                    1u);

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (uint ordinal = 2u; ordinal < 18u; ordinal++)
                {
                    GpuDrivenInstancePolicyUpdatePlan plan =
                        GpuDrivenInstancePolicyInputGenerator.BuildDirtyRanges(
                            count,
                            1000,
                            42,
                            ranges);
                    GpuDrivenInstancePolicyInputGenerator.ApplyDirtyUpdates(
                        baseStates.AsReadOnly(),
                        staging,
                        ranges,
                        plan,
                        ordinal);
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(allocated, Is.Zero);
            }
        }

        private static NativeArray<GpuInstanceDirtyRange> CreateRanges()
        {
            return new NativeArray<GpuInstanceDirtyRange>(
                GpuDrivenInstancePolicyInputGenerator.MaximumRangeCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        private static NativeArray<GpuInstanceState> CreateBaseStates(int count)
        {
            var result = new NativeArray<GpuInstanceState>(
                count,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            for (int index = 0; index < count; index++)
            {
                result[index] = new GpuInstanceState(
                    new Vector3(
                        (index % 100) - 50f,
                        ((index / 100) % 16) - 8f,
                        (index % 31) - 15f),
                    0.5f + (index % 3) * 0.125f,
                    new Vector4(10f, 20f, 30f, 40f),
                    checked((uint)(1000 + index)),
                    checked((uint)(index % 8)),
                    checked((uint)(1 + index % 4)),
                    1u << (index % 4));
            }
            return result;
        }

        private static NativeArray<GpuInstanceState> CreateSentinelStates(
            int count)
        {
            var result = new NativeArray<GpuInstanceState>(
                count,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            GpuInstanceState sentinel = Sentinel();
            for (int index = 0; index < count; index++)
            {
                result[index] = sentinel;
            }
            return result;
        }

        private static GpuInstanceState Sentinel()
        {
            return new GpuInstanceState(
                new Vector3(9000f, 9001f, 9002f),
                9f,
                new Vector4(91f, 92f, 93f, 94f),
                uint.MaxValue,
                uint.MaxValue - 1u,
                uint.MaxValue - 2u,
                uint.MaxValue - 3u);
        }

        private static void AssertCullingAndHierarchyFieldsEqual(
            GpuInstanceState expected,
            GpuInstanceState actual)
        {
            Assert.That(
                actual.PositionRadius,
                Is.EqualTo(expected.PositionRadius));
            Assert.That(
                actual.LodDistances,
                Is.EqualTo(expected.LodDistances));
            Assert.That(
                actual.DrawGroupBase,
                Is.EqualTo(expected.DrawGroupBase));
            Assert.That(actual.LodCount, Is.EqualTo(expected.LodCount));
            Assert.That(actual.ViewMask, Is.EqualTo(expected.ViewMask));
        }

        private static void AssertStatesEqual(
            GpuInstanceState expected,
            GpuInstanceState actual)
        {
            AssertCullingAndHierarchyFieldsEqual(expected, actual);
            Assert.That(
                actual.ApplicationId,
                Is.EqualTo(expected.ApplicationId));
        }

        private static int SumAndValidateRanges(
            NativeArray<GpuInstanceDirtyRange> ranges,
            int rangeCount,
            int instanceCount)
        {
            int sum = 0;
            int previousEnd = 0;
            for (int index = 0; index < rangeCount; index++)
            {
                GpuInstanceDirtyRange range = ranges[index];
                Assert.That(range.Count, Is.GreaterThan(0));
                Assert.That(
                    range.StartIndex,
                    Is.GreaterThanOrEqualTo(previousEnd));
                Assert.That(range.EndIndex, Is.LessThanOrEqualTo(instanceCount));
                if (index > 0)
                {
                    Assert.That(
                        range.StartIndex,
                        Is.GreaterThan(previousEnd),
                        "Adjacent stripes should have been merged.");
                }
                previousEnd = range.EndIndex;
                sum = checked(sum + range.Count);
            }
            return sum;
        }

        private static bool Contains(
            NativeArray<GpuInstanceDirtyRange> ranges,
            int rangeCount,
            int index)
        {
            for (int rangeIndex = 0;
                 rangeIndex < rangeCount;
                 rangeIndex++)
            {
                GpuInstanceDirtyRange range = ranges[rangeIndex];
                if (index >= range.StartIndex && index < range.EndIndex)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
