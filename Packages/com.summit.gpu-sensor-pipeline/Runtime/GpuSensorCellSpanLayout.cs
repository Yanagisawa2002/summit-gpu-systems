using System;

namespace Summit.GpuSensorPipeline
{
    /// <summary>Pure integer layout contract for the optional CellSpans consumer.</summary>
    public static class GpuSensorCellSpanLayout
    {
        public const int MaxSpanCount = 64 * 64;
        public const int SpanBlockSize = 256;
        public const int SpanBlockCount = MaxSpanCount / SpanBlockSize;
        public const int PointsPerChunk = 256;
        public const int MaxEntryCapacity = 65535 * 256;
        public const long ScratchBytes = MaxSpanCount * 16L + (SpanBlockCount + 1) * 4L + 12;

        public static int MaximumChunkCount(int entryCapacity)
        {
            ValidateCapacity(entryCapacity);
            // Sum ceil(length / 256) over disjoint nonempty spans, never over cells.
            return entryCapacity / PointsPerChunk + Math.Min(entryCapacity, MaxSpanCount);
        }

        public static void ValidateCapacity(int capacity)
        {
            if (capacity < 1 || capacity > MaxEntryCapacity)
                throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        public static int GetSpanCount(GpuSensorRangeQuery query)
        {
            Bounds(query, out _, out _, out uint y0, out uint y1, out uint z0, out uint z1);
            return checked((int)((y1 - y0 + 1) * (z1 - z0 + 1)));
        }

        /// <summary>CSR interval [offsets[firstCell], offsets[endCell]) for one y/z row.</summary>
        public static void GetSpanCells(GpuSensorRangeQuery query, int span,
            out int firstCell, out int endCell)
        {
            Bounds(query, out uint x0, out uint x1, out uint y0, out uint y1, out uint z0, out uint z1);
            uint rows = y1 - y0 + 1;
            if (span < 0 || (uint)span >= rows * (z1 - z0 + 1))
                throw new ArgumentOutOfRangeException(nameof(span));
            uint yz = ((y0 + (uint)span % rows) << 6) | ((z0 + (uint)span / rows) << 12);
            firstCell = (int)(yz | x0);
            endCell = (int)(yz | x1) + 1;
        }

        private static void Bounds(GpuSensorRangeQuery q, out uint x0, out uint x1,
            out uint y0, out uint y1, out uint z0, out uint z1)
        {
            if (q.CenterX > 65535 || q.CenterY > 65535 || q.CenterZ > 65535 || q.Radius > 65535)
                throw new ArgumentOutOfRangeException(nameof(q), "Query coordinates and radius must fit uint16.");
            x0 = (q.CenterX - Math.Min(q.CenterX, q.Radius)) >> 10;
            y0 = (q.CenterY - Math.Min(q.CenterY, q.Radius)) >> 10;
            z0 = (q.CenterZ - Math.Min(q.CenterZ, q.Radius)) >> 10;
            x1 = Math.Min(q.CenterX + q.Radius, 65535u) >> 10;
            y1 = Math.Min(q.CenterY + q.Radius, 65535u) >> 10;
            z1 = Math.Min(q.CenterZ + q.Radius, 65535u) >> 10;
        }
    }
}
