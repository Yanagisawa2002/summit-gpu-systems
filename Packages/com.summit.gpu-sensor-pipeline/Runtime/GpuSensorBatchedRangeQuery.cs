using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Explicit Wave candidate that scans trusted CSR membership once for a
    /// complete query segment. Two dispatches, no scratch or CPU synchronization.
    /// Snapshot/index/outputs must stay alive and ordered on the same queue.
    /// </summary>
    public sealed class GpuSensorBatchedRangeQuery : IDisposable
    {
        public const int DispatchCount = 2;
        private readonly ComputeShader shader;
        private readonly int clear, consume;
        private bool disposed;
        public int ElementCapacity { get; }
        public int IndexEntryCapacity { get; }
        public long ScratchBytes => 0;

        public GpuSensorBatchedRangeQuery(int elementCapacity, int indexEntryCapacity = 0)
        {
            GpuSensorChunkedRangeQuery.CalculateChunkCapacity(elementCapacity);
            ElementCapacity = elementCapacity;
            IndexEntryCapacity = indexEntryCapacity == 0 ? elementCapacity : indexEntryCapacity;
            GpuSensorChunkedRangeQuery.CalculateChunkCapacity(IndexEntryCapacity);
            if (IndexEntryCapacity < elementCapacity) throw new ArgumentOutOfRangeException(nameof(indexEntryCapacity));
            if (!GpuSensorChunkedRangeQuery.SupportsWaveOperations)
                throw new InvalidOperationException("BatchedPointScanWave requires native wave operations.");
            shader = Resources.Load<ComputeShader>("GpuSensorPipeline/GpuSensorBatchedRangeQuery");
            if (shader == null) throw new InvalidOperationException("Batched query shader unavailable.");
            clear = shader.FindKernel("ClearBatch"); consume = shader.FindKernel("ConsumeBatch");
            if (!shader.IsSupported(clear) || !shader.IsSupported(consume))
                throw new InvalidOperationException("Batched query shader is unsupported.");
        }

        public void Record(CommandBuffer commands, GraphicsBuffer samples, GraphicsBuffer offsets,
            GraphicsBuffer ids, GraphicsBuffer queries, GraphicsBuffer digests, int elementCount,
            int queryStart, int queryCount, bool quantizeIntensity = false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorBatchedRangeQuery));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (elementCount < 0 || elementCount > ElementCapacity) throw new ArgumentOutOfRangeException(nameof(elementCount));
            if (queryStart < 0 || queryCount < 0 || queryCount > 65535 || (long)queryStart + queryCount > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(queryCount));
            Validate(samples, Math.Max(1, elementCount), 16); Validate(offsets, 262145, 4);
            Validate(ids, 1, 4); Validate(queries, queryStart + queryCount, 16); Validate(digests, queryStart + queryCount, 16);
            if (ids.count > IndexEntryCapacity) throw new ArgumentException("CSR exceeds configured entry capacity.");
            if (samples == queries || samples == digests || queries == digests || offsets == ids)
                throw new ArgumentException("Query buffers must not alias.");
            if (queryCount == 0) return;
            commands.SetComputeIntParam(shader, "_ElementCount", elementCount);
            commands.SetComputeIntParam(shader, "_EntryCapacity", ids.count);
            commands.SetComputeIntParam(shader, "_QueryStart", queryStart);
            commands.SetComputeIntParam(shader, "_QueryCount", queryCount);
            commands.SetComputeIntParam(shader, "_QuantizeIntensity", quantizeIntensity ? 1 : 0);
            commands.SetComputeBufferParam(shader, clear, "_QueryDigests", digests);
            commands.SetComputeBufferParam(shader, consume, "_Samples", samples);
            commands.SetComputeBufferParam(shader, consume, "_BinOffsets", offsets);
            commands.SetComputeBufferParam(shader, consume, "_BinnedIds", ids);
            commands.SetComputeBufferParam(shader, consume, "_Queries", queries);
            commands.SetComputeBufferParam(shader, consume, "_QueryDigests", digests);
            commands.DispatchCompute(shader, clear, (queryCount + 255) / 256, 1, 1);
            int groups = (ids.count + 255) / 256;
            commands.DispatchCompute(shader, consume, Math.Min(groups, 65535), (groups + 65534) / 65535, 1);
        }

        private static void Validate(GraphicsBuffer b, int count, int stride)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if ((b.target & GraphicsBuffer.Target.Structured) == 0 || b.stride != stride || b.count < count)
                throw new ArgumentException("Invalid structured buffer shape.");
        }
        public void Dispose() { disposed = true; }
    }
}
