using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances
{
    public enum GpuInstanceUploadMode
    {
        None = 0,
        DirtyRanges = 1,
        Full = 2,
    }

    public enum GpuInstanceFullUploadReason
    {
        None = 0,
        Explicit = 1,
        RangeCapacityExceeded = 2,
    }

    /// <summary>
    /// A half-open range of instance records in the authoritative CPU mirror.
    /// </summary>
    public readonly struct GpuInstanceDirtyRange :
        IComparable<GpuInstanceDirtyRange>
    {
        public GpuInstanceDirtyRange(int startIndex, int count)
        {
            StartIndex = startIndex;
            Count = count;
        }

        public int StartIndex { get; }

        public int Count { get; }

        public int EndIndex => checked(StartIndex + Count);

        public int CompareTo(GpuInstanceDirtyRange other)
        {
            int start = StartIndex.CompareTo(other.StartIndex);
            return start != 0 ? start : Count.CompareTo(other.Count);
        }
    }

    /// <summary>
    /// Exact logical-byte and command accounting for one upload decision.
    /// </summary>
    public readonly struct GpuInstanceUploadReceipt
    {
        internal GpuInstanceUploadReceipt(
            GpuInstanceUploadMode mode,
            GpuInstanceFullUploadReason fullUploadReason,
            int inputRangeCount,
            int dirtyRecordCount,
            bool dirtyRecordCountExact,
            int uploadedRecordCount,
            int uploadCallCount)
        {
            Mode = mode;
            FullUploadReason = fullUploadReason;
            InputRangeCount = inputRangeCount;
            DirtyRecordCount = dirtyRecordCount;
            DirtyRecordCountExact = dirtyRecordCountExact;
            UploadedRecordCount = uploadedRecordCount;
            UploadCallCount = uploadCallCount;
        }

        public GpuInstanceUploadMode Mode { get; }

        public GpuInstanceFullUploadReason FullUploadReason { get; }

        public int InputRangeCount { get; }

        public int DirtyRecordCount { get; }

        public bool DirtyRecordCountExact { get; }

        public int UploadedRecordCount { get; }

        public int UploadCallCount { get; }

        public int BridgedCleanRecordCount => DirtyRecordCountExact
            ? checked(UploadedRecordCount - DirtyRecordCount)
            : 0;

        /// <summary>
        /// Bytes requested through the Unity upload API. This is not a claim
        /// about PCIe traffic, driver staging, or physical copy-engine bytes.
        /// </summary>
        public long LogicalUploadBytes =>
            checked((long)UploadedRecordCount * GpuInstanceState.Stride);
    }

    /// <summary>
    /// An immutable, single-use dirty-upload plan.
    /// </summary>
    /// <remarks>
    /// The token is bound to the uploader, source array, caller-owned nonzero
    /// source revision, destination buffer, active count, and the uploader
    /// scratch generation that produced it. Creating any later plan on the
    /// same uploader invalidates an outstanding token. Recording consumes the
    /// token. Copies share the same validity and cannot be used to record the
    /// plan more than once.
    /// </remarks>
    public readonly struct GpuInstanceDirtyUploadPlan
    {
        private readonly GpuInstanceStateUploader owner;
        private readonly ulong generation;
        private readonly GraphicsBuffer destination;
        private readonly NativeArray<GpuInstanceState> source;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private readonly AtomicSafetyHandle sourceSafetyHandle;
        private readonly bool sourceHasSafetyHandle;
#endif

        internal GpuInstanceDirtyUploadPlan(
            GpuInstanceStateUploader owner,
            ulong generation,
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            ulong sourceRevision,
            ulong expectedResidentStateRevision,
            GpuInstanceUploadReceipt receipt)
        {
            this.owner = owner;
            this.generation = generation;
            this.destination = destination;
            this.source = source;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            sourceHasSafetyHandle = source.IsCreated;
            sourceSafetyHandle = sourceHasSafetyHandle
                ? NativeArrayUnsafeUtility.GetAtomicSafetyHandle(source)
                : default;
#endif
            ActiveCount = activeCount;
            SourceRevision = sourceRevision;
            ExpectedResidentStateRevision = expectedResidentStateRevision;
            Receipt = receipt;
        }

        /// <summary>
        /// The exact upload decision and logical-byte accounting.
        /// </summary>
        public GpuInstanceUploadReceipt Receipt { get; }

        /// <summary>
        /// The active instance count against which every range was validated.
        /// </summary>
        public int ActiveCount { get; }

        /// <summary>
        /// The nonzero caller revision of the authoritative source contents.
        /// </summary>
        public ulong SourceRevision { get; }

        /// <summary>
        /// The caller-owned nonzero revision of the destination contents from
        /// which the declared dirty ranges were computed. Zero means that the
        /// legacy planning overload did not bind a resident base revision.
        /// </summary>
        public ulong ExpectedResidentStateRevision { get; }

        /// <summary>
        /// True only while this token is the owner's unconsumed current plan.
        /// </summary>
        public bool IsValid
        {
            get
            {
                if (owner == null || !owner.IsPlanCurrent(generation))
                {
                    return false;
                }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (sourceHasSafetyHandle)
                {
                    AtomicSafetyHandle handle = sourceSafetyHandle;
                    if (!AtomicSafetyHandle.IsHandleValid(handle))
                    {
                        return false;
                    }
                    try
                    {
                        AtomicSafetyHandle.CheckReadAndThrow(
                            handle);
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException ||
                        exception is ArgumentException)
                    {
                        return false;
                    }
                }
#endif
                return true;
            }
        }

        internal GpuInstanceStateUploader Owner => owner;

        internal ulong Generation => generation;

        internal GraphicsBuffer Destination => destination;

        internal NativeArray<GpuInstanceState> Source => source;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal void CheckSourceExistsAndThrow()
        {
            if (!sourceHasSafetyHandle)
            {
                return;
            }
            try
            {
                AtomicSafetyHandle handle = sourceSafetyHandle;
                if (!AtomicSafetyHandle.IsHandleValid(handle))
                {
                    throw new InvalidOperationException(
                        "The source NativeArray bound to this upload plan " +
                        "is no longer valid.");
                }
                AtomicSafetyHandle.CheckReadAndThrow(handle);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException ||
                exception is ArgumentException)
            {
                throw new InvalidOperationException(
                    "The source NativeArray bound to this upload plan " +
                    "is no longer valid.",
                    exception);
            }
        }
#endif
    }

    /// <summary>
    /// Allocation-free dirty-range normalization and instance-state upload.
    /// </summary>
    /// <remarks>
    /// This class deliberately does not choose between full and dirty uploads
    /// using a performance heuristic. RecordFull and RecordDirty make that
    /// experimental variable explicit. Dirty ranges are sorted, unioned, and
    /// optionally bridged by a caller-selected fixed gap. If the declared
    /// range capacity is exceeded, RecordDirty fails safe to one full upload.
    /// PlanDirtyUpload and RecordPlanned let a policy inspect and record one
    /// immutable, owner-bound normalization result without planning twice.
    /// Their caller-owned source revision must be nonzero and must change when
    /// the source contents or backing allocation changes.
    ///
    /// A NativeArray passed to RecordFull, RecordDirty, or RecordPlanned must
    /// remain immutable until an AllGPUOperations fence after command execution
    /// has passed. The uploader is not thread-safe.
    /// </remarks>
    public sealed class GpuInstanceStateUploader : IDisposable
    {
        private NativeArray<GpuInstanceDirtyRange> rangeScratch;
        private readonly int maximumMergedGapRecords;
        private int plannedRangeCount;
        private ulong planGeneration;
        private bool planGenerationExhausted;
        private bool currentPlanAvailable;
        private bool disposed;

        public GpuInstanceStateUploader(
            int rangeCapacity,
            int maximumMergedGapRecords = 0)
        {
            if (rangeCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rangeCapacity));
            }
            if (maximumMergedGapRecords < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumMergedGapRecords));
            }

            rangeScratch = new NativeArray<GpuInstanceDirtyRange>(
                rangeCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            this.maximumMergedGapRecords = maximumMergedGapRecords;
        }

        public int RangeCapacity
        {
            get
            {
                ThrowIfDisposed();
                return rangeScratch.Length;
            }
        }

        public int MaximumMergedGapRecords => maximumMergedGapRecords;

        public int PlannedRangeCount
        {
            get
            {
                ThrowIfDisposed();
                return plannedRangeCount;
            }
        }

        public GpuInstanceDirtyRange GetPlannedRange(int index)
        {
            ThrowIfDisposed();
            if (index < 0 || index >= plannedRangeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return rangeScratch[index];
        }

        public GpuInstanceUploadReceipt PlanDirty(
            int activeCount,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            ThrowIfDisposed();
            BeginPlanGeneration();
            return PlanDirtyCore(
                activeCount,
                dirtyRanges,
                dirtyRangeCount);
        }

        /// <summary>
        /// Normalizes dirty ranges once and returns the exact plan that can be
        /// inspected before it is recorded with <see cref="RecordPlanned"/>.
        /// </summary>
        public GpuInstanceDirtyUploadPlan PlanDirtyUpload(
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            ulong sourceRevision,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            return PlanDirtyUploadCore(
                destination,
                source,
                activeCount,
                sourceRevision,
                0UL,
                false,
                dirtyRanges,
                dirtyRangeCount);
        }

        /// <summary>
        /// Normalizes dirty ranges and binds them to the exact nonzero resident
        /// destination revision from which those ranges were computed.
        /// </summary>
        public GpuInstanceDirtyUploadPlan PlanDirtyUpload(
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            ulong sourceRevision,
            ulong expectedResidentStateRevision,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            return PlanDirtyUploadCore(
                destination,
                source,
                activeCount,
                sourceRevision,
                expectedResidentStateRevision,
                true,
                dirtyRanges,
                dirtyRangeCount);
        }

        private GpuInstanceDirtyUploadPlan PlanDirtyUploadCore(
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            ulong sourceRevision,
            ulong expectedResidentStateRevision,
            bool requireExpectedResidentStateRevision,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            ThrowIfDisposed();
            ulong generation = BeginPlanGeneration();
            ValidateSourceRevision(sourceRevision);
            if (requireExpectedResidentStateRevision &&
                expectedResidentStateRevision == 0UL)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedResidentStateRevision),
                    "expectedResidentStateRevision must be nonzero.");
            }
            ValidateUploadInputs(destination, source, activeCount);
            GpuInstanceUploadReceipt receipt = PlanDirtyCore(
                activeCount,
                dirtyRanges,
                dirtyRangeCount);
            currentPlanAvailable = true;
            return new GpuInstanceDirtyUploadPlan(
                this,
                generation,
                destination,
                source,
                activeCount,
                sourceRevision,
                expectedResidentStateRevision,
                receipt);
        }

        private GpuInstanceUploadReceipt PlanDirtyCore(
            int activeCount,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            plannedRangeCount = 0;
            ValidateActiveCount(activeCount);
            if (dirtyRangeCount < 0 ||
                (!dirtyRanges.IsCreated && dirtyRangeCount != 0) ||
                (dirtyRanges.IsCreated &&
                    dirtyRangeCount > dirtyRanges.Length))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dirtyRangeCount));
            }
            if (dirtyRangeCount == 0)
            {
                plannedRangeCount = 0;
                return EmptyReceipt(0);
            }

            int nonEmptyRangeCount = 0;
            for (int index = 0; index < dirtyRangeCount; index++)
            {
                GpuInstanceDirtyRange range = dirtyRanges[index];
                ValidateRange(range, activeCount, index);
                if (range.Count > 0)
                {
                    nonEmptyRangeCount++;
                }
            }
            if (nonEmptyRangeCount == 0)
            {
                plannedRangeCount = 0;
                return EmptyReceipt(dirtyRangeCount);
            }
            if (nonEmptyRangeCount > rangeScratch.Length)
            {
                plannedRangeCount = 0;
                return FullReceipt(
                    GpuInstanceFullUploadReason.RangeCapacityExceeded,
                    dirtyRangeCount,
                    0,
                    false,
                    activeCount);
            }

            int copied = 0;
            for (int index = 0; index < dirtyRangeCount; index++)
            {
                GpuInstanceDirtyRange range = dirtyRanges[index];
                if (range.Count > 0)
                {
                    rangeScratch[copied++] = range;
                }
            }
            rangeScratch.GetSubArray(0, copied).Sort();

            int dirtyRecordCount = CountExactUnion(copied);
            int mergedCount = MergeUploadRanges(copied);
            int uploadedRecordCount = 0;
            for (int index = 0; index < mergedCount; index++)
            {
                uploadedRecordCount = checked(
                    uploadedRecordCount + rangeScratch[index].Count);
            }

            plannedRangeCount = mergedCount;
            return new GpuInstanceUploadReceipt(
                GpuInstanceUploadMode.DirtyRanges,
                GpuInstanceFullUploadReason.None,
                dirtyRangeCount,
                dirtyRecordCount,
                true,
                uploadedRecordCount,
                mergedCount);
        }

        public GpuInstanceUploadReceipt RecordDirty(
            CommandBuffer commands,
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            ValidateRecordInputs(commands, destination, source, activeCount);
            BeginPlanGeneration();
            GpuInstanceUploadReceipt receipt = PlanDirtyCore(
                activeCount,
                dirtyRanges,
                dirtyRangeCount);
            return RecordPrepared(
                commands,
                destination,
                source,
                activeCount,
                receipt);
        }

        /// <summary>
        /// Records exactly one previously inspected dirty-upload plan.
        /// </summary>
        /// <remarks>
        /// All token, active-count, source, destination, and capacity checks
        /// complete before the first command is recorded. A validation failure
        /// leaves the current valid token unconsumed so the caller can correct
        /// its arguments. A command-recording failure consumes the token.
        /// </remarks>
        public GpuInstanceUploadReceipt RecordPlanned(
            CommandBuffer commands,
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            ulong sourceRevision,
            in GpuInstanceDirtyUploadPlan plan)
        {
            ThrowIfDisposed();
            ValidateCurrentPlan(in plan);
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (activeCount != plan.ActiveCount)
            {
                throw new ArgumentException(
                    "activeCount must match the planned active count.",
                    nameof(activeCount));
            }
            ValidateSourceRevision(sourceRevision);
            if (sourceRevision != plan.SourceRevision)
            {
                throw new ArgumentException(
                    "sourceRevision must match the planned source revision.",
                    nameof(sourceRevision));
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            plan.CheckSourceExistsAndThrow();
#endif
            ValidateUploadInputs(destination, source, activeCount);
            if (!ReferenceEquals(destination, plan.Destination))
            {
                throw new ArgumentException(
                    "Destination must match the buffer bound to the plan.",
                    nameof(destination));
            }
            if (!source.Equals(plan.Source))
            {
                throw new ArgumentException(
                    "Source must match the NativeArray bound to the plan.",
                    nameof(source));
            }

            currentPlanAvailable = false;
            return RecordPrepared(
                commands,
                destination,
                source,
                activeCount,
                plan.Receipt);
        }

        private GpuInstanceUploadReceipt RecordPrepared(
            CommandBuffer commands,
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            GpuInstanceUploadReceipt receipt)
        {
            if (receipt.Mode == GpuInstanceUploadMode.Full)
            {
                commands.SetBufferData(
                    destination,
                    source,
                    0,
                    0,
                    activeCount);
                return receipt;
            }
            for (int index = 0; index < plannedRangeCount; index++)
            {
                GpuInstanceDirtyRange range = rangeScratch[index];
                commands.SetBufferData(
                    destination,
                    source,
                    range.StartIndex,
                    range.StartIndex,
                    range.Count);
            }
            return receipt;
        }

        public GpuInstanceUploadReceipt RecordFull(
            CommandBuffer commands,
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount)
        {
            ValidateRecordInputs(commands, destination, source, activeCount);
            InvalidateCurrentPlan();
            if (activeCount == 0)
            {
                return EmptyReceipt(0);
            }
            commands.SetBufferData(
                destination,
                source,
                0,
                0,
                activeCount);
            return FullReceipt(
                GpuInstanceFullUploadReason.Explicit,
                0,
                activeCount,
                true,
                activeCount);
        }

        public GpuInstanceUploadReceipt UploadDirty(
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount,
            NativeArray<GpuInstanceDirtyRange> dirtyRanges,
            int dirtyRangeCount)
        {
            ValidateUploadInputs(destination, source, activeCount);
            GpuInstanceUploadReceipt receipt = PlanDirty(
                activeCount,
                dirtyRanges,
                dirtyRangeCount);
            if (receipt.Mode == GpuInstanceUploadMode.Full)
            {
                destination.SetData(source, 0, 0, activeCount);
                return receipt;
            }
            for (int index = 0; index < plannedRangeCount; index++)
            {
                GpuInstanceDirtyRange range = rangeScratch[index];
                destination.SetData(
                    source,
                    range.StartIndex,
                    range.StartIndex,
                    range.Count);
            }
            return receipt;
        }

        public GpuInstanceUploadReceipt UploadFull(
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount)
        {
            ValidateUploadInputs(destination, source, activeCount);
            InvalidateCurrentPlan();
            if (activeCount == 0)
            {
                return EmptyReceipt(0);
            }
            destination.SetData(source, 0, 0, activeCount);
            return FullReceipt(
                GpuInstanceFullUploadReason.Explicit,
                0,
                activeCount,
                true,
                activeCount);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            InvalidateCurrentPlan();
            if (rangeScratch.IsCreated)
            {
                rangeScratch.Dispose();
            }
        }

        private int CountExactUnion(int sortedRangeCount)
        {
            int result = 0;
            int currentStart = rangeScratch[0].StartIndex;
            int currentEnd = rangeScratch[0].EndIndex;
            for (int index = 1; index < sortedRangeCount; index++)
            {
                GpuInstanceDirtyRange next = rangeScratch[index];
                if (next.StartIndex <= currentEnd)
                {
                    currentEnd = Math.Max(currentEnd, next.EndIndex);
                    continue;
                }
                result = checked(result + currentEnd - currentStart);
                currentStart = next.StartIndex;
                currentEnd = next.EndIndex;
            }
            return checked(result + currentEnd - currentStart);
        }

        private int MergeUploadRanges(int sortedRangeCount)
        {
            int mergedCount = 0;
            int currentStart = rangeScratch[0].StartIndex;
            int currentEnd = rangeScratch[0].EndIndex;
            for (int index = 1; index < sortedRangeCount; index++)
            {
                GpuInstanceDirtyRange next = rangeScratch[index];
                int gap = next.StartIndex - currentEnd;
                if (gap <= maximumMergedGapRecords)
                {
                    currentEnd = Math.Max(currentEnd, next.EndIndex);
                    continue;
                }
                rangeScratch[mergedCount++] = new GpuInstanceDirtyRange(
                    currentStart,
                    currentEnd - currentStart);
                currentStart = next.StartIndex;
                currentEnd = next.EndIndex;
            }
            rangeScratch[mergedCount++] = new GpuInstanceDirtyRange(
                currentStart,
                currentEnd - currentStart);
            return mergedCount;
        }

        private ulong BeginPlanGeneration()
        {
            currentPlanAvailable = false;
            plannedRangeCount = 0;
            if (planGenerationExhausted)
            {
                throw new InvalidOperationException(
                    "Upload plan generation space is permanently exhausted.");
            }
            if (planGeneration == ulong.MaxValue)
            {
                planGenerationExhausted = true;
                throw new InvalidOperationException(
                    "Upload plan generation space is permanently exhausted.");
            }
            planGeneration++;
            return planGeneration;
        }

        private void InvalidateCurrentPlan()
        {
            currentPlanAvailable = false;
            plannedRangeCount = 0;
        }

        internal bool IsPlanCurrent(ulong generation)
        {
            return !disposed &&
                !planGenerationExhausted &&
                currentPlanAvailable &&
                generation != 0 &&
                generation == planGeneration;
        }

        private void ValidateCurrentPlan(
            in GpuInstanceDirtyUploadPlan plan)
        {
            if (!ReferenceEquals(plan.Owner, this))
            {
                throw new InvalidOperationException(
                    "The upload plan belongs to another uploader or is " +
                    "uninitialized.");
            }
            if (!IsPlanCurrent(plan.Generation))
            {
                throw new InvalidOperationException(
                    "The upload plan is stale or has already been consumed.");
            }
        }

        private void ValidateRecordInputs(
            CommandBuffer commands,
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            ValidateUploadInputs(destination, source, activeCount);
        }

        private void ValidateUploadInputs(
            GraphicsBuffer destination,
            NativeArray<GpuInstanceState> source,
            int activeCount)
        {
            ThrowIfDisposed();
            ValidateActiveCount(activeCount);
            if (activeCount > 0 &&
                (!source.IsCreated || source.Length < activeCount))
            {
                throw new ArgumentException(
                    "Source NativeArray must cover activeCount.",
                    nameof(source));
            }
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            if ((destination.target & GraphicsBuffer.Target.Structured) == 0 ||
                destination.stride != GpuInstanceState.Stride ||
                destination.count < Math.Max(1, activeCount))
            {
                throw new ArgumentException(
                    "Destination must be a structured GraphicsBuffer with " +
                    $"stride {GpuInstanceState.Stride} and enough records.",
                    nameof(destination));
            }
        }

        private static void ValidateActiveCount(int activeCount)
        {
            if (activeCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(activeCount));
            }
        }

        private static void ValidateSourceRevision(ulong sourceRevision)
        {
            if (sourceRevision == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceRevision),
                    "sourceRevision must be nonzero.");
            }
        }

        private static void ValidateRange(
            GpuInstanceDirtyRange range,
            int activeCount,
            int rangeIndex)
        {
            if (range.StartIndex < 0 || range.Count < 0 ||
                range.Count > activeCount ||
                range.StartIndex > activeCount - range.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(range),
                    $"Dirty range {rangeIndex} is outside activeCount.");
            }
        }

        private static GpuInstanceUploadReceipt EmptyReceipt(
            int inputRangeCount)
        {
            return new GpuInstanceUploadReceipt(
                GpuInstanceUploadMode.None,
                GpuInstanceFullUploadReason.None,
                inputRangeCount,
                0,
                true,
                0,
                0);
        }

        private static GpuInstanceUploadReceipt FullReceipt(
            GpuInstanceFullUploadReason reason,
            int inputRangeCount,
            int dirtyRecordCount,
            bool dirtyRecordCountExact,
            int activeCount)
        {
            return new GpuInstanceUploadReceipt(
                GpuInstanceUploadMode.Full,
                reason,
                inputRangeCount,
                dirtyRecordCount,
                dirtyRecordCountExact,
                activeCount,
                activeCount == 0 ? 0 : 1);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuInstanceStateUploader));
            }
        }
    }
}
