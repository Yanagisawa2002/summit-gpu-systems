using System;

namespace Summit.GpuResidencyManager
{
    /// <summary>Single-threaded planner. Successful plans must execute in order; retire only after consumers finish.</summary>
    public sealed class GpuPageResidencyPlanner
    {
        public const uint InvalidPhysicalSlot = 0xffffffffu;
        private readonly int[] virtualToPhysical, physicalToVirtual, free, heap, heapPosition, pins;
        private readonly long[] lastUsed, serviceAge, selectedFrame, pendingSince;
        private readonly bool[] seen, active, waiting;
        private readonly GpuPageRequest[] requests;
        private readonly int[] order, previousRequests;
        private readonly GpuResidencyFramePlan[] plans;
        private int freeCount, heapCount, previousCount;
        private long previousFrame = -1;

        public GpuPageResidencyPlanner(int virtualPageCount, int physicalSlotCount,
            int maximumRequestedPages, GpuResidencyPolicy policy, int maximumInFlightFrames = 3)
        {
            if (virtualPageCount < 1) throw new ArgumentOutOfRangeException(nameof(virtualPageCount));
            if (physicalSlotCount < 1 || physicalSlotCount > virtualPageCount)
                throw new ArgumentOutOfRangeException(nameof(physicalSlotCount));
            if (maximumRequestedPages < 1 || maximumRequestedPages > virtualPageCount)
                throw new ArgumentOutOfRangeException(nameof(maximumRequestedPages));
            if (maximumInFlightFrames < 1) throw new ArgumentOutOfRangeException(nameof(maximumInFlightFrames));
            if (!Enum.IsDefined(typeof(GpuResidencyPolicy), policy)) throw new ArgumentOutOfRangeException(nameof(policy));
            VirtualPageCount = virtualPageCount; PhysicalSlotCount = physicalSlotCount;
            MaximumRequestedPages = maximumRequestedPages; Policy = policy;
            virtualToPhysical = new int[virtualPageCount]; physicalToVirtual = new int[physicalSlotCount];
            free = new int[physicalSlotCount]; heap = new int[physicalSlotCount];
            heapPosition = new int[physicalSlotCount]; pins = new int[physicalSlotCount];
            lastUsed = new long[physicalSlotCount]; serviceAge = new long[virtualPageCount]; selectedFrame = new long[virtualPageCount]; pendingSince = new long[virtualPageCount];
            waiting = new bool[virtualPageCount]; seen = new bool[virtualPageCount]; active = new bool[virtualPageCount];
            requests = new GpuPageRequest[maximumRequestedPages]; order = new int[maximumRequestedPages];
            previousRequests = new int[maximumRequestedPages];
            plans = new GpuResidencyFramePlan[maximumInFlightFrames];
            for (int i = 0; i < plans.Length; i++)
                plans[i] = new GpuResidencyFramePlan(this, maximumRequestedPages, physicalSlotCount);
            Reset();
        }
        public int VirtualPageCount { get; }
        public int PhysicalSlotCount { get; }
        public int MaximumRequestedPages { get; }
        public GpuResidencyPolicy Policy { get; }

        public void Reset()
        {
            foreach (var plan in plans) if (plan.IsActive) throw new InvalidOperationException("Retire every plan before resetting.");
            Array.Fill(virtualToPhysical, -1); Array.Fill(physicalToVirtual, -1);
            Array.Fill(heapPosition, -1); Array.Clear(pins, 0, pins.Length);
            Array.Clear(waiting, 0, waiting.Length); Array.Fill(selectedFrame, -1L); Array.Clear(active, 0, active.Length); Array.Fill(lastUsed, -1L);
            for (int i = 0; i < free.Length; i++) free[i] = free.Length - 1 - i;
            freeCount = free.Length; heapCount = 0; previousCount = 0; previousFrame = -1;
        }

        public GpuResidencyFramePlan PlanFrame(int[] requestedPages, int frameIndex)
        {
            if (requestedPages == null) throw new ArgumentNullException(nameof(requestedPages));
            ValidateCount(requestedPages.Length);
            for (int i = 0; i < requestedPages.Length; i++) requests[i] = new GpuPageRequest(requestedPages[i]);
            return Plan(frameIndex, requestedPages.Length, PhysicalSlotCount);
        }

