using System;

namespace Summit.GpuResidencyManager
{
    public sealed class GpuPageResidencyPlanner
    {
        public const uint InvalidPhysicalSlot = 0xffffffffu;

        private readonly int[] virtualToPhysical;
        private readonly int[] physicalToVirtual;
        private readonly int[] lastUsedFrame;
        private readonly int[] requestStamp;
        private readonly GpuPageUpload[] uploads;
        private readonly GpuPageTableDelta[] deltas;
        private int previousMappedCount;

        public GpuPageResidencyPlanner(
            int virtualPageCount,
            int physicalSlotCount,
            int maximumRequestedPages,
            GpuResidencyPolicy policy)
        {
            if (virtualPageCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(virtualPageCount));
            }
            if (physicalSlotCount < maximumRequestedPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(physicalSlotCount));
            }
            if (maximumRequestedPages < 1 ||
                maximumRequestedPages > virtualPageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumRequestedPages));
            }

            VirtualPageCount = virtualPageCount;
            PhysicalSlotCount = physicalSlotCount;
            MaximumRequestedPages = maximumRequestedPages;
            Policy = policy;
            virtualToPhysical = new int[virtualPageCount];
            physicalToVirtual = new int[physicalSlotCount];
            lastUsedFrame = new int[physicalSlotCount];
            requestStamp = new int[virtualPageCount];
            uploads = new GpuPageUpload[maximumRequestedPages];
            deltas = new GpuPageTableDelta[maximumRequestedPages * 2];
            Reset();
        }

        public int VirtualPageCount { get; }

        public int PhysicalSlotCount { get; }

        public int MaximumRequestedPages { get; }

        public GpuResidencyPolicy Policy { get; }

        public void Reset()
        {
            Array.Fill(virtualToPhysical, -1);
            Array.Fill(physicalToVirtual, -1);
            Array.Fill(lastUsedFrame, -1);
            Array.Fill(requestStamp, -1);
            previousMappedCount = 0;
        }

        public GpuResidencyFramePlan PlanFrame(
            int[] requestedPages,
            int frameIndex)
        {
            if (requestedPages == null)
            {
                throw new ArgumentNullException(nameof(requestedPages));
            }
            if (requestedPages.Length < 1 ||
                requestedPages.Length > MaximumRequestedPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedPages));
            }
            if (frameIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
            }
            ValidateRequests(requestedPages, frameIndex);

            return Policy == GpuResidencyPolicy.RebuildVisibleSet
                ? PlanRebuild(requestedPages, frameIndex)
                : PlanPersistent(requestedPages, frameIndex);
        }

        public int PhysicalSlotForVirtualPage(int virtualPage)
        {
            if (virtualPage < 0 || virtualPage >= VirtualPageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(virtualPage));
            }
            return virtualToPhysical[virtualPage];
        }

        private GpuResidencyFramePlan PlanRebuild(
            int[] requestedPages,
            int frameIndex)
        {
            int priorMappedCount = previousMappedCount;
            int deltaCount = 0;
            for (int slot = 0; slot < priorMappedCount; slot++)
            {
                int previousVirtual = physicalToVirtual[slot];
                if (previousVirtual >= 0)
                {
                    virtualToPhysical[previousVirtual] = -1;
                    if (requestStamp[previousVirtual] != frameIndex)
                    {
                        deltas[deltaCount++] = new GpuPageTableDelta(
                            checked((uint)previousVirtual),
                            InvalidPhysicalSlot);
                    }
                }
                physicalToVirtual[slot] = -1;
            }

            for (int index = 0; index < requestedPages.Length; index++)
            {
                int virtualPage = requestedPages[index];
                int slot = index;
                virtualToPhysical[virtualPage] = slot;
                physicalToVirtual[slot] = virtualPage;
                lastUsedFrame[slot] = frameIndex;
                uploads[index] = new GpuPageUpload(
                    checked((uint)virtualPage),
                    checked((uint)slot));
                deltas[deltaCount++] = new GpuPageTableDelta(
                    checked((uint)virtualPage),
                    checked((uint)slot));
            }
            previousMappedCount = requestedPages.Length;
            return new GpuResidencyFramePlan(
                requestedPages,
                uploads,
                requestedPages.Length,
                deltas,
                deltaCount,
                hitCount: 0,
                evictionCount: priorMappedCount);
        }

        private GpuResidencyFramePlan PlanPersistent(
            int[] requestedPages,
            int frameIndex)
        {
            int hitCount = 0;
            int uploadCount = 0;
            int deltaCount = 0;
            int evictionCount = 0;
            for (int index = 0; index < requestedPages.Length; index++)
            {
                int virtualPage = requestedPages[index];
                int slot = virtualToPhysical[virtualPage];
                if (slot >= 0)
                {
                    hitCount++;
                    lastUsedFrame[slot] = frameIndex;
                    continue;
                }

                slot = FindVictimSlot(frameIndex);
                int evictedVirtual = physicalToVirtual[slot];
                if (evictedVirtual >= 0)
                {
                    virtualToPhysical[evictedVirtual] = -1;
                    deltas[deltaCount++] = new GpuPageTableDelta(
                        checked((uint)evictedVirtual),
                        InvalidPhysicalSlot);
                    evictionCount++;
                }
                virtualToPhysical[virtualPage] = slot;
                physicalToVirtual[slot] = virtualPage;
                lastUsedFrame[slot] = frameIndex;
                uploads[uploadCount++] = new GpuPageUpload(
                    checked((uint)virtualPage),
                    checked((uint)slot));
                deltas[deltaCount++] = new GpuPageTableDelta(
                    checked((uint)virtualPage),
                    checked((uint)slot));
            }
            previousMappedCount = Math.Min(
                PhysicalSlotCount,
                previousMappedCount + uploadCount);
            return new GpuResidencyFramePlan(
                requestedPages,
                uploads,
                uploadCount,
                deltas,
                deltaCount,
                hitCount,
                evictionCount);
        }

        private int FindVictimSlot(int frameIndex)
        {
            int oldestSlot = -1;
            int oldestFrame = int.MaxValue;
            for (int slot = 0; slot < PhysicalSlotCount; slot++)
            {
                int virtualPage = physicalToVirtual[slot];
                if (virtualPage < 0)
                {
                    return slot;
                }
                if (requestStamp[virtualPage] == frameIndex)
                {
                    continue;
                }
                if (lastUsedFrame[slot] < oldestFrame)
                {
                    oldestFrame = lastUsedFrame[slot];
                    oldestSlot = slot;
                }
            }
            if (oldestSlot < 0)
            {
                throw new InvalidOperationException(
                    "No non-requested physical slot is available.");
            }
            return oldestSlot;
        }

        private void ValidateRequests(int[] requestedPages, int frameIndex)
        {
            for (int index = 0; index < requestedPages.Length; index++)
            {
                int page = requestedPages[index];
                if (page < 0 || page >= VirtualPageCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(requestedPages));
                }
                if (requestStamp[page] == frameIndex)
                {
                    throw new ArgumentException(
                        "Requested pages must be unique.",
                        nameof(requestedPages));
                }
                requestStamp[page] = frameIndex;
            }
        }
    }

    public sealed class GpuResidencyFramePlan
    {
        internal GpuResidencyFramePlan(
            int[] requestedPages,
            GpuPageUpload[] uploads,
            int uploadCount,
            GpuPageTableDelta[] deltas,
            int deltaCount,
            int hitCount,
            int evictionCount)
        {
            RequestedPages = requestedPages;
            Uploads = uploads;
            UploadCount = uploadCount;
            Deltas = deltas;
            DeltaCount = deltaCount;
            HitCount = hitCount;
            EvictionCount = evictionCount;
        }

        public int[] RequestedPages { get; }
        public GpuPageUpload[] Uploads { get; }
        public int UploadCount { get; }
        public GpuPageTableDelta[] Deltas { get; }
        public int DeltaCount { get; }
        public int HitCount { get; }
        public int EvictionCount { get; }
        public int MissCount => UploadCount;
    }
}
