using System;

namespace Summit.GpuSensorPipeline
{
    /// <summary>Frozen public benchmark fixtures shared by the microbenchmark and integration scene.</summary>
    public static class GpuSensorBenchmarkFixtures
    {
        public const string QueryFixtureId = "r9700-query-v1-seed51ed270b";
        public const string IndexFixtureId = "r9700-index-update-v1";
        public static GpuSensorSample[] Samples(string distribution, int count)
        {
            var samples = new GpuSensorSample[count];
            uint state = 0x51ed270b;
            for (int i = 0; i < count; i++)
            {
                uint x = Next(ref state) & 65535u;
                uint y = Next(ref state) & 65535u;
                uint z = Next(ref state) & 65535u;
                if (distribution == "sparse")
                {
                    uint key = (uint)i % 262144u;
                    x = (key & 63u) * 1024u + 17u;
                    y = ((key >> 6) & 63u) * 1024u + 19u;
                    z = ((key >> 12) & 63u) * 1024u + 23u;
                }
                else if (distribution == "hotspot" && i % 100 != 0)
                { x = 32768u + (x & 255u); y = 32768u + (y & 255u); z = 32768u + (z & 255u); }
                else if (distribution == "single-cell")
                { x = 32768u; y = 32768u; z = 32768u; }
                else if (distribution != "uniform" && distribution != "hotspot")
                    throw new ArgumentException("Unknown distribution.", nameof(distribution));
                samples[i] = new GpuSensorSample(x, y, z, Next(ref state));
            }
            // Explicit fixed-grid boundary points, retained in every distribution.
            uint[] edges = { 0, 1023, 1024, 65535 };
            for (int i = 0; i < Math.Min(count, edges.Length); i++)
                samples[i] = new GpuSensorSample(edges[i], edges[i], edges[i], uint.MaxValue - (uint)i);
            return samples;
        }

        public static GpuSensorRangeQuery[] Queries() => new[]
        {
            new GpuSensorRangeQuery(0, 0, 0, 65535),
            new GpuSensorRangeQuery(32768, 32768, 32768, 1023),
            new GpuSensorRangeQuery(32768, 32768, 32768, 0),
            new GpuSensorRangeQuery(1024, 1024, 1024, 1),
            new GpuSensorRangeQuery(65535, 65535, 65535, 0),
            new GpuSensorRangeQuery(0, 0, 0, 0),
            new GpuSensorRangeQuery(17, 18, 22, 0), // Guaranteed empty in sparse/hotspot fixtures.
            new GpuSensorRangeQuery(32768, 32768, 32768, 65535),
            new GpuSensorRangeQuery(32768, 32768, 32768, 255)
        };

        private static uint Next(ref uint state)
        { unchecked { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; } }
    }
}
