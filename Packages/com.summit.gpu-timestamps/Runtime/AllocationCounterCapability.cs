using System;

namespace Summit.GpuTimestamps
{
    public interface IManagedAllocationCounter
    {
        string Scope { get; }
        int ThreadId { get; }
        bool TryRead(out long bytes);
    }

    public readonly struct AllocationCounterCapability
    {
        public const string CurrentThreadScope = "managed-current-thread";
        public readonly bool Available;
        public readonly string Reason;
        public readonly int ThreadId;
        public readonly long PositiveControlDelta;
        public AllocationCounterCapability(bool available, string reason, int threadId, long delta)
        { Available = available; Reason = reason; ThreadId = threadId; PositiveControlDelta = delta; }

        // Caller supplies the control; tests use a virtual counter. No collection happens
        // merely by loading this class, constructing a collector, or reading a profile.
        public static AllocationCounterCapability Probe(IManagedAllocationCounter counter,
            Action positiveControl, long expectedMinimumBytes)
        {
            if (counter == null || positiveControl == null) throw new ArgumentNullException();
            if (expectedMinimumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(expectedMinimumBytes));
            int thread = counter.ThreadId;
            if (counter.Scope != CurrentThreadScope || !counter.TryRead(out long before) || before < 0)
                return new AllocationCounterCapability(false, "unsupported-counter-or-scope", thread, -1);
            positiveControl();
            if (thread != counter.ThreadId || !counter.TryRead(out long after) || after < before)
                return new AllocationCounterCapability(false, "counter-reset-or-thread-change", thread, -1);
            long delta = after - before;
            return new AllocationCounterCapability(delta >= expectedMinimumBytes,
                delta >= expectedMinimumBytes ? "positive-control-passed" : "positive-control-not-detected", thread, delta);
        }

        public long Delta(IManagedAllocationCounter counter, long before)
        {
            if (!Available || counter == null || counter.Scope != CurrentThreadScope || counter.ThreadId != ThreadId ||
                before < 0 || !counter.TryRead(out long after) || after < before) return -1;
            return after - before;
        }
    }
}
