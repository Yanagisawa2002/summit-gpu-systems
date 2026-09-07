using System;

namespace Summit.GpuDeadlineScheduler
{
    public enum GpuAdmissionResult { Accepted, Capacity, CostBudget, InvalidJob, DuplicateJob, InvalidDependency, Cycle }
    public enum GpuRuntimeJobState { Free, Pending, Submitted, Completed }

    public readonly struct GpuJobDependency
    {
        public GpuJobDependency(int prerequisiteId, int dependentId)
        { PrerequisiteId = prerequisiteId; DependentId = dependentId; }
        public int PrerequisiteId { get; }
        public int DependentId { get; }
    }

    public readonly struct GpuRuntimeJob
    {
        public GpuRuntimeJob(GpuDeadlineJob job, int costKey) { Job = job; CostKey = costKey; }
        public GpuDeadlineJob Job { get; }
        public int CostKey { get; }
    }

    /// <summary>Measured evidence is scoped by the application to the current workload/environment.
    /// Default, expired or failing evidence always selects the main queue.</summary>
    public readonly struct GpuAsyncAdmissionEvidence
    {
        private readonly bool accepted;
        private readonly long measuredAt, expiresAt;
        public GpuAsyncAdmissionEvidence(int pairedSamples, double mainAverage, double asyncAverage,
            double mainP99, double asyncP99, double mainCriticalP99, double asyncCriticalP99,
            double mainMissRate, double asyncMissRate, long measuredAtMicroseconds, long validUntilMicroseconds)
        {
            measuredAt = measuredAtMicroseconds; expiresAt = validUntilMicroseconds;
            accepted = pairedSamples >= 16 && Positive(mainAverage) && Positive(asyncAverage) &&
                Positive(mainP99) && Positive(asyncP99) && Positive(mainCriticalP99) && Positive(asyncCriticalP99) &&
                mainMissRate >= 0 && mainMissRate <= 1 && asyncMissRate >= 0 && asyncMissRate <= 1 &&
                asyncAverage <= mainAverage * 0.98 && asyncP99 <= mainP99 &&
                asyncCriticalP99 <= mainCriticalP99 && asyncMissRate <= mainMissRate &&
                measuredAt >= 0 && expiresAt > measuredAt;
        }
        public bool Allows(bool supported, long now) => supported && accepted && now >= measuredAt && now < expiresAt;
        private static bool Positive(double value) => value > 0 && !double.IsInfinity(value);
    }

    public readonly struct GpuRuntimeDispatch
    {
        internal GpuRuntimeDispatch(int slot, long ticket, GpuDeadlineJob job, GpuDeadlineQueue queue, int cost, long wait)
        { Slot = slot; Ticket = ticket; Job = job; Queue = queue; EstimatedCostMicroseconds = cost; WaitMicroseconds = wait; }
        public int Slot { get; }
        public long Ticket { get; }
        public GpuDeadlineJob Job { get; }
        public GpuDeadlineQueue Queue { get; }
        public int EstimatedCostMicroseconds { get; }
        public long WaitMicroseconds { get; }
    }

    /// <summary>Bounded, single-threaded admission and non-preemptive dispatch planning. All hot APIs reuse
    /// constructor storage. Completed jobs retain their IDs until ReleaseCompleted is called.</summary>
    public sealed class GpuRuntimeScheduler
    {
        private struct Entry
        {
            public GpuRuntimeJob Request;
            public GpuRuntimeJobState State;
            public long Arrival, Ticket;
            public int SubmittedCost;
            public GpuDeadlineQueue Queue;
        }
        private readonly Entry[] entries, staging;
        private readonly bool[] edges, stagedEdges;
        private readonly int[] indegrees;
        private readonly bool[] visited;
        private readonly long costBudget, agingThreshold;
        private readonly int maximumInFlight;
        private readonly bool fifo;
        private long sequence, clock;
        private int inFlight;

