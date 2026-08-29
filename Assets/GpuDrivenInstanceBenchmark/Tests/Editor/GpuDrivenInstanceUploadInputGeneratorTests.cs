using System;
using Summit.GpuDrivenInstances;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceUploadInputGeneratorTests
    {
        [TestCase(10000, 0, 0)]
        [TestCase(10000, 1, 100)]
        [TestCase(10000, 10, 1000)]
        [TestCase(10000, 100, 10000)]
        [TestCase(100000, 0, 0)]
        [TestCase(100000, 1, 1000)]
        [TestCase(100000, 10, 10000)]
        [TestCase(100000, 100, 100000)]
        public void FrozenFractionsProduceExactSortedCoverage(
            int instanceCount,
            int movingPercent,
            int expectedChanged)
        {
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstanceUploadPlan plan =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        instanceCount,
                        movingPercent,
                        20260830,
                        ranges);

                Assert.That(plan.InstanceCount, Is.EqualTo(instanceCount));
                Assert.That(plan.MovingPercent, Is.EqualTo(movingPercent));
                Assert.That(
                    plan.ChangedInstanceCount,
                    Is.EqualTo(expectedChanged));
                Assert.That(
                    plan.RangeCount,
                    Is.InRange(
                        expectedChanged == 0 ? 0 : 1,
                        GpuDrivenInstanceUploadInputGenerator
                            .MaximumRangeCount));
                Assert.That(
                    SumAndValidateRanges(ranges, plan.RangeCount, instanceCount),
                    Is.EqualTo(expectedChanged));
                Assert.That(
                    GpuDrivenInstanceUploadInputGenerator.ComputePlanHash(
                        plan,
                        ranges),
                    Is.EqualTo(plan.RangePlanHash));

                if (movingPercent == 0)
                {
                    Assert.That(plan.RangeCount, Is.Zero);
                }
                if (movingPercent == 100)
                {
                    Assert.That(plan.RangeCount, Is.EqualTo(1));
                    Assert.That(ranges[0].StartIndex, Is.Zero);
                    Assert.That(ranges[0].Count, Is.EqualTo(instanceCount));
                }
            }
        }

        [Test]
        public void RangeTopologyIsDeterministicAndSeeded()
        {
            using (var left = CreateRanges())
            using (var right = CreateRanges())
            using (var different = CreateRanges())
            {
                GpuDrivenInstanceUploadPlan first =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        100000,
                        10,
                        12345,
                        left);
                GpuDrivenInstanceUploadPlan repeated =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        100000,
                        10,
                        12345,
                        right);
                GpuDrivenInstanceUploadPlan otherSeed =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        100000,
                        10,
                        54321,
                        different);

                Assert.That(repeated.RangePlanHash, Is.EqualTo(first.RangePlanHash));
                Assert.That(repeated.RangeCount, Is.EqualTo(first.RangeCount));
                for (int index = 0; index < first.RangeCount; index++)
                {
                    Assert.That(
                        right[index].StartIndex,
                        Is.EqualTo(left[index].StartIndex));
                    Assert.That(
                        right[index].Count,
                        Is.EqualTo(left[index].Count));
                }
                Assert.That(
                    otherSeed.RangePlanHash,
                    Is.Not.EqualTo(first.RangePlanHash));
            }
        }

        [Test]
        public void DirtyApplyWritesOnlyRangesAndPreservesRecordContract()
        {
            const int count = 1600;
            using (var baseStates = CreateBaseStates(count))
            using (var firstSlot = CreateSentinelStates(count))
            using (var secondSlot = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstanceUploadPlan plan =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        count,
                        10,
                        991,
                        ranges);
                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    firstSlot,
                    ranges,
                    plan,
                    17u);
                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
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
                        AssertSentinel(firstSlot[index]);
                        AssertSentinel(secondSlot[index]);
                        continue;
                    }

                    GpuInstanceState baseline = baseStates[index];
                    GpuInstanceState actual = firstSlot[index];
                    Assert.That(
                        actual.PositionRadius,
                        Is.EqualTo(secondSlot[index].PositionRadius));
                    Assert.That(
                        actual.PositionRadius,
                        Is.Not.EqualTo(baseline.PositionRadius));
                    Assert.That(
                        actual.PositionRadius.w,
                        Is.EqualTo(baseline.PositionRadius.w));
                    AssertNonPositionFieldsEqual(baseline, actual);
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
                GpuDrivenInstanceUploadPlan plan =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        count,
                        10,
                        77,
                        ranges);
                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    reusedSlot,
                    ranges,
                    plan,
                    1u);
                Vector4 ordinalOne =
                    reusedSlot[ranges[0].StartIndex].PositionRadius;

                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    reusedSlot,
                    ranges,
                    plan,
                    2u);
                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
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
                        Assert.That(
                            reusedSlot[index].PositionRadius,
                            Is.EqualTo(freshSlot[index].PositionRadius));
                    }
                }
                Assert.That(
                    reusedSlot[ranges[0].StartIndex].PositionRadius,
                    Is.Not.EqualTo(ordinalOne));
                Assert.That(
                    GpuDrivenInstanceUploadInputGenerator.ComputeUpdateHash(
                        plan,
                        1u),
                    Is.Not.EqualTo(
                        GpuDrivenInstanceUploadInputGenerator.ComputeUpdateHash(
                            plan,
                            2u)));
            }
        }

        [Test]
        public void FullPopulateMatchesDirtyLogicalStateAndInitializesUnchanged()
        {
            const int count = 1600;
            using (var baseStates = CreateBaseStates(count))
            using (var dirtySlot = CreateSentinelStates(count))
            using (var fullSlot = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstanceUploadPlan plan =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        count,
                        1,
                        809,
                        ranges);
                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    dirtySlot,
                    ranges,
                    plan,
                    29u);
                GpuDrivenInstanceUploadInputGenerator.PopulateFullState(
                    baseStates.AsReadOnly(),
                    fullSlot,
                    ranges,
                    plan,
                    29u);

                for (int index = 0; index < count; index++)
                {
                    if (Contains(ranges, plan.RangeCount, index))
                    {
                        AssertStatesEqual(dirtySlot[index], fullSlot[index]);
                    }
                    else
                    {
                        AssertStatesEqual(baseStates[index], fullSlot[index]);
                    }
                }
            }
        }

        [Test]
        public void SteadyStateBuildAndApplyAllocateNoManagedMemory()
        {
            const int count = 1600;
            using (var baseStates = CreateBaseStates(count))
            using (var staging = CreateSentinelStates(count))
            using (var ranges = CreateRanges())
            {
                GpuDrivenInstanceUploadPlan warmup =
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        count,
                        10,
                        42,
                        ranges);
                GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
                    baseStates.AsReadOnly(),
                    staging,
                    ranges,
                    warmup,
                    1u);

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (uint ordinal = 2u; ordinal < 34u; ordinal++)
                {
                    GpuDrivenInstanceUploadPlan plan =
                        GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                            count,
                            10,
                            42,
                            ranges);
                    GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
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

        [Test]
        public void InvalidFractionAndUndersizedRangeStorageFailClosed()
        {
            using (var ranges = new NativeArray<GpuInstanceDirtyRange>(
                       GpuDrivenInstanceUploadInputGenerator
                           .MaximumRangeCount - 1,
                       Allocator.Persistent))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuDrivenInstanceUploadInputGenerator
                        .ChangedInstanceCount(1000, 5));
                Assert.Throws<ArgumentException>(() =>
                    GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                        1000,
                        10,
                        1,
                        ranges));
            }
        }

        private static NativeArray<GpuInstanceDirtyRange> CreateRanges()
        {
            return new NativeArray<GpuInstanceDirtyRange>(
                GpuDrivenInstanceUploadInputGenerator.MaximumRangeCount,
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

        private static void AssertSentinel(GpuInstanceState actual)
        {
            AssertStatesEqual(Sentinel(), actual);
        }

        private static void AssertNonPositionFieldsEqual(
            GpuInstanceState expected,
            GpuInstanceState actual)
        {
            Assert.That(actual.LodDistances, Is.EqualTo(expected.LodDistances));
            Assert.That(actual.ApplicationId, Is.EqualTo(expected.ApplicationId));
            Assert.That(actual.DrawGroupBase, Is.EqualTo(expected.DrawGroupBase));
            Assert.That(actual.LodCount, Is.EqualTo(expected.LodCount));
            Assert.That(actual.ViewMask, Is.EqualTo(expected.ViewMask));
        }

        private static void AssertStatesEqual(
            GpuInstanceState expected,
            GpuInstanceState actual)
        {
            Assert.That(actual.PositionRadius, Is.EqualTo(expected.PositionRadius));
            AssertNonPositionFieldsEqual(expected, actual);
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
                Assert.That(range.StartIndex, Is.GreaterThanOrEqualTo(previousEnd));
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
