using System.Runtime.InteropServices;
using UnityEngine;

namespace Summit.GpuDrivenInstances
{
    /// <summary>
    /// Conservative bounds and immutable membership for one contiguous cluster.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuInstanceCluster
    {
        public const int Stride = 32;
        public const int MaximumInstanceCount = 64;

        public Vector4 PositionRadius;
        public uint FirstInstance;
        public uint InstanceCount;
        public uint UnionViewMask;
        public uint Reserved;

        public GpuInstanceCluster(
            Vector4 positionRadius,
            uint firstInstance,
            uint instanceCount,
            uint unionViewMask)
        {
            PositionRadius = positionRadius;
            FirstInstance = firstInstance;
            InstanceCount = instanceCount;
            UnionViewMask = unionViewMask;
            Reserved = 0u;
        }
    }
}