        public GpuRuntimeScheduler(int capacity, long outstandingCostBudgetMicroseconds,
            long backgroundAgingMicroseconds, int maximumInFlightJobs, GpuRuntimeCostEstimator costs, bool fifo = false)
        {
            if (capacity < 1 || capacity > 1024 || outstandingCostBudgetMicroseconds < 1 ||
                backgroundAgingMicroseconds < 1 || maximumInFlightJobs < 1 || maximumInFlightJobs > capacity)
                throw new ArgumentOutOfRangeException();
            Costs = costs ?? throw new ArgumentNullException(nameof(costs));
            entries = new Entry[capacity]; staging = new Entry[capacity];
            edges = new bool[capacity * capacity]; stagedEdges = new bool[edges.Length];
            indegrees = new int[capacity]; visited = new bool[capacity];
            costBudget = outstandingCostBudgetMicroseconds; agingThreshold = backgroundAgingMicroseconds;
            maximumInFlight = maximumInFlightJobs; this.fifo = fifo;
        }

        public GpuRuntimeCostEstimator Costs { get; }
        public int Capacity => entries.Length;
        public int InFlightCount => inFlight;
        public int PendingCount { get { int count = 0; for (int i = 0; i < Capacity; i++) if (entries[i].State == GpuRuntimeJobState.Pending) count++; return count; } }
        public GpuRuntimeJobState State(int slot) { ValidateSlot(slot); return entries[slot].State; }
        public long Ticket(int slot) { ValidateSlot(slot); return entries[slot].Ticket; }
        public GpuDeadlineQueue Queue(int slot) { ValidateSlot(slot); return entries[slot].Queue; }
        public bool DependsOn(int slot, int prerequisiteSlot) { ValidateSlot(slot); ValidateSlot(prerequisiteSlot); return edges[slot * Capacity + prerequisiteSlot]; }

