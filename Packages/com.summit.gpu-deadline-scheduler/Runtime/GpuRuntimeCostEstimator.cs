using System;

namespace Summit.GpuDeadlineScheduler
{
    /// <summary>Fixed storage for application-defined workload keys and delayed GPU duration samples.
    /// Single-threaded. A key must describe size, kernel, queue and relevant runtime environment.</summary>
    public sealed class GpuRuntimeCostEstimator
    {
        private struct Cell
        {
            public int Revision, Minimum, Maximum, Estimate, Samples;
            public long LastSequence;
        }
        private struct Pending
        {
            public long Token, Issued;
            public int Key, Revision;
        }
        private readonly Cell[] cells;
        private readonly Pending[] pending;
        private readonly long maximumSampleAge;
        private long sequence;

        public GpuRuntimeCostEstimator(int keyCapacity, int sampleCapacity, long maximumSampleAgeMicroseconds)
        {
            if (keyCapacity < 1 || sampleCapacity < 1 || maximumSampleAgeMicroseconds < 1)
                throw new ArgumentOutOfRangeException();
            cells = new Cell[keyCapacity];
            pending = new Pending[sampleCapacity];
            maximumSampleAge = maximumSampleAgeMicroseconds;
        }

        public void Configure(int key, int revision, int coldStartMicroseconds, int minimumMicroseconds, int maximumMicroseconds)
        {
            ValidateKey(key);
            if (revision < 1 || minimumMicroseconds < 1 || maximumMicroseconds < minimumMicroseconds ||
                coldStartMicroseconds < minimumMicroseconds || coldStartMicroseconds > maximumMicroseconds)
                throw new ArgumentOutOfRangeException();
            // Reconfiguration always invalidates outstanding samples, even when the revision is reused.
            for (int i = 0; i < pending.Length; i++)
                if (pending[i].Key == key) pending[i] = default;
            cells[key] = new Cell { Revision = revision, Minimum = minimumMicroseconds,
                Maximum = maximumMicroseconds, Estimate = coldStartMicroseconds };
        }

        public int Estimate(int key) { ValidateConfigured(key); return cells[key].Estimate; }
        public int SampleCount(int key) { ValidateConfigured(key); return cells[key].Samples; }

        public long BeginSample(int key, long nowMicroseconds)
        {
            ValidateConfigured(key);
            if (nowMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(nowMicroseconds));
            long token = checked(++sequence);
            pending[(int)(token % pending.Length)] = new Pending { Token = token,
                Issued = nowMicroseconds, Key = key, Revision = cells[key].Revision };
            return token;
        }

        /// <summary>Duration must be a valid per-dispatch GPU timestamp interval, never CPU fence latency.
        /// Returns false for expired, overwritten, duplicate, out-of-order or invalid samples.</summary>
        public bool TryUpdate(long token, long nowMicroseconds, double gpuDurationMicroseconds, bool valid)
        {
            if (token <= 0) return false;
            int slot = (int)(token % pending.Length);
            Pending sample = pending[slot];
            if (sample.Token != token) return false;
            pending[slot] = default;
            ref Cell cell = ref cells[sample.Key];
            if (!valid || nowMicroseconds < sample.Issued || nowMicroseconds - sample.Issued > maximumSampleAge ||
                sample.Revision != cell.Revision || token <= cell.LastSequence ||
                double.IsNaN(gpuDurationMicroseconds) || double.IsInfinity(gpuDurationMicroseconds) || gpuDurationMicroseconds <= 0)
                return false;
            // Winsorize both cold-start and steady-state samples. Repeated legitimate changes still converge.
            double bounded = Math.Max(cell.Minimum, Math.Min(cell.Maximum, gpuDurationMicroseconds));
            bounded = Math.Max(cell.Estimate / 4.0, Math.Min(cell.Estimate * 4.0, bounded));
            cell.Estimate = (int)Math.Max(cell.Minimum, Math.Min(cell.Maximum,
                Math.Ceiling(cell.Estimate * 0.75 + bounded * 0.25)));
            cell.Samples = cell.Samples == int.MaxValue ? int.MaxValue : cell.Samples + 1;
            cell.LastSequence = token;
            return true;
        }

        private void ValidateKey(int key)
        {
            if (key < 0 || key >= cells.Length) throw new ArgumentOutOfRangeException(nameof(key));
        }
        private void ValidateConfigured(int key)
        {
            ValidateKey(key);
            if (cells[key].Revision == 0) throw new InvalidOperationException("Configure the workload key first.");
        }
    }
}
