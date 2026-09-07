using System;
using Summit.GpuSensorPipeline;

namespace Summit.PublicIntegration
{
    public static class IntegrationFixture
    {
        public const int Capacity = 262144;
        public const int ContentSlots = 16384;
        public const int QueryCount = 9;
        public static readonly string[] Scenarios = { "sparse-low-change", "hotspot-dynamic", "streaming-switch" };
        public static GpuSensorRangeQuery[] Queries() => GpuSensorBenchmarkFixtures.Queries();
        public static GpuSensorSample[] Create(string scene, uint seed)
        {
            if (Array.IndexOf(Scenarios, scene)<0) throw new ArgumentException("Unknown frozen scene");
            var data=GpuSensorBenchmarkFixtures.Samples(scene==Scenarios[0]?"sparse":scene==Scenarios[1]?"hotspot":"uniform",Capacity);
            for(int i=0;i<data.Length;i++) data[i].Payload ^= seed;
            return data;
        }
        public static int Advance(string scene,GpuSensorSample[] data,uint[] active,int frame)
        {
            if(frame==0 || scene==Scenarios[0]) return 0;
            int rate=scene==Scenarios[1]?1:5;
            GpuSensorIndexUpdateTrace.Advance(data,active,0,rate,scene==Scenarios[1]?1:20,frame);
            return Capacity*rate/100;
        }
        public static GpuSensorSample ContentSample(int bundle,int id)
        {
            return new GpuSensorSample((uint)((id*73+bundle*19000)&65535),
                (uint)((id*151+bundle*7000)&65535),(uint)((id*227+bundle*13000)&65535),
                unchecked((uint)(id+1)*2654435761u^(uint)bundle));
        }
        public static GpuSensorQueryDigest[] Oracle(GpuSensorSample[] data,uint[] active)
        {
            return GpuSensorBenchmarkOracle.QueryAll(data,Capacity,Queries(),active);
        }
    }
}