        /// <summary>Input is a complete interest snapshot, including prefetch. Omitted pending pages are cancelled.</summary>
        public GpuResidencyFramePlan PlanFrame(GpuPageRequest[] interest, long frameId, int uploadBudgetPages)
        {
            if (interest == null) throw new ArgumentNullException(nameof(interest));
            ValidateCount(interest.Length);
            Array.Copy(interest, requests, interest.Length);
            return Plan(frameId, interest.Length, uploadBudgetPages);
        }

        private void ValidateCount(int count)
        {
            if (count > MaximumRequestedPages) throw new ArgumentOutOfRangeException(nameof(count));
        }

        private GpuResidencyFramePlan Plan(long frame, int count, int budget)
        {
            if (frame < 0 || frame <= previousFrame || frame > long.MaxValue - 8192)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame IDs must strictly increase, with 8192 reserved for priority arithmetic.");
            if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
            // Validation touches scratch only. Even a duplicate/invalid suffix can be retried with the same frame ID.
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int p = requests[i].VirtualPage;
                    if (p < 0 || p >= VirtualPageCount || requests[i].Priority < 0 || requests[i].Priority > 255)
                        throw new ArgumentOutOfRangeException(nameof(requests));
                    if (seen[p]) throw new ArgumentException("Interest pages must be unique, including prefetch.");
                    seen[p] = true;
                }
            }
            finally
            {
                for (int i = 0; i < count; i++)
                { int p = requests[i].VirtualPage; if (p >= 0 && p < seen.Length) seen[p] = false; }
            }
            GpuResidencyFramePlan plan = null;
            foreach (var candidate in plans) if (!candidate.IsActive) { plan = candidate; break; }
            if (plan == null) throw new InvalidOperationException("All frame leases are in flight. Retire a completed plan first.");
            if (Policy == GpuResidencyPolicy.RebuildVisibleSet)
                foreach (var candidate in plans) if (candidate.IsActive)
                    throw new InvalidOperationException("Rebuild requires all previous consumers to complete.");