        /// <summary>Atomic batch admission. Edges may refer to retained existing prerequisites; dependents must
        /// belong to this new batch. Rejection leaves all live jobs unchanged; callers own retry/backpressure.</summary>
        public GpuAdmissionResult TryAdmit(GpuRuntimeJob[] jobs, int count, GpuJobDependency[] dependencies, int dependencyCount, long now)
        {
            CheckClock(now);
            if (jobs == null || count < 0 || count > jobs.Length || dependencies == null || dependencyCount < 0 || dependencyCount > dependencies.Length)
                throw new ArgumentOutOfRangeException();
            if (count > Capacity) return GpuAdmissionResult.Capacity;
            if (dependencyCount > edges.Length) return GpuAdmissionResult.InvalidDependency;
            if (sequence > long.MaxValue - count) throw new InvalidOperationException("Job ticket space exhausted; create a new scheduler after draining.");
            Array.Copy(entries, staging, Capacity); Array.Copy(edges, stagedEdges, edges.Length);
            long outstanding = 0;
            for (int i = 0; i < Capacity; i++)
                if (entries[i].State == GpuRuntimeJobState.Pending) outstanding += Costs.Estimate(entries[i].Request.CostKey);
                else if (entries[i].State == GpuRuntimeJobState.Submitted) outstanding += entries[i].SubmittedCost;
            for (int j = 0; j < count; j++)
            {
                GpuDeadlineJob job = jobs[j].Job;
                if (job.WorkItemCount < 1 || job.Iterations < 1 || job.RelativeDeadlineMicroseconds < 1 ||
                    (int)job.DeadlineClass < 0 || (int)job.DeadlineClass > 2 || now > long.MaxValue - job.RelativeDeadlineMicroseconds)
                    return GpuAdmissionResult.InvalidJob;
                if (Find(staging, job.JobId) >= 0) return GpuAdmissionResult.DuplicateJob;
                int cost;
                try { cost = Costs.Estimate(jobs[j].CostKey); }
                catch (ArgumentOutOfRangeException) { return GpuAdmissionResult.InvalidJob; }
                catch (InvalidOperationException) { return GpuAdmissionResult.InvalidJob; }
                outstanding += cost;
                int free = -1;
                for (int i = 0; i < Capacity; i++) if (staging[i].State == GpuRuntimeJobState.Free) { free = i; break; }
                if (free < 0) return GpuAdmissionResult.Capacity;
                staging[free] = new Entry { Request = jobs[j], State = GpuRuntimeJobState.Pending, Arrival = now };
            }
            if (outstanding > costBudget) return GpuAdmissionResult.CostBudget;
            for (int e = 0; e < dependencyCount; e++)
            {
                int before = Find(staging, dependencies[e].PrerequisiteId), after = Find(staging, dependencies[e].DependentId);
                if (before < 0 || after < 0 || entries[after].State != GpuRuntimeJobState.Free || stagedEdges[after * Capacity + before])
                    return GpuAdmissionResult.InvalidDependency;
                stagedEdges[after * Capacity + before] = true;
            }
            Array.Clear(indegrees, 0, Capacity); Array.Clear(visited, 0, Capacity);
            int remaining = 0;
            for (int i = 0; i < Capacity; i++) if (staging[i].State != GpuRuntimeJobState.Free)
            { remaining++; for (int p = 0; p < Capacity; p++) if (stagedEdges[i * Capacity + p]) indegrees[i]++; }
            while (remaining > 0)
            {
                int ready = -1;
                for (int i = 0; i < Capacity; i++) if (staging[i].State != GpuRuntimeJobState.Free && !visited[i] && indegrees[i] == 0) { ready = i; break; }
                if (ready < 0) return GpuAdmissionResult.Cycle;
                visited[ready] = true; remaining--;
                for (int i = 0; i < Capacity; i++) if (stagedEdges[i * Capacity + ready]) indegrees[i]--;
            }
            for (int i = 0; i < Capacity; i++) if (staging[i].State != GpuRuntimeJobState.Free && entries[i].State == GpuRuntimeJobState.Free)
                staging[i].Ticket = checked(++sequence);
            Array.Copy(staging, entries, Capacity); Array.Copy(stagedEdges, edges, edges.Length);
            return GpuAdmissionResult.Accepted;
        }

        /// <summary>Returns one ready job. Dependencies may already be submitted: the executor MUST wait on
        /// their fences across queues. Calling this does not submit or reserve the job.</summary>
        public bool TryPrepare(long now, bool supportsAsync, GpuAsyncAdmissionEvidence evidence, out GpuRuntimeDispatch dispatch)
        {
            CheckClock(now); dispatch = default;
            if (inFlight >= maximumInFlight) return false;
            int best = -1;
            for (int i = 0; i < Capacity; i++)
            {
                if (entries[i].State != GpuRuntimeJobState.Pending) continue;
                bool ready = true;
                for (int p = 0; p < Capacity; p++) if (edges[i * Capacity + p] && entries[p].State == GpuRuntimeJobState.Pending) { ready = false; break; }
                if (ready && (best < 0 || Before(i, best, now))) best = i;
            }
            if (best < 0) return false;
            Entry selected = entries[best];
            GpuDeadlineQueue queue = evidence.Allows(supportsAsync, now) && selected.Request.Job.DeadlineClass != GpuDeadlineClass.Background
                ? GpuDeadlineQueue.ComputeUrgent : GpuDeadlineQueue.MainGraphics;
            dispatch = new GpuRuntimeDispatch(best, selected.Ticket, selected.Request.Job, queue,
                Costs.Estimate(selected.Request.CostKey), now - selected.Arrival);
            return true;
        }

