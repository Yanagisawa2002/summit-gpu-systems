using System.Runtime.InteropServices;

namespace Summit.GpuDrivenInstances
{
    /// <summary>
    /// Four immutable words copied into each indexed-indirect argument record.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuDrawTemplate
    {
        public const int Stride = 16;

        public uint IndexCountPerInstance;
        public uint StartIndex;
        public uint BaseVertex;
        public uint Reserved;

        public GpuDrawTemplate(
            uint indexCountPerInstance,
            uint startIndex,
            uint baseVertex,
            uint reserved = 0u)
        {
            IndexCountPerInstance = indexCountPerInstance;
            StartIndex = startIndex;
            BaseVertex = baseVertex;
            Reserved = reserved;
        }
    }
}
