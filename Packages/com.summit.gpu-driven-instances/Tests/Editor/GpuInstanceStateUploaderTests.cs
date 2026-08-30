using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances.Tests
{
    public sealed class GpuInstanceStateUploaderTests
    {
        private const ulong SourceRevision = 1UL;

        [Test]
        public void EmptyPlanIsAnExactNoOp()
        {
            using (var uploader = new GpuInstanceStateUploader(8))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    default,
                    0);

                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.None));
                Assert.That(receipt.DirtyRecordCount, Is.Zero);
                Assert.That(receipt.UploadedRecordCount, Is.Zero);
                Assert.That(receipt.UploadCallCount, Is.Zero);
                Assert.That(receipt.LogicalUploadBytes, Is.Zero);
                Assert.That(uploader.PlannedRangeCount, Is.Zero);
            }
        }

        [Test]
        public void PlanSortsUnionsAndBridgesBoundedGaps()
        {
            using (var uploader = new GpuInstanceStateUploader(8, 4))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(20, 3),
                new GpuInstanceDirtyRange(14, 2),
                new GpuInstanceDirtyRange(10, 5)))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(receipt.InputRangeCount, Is.EqualTo(3));
                Assert.That(receipt.DirtyRecordCountExact, Is.True);
                Assert.That(receipt.DirtyRecordCount, Is.EqualTo(9));
                Assert.That(receipt.UploadedRecordCount, Is.EqualTo(13));
                Assert.That(receipt.BridgedCleanRecordCount, Is.EqualTo(4));
                Assert.That(receipt.UploadCallCount, Is.EqualTo(1));
                Assert.That(receipt.LogicalUploadBytes,
                    Is.EqualTo(13L * GpuInstanceState.Stride));
                Assert.That(uploader.GetPlannedRange(0).StartIndex,
                    Is.EqualTo(10));
                Assert.That(uploader.GetPlannedRange(0).Count,
                    Is.EqualTo(13));
            }
        }

        [Test]
        public void GapImmediatelyAbovePolicyRemainsSeparate()
        {
            using (var uploader = new GpuInstanceStateUploader(4, 4))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(0, 2),
                new GpuInstanceDirtyRange(7, 1)))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.UploadCallCount, Is.EqualTo(2));
                Assert.That(receipt.DirtyRecordCount, Is.EqualTo(3));
                Assert.That(receipt.UploadedRecordCount, Is.EqualTo(3));
            }
        }

        [Test]
        public void DuplicateAndContainedRangesCountExactUnionOnce()
        {
            using (var uploader = new GpuInstanceStateUploader(8))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(10, 10),
                new GpuInstanceDirtyRange(12, 2),
                new GpuInstanceDirtyRange(10, 10),
                new GpuInstanceDirtyRange(20, 1)))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.DirtyRecordCount, Is.EqualTo(11));
                Assert.That(receipt.UploadedRecordCount, Is.EqualTo(11));
                Assert.That(receipt.UploadCallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void OneHundredPercentDirtyRemainsExplicitDirtyPath()
        {
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(0, 100)))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(receipt.FullUploadReason,
                    Is.EqualTo(GpuInstanceFullUploadReason.None));
                Assert.That(receipt.UploadedRecordCount, Is.EqualTo(100));
                Assert.That(receipt.UploadCallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CapacityOverflowFailsSafeToFullUpload()
        {
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(0, 1),
                new GpuInstanceDirtyRange(10, 1),
                new GpuInstanceDirtyRange(20, 1)))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.Full));
                Assert.That(receipt.FullUploadReason,
                    Is.EqualTo(
                        GpuInstanceFullUploadReason.RangeCapacityExceeded));
                Assert.That(receipt.DirtyRecordCountExact, Is.False);
                Assert.That(receipt.DirtyRecordCount, Is.Zero);
                Assert.That(receipt.UploadedRecordCount, Is.EqualTo(100));
                Assert.That(receipt.UploadCallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ZeroLengthRangesAreLegalAndIgnored()
        {
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(100, 0)))
            {
                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    100,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.None));
                Assert.That(receipt.InputRangeCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void InvalidRangesFailBeforePlanning()
        {
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var negative = Ranges(
                new GpuInstanceDirtyRange(-1, 1)))
            using (var pastEnd = Ranges(
                new GpuInstanceDirtyRange(100, 1)))
            using (var overflow = Ranges(
                new GpuInstanceDirtyRange(1, int.MaxValue)))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.PlanDirty(100, negative, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.PlanDirty(100, pastEnd, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.PlanDirty(100, overflow, 1));
            }
        }

        [Test]
        public void FailedPlanCannotExposeAStalePreviousPlan()
        {
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var valid = Ranges(
                new GpuInstanceDirtyRange(1, 1)))
            using (var invalid = Ranges(
                new GpuInstanceDirtyRange(-1, 1)))
            {
                uploader.PlanDirty(10, valid, 1);
                Assert.That(uploader.PlannedRangeCount, Is.EqualTo(1));

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.PlanDirty(10, invalid, 1));
                Assert.That(uploader.PlannedRangeCount, Is.Zero);
            }
        }

        [Test]
        public void ZeroActiveCountNeedsNoCreatedSource()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore("A graphics-capable device is required.");
            }

            using (var uploader = new GpuInstanceStateUploader(2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                GpuInstanceUploadReceipt full = uploader.RecordFull(
                    commands,
                    buffer,
                    default,
                    0);
                GpuInstanceUploadReceipt dirty = uploader.RecordDirty(
                    commands,
                    buffer,
                    default,
                    0,
                    default,
                    0);

                Assert.That(full.UploadCallCount, Is.Zero);
                Assert.That(dirty.UploadCallCount, Is.Zero);
                Assert.That(commands.sizeInBytes, Is.Zero);
            }
        }

        [Test]
        public void WarmPlannerDoesNotAllocateManagedMemory()
        {
            using (var uploader = new GpuInstanceStateUploader(16, 2))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(400, 8),
                new GpuInstanceDirtyRange(100, 8),
                new GpuInstanceDirtyRange(200, 8),
                new GpuInstanceDirtyRange(300, 8)))
            {
                uploader.PlanDirty(1000, dirty, dirty.Length);

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 64; iteration++)
                {
                    uploader.PlanDirty(1000, dirty, dirty.Length);
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(allocated, Is.Zero);
            }
        }

        [Test]
        public void PlannedDirtyUploadRecordsExactlyTheInspectedPlan()
        {
            RequireGraphicsDevice();

            const int count = 32;
            using (var baseline = States(count, 1000u))
            using (var changed = States(count, 1000u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(20, 1),
                new GpuInstanceDirtyRange(5, 2)))
            using (var uploader = new GpuInstanceStateUploader(4))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                count,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                SetApplicationId(changed, 5, 5005u);
                SetApplicationId(changed, 6, 5006u);
                SetApplicationId(changed, 20, 5020u);
                buffer.SetData(baseline);

                GpuInstanceDirtyUploadPlan plan =
                    uploader.PlanDirtyUpload(
                        buffer,
                        changed,
                        count,
                        SourceRevision,
                        dirty,
                        dirty.Length);

                Assert.That(plan.IsValid, Is.True);
                Assert.That(plan.ActiveCount, Is.EqualTo(count));
                Assert.That(plan.SourceRevision,
                    Is.EqualTo(SourceRevision));
                Assert.That(plan.Receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(plan.Receipt.UploadCallCount, Is.EqualTo(2));
                Assert.That(plan.Receipt.UploadedRecordCount,
                    Is.EqualTo(3));
                Assert.That(uploader.GetPlannedRange(0).StartIndex,
                    Is.EqualTo(5));
                Assert.That(uploader.GetPlannedRange(1).StartIndex,
                    Is.EqualTo(20));

                GpuInstanceUploadReceipt recorded =
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        changed,
                        count,
                        SourceRevision,
                        in plan);
                AssertReceiptsEqual(plan.Receipt, recorded);
                Assert.That(plan.IsValid, Is.False);

                Graphics.ExecuteCommandBuffer(commands);
                AssertBufferStates(buffer, changed);
            }
        }

        [Test]
        public void PlannedNoneAndCapacityFallbackRemainExact()
        {
            RequireGraphicsDevice();

            const int count = 8;
            using (var source = States(count, 200u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(1, 1),
                new GpuInstanceDirtyRange(6, 1)))
            using (var uploader = new GpuInstanceStateUploader(1))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                count,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                GpuInstanceDirtyUploadPlan none =
                    uploader.PlanDirtyUpload(
                        buffer,
                        source,
                        count,
                        SourceRevision,
                        default,
                        0);
                Assert.That(none.Receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.None));
                Assert.That(none.Receipt.UploadCallCount, Is.Zero);
                uploader.RecordPlanned(
                    commands,
                    buffer,
                    source,
                    count,
                    SourceRevision,
                    in none);
                Assert.That(commands.sizeInBytes, Is.Zero);

                GpuInstanceDirtyUploadPlan full =
                    uploader.PlanDirtyUpload(
                        buffer,
                        source,
                        count,
                        SourceRevision,
                        dirty,
                        dirty.Length);
                Assert.That(full.Receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.Full));
                Assert.That(full.Receipt.FullUploadReason,
                    Is.EqualTo(
                        GpuInstanceFullUploadReason.RangeCapacityExceeded));
                Assert.That(full.Receipt.DirtyRecordCountExact, Is.False);
                Assert.That(full.Receipt.UploadedRecordCount,
                    Is.EqualTo(count));
                uploader.RecordPlanned(
                    commands,
                    buffer,
                    source,
                    count,
                    SourceRevision,
                    in full);
                Graphics.ExecuteCommandBuffer(commands);
                AssertBufferStates(buffer, source);
            }
        }

        [Test]
        public void PlannedTokenIsOwnerGenerationAndSingleUseBound()
        {
            RequireGraphicsDevice();

            using (var source = States(4, 0u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(1, 1)))
            using (var first = new GpuInstanceStateUploader(2))
            using (var second = new GpuInstanceStateUploader(2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                4,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                GpuInstanceDirtyUploadPlan stale =
                    first.PlanDirtyUpload(
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        dirty,
                        dirty.Length);
                GpuInstanceDirtyUploadPlan current =
                    first.PlanDirtyUpload(
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        dirty,
                        dirty.Length);
                Assert.That(stale.IsValid, Is.False);
                Assert.That(current.IsValid, Is.True);

                Assert.Throws<InvalidOperationException>(() =>
                    first.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        stale));
                Assert.Throws<InvalidOperationException>(() =>
                    second.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        current));
                Assert.That(commands.sizeInBytes, Is.Zero);
                Assert.That(current.IsValid, Is.True);

                GpuInstanceDirtyUploadPlan copied = current;
                first.RecordPlanned(
                    commands,
                    buffer,
                    source,
                    4,
                    SourceRevision,
                    in current);
                int recordedBytes = commands.sizeInBytes;
                Assert.Throws<InvalidOperationException>(() =>
                    first.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        copied));
                Assert.That(commands.sizeInBytes,
                    Is.EqualTo(recordedBytes));
                Assert.That(copied.IsValid, Is.False);
            }
        }

        [Test]
        public void ReceiptOnlyPlanningPreservesItsOriginalRangeContract()
        {
            RequireGraphicsDevice();

            using (var source = States(16, 0u))
            using (var firstRanges = Ranges(
                new GpuInstanceDirtyRange(1, 1)))
            using (var receiptOnlyRanges = Ranges(
                new GpuInstanceDirtyRange(12, 2),
                new GpuInstanceDirtyRange(4, 1)))
            using (var uploader = new GpuInstanceStateUploader(4))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                16,
                GpuInstanceState.Stride))
            {
                GpuInstanceDirtyUploadPlan token =
                    uploader.PlanDirtyUpload(
                        buffer,
                        source,
                        16,
                        SourceRevision,
                        firstRanges,
                        firstRanges.Length);

                GpuInstanceUploadReceipt receipt = uploader.PlanDirty(
                    16,
                    receiptOnlyRanges,
                    receiptOnlyRanges.Length);

                Assert.That(token.IsValid, Is.False);
                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                Assert.That(receipt.UploadCallCount, Is.EqualTo(2));
                Assert.That(uploader.PlannedRangeCount, Is.EqualTo(2));
                Assert.That(uploader.GetPlannedRange(0).StartIndex,
                    Is.EqualTo(4));
                Assert.That(uploader.GetPlannedRange(1).StartIndex,
                    Is.EqualTo(12));
            }
        }

        [Test]
        public void RecordPlannedRejectsRevisionAndResourceMismatches()
        {
            RequireGraphicsDevice();

            using (var source = States(4, 0u))
            using (var otherSource = States(4, 100u))
            using (var shortSource = States(3, 200u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(1, 1)))
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                4,
                GpuInstanceState.Stride))
            using (var otherBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                4,
                GpuInstanceState.Stride))
            using (var shortBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                3,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                GpuInstanceDirtyUploadPlan plan =
                    uploader.PlanDirtyUpload(
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        dirty,
                        dirty.Length);

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        4,
                        0UL,
                        plan));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        4,
                        SourceRevision + 1UL,
                        plan));
                Assert.Throws<ArgumentNullException>(() =>
                    uploader.RecordPlanned(
                        null,
                        buffer,
                        source,
                        4,
                        SourceRevision,
                        plan));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        3,
                        SourceRevision,
                        plan));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        shortSource,
                        4,
                        SourceRevision,
                        plan));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        shortBuffer,
                        source,
                        4,
                        SourceRevision,
                        plan));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        otherBuffer,
                        source,
                        4,
                        SourceRevision,
                        plan));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        otherSource,
                        4,
                        SourceRevision,
                        plan));

                Assert.That(commands.sizeInBytes, Is.Zero);
                Assert.That(plan.IsValid, Is.True);
                uploader.RecordPlanned(
                    commands,
                    buffer,
                    source,
                    4,
                    SourceRevision,
                    in plan);
                Assert.That(plan.IsValid, Is.False);
            }
        }

        [Test]
        public void PlanDirtyUploadRejectsZeroRevisionAndInvalidCapacities()
        {
            RequireGraphicsDevice();

            using (var source = States(4, 0u))
            using (var shortSource = States(3, 0u))
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                4,
                GpuInstanceState.Stride))
            using (var shortBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                3,
                GpuInstanceState.Stride))
            using (var wrongStride = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                4,
                sizeof(uint)))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.PlanDirtyUpload(
                        buffer,
                        source,
                        4,
                        0UL,
                        default,
                        0));
                Assert.Throws<ArgumentException>(() =>
                    uploader.PlanDirtyUpload(
                        buffer,
                        shortSource,
                        4,
                        SourceRevision,
                        default,
                        0));
                Assert.Throws<ArgumentException>(() =>
                    uploader.PlanDirtyUpload(
                        shortBuffer,
                        source,
                        4,
                        SourceRevision,
                        default,
                        0));
                Assert.Throws<ArgumentException>(() =>
                    uploader.PlanDirtyUpload(
                        wrongStride,
                        source,
                        4,
                        SourceRevision,
                        default,
                        0));
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Test]
        public void DisposedPlannedSourceCannotBeReplacedWithSameRevision()
        {
            RequireGraphicsDevice();

            NativeArray<GpuInstanceState> source = States(4, 0u);
            try
            {
                using (var dirty = Ranges(
                    new GpuInstanceDirtyRange(1, 1)))
                using (var uploader = new GpuInstanceStateUploader(2))
                using (var buffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    4,
                    GpuInstanceState.Stride))
                using (var commands = new CommandBuffer())
                {
                    GpuInstanceDirtyUploadPlan plan =
                        uploader.PlanDirtyUpload(
                            buffer,
                            source,
                            4,
                            SourceRevision,
                            dirty,
                            dirty.Length);

                    source.Dispose();
                    using (var replacement = States(4, 100u))
                    {
                        Assert.That(
                            () => uploader.RecordPlanned(
                                commands,
                                buffer,
                                replacement,
                                4,
                                SourceRevision,
                                plan),
                            Throws.InstanceOf<ArgumentException>());
                    }

                    Assert.That(commands.sizeInBytes, Is.Zero);
                }
            }
            finally
            {
                if (source.IsCreated)
                {
                    source.Dispose();
                }
            }
        }
