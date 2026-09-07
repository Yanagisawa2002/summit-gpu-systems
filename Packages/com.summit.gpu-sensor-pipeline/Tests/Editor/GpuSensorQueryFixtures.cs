using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Summit.GpuPrimitives;

namespace Summit.GpuSensorPipeline.Tests
{
    internal static class GpuSensorQueryFixtures
    {
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

        public static GpuSensorPipeline Create(GpuSensorSample[] samples, GpuSensorQueryBackend backend)
        {
            var pipeline = new GpuSensorPipeline(samples.Length, Queries().Length,
                GpuPrimitiveBackend.Portable, false, queryBackend: backend);
            try
            {
                pipeline.SetStableIds(Enumerable.Range(0, samples.Length).Select(i => (uint)i).ToArray());
                pipeline.SetQueries(Queries());
                using (var commands = new CommandBuffer())
                {
                    pipeline.RecordCpuProduced(commands, samples,
                        samples.Select(GpuSensorDeterministicGenerator.ComputeKey).ToArray(),
                        samples.Length, Queries().Length, 0);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                Read(pipeline); // Complete setup before query-only validation or timing.
                return pipeline;
            }
            catch { pipeline.Dispose(); throw; }
        }

        public static GpuSensorQueryDigest[] Read(GpuSensorPipeline pipeline)
        {
            var digests = new GpuSensorQueryDigest[Queries().Length];
            pipeline.QueryDigests.GetData(digests);
            return digests;
        }

        private static uint Next(ref uint state)
        { unchecked { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; } }
    }
}
