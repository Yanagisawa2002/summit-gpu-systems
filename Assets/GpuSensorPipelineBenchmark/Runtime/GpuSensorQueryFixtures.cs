using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using SensorPipeline = Summit.GpuSensorPipeline.GpuSensorPipeline;

namespace Summit.GpuSensorQueryBenchmark
{
    internal static class GpuSensorQueryFixtures
    {
        public static GpuSensorSample[] Samples(string distribution, int count) =>
            GpuSensorBenchmarkFixtures.Samples(distribution, count);

        public static GpuSensorRangeQuery[] Queries() => GpuSensorBenchmarkFixtures.Queries();

        public static SensorPipeline Create(GpuSensorSample[] samples, GpuSensorQueryBackend backend)
        {
            var pipeline = new SensorPipeline(samples.Length, Queries().Length,
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

        public static GpuSensorQueryDigest[] Read(SensorPipeline pipeline)
        {
            var digests = new GpuSensorQueryDigest[Queries().Length];
            pipeline.QueryDigests.GetData(digests);
            return digests;
        }

    }
}
