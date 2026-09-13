using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Explicit Unmeasured compact CSR consumer view. Counts are maintained by the
    /// incremental index; four additional dispatches scan them and scatter current
    /// stable-slot keys. Does not traverse reserved holes or alter the source index.
    /// </summary>
    public sealed class GpuSensorCompactIndexView : IDisposable
    {
        public const int DispatchCount = 4;
        private const int Bins = 262144;
        private readonly ComputeShader shader;
        private readonly int scanCells, scanBlocks, prepare, scatter;
        private readonly GraphicsBuffer blockOffsets, heads;
        private bool disposed;
        public int Capacity { get; }
        public GraphicsBuffer BinOffsets { get; }
        public GraphicsBuffer BinnedIds { get; }
        public long ResidentBytes => CalculateResidentBytes(Capacity);
        public string EvidenceStatus => "Unmeasured";

        public static long CalculateResidentBytes(int capacity)
        {
            GpuSensorCellSpanLayout.ValidateCapacity(capacity);
            return 4L * (capacity + 2L * Bins + 1 + Bins / 256);
        }

        public GpuSensorCompactIndexView(int capacity)
        {
            CalculateResidentBytes(capacity);
            Capacity = capacity;
            shader = Resources.Load<ComputeShader>("GpuSensorPipeline/GpuSensorCompactIndexView");
            if (shader == null) throw new InvalidOperationException("Compact index shader unavailable.");
            scanCells = shader.FindKernel("ScanLiveCells");
            scanBlocks = shader.FindKernel("ScanLiveBlocks");
            prepare = shader.FindKernel("PrepareCompactCells");
            scatter = shader.FindKernel("ScatterLiveSlots");
            if (!shader.IsSupported(scanCells) || !shader.IsSupported(scanBlocks) ||
                !shader.IsSupported(prepare) || !shader.IsSupported(scatter))
                throw new NotSupportedException("Compact index kernels unsupported.");
            try
            {
                BinOffsets = Buffer(Bins + 1); BinnedIds = Buffer(capacity);
                blockOffsets = Buffer(Bins / 256); heads = Buffer(Bins);
            }
            catch { Dispose(); throw; }
        }

        /// <summary>
        /// Record AFTER the latest index update and BEFORE queries, on one ordered
        /// queue. Query index.Samples using this view's offsets/IDs. A never-updated
        /// index produces an empty view. Re-record after membership changes; there
        /// is no hidden dirty tracking, readback, allocation or automatic promotion.
        /// Two simultaneous queues/views require caller fences or separate owners.
        /// </summary>
        public void Record(CommandBuffer commands, GpuSensorIncrementalIndex index)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorCompactIndexView));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (index == null) throw new ArgumentNullException(nameof(index));
            index.ValidateCompactView(Capacity);
            commands.SetComputeIntParam(shader, "_Capacity", Capacity);
            commands.SetComputeBufferParam(shader, scanCells, "_LiveCounts", index.LiveCounts);
            commands.SetComputeBufferParam(shader, scanCells, "_IndexState", index.Diagnostics);
            commands.SetComputeBufferParam(shader, scanCells, "_Offsets", BinOffsets);
            commands.SetComputeBufferParam(shader, scanCells, "_BlockOffsets", blockOffsets);
            commands.SetComputeBufferParam(shader, scanBlocks, "_BlockOffsets", blockOffsets);
            commands.SetComputeBufferParam(shader, scanBlocks, "_Offsets", BinOffsets);
            commands.SetComputeBufferParam(shader, prepare, "_BlockOffsets", blockOffsets);
            commands.SetComputeBufferParam(shader, prepare, "_Offsets", BinOffsets);
            commands.SetComputeBufferParam(shader, prepare, "_Heads", heads);
            commands.SetComputeBufferParam(shader, scatter, "_CurrentKeys", index.CurrentKeys);
            commands.SetComputeBufferParam(shader, scatter, "_IndexState", index.Diagnostics);
            commands.SetComputeBufferParam(shader, scatter, "_Heads", heads);
            commands.SetComputeBufferParam(shader, scatter, "_Members", BinnedIds);
            commands.DispatchCompute(shader, scanCells, Bins / 256, 1, 1);
            commands.DispatchCompute(shader, scanBlocks, 1, 1, 1);
            commands.DispatchCompute(shader, prepare, Bins / 256, 1, 1);
            commands.DispatchCompute(shader, scatter, (Capacity + 255) / 256, 1, 1);
        }

        private static GraphicsBuffer Buffer(int count) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            BinOffsets?.Dispose(); BinnedIds?.Dispose(); blockOffsets?.Dispose(); heads?.Dispose();
        }
    }
}
