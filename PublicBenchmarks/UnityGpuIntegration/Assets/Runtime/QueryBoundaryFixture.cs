using System;
using Summit.GpuSensorPipeline;
namespace Summit.PublicIntegration
{
    public static class QueryBoundaryFixture
    {
        public const int N = 262144, Jobs = 128, Moving = 4096;
        public static readonly string[] Cases = { "sparse-small", "uniform-small", "uniform-wide", "hotspot-small", "hotspot-wide", "uniform-one" };
        public static readonly string[] Arms = { "cell", "chunks", "scan", "batch" };
        public static GpuSensorSample[] Samples(string distribution, uint seed, int count = N)
        {
            var s = GpuSensorBenchmarkFixtures.Samples(distribution, count);
            for (int i = 0; i < count; i++) s[i].Payload ^= seed;
            return s;
        }
        public static GpuSensorRangeQuery[] Queries(string name)
        {
            int count = name.EndsWith("one", StringComparison.Ordinal) ? 1 : 9;
            uint radius = name.EndsWith("small", StringComparison.Ordinal) ? 255u : 8192u;
            var q = new GpuSensorRangeQuery[count];
            q[0] = new GpuSensorRangeQuery(32768,32768,32768,radius);
            for (int i = 1; i < count; i++)
                q[i] = new GpuSensorRangeQuery((uint)(i*7301%65536),(uint)(i*13111%65536),(uint)(i*19001%65536),radius);
            return q;
        }
        public static GpuSensorRangeQuery[] Zones()
        {
            var q = new GpuSensorRangeQuery[9];
            uint[] center = {10000,32768,55000};
            for(int i=0;i<9;i++)q[i]=new GpuSensorRangeQuery(center[i%3],center[i/3],32768,2048);
            return q;
        }
        // A moving obstacle cohort visits one of nine regions per sensor update.
        // All algorithms see identical snapshots; thresholds are frozen from the
        // empty-cohort background, not chosen from timings or the winning arm.
        public static void MoveCohort(GpuSensorSample[] s, int job)
        {
            var zones=Zones();
            for(int i=0;i<Moving;i++)
            {
                if(job<0) { s[i].X=100;s[i].Y=100;s[i].Z=100; }
                else {
                    var z=zones[job%9];
                    s[i].X=z.CenterX+(uint)(i%128);s[i].Y=z.CenterY+(uint)(i/128);s[i].Z=z.CenterZ;
                }
            }
        }
        public static string Distribution(string name) => name.Split('-')[0];
    }
}