            for (int i = 0; i < count; i++) seen[requests[i].VirtualPage] = true;
            for (int i = 0; i < previousCount; i++)
                if (!seen[previousRequests[i]]) { active[previousRequests[i]] = false; waiting[previousRequests[i]] = false; }
            plan.Begin(frame, previousFrame);
            for (int i = 0; i < count; i++)
            {
                int p = requests[i].VirtualPage;
                if (!active[p]) { active[p] = true; serviceAge[p] = frame; }
                if (virtualToPhysical[p] < 0 && !waiting[p]) { waiting[p] = true; pendingSince[p] = frame; }
                seen[p] = false; previousRequests[i] = p; order[i] = i;
                if (!requests[i].Prefetch)
                {
                    plan.RequestedPages[plan.RequestedCount++] = p;
                    if (virtualToPhysical[p] >= 0 && Policy != GpuResidencyPolicy.RebuildVisibleSet) plan.HitCount++;
                }
            }
            previousCount = count; previousFrame = frame;
            plan.MissCount = plan.RequestedCount - plan.HitCount;
            if (Policy == GpuResidencyPolicy.RebuildVisibleSet)
            {
                for (int slot = 0; slot < PhysicalSlotCount; slot++)
                    if (physicalToVirtual[slot] >= 0)
                    {
                        int p = physicalToVirtual[slot];
                        plan.Deltas[plan.DeltaCount++] = new GpuPageTableDelta((uint)p, InvalidPhysicalSlot);
                        virtualToPhysical[p] = -1; physicalToVirtual[slot] = -1;
                        if (active[p] && !waiting[p]) { waiting[p] = true; pendingSince[p] = frame; } plan.EvictionCount++;
                    }
                heapCount = 0; Array.Fill(heapPosition, -1);
                freeCount = free.Length;
                for (int i = 0; i < free.Length; i++) free[i] = free.Length - 1 - i;
            }
            // In-place heapsort: deterministic aged priority, then virtual page ID. No comparer/delegate allocations.
            for (int i = count / 2 - 1; i >= 0; i--) SortDown(i, count);
            for (int n = count - 1; n > 0; n--) { Swap(order, 0, n); SortDown(0, n); }
            int admitted = Math.Min(count, PhysicalSlotCount);
            // Protect selected hits before processing misses. Oversubscribed interests age into this bounded set.
            for (int i = 0; i < admitted; i++)
            {
                int slot = virtualToPhysical[requests[order[i]].VirtualPage];
                if (slot >= 0) Pin(plan, slot);
            }
            for (int i = 0; i < admitted; i++)
            {
                var request = requests[order[i]]; int p = request.VirtualPage;
                int slot = virtualToPhysical[p];
                if (slot < 0)
                {
                    if (plan.UploadCount >= budget) continue;
                    slot = TakeSlot();
                    if (slot < 0) continue; // All slots may be held by earlier GPU consumers.
                    int old = physicalToVirtual[slot];
                    if (old >= 0)
                    {
                        virtualToPhysical[old] = -1;
                        if (active[old] && !waiting[old]) { waiting[old] = true; pendingSince[old] = frame; }
                        plan.Deltas[plan.DeltaCount++] = new GpuPageTableDelta((uint)old, InvalidPhysicalSlot);
                        plan.EvictionCount++;
                    }
                    virtualToPhysical[p] = slot; physicalToVirtual[slot] = p;
                    plan.Uploads[plan.UploadCount++] = new GpuPageUpload((uint)p, (uint)slot);
                    plan.Deltas[plan.DeltaCount++] = new GpuPageTableDelta((uint)p, (uint)slot);
                    plan.TotalServiceLatencyFrames += frame - pendingSince[p];
                    plan.MaximumServiceLatencyFrames = Math.Max(plan.MaximumServiceLatencyFrames, frame - pendingSince[p]);
                    if (request.Prefetch) plan.PrefetchUploadCount++;
                    Pin(plan, slot);
                }
                waiting[p] = false; lastUsed[slot] = frame; serviceAge[p] = frame; selectedFrame[p] = frame;
            }
            for (int i = 0; i < plan.RequestedCount; i++)
            {
                int slot = virtualToPhysical[plan.RequestedPages[i]];
                // Only admitted/pinned pages are available to this plan, even if another unpinned mapping survives.
                bool protectedSlot = slot >= 0 && selectedFrame[plan.RequestedPages[i]] == frame;
                plan.RequestedPhysicalSlots[i] = protectedSlot ? slot : -1;
                if (protectedSlot) plan.AvailableCount++;
            }
            int compacted = 0;
            for (int i = 0; i < plan.DeltaCount; i++)
            {
                var delta = plan.Deltas[i];
                if (delta.PhysicalSlot == InvalidPhysicalSlot && virtualToPhysical[delta.VirtualPage] >= 0) continue;
                plan.Deltas[compacted++] = delta;
            }
            plan.DeltaCount = compacted;
            plan.PendingCount = 0;
            for (int i = 0; i < count; i++)
                if (virtualToPhysical[requests[i].VirtualPage] < 0) plan.PendingCount++;
            return plan;
        }

        private long Rank(int index)
        {
            var r = requests[index];
            return serviceAge[r.VirtualPage] - (long)r.Priority * 8 + (r.Prefetch ? 4096 : 0);
        }
        private bool RequestGreater(int a, int b)
        { long x = Rank(a), y = Rank(b); return x > y || (x == y && requests[a].VirtualPage > requests[b].VirtualPage); }
        private void SortDown(int root, int size)
        {
            while (root < size / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < size && RequestGreater(order[child + 1], order[child])) child++;
                if (!RequestGreater(order[child], order[root])) break;
                Swap(order, root, child); root = child;
            }
        }
        private static void Swap(int[] a, int x, int y) { int t = a[x]; a[x] = a[y]; a[y] = t; }
        private bool Older(int a, int b) => lastUsed[a] < lastUsed[b] || (lastUsed[a] == lastUsed[b] && a < b);
        private void HeapSwap(int a, int b)
        { Swap(heap, a, b); heapPosition[heap[a]] = a; heapPosition[heap[b]] = b; }
        private void Remove(int slot)
        {
            if (Policy == GpuResidencyPolicy.PersistentLru) return;
            int at = heapPosition[slot]; if (at < 0) return;
            heapPosition[slot] = -1; int tail = heap[--heapCount];
            if (at == heapCount) return;
            heap[at] = tail; heapPosition[tail] = at;
            while (at > 0 && Older(heap[at], heap[(at - 1) / 2])) { int p = (at - 1) / 2; HeapSwap(at, p); at = p; }
            while (at < heapCount / 2)
            {
                int c = at * 2 + 1;
                if (c + 1 < heapCount && Older(heap[c + 1], heap[c])) c++;
                if (!Older(heap[c], heap[at])) break;
                HeapSwap(at, c); at = c;
            }
        }
        private void Insert(int slot)
        {
            if (Policy == GpuResidencyPolicy.PersistentLru) return;
            int at = heapCount++; heap[at] = slot; heapPosition[slot] = at;
            while (at > 0 && Older(heap[at], heap[(at - 1) / 2])) { int p = (at - 1) / 2; HeapSwap(at, p); at = p; }
        }
        private int TakeSlot()
        {
            if (freeCount > 0) return free[--freeCount];
            int slot = -1;
            if (Policy == GpuResidencyPolicy.PersistentLru)
            {
                for (int i = 0; i < PhysicalSlotCount; i++)
                    if (pins[i] == 0 && (slot < 0 || Older(i, slot))) slot = i;
            }
            else if (heapCount > 0) slot = heap[0];
            if (slot >= 0) Remove(slot);
            return slot;
        }
        private void Pin(GpuResidencyFramePlan plan, int slot)
        { if (pins[slot]++ == 0) Remove(slot); plan.PinnedSlots[plan.PinnedCount++] = slot; }
        public void CompleteFrame(GpuResidencyFramePlan plan)
        {
            if (plan == null || plan.Owner != this || !plan.IsActive)
                throw new ArgumentException("Expected an active lease from this planner.", nameof(plan));
            for (int i = 0; i < plan.PinnedCount; i++)
            { int slot = plan.PinnedSlots[i]; if (--pins[slot] == 0) Insert(slot); }
            plan.IsActive = false;
        }
        /// <summary>Scheduled mapping, not proof of completed GPU upload. Use a plan's availability plus GPU ordering.</summary>
        public int PhysicalSlotForVirtualPage(int virtualPage)
        {
            if (virtualPage < 0 || virtualPage >= VirtualPageCount) throw new ArgumentOutOfRangeException(nameof(virtualPage));
            return virtualToPhysical[virtualPage];
        }
    }

    /// <summary>Preallocated lease. Arrays are read-only to callers, valid only until CompleteFrame; use explicit counts.</summary>
    public sealed class GpuResidencyFramePlan
    {
        internal readonly GpuPageResidencyPlanner Owner;
        internal readonly int[] PinnedSlots;
        internal int PinnedCount;
        internal GpuResidencyFramePlan(GpuPageResidencyPlanner owner, int requests, int slots)
        {
            Owner = owner; RequestedPages = new int[requests]; RequestedPhysicalSlots = new int[requests];
            Uploads = new GpuPageUpload[Math.Min(requests, slots)];
            Deltas = new GpuPageTableDelta[checked(slots + Math.Min(requests, slots))];
            PinnedSlots = new int[slots];
        }
        internal void Begin(long frame, long predecessor)
        {
            FrameId = frame; PreviousFrameId = predecessor; IsActive = true;
            RequestedCount = UploadCount = DeltaCount = HitCount = MissCount = EvictionCount = PinnedCount = 0;
            AvailableCount = PendingCount = PrefetchUploadCount = 0;
            TotalServiceLatencyFrames = MaximumServiceLatencyFrames = 0;
        }
        /// <summary>Only after fence completion or after all consumers are ordered before future writes on the same queue.</summary>
        public void OwnerComplete() => Owner.CompleteFrame(this);
        public bool IsActive { get; internal set; }
        public long PreviousFrameId { get; private set; }
        public long FrameId { get; private set; }
        public int[] RequestedPages { get; }
        public int[] RequestedPhysicalSlots { get; }
        public GpuPageUpload[] Uploads { get; }
        public GpuPageTableDelta[] Deltas { get; }
        public int RequestedCount { get; internal set; }
        public int UploadCount { get; internal set; }
        public int DeltaCount { get; internal set; }
        public int HitCount { get; internal set; }
        public int MissCount { get; internal set; }
        public int EvictionCount { get; internal set; }
        public int AvailableCount { get; internal set; }
        public int DeferredDemandCount => RequestedCount - AvailableCount;
        public int PendingCount { get; internal set; }
        public int PrefetchUploadCount { get; internal set; }
        public double TotalServiceLatencyFrames { get; internal set; }
        public long MaximumServiceLatencyFrames { get; internal set; }
    }
}
