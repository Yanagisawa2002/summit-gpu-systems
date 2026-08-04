namespace Summit.GpuResidencyManager
{
    public static class GpuPointPageGenerator
    {
        public static GpuPointPageValue Generate(
            uint virtualPage,
            uint pointIndex,
            uint seed)
        {
            uint baseValue = Mix(
                seed ^
                unchecked(virtualPage * 0x9E3779B9u) ^
                unchecked(pointIndex * 0x85EBCA6Bu));
            return new GpuPointPageValue(
                Mix(baseValue ^ 0xA511E9B3u),
                Mix(baseValue ^ 0x63D83595u),
                Mix(baseValue ^ 0xC2B2AE35u),
                Mix(baseValue ^ 0x27D4EB2Du));
        }

        public static GpuPageDigest Digest(
            uint virtualPage,
            int pointsPerPage,
            uint seed)
        {
            uint x = 2166136261u ^ virtualPage;
            uint y = 0x9E3779B9u + virtualPage;
            uint z = 0x85EBCA6Bu ^ unchecked((uint)pointsPerPage);
            uint w = 0xC2B2AE35u + virtualPage;
            for (uint point = 0u;
                point < unchecked((uint)pointsPerPage);
                point++)
            {
                GpuPointPageValue value = Generate(
                    virtualPage,
                    point,
                    seed);
                x = unchecked((x ^ value.X) * 16777619u);
                y = unchecked((y + value.Y) * 2246822519u);
                z = unchecked((z ^ value.Z) * 3266489917u);
                w = unchecked((w + value.Attribute) * 668265263u);
            }
            return new GpuPageDigest(x, y, z, w);
        }

        public static uint Mix(uint value)
        {
            value ^= value >> 16;
            value = unchecked(value * 0x7FEB352Du);
            value ^= value >> 15;
            value = unchecked(value * 0x846CA68Bu);
            value ^= value >> 16;
            return value;
        }
    }
}
