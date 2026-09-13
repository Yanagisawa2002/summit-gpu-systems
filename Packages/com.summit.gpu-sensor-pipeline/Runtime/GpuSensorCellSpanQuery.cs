using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Unmeasured, explicit spatial candidate. Merges adjacent x cells into y/z
    /// spans, then maps fixed-size point chunks through a prefix directory.
    /// Owns fixed scratch reused by ordered queries on one queue; no CPU readback.
    /// </summary>
    public sealed class GpuSensorCellSpanQuery : IDisposable
    {
        public const int DispatchesPerQuery = 3;
        private readonly ComputeShader shader;
        private readonly GraphicsBuffer spans, blockEnds, arguments;
        private readonly int build, prepare, consume;
        private bool disposed;
        public int ElementCapacity { get; }
        public int IndexEntryCapacity { get; }
        public GpuSensorQueryBackend Backend { get; }
        public long ScratchBytes => GpuSensorCellSpanLayout.ScratchBytes;
        public string EvidenceStatus => "Unmeasured";

        public GpuSensorCellSpanQuery(int elementCapacity,
            GpuSensorQueryBackend backend = GpuSensorQueryBackend.CellSpans, int indexEntryCapacity = 0)
        {
            GpuSensorCellSpanLayout.ValidateCapacity(elementCapacity);
            IndexEntryCapacity = indexEntryCapacity == 0 ? elementCapacity : indexEntryCapacity;
            GpuSensorCellSpanLayout.ValidateCapacity(IndexEntryCapacity);
            if (backend != GpuSensorQueryBackend.CellSpans && backend != GpuSensorQueryBackend.CellSpansWave)
                throw new ArgumentOutOfRangeException(nameof(backend));
            if (backend == GpuSensorQueryBackend.CellSpansWave && !GpuSensorChunkedRangeQuery.SupportsWaveOperations)
                throw new NotSupportedException("CellSpansWave requires native wave operations; select CellSpans explicitly.");
            shader = Resources.Load<ComputeShader>("GpuSensorPipeline/GpuSensorCellSpanQuery" +
                (backend == GpuSensorQueryBackend.CellSpansWave ? "Wave" : ""));
            if (shader == null) throw new InvalidOperationException("Cell span query shader unavailable.");
            build = shader.FindKernel("BuildSpans");
            prepare = shader.FindKernel("PrepareSpanDispatch");
            consume = shader.FindKernel("ConsumeSpans");
            if (!shader.IsSupported(build) || !shader.IsSupported(prepare) || !shader.IsSupported(consume))
                throw new NotSupportedException("Cell span query kernels unsupported.");
            try
            {
                spans = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GpuSensorCellSpanLayout.MaxSpanCount, 16);
                blockEnds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GpuSensorCellSpanLayout.SpanBlockCount + 1, 4);
                arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 3, 4);
            }
            catch { Dispose(); throw; }
            ElementCapacity = elementCapacity;
            Backend = backend;
        }

        /// <summary>
        /// Trusted fixed-grid CSR: monotonic zero-based offsets ending within ids.count;
        /// each live stable ID appears exactly once in its sample's cell. IDs outside
        /// elementCount, including tombstones, are skipped. Query center/radius are uint16.
        /// Only [queryStart, queryStart+queryCount) is overwritten; zero queries is a no-op.
        /// An index may reserve more membership slots than there are sample slots.
        /// Inputs and scratch must remain alive/ordered until GPU completion.
        /// </summary>
        public void Record(CommandBuffer commands, GraphicsBuffer samples, GraphicsBuffer offsets,
            GraphicsBuffer ids, GraphicsBuffer queries, GraphicsBuffer digests, int elementCount,
            int queryStart, int queryCount, bool quantizeIntensity = false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorCellSpanQuery));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (elementCount < 0 || elementCount > ElementCapacity) throw new ArgumentOutOfRangeException(nameof(elementCount));
            if (queryStart < 0 || queryCount < 0 || queryCount > 65535 || (long)queryStart + queryCount > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(queryCount));
            Validate(samples, Math.Max(1, elementCount), 16, nameof(samples));
            Validate(offsets, GpuSensorPipeline.FixedBinCount + 1, 4, nameof(offsets));
            Validate(ids, 1, 4, nameof(ids));
            Validate(queries, queryStart + queryCount, 16, nameof(queries));
            Validate(digests, queryStart + queryCount, 16, nameof(digests));
            if (ids.count > IndexEntryCapacity) throw new ArgumentException("CSR exceeds configured capacity.", nameof(ids));
            // Different-stride buffers cannot pass Validate and alias each other.
            if (samples == queries || samples == digests || queries == digests || offsets == ids)
                throw new ArgumentException("Query buffers must not alias.");
            if (queryCount == 0) return;
            commands.SetComputeIntParam(shader, "_ElementCount", elementCount);
            commands.SetComputeIntParam(shader, "_QuantizeIntensity", quantizeIntensity ? 1 : 0);
            commands.SetComputeBufferParam(shader, build, "_BinOffsets", offsets);
            commands.SetComputeBufferParam(shader, build, "_Queries", queries);
            commands.SetComputeBufferParam(shader, build, "_QueryDigests", digests);
            commands.SetComputeBufferParam(shader, build, "_Spans", spans);
            commands.SetComputeBufferParam(shader, build, "_BlockEnds", blockEnds);
            commands.SetComputeBufferParam(shader, prepare, "_BlockEnds", blockEnds);
            commands.SetComputeBufferParam(shader, prepare, "_Arguments", arguments);
            commands.SetComputeBufferParam(shader, consume, "_Spans", spans);
            commands.SetComputeBufferParam(shader, consume, "_BlockEnds", blockEnds);
            commands.SetComputeBufferParam(shader, consume, "_Samples", samples);
            commands.SetComputeBufferParam(shader, consume, "_BinnedIds", ids);
            commands.SetComputeBufferParam(shader, consume, "_Queries", queries);
            commands.SetComputeBufferParam(shader, consume, "_QueryDigests", digests);
            for (int q = queryStart; q < queryStart + queryCount; q++)
            {
                commands.SetComputeIntParam(shader, "_QueryIndex", q);
                commands.DispatchCompute(shader, build, GpuSensorCellSpanLayout.SpanBlockCount, 1, 1);
                commands.DispatchCompute(shader, prepare, 1, 1, 1);
                commands.DispatchCompute(shader, consume, arguments, 0);
            }
        }

        private static void Validate(GraphicsBuffer buffer, int count, int stride, string name)
        {
            if (buffer == null) throw new ArgumentNullException(name);
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 || buffer.count < count || buffer.stride != stride)
                throw new ArgumentException("Invalid structured buffer shape.", name);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            spans?.Dispose(); blockEnds?.Dispose(); arguments?.Dispose();
        }
    }
}
