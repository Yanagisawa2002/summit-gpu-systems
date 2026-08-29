using System.Runtime.InteropServices;
using UnityEngine;

namespace Summit.GpuDrivenInstances
{
    /// <summary>
    /// Project-neutral source record consumed by the visibility pipeline.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuInstanceState
    {
        public const int Stride = 48;
        public const int MaximumLodCount = 4;

        public Vector4 PositionRadius;
        public Vector4 LodDistances;
        public uint ApplicationId;
        public uint DrawGroupBase;
        public uint LodCount;
        public uint ViewMask;

        public GpuInstanceState(
            Vector3 position,
            float radius,
            Vector4 lodDistances,
            uint applicationId,
            uint drawGroupBase,
            uint lodCount,
            uint viewMask)
        {
            PositionRadius = new Vector4(
                position.x,
                position.y,
                position.z,
                radius);
            LodDistances = lodDistances;
            ApplicationId = applicationId;
            DrawGroupBase = drawGroupBase;
            LodCount = lodCount;
            ViewMask = viewMask;
        }
    }
}
