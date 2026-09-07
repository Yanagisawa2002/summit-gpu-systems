using System;
using UnityEngine;
using UnityEngine.Rendering;
using Primitives = Summit.GpuPrimitives.GpuPrimitives;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Optional query-only consumer of a trusted fixed-grid CSR index. Scratch is
    /// reused per query on one ordered queue; synchronize before disposal or reuse
    /// from another queue. No CPU readback or index maintenance is recorded.
    /// </summary>
    public sealed class GpuSensorChunkedRangeQuery : IDisposable
    {
        public const int PointsPerChunk = 256;
        public const int SetupGroups = 16;
        public const int DispatchesPerQuery = 4;
        private readonly ComputeShader shader;
        private readonly int clear, build, prepare, consume;
        private readonly GraphicsBuffer chunks, count, arguments;
        private bool disposed;

        public GpuSensorChunkedRangeQuery(int elementCapacity,
            GpuSensorQueryBackend backend = GpuSensorQueryBackend.PointChunks,
            int indexEntryCapacity = 0)
        {
            CalculateChunkCapacity(elementCapacity);
            IndexEntryCapacity = indexEntryCapacity == 0 ? elementCapacity : indexEntryCapacity;
            ChunkCapacity = CalculateChunkCapacity(IndexEntryCapacity);
            if (backend != GpuSensorQueryBackend.PointChunks &&
                backend != GpuSensorQueryBackend.PointChunksWave)
                throw new ArgumentOutOfRangeException(nameof(backend));
            if (backend == GpuSensorQueryBackend.PointChunksWave && !SupportsWaveOperations)
                throw new InvalidOperationException("PointChunksWave is unsupported; select PointChunks or CellSerial explicitly.");
            string path = "GpuSensorPipeline/GpuSensorChunkedRangeQuery" +
                (backend == GpuSensorQueryBackend.PointChunksWave ? "Wave" : "");
            shader = Resources.Load<ComputeShader>(path);
            if (shader == null) throw new InvalidOperationException("Missing query shader: " + path);
            clear = shader.FindKernel("ClearQuery");
            build = shader.FindKernel("BuildChunks");
            prepare = shader.FindKernel("PrepareDispatch");
            consume = shader.FindKernel("ConsumeChunks");
            if (!shader.IsSupported(clear) || !shader.IsSupported(build) ||
                !shader.IsSupported(prepare) || !shader.IsSupported(consume))
                throw new InvalidOperationException("Query shader kernels are unsupported: " + path);
            try
            {
                chunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, ChunkCapacity, 8);
                count = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
                arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 3, 4);
            }
            catch { chunks?.Dispose(); count?.Dispose(); arguments?.Dispose(); throw; }
            ElementCapacity = elementCapacity;
            Backend = backend;
        }

        public static bool SupportsWaveOperations => Primitives.SupportsWaveOperations;
        public int ElementCapacity { get; }
        public int IndexEntryCapacity { get; }
        public int ChunkCapacity { get; }
        public GpuSensorQueryBackend Backend { get; }
        public long ScratchBytes => (long)ChunkCapacity * 8 + 16;

        // sum ceil(occupancy / K) <= floor(N / K) + min(N, number of cells).
        // This bound is independent of distribution, query radius and CSR order.
        public static int CalculateChunkCapacity(int elementCapacity)
        {
            if (elementCapacity < 1 || elementCapacity > Primitives.MaxElementCount)
                throw new ArgumentOutOfRangeException(nameof(elementCapacity));
            return checked(elementCapacity / PointsPerChunk +
                Math.Min(elementCapacity, GpuSensorDeterministicGenerator.BinCount));
        }

        /// <summary>
        /// Inclusive, clamped AABB semantics, matching CellSerial. Buffers must not
        /// alias; CSR offsets must start at zero, be monotonic with terminal offset
        /// no larger than IndexEntryCapacity (including reserved tombstone slots).
        /// Valid IDs address samples; IDs >= elementCount are skipped. Membership
        /// must match the fixed 16-bit grid.
        /// Zero queries is a no-op; zero elements writes empty digests. Other output
        /// slots remain untouched. Inputs must remain alive until GPU completion.
        /// </summary>
        public void Record(CommandBuffer commands, GraphicsBuffer samples,
            GraphicsBuffer offsets, GraphicsBuffer ids, GraphicsBuffer queries,
            GraphicsBuffer digests, int elementCount, int queryStart, int queryCount,
            bool quantizeIntensity = false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorChunkedRangeQuery));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (elementCount < 0 || elementCount > ElementCapacity)
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            if (queryStart < 0 || queryCount < 0 || queryCount > 65535 ||
                (long)queryStart + queryCount > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(queryCount));
            ValidateBuffer(samples, Math.Max(1, elementCount), 16, nameof(samples));
            ValidateBuffer(offsets, GpuSensorDeterministicGenerator.BinCount + 1, 4, nameof(offsets));
            ValidateBuffer(ids, 1, 4, nameof(ids));
            if (ids.count > IndexEntryCapacity)
                throw new ArgumentException("CSR entry buffer exceeds reserved scratch capacity.", nameof(ids));
            ValidateBuffer(queries, queryStart + queryCount, 16, nameof(queries));
            ValidateBuffer(digests, queryStart + queryCount, 16, nameof(digests));
            if (digests == samples || digests == queries || samples == queries || offsets == ids)
                throw new ArgumentException("Query buffers must not alias.");
            if (queryCount == 0) return;
            commands.SetComputeIntParam(shader, "_ElementCount", elementCount);
            commands.SetComputeIntParam(shader, "_QuantizeIntensity", quantizeIntensity ? 1 : 0);
            commands.SetComputeBufferParam(shader, clear, "_Count", count);
            commands.SetComputeBufferParam(shader, clear, "_QueryDigests", digests);
            commands.SetComputeBufferParam(shader, build, "_Count", count);
            commands.SetComputeBufferParam(shader, build, "_Chunks", chunks);
            commands.SetComputeBufferParam(shader, build, "_Queries", queries);
            commands.SetComputeBufferParam(shader, build, "_BinOffsets", offsets);
            commands.SetComputeBufferParam(shader, prepare, "_Count", count);
            commands.SetComputeBufferParam(shader, prepare, "_Arguments", arguments);
            commands.SetComputeBufferParam(shader, consume, "_Count", count);
            commands.SetComputeBufferParam(shader, consume, "_Chunks", chunks);
            commands.SetComputeBufferParam(shader, consume, "_Samples", samples);
            commands.SetComputeBufferParam(shader, consume, "_BinnedIds", ids);
            commands.SetComputeBufferParam(shader, consume, "_Queries", queries);
            commands.SetComputeBufferParam(shader, consume, "_QueryDigests", digests);
            for (int q = queryStart; q < queryStart + queryCount; q++)
            {
                commands.SetComputeIntParam(shader, "_QueryIndex", q);
                commands.DispatchCompute(shader, clear, 1, 1, 1);
                commands.DispatchCompute(shader, build, SetupGroups, 1, 1);
                commands.DispatchCompute(shader, prepare, 1, 1, 1);
                commands.DispatchCompute(shader, consume, arguments, 0);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            chunks.Dispose(); count.Dispose(); arguments.Dispose();
        }

        private static void ValidateBuffer(GraphicsBuffer buffer, int size, int stride, string name)
        {
            if (buffer == null) throw new ArgumentNullException(name);
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != stride || buffer.count < size)
                throw new ArgumentException("Invalid structured buffer shape.", name);
        }
    }
}
