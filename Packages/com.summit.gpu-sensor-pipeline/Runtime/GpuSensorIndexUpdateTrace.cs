using System;

namespace Summit.GpuSensorPipeline
{
    /// <summary>Reproducible fixtures only; the runtime index accepts arbitrary GPU snapshots.</summary>
    public static class GpuSensorIndexUpdateTrace
    {
        public static readonly int[] Rates = { 0, 1, 5, 20, 100 };

        public static void Initialize(GpuSensorSample[] samples, uint[] active, bool concentrated)
        {
            Validate(samples, active);
            for (int id = 0; id < samples.Length; id++)
            {
                uint x = concentrated ? 512u : (uint)(id % 32 * 1024 + 512);
                uint y = concentrated ? 512u : (uint)(id / 32 % 32 * 1024 + 512);
                uint z = concentrated ? 512u : (uint)(id / 1024 % 32 * 1024 + 512);
                samples[id] = new GpuSensorSample(x, y, z, (uint)(id * 17 + 3));
                active[id] = 1;
            }
        }

        /// <summary>
        /// changePercent selects dynamic slots; crossingPercent selects a fraction
        /// of those changed slots. Payload and subcell position always change.
        /// Crossings toggle neighboring x cells; teleports toggle the domain half.
        /// Static slots remain unchanged. Integer selection counts are floored.
        /// </summary>
        public static void Advance(GpuSensorSample[] samples, uint[] active,
            int staticSlots, int changePercent, int crossingPercent, int step, bool teleport = false)
        {
            Validate(samples, active);
            if (staticSlots < 0 || staticSlots > samples.Length || changePercent < 0 ||
                changePercent > 100 || crossingPercent < 0 || crossingPercent > 100 || step < 0)
                throw new ArgumentOutOfRangeException();
            int dynamicCount = samples.Length - staticSlots;
            int changed = (int)((long)dynamicCount * changePercent / 100);
            int crossing = (int)((long)changed * crossingPercent / 100);
            for (int i = 0; i < changed; i++)
            {
                int id = staticSlots + (int)(((long)step * 7 + i) % dynamicCount);
                GpuSensorSample sample = samples[id];
                sample.Payload ^= (uint)(step + 1) * 2654435761u;
                sample.X ^= 1u;
                if (i < crossing) sample.X ^= teleport ? 32768u : 1024u;
                samples[id] = sample;
            }
        }

        private static void Validate(GpuSensorSample[] samples, uint[] active)
        {
            if (samples == null || active == null || samples.Length != active.Length)
                throw new ArgumentException("Trace arrays must have equal lengths.");
        }
    }
}
