using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>Index-free reference: one point block per query and wave reductions.
    /// Active slots, not CSR membership, are authoritative. No index is required.</summary>
    public sealed class GpuSensorParallelScanQuery : IDisposable
    {
        readonly ComputeShader shader;
        readonly int clear, scan;
        bool disposed;
        public int Capacity { get; }
        public GpuSensorParallelScanQuery(int capacity)
        {
            if (capacity < 1 || capacity > 65535 * 256) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!GpuSensorChunkedRangeQuery.SupportsWaveOperations) throw new NotSupportedException("Native wave operations required.");
            Capacity = capacity;
            shader = Resources.Load<ComputeShader>("GpuSensorPipeline/GpuSensorParallelScanQuery");
            if (shader == null) throw new InvalidOperationException("Parallel scan shader missing.");
            clear = shader.FindKernel("Clear"); scan = shader.FindKernel("Scan");
            if (!shader.IsSupported(clear) || !shader.IsSupported(scan)) throw new NotSupportedException("Parallel scan kernels unsupported.");
        }
        public void Record(CommandBuffer commands, GraphicsBuffer samples, GraphicsBuffer active,
            GraphicsBuffer queries, GraphicsBuffer results, int count, int queryCount)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorParallelScanQuery));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (count < 0 || count > Capacity) throw new ArgumentOutOfRangeException(nameof(count));
            if (queryCount < 1 || queryCount > 65535) throw new ArgumentOutOfRangeException(nameof(queryCount));
            Validate(samples, Math.Max(1, count), 16); Validate(active, Math.Max(1, count), 4);
            Validate(queries, queryCount, 16); Validate(results, queryCount, 16);
            if (samples == results || active == results || queries == results || samples == queries)
                throw new ArgumentException("Scan inputs and outputs must not alias.");
            commands.SetComputeIntParam(shader, "_Count", count);
            commands.SetComputeIntParam(shader, "_QueryCount", queryCount);
            commands.SetComputeBufferParam(shader, clear, "_Results", results);
            commands.SetComputeBufferParam(shader, scan, "_Samples", samples);
            commands.SetComputeBufferParam(shader, scan, "_Active", active);
            commands.SetComputeBufferParam(shader, scan, "_Queries", queries);
            commands.SetComputeBufferParam(shader, scan, "_Results", results);
            commands.DispatchCompute(shader, clear, (queryCount + 255) / 256, 1, 1);
            if (count != 0) commands.DispatchCompute(shader, scan, (count + 255) / 256, queryCount, 1);
        }
        static void Validate(GraphicsBuffer b, int count, int stride)
        {
            if (b == null || (b.target & GraphicsBuffer.Target.Structured) == 0 || b.count < count || b.stride != stride)
                throw new ArgumentException("Invalid scan buffer shape.");
        }
        public void Dispose() { disposed = true; }
    }
}
