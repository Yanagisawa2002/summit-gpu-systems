using System;
using System.Collections.Generic;

namespace Summit.ExternalWorkloads
{
    // Deterministic ownership state used by the real async consumer. Cancellation
    // invalidates the result, but does not make in-flight resources safe to release.
    public sealed class ConsumerEpoch
    {
        readonly Dictionary<ulong, uint> pending = new Dictionary<ulong, uint>();
        ulong sequence;
        uint generation = 1;
        public int PendingCount => pending.Count;
        public uint Generation => generation;
        public ulong Submit()
        {
            if (sequence == ulong.MaxValue) throw new InvalidOperationException("Consumer sequence exhausted.");
            ulong id = ++sequence; pending.Add(id, generation); return id;
        }
        public void Invalidate()
        { if (generation == uint.MaxValue) throw new InvalidOperationException("Consumer generation exhausted."); generation++; }
        public bool Complete(ulong id)
        {
            if (!pending.TryGetValue(id, out uint submitted)) throw new ArgumentException("Unknown or duplicate completion.");
            pending.Remove(id); return submitted == generation;
        }
        public void RequireDrained()
        { if (pending.Count != 0) throw new InvalidOperationException("Consumer resources still belong to submitted work."); }
    }
}