        /// <summary>Commit immediately after successful submission, before preparing another job.</summary>
        public void MarkSubmitted(GpuRuntimeDispatch dispatch)
        {
            ValidateSlot(dispatch.Slot);
            ref Entry entry = ref entries[dispatch.Slot];
            if (entry.Ticket != dispatch.Ticket || entry.State != GpuRuntimeJobState.Pending || inFlight >= maximumInFlight)
                throw new InvalidOperationException("Stale dispatch or submission limit reached.");
            for (int p = 0; p < Capacity; p++) if (edges[dispatch.Slot * Capacity + p] && entries[p].State == GpuRuntimeJobState.Pending)
                throw new InvalidOperationException("Prerequisite has not been submitted.");
            entry.State = GpuRuntimeJobState.Submitted; entry.SubmittedCost = dispatch.EstimatedCostMicroseconds;
            entry.Queue = dispatch.Queue; inFlight++;
        }

        public long BeginCostSample(GpuRuntimeDispatch dispatch, long now)
        {
            CheckClock(now); ValidateSlot(dispatch.Slot);
            if (entries[dispatch.Slot].Ticket != dispatch.Ticket || entries[dispatch.Slot].State != GpuRuntimeJobState.Submitted)
                throw new InvalidOperationException("Sample requires the current submitted dispatch.");
            return Costs.BeginSample(entries[dispatch.Slot].Request.CostKey, now);
        }

        public bool TryComplete(int slot, long ticket)
        {
            ValidateSlot(slot);
            if (entries[slot].Ticket != ticket || entries[slot].State != GpuRuntimeJobState.Submitted) return false;
            entries[slot].State = GpuRuntimeJobState.Completed; inFlight--; return true;
        }

        /// <summary>Reclaims completed jobs only when no pending/submitted dependent needs their identity/fence.
        /// New admissions cannot reference an ID after reclamation. Delayed estimator tokens remain independent.</summary>
        public int ReleaseCompleted()
        {
            int released = 0;
            for (int p = 0; p < Capacity; p++)
            {
                if (entries[p].State != GpuRuntimeJobState.Completed) continue;
                bool retained = false;
                for (int i = 0; i < Capacity; i++) if (edges[i * Capacity + p] &&
                    (entries[i].State == GpuRuntimeJobState.Pending || entries[i].State == GpuRuntimeJobState.Submitted)) { retained = true; break; }
                if (retained) continue;
                entries[p] = default; released++;
                for (int i = 0; i < Capacity; i++) { edges[p * Capacity + i] = false; edges[i * Capacity + p] = false; }
            }
            return released;
        }

        private bool Before(int left, int right, long now)
        {
            Entry a = entries[left], b = entries[right];
            if (fifo) return a.Ticket < b.Ticket;
            // Age every class so a background prerequisite cannot be starved by a critical stream.
            bool agedA = now - a.Arrival >= agingThreshold, agedB = now - b.Arrival >= agingThreshold;
            if (agedA != agedB) return agedA;
            if (agedA) return a.Ticket < b.Ticket;
            long slackA = Slack(a, now);
            long slackB = Slack(b, now);
            return slackA != slackB ? slackA < slackB : a.Ticket < b.Ticket;
        }
        private long Slack(Entry entry, long now)
        {
            long relative = (long)entry.Request.Job.RelativeDeadlineMicroseconds - Costs.Estimate(entry.Request.CostKey);
            long age = now - entry.Arrival;
            return relative < 0 && age > long.MaxValue + relative ? long.MinValue : relative - age;
        }
        private static int Find(Entry[] data, int id)
        { for (int i = 0; i < data.Length; i++) if (data[i].State != GpuRuntimeJobState.Free && data[i].Request.Job.JobId == id) return i; return -1; }
        private void ValidateSlot(int slot) { if (slot < 0 || slot >= Capacity) throw new ArgumentOutOfRangeException(nameof(slot)); }
        private void CheckClock(long now) { if (now < clock) throw new ArgumentOutOfRangeException(nameof(now), "Clock must be monotonic."); clock = now; }
    }
}