#endif

        [Test]
        public void PlanGenerationExhaustionIsPermanentFailClosed()
        {
            using (var uploader = new GpuInstanceStateUploader(1))
            {
                FieldInfo generation = typeof(GpuInstanceStateUploader)
                    .GetField(
                        "planGeneration",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo exhausted = typeof(GpuInstanceStateUploader)
                    .GetField(
                        "planGenerationExhausted",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(generation, Is.Not.Null);
                Assert.That(exhausted, Is.Not.Null);

                generation.SetValue(uploader, ulong.MaxValue - 1UL);
                GpuInstanceUploadReceipt finalGeneration =
                    uploader.PlanDirty(1, default, 0);
                Assert.That(finalGeneration.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.None));
                Assert.That(
                    generation.GetValue(uploader),
                    Is.EqualTo(ulong.MaxValue));

                Assert.Throws<InvalidOperationException>(() =>
                    uploader.PlanDirty(1, default, 0));
                Assert.That(exhausted.GetValue(uploader), Is.EqualTo(true));
                Assert.That(uploader.PlannedRangeCount, Is.Zero);

                generation.SetValue(uploader, 0UL);
                Assert.Throws<InvalidOperationException>(() =>
                    uploader.PlanDirty(1, default, 0));
            }
        }

        [Test]
        public void DefaultPlannedTokenIsRejectedWithoutRecording()
        {
            RequireGraphicsDevice();

            using (var source = States(1, 0u))
            using (var uploader = new GpuInstanceStateUploader(1))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                GpuInstanceDirtyUploadPlan plan = default;
                Assert.That(plan.IsValid, Is.False);
                Assert.Throws<InvalidOperationException>(() =>
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        1,
                        SourceRevision,
                        plan));
                Assert.That(commands.sizeInBytes, Is.Zero);
            }
        }

        [Test]
        public void WarmPlannedRecordingDoesNotAllocateManagedMemory()
        {
            RequireGraphicsDevice();

            using (var source = States(1000, 0u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(400, 8),
                new GpuInstanceDirtyRange(100, 8),
                new GpuInstanceDirtyRange(200, 8),
                new GpuInstanceDirtyRange(300, 8)))
            using (var uploader = new GpuInstanceStateUploader(16, 2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1000,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    GpuInstanceDirtyUploadPlan warm =
                        uploader.PlanDirtyUpload(
                            buffer,
                            source,
                            1000,
                            SourceRevision,
                            dirty,
                            dirty.Length);
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        1000,
                        SourceRevision,
                        in warm);
                    commands.Clear();
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 64; iteration++)
                {
                    GpuInstanceDirtyUploadPlan plan =
                        uploader.PlanDirtyUpload(
                            buffer,
                            source,
                            1000,
                            SourceRevision,
                            dirty,
                            dirty.Length);
                    uploader.RecordPlanned(
                        commands,
                        buffer,
                        source,
                        1000,
                        SourceRevision,
                        in plan);
                    commands.Clear();
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(allocated, Is.Zero);
            }
        }

        [Test]
        public void PartialUploadPreservesUntouchedRecords()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore("A graphics-capable device is required.");
            }

            const int count = 32;
            using (var baseline = States(count, 1000u))
            using (var changed = States(count, 1000u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(20, 1),
                new GpuInstanceDirtyRange(5, 2)))
            using (var uploader = new GpuInstanceStateUploader(4))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                count,
                GpuInstanceState.Stride))
            {
                SetApplicationId(changed, 5, 5005u);
                SetApplicationId(changed, 6, 5006u);
                SetApplicationId(changed, 20, 5020u);
                buffer.SetData(baseline);

                GpuInstanceUploadReceipt receipt = uploader.UploadDirty(
                    buffer,
                    changed,
                    count,
                    dirty,
                    dirty.Length);

                Assert.That(receipt.UploadCallCount, Is.EqualTo(2));
                AssertBufferStates(buffer, changed);
            }
        }

        [Test]
        public void RecordedPartialUploadMatchesImmediateUpload()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore("A graphics-capable device is required.");
            }

            const int count = 16;
            using (var baseline = States(count, 100u))
            using (var changed = States(count, 100u))
            using (var dirty = Ranges(
                new GpuInstanceDirtyRange(3, 1)))
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                count,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                SetApplicationId(changed, 3, 9003u);
                buffer.SetData(baseline);
                GpuInstanceUploadReceipt receipt = uploader.RecordDirty(
                    commands,
                    buffer,
                    changed,
                    count,
                    dirty,
                    dirty.Length);
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(receipt.Mode,
                    Is.EqualTo(GpuInstanceUploadMode.DirtyRanges));
                AssertBufferStates(buffer, changed);
            }
        }

        [Test]
        public void FullUploadPathsReportAndWriteEveryRecord()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore("A graphics-capable device is required.");
            }

            const int count = 8;
            using (var first = States(count, 100u))
            using (var second = States(count, 500u))
            using (var uploader = new GpuInstanceStateUploader(2))
            using (var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                count,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                GpuInstanceUploadReceipt immediate = uploader.UploadFull(
                    buffer,
                    first,
                    count);
                Assert.That(immediate.FullUploadReason,
                    Is.EqualTo(GpuInstanceFullUploadReason.Explicit));
                Assert.That(immediate.UploadCallCount, Is.EqualTo(1));
                Assert.That(immediate.LogicalUploadBytes,
                    Is.EqualTo((long)count * GpuInstanceState.Stride));
                AssertBufferStates(buffer, first);

                GpuInstanceUploadReceipt recorded = uploader.RecordFull(
                    commands,
                    buffer,
                    second,
                    count);
                Graphics.ExecuteCommandBuffer(commands);
                Assert.That(recorded.UploadedRecordCount,
                    Is.EqualTo(count));
                AssertBufferStates(buffer, second);
            }
        }

        [Test]
        public void RecordRejectsInvalidGpuContracts()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore("A graphics-capable device is required.");
            }

            using (var source = States(1, 0u))
            using (var uploader = new GpuInstanceStateUploader(1))
            using (var wrongStride = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(uint)))
            using (var valid = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                GpuInstanceState.Stride))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentNullException>(() =>
                    uploader.RecordFull(null, valid, source, 1));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordFull(commands, wrongStride, source, 1));
                Assert.Throws<ArgumentException>(() =>
                    uploader.RecordFull(commands, valid, source, 2));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    uploader.RecordFull(commands, valid, source, -1));
            }
        }

        [Test]
        public void DisposeIsIdempotentAndRejectsFurtherUse()
        {
            var uploader = new GpuInstanceStateUploader(2);
            uploader.Dispose();
            uploader.Dispose();

            Assert.Throws<ObjectDisposedException>(() =>
                uploader.PlanDirty(1, default, 0));
        }

        private static NativeArray<GpuInstanceDirtyRange> Ranges(
            params GpuInstanceDirtyRange[] values)
        {
            return new NativeArray<GpuInstanceDirtyRange>(
                values,
                Allocator.Temp);
        }

        private static NativeArray<GpuInstanceState> States(
            int count,
            uint idBase)
        {
            var result = new NativeArray<GpuInstanceState>(
                count,
                Allocator.Temp);
            for (int index = 0; index < count; index++)
            {
                result[index] = new GpuInstanceState(
                    new Vector3(index, 0f, 0f),
                    1f,
                    new Vector4(10f, 0f, 0f, 0f),
                    idBase + (uint)index,
                    0u,
                    1u,
                    uint.MaxValue);
            }
            return result;
        }

        private static void RequireGraphicsDevice()
        {
            if (!GpuDrivenInstancePipeline.SupportsCurrentDevice)
            {
                Assert.Ignore("A graphics-capable device is required.");
            }
        }

        private static void AssertReceiptsEqual(
            GpuInstanceUploadReceipt expected,
            GpuInstanceUploadReceipt actual)
        {
            Assert.That(actual.Mode, Is.EqualTo(expected.Mode));
            Assert.That(actual.FullUploadReason,
                Is.EqualTo(expected.FullUploadReason));
            Assert.That(actual.InputRangeCount,
                Is.EqualTo(expected.InputRangeCount));
            Assert.That(actual.DirtyRecordCount,
                Is.EqualTo(expected.DirtyRecordCount));
            Assert.That(actual.DirtyRecordCountExact,
                Is.EqualTo(expected.DirtyRecordCountExact));
            Assert.That(actual.UploadedRecordCount,
                Is.EqualTo(expected.UploadedRecordCount));
            Assert.That(actual.UploadCallCount,
                Is.EqualTo(expected.UploadCallCount));
            Assert.That(actual.LogicalUploadBytes,
                Is.EqualTo(expected.LogicalUploadBytes));
        }

        private static GpuInstanceState WithApplicationId(
            GpuInstanceState state,
            uint applicationId)
        {
            state.ApplicationId = applicationId;
            return state;
        }

        private static void SetApplicationId(
            NativeArray<GpuInstanceState> states,
            int index,
            uint applicationId)
        {
            states[index] = WithApplicationId(
                states[index],
                applicationId);
        }

        private static void AssertBufferStates(
            GraphicsBuffer buffer,
            NativeArray<GpuInstanceState> expected)
        {
            var actual = new GpuInstanceState[expected.Length];
            buffer.GetData(actual);
            for (int index = 0; index < expected.Length; index++)
            {
                GpuInstanceState expectedState = expected[index];
                GpuInstanceState actualState = actual[index];
                Assert.That(actualState.PositionRadius,
                    Is.EqualTo(expectedState.PositionRadius),
                    $"PositionRadius mismatch at {index}.");
                Assert.That(actualState.LodDistances,
                    Is.EqualTo(expectedState.LodDistances),
                    $"LodDistances mismatch at {index}.");
                Assert.That(actualState.ApplicationId,
                    Is.EqualTo(expectedState.ApplicationId),
                    $"ApplicationId mismatch at {index}.");
                Assert.That(actualState.DrawGroupBase,
                    Is.EqualTo(expectedState.DrawGroupBase),
                    $"DrawGroupBase mismatch at {index}.");
                Assert.That(actualState.LodCount,
                    Is.EqualTo(expectedState.LodCount),
                    $"LodCount mismatch at {index}.");
                Assert.That(actualState.ViewMask,
                    Is.EqualTo(expectedState.ViewMask),
                    $"ViewMask mismatch at {index}.");
            }
        }
    }
}
