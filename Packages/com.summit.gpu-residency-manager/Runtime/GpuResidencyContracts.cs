using System.Runtime.InteropServices;

namespace Summit.GpuResidencyManager
{
    public enum GpuResidencyPolicy
    {
        RebuildVisibleSet = 0,
        PersistentLru = 1, // Original full-scan baseline; retained as the unmeasured default comparison.
        PersistentHeapLru = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct GpuPageUpload
    {
        public GpuPageUpload(uint virtualPage, uint physicalSlot)
        {
            VirtualPage = virtualPage;
            PhysicalSlot = physicalSlot;
        }

        public uint VirtualPage { get; }

        public uint PhysicalSlot { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct GpuPageTableDelta
    {
        public GpuPageTableDelta(uint virtualPage, uint physicalSlot)
        {
            VirtualPage = virtualPage;
            PhysicalSlot = physicalSlot;
        }

        public uint VirtualPage { get; }

        public uint PhysicalSlot { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct GpuPointPageValue
    {
        public GpuPointPageValue(uint x, uint y, uint z, uint attribute)
        {
            X = x;
            Y = y;
            Z = z;
            Attribute = attribute;
        }

        public uint X { get; }
        public uint Y { get; }
        public uint Z { get; }
        public uint Attribute { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct GpuPageDigest
    {
        public GpuPageDigest(uint x, uint y, uint z, uint w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public uint X { get; }
        public uint Y { get; }
        public uint Z { get; }
        public uint W { get; }
    }
}
