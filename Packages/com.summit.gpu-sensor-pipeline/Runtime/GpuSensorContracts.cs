using System;
using System.Runtime.InteropServices;

namespace Summit.GpuSensorPipeline
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    public struct GpuSensorSample : IEquatable<GpuSensorSample>
    {
        public uint X;
        public uint Y;
        public uint Z;
        public uint Payload;

        public GpuSensorSample(uint x, uint y, uint z, uint payload)
        {
            X = x;
            Y = y;
            Z = z;
            Payload = payload;
        }

        public bool Equals(GpuSensorSample other) =>
            X == other.X && Y == other.Y && Z == other.Z &&
            Payload == other.Payload;

        public override bool Equals(object obj) =>
            obj is GpuSensorSample other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)X;
                hash = (hash * 397) ^ (int)Y;
                hash = (hash * 397) ^ (int)Z;
                return (hash * 397) ^ (int)Payload;
            }
        }

        public static bool operator ==(
            GpuSensorSample left,
            GpuSensorSample right) => left.Equals(right);

        public static bool operator !=(
            GpuSensorSample left,
            GpuSensorSample right) => !left.Equals(right);
    }

    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    public struct GpuSensorRangeQuery : IEquatable<GpuSensorRangeQuery>
    {
        public uint CenterX;
        public uint CenterY;
        public uint CenterZ;
        public uint Radius;

        public GpuSensorRangeQuery(
            uint centerX,
            uint centerY,
            uint centerZ,
            uint radius)
        {
            CenterX = centerX;
            CenterY = centerY;
            CenterZ = centerZ;
            Radius = radius;
        }

        public bool Equals(GpuSensorRangeQuery other) =>
            CenterX == other.CenterX && CenterY == other.CenterY &&
            CenterZ == other.CenterZ && Radius == other.Radius;

        public override bool Equals(object obj) =>
            obj is GpuSensorRangeQuery other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)CenterX;
                hash = (hash * 397) ^ (int)CenterY;
                hash = (hash * 397) ^ (int)CenterZ;
                return (hash * 397) ^ (int)Radius;
            }
        }

        public static bool operator ==(
            GpuSensorRangeQuery left,
            GpuSensorRangeQuery right) => left.Equals(right);

        public static bool operator !=(
            GpuSensorRangeQuery left,
            GpuSensorRangeQuery right) => !left.Equals(right);
    }

    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    public struct GpuSensorQueryDigest : IEquatable<GpuSensorQueryDigest>
    {
        public uint Count;
        public uint XorHash;
        public uint SumHash0;
        public uint SumHash1;

        public GpuSensorQueryDigest(
            uint count,
            uint xorHash,
            uint sumHash0,
            uint sumHash1)
        {
            Count = count;
            XorHash = xorHash;
            SumHash0 = sumHash0;
            SumHash1 = sumHash1;
        }

        public bool Equals(GpuSensorQueryDigest other) =>
            Count == other.Count && XorHash == other.XorHash &&
            SumHash0 == other.SumHash0 && SumHash1 == other.SumHash1;

        public override bool Equals(object obj) =>
            obj is GpuSensorQueryDigest other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Count;
                hash = (hash * 397) ^ (int)XorHash;
                hash = (hash * 397) ^ (int)SumHash0;
                return (hash * 397) ^ (int)SumHash1;
            }
        }

        public static bool operator ==(
            GpuSensorQueryDigest left,
            GpuSensorQueryDigest right) => left.Equals(right);

        public static bool operator !=(
            GpuSensorQueryDigest left,
            GpuSensorQueryDigest right) => !left.Equals(right);
    }
}
