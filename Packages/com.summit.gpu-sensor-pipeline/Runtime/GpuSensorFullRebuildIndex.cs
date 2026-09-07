using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Independent compact Direct CSR reference/fallback for external snapshots.
    /// Recomputes every key and rebuilds every membership; shares only the input
    /// and consumer contracts with GpuSensorIncrementalIndex. Query the original
    /// inputSamples with these CSR outputs using Capacity as the ID bound.
    /// </summary>
    public sealed class GpuSensorFullRebuildIndex : IDisposable
    {
        private readonly ComputeShader shader;
        private readonly int kernel;
        private readonly GpuDirectSpatialBinner binner;
        private readonly GraphicsBuffer keys, ids, counts;
        private bool disposed;
        public int Capacity { get; }
        public GpuPrimitiveBackend Backend { get; }
        public GraphicsBuffer BinOffsets { get; }
        public GraphicsBuffer BinnedIds { get; }
        public GraphicsBuffer Diagnostics { get; }
        public long ResidentBytes => (long)Capacity * 12 +
            (2L * GpuSensorPipeline.FixedBinCount + 3) * 4 + binner.ScratchBytes;

        public GpuSensorFullRebuildIndex(int capacity, GpuPrimitiveBackend backend = GpuPrimitiveBackend.Portable)
        {
            if (capacity < 1 || capacity > GpuPrimitives.GpuPrimitives.MaxElementCount)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (backend != GpuPrimitiveBackend.Portable && backend != GpuPrimitiveBackend.WaveOps)
                throw new ArgumentOutOfRangeException(nameof(backend));
            if (backend == GpuPrimitiveBackend.WaveOps && !GpuPrimitives.GpuPrimitives.SupportsWaveOperations)
                throw new InvalidOperationException("WaveOps not supported.");
            Capacity = capacity; Backend = backend;
            shader = Resources.Load<ComputeShader>("GpuSensorPipeline/GpuSensorSnapshotKeys");
            if (shader == null) throw new InvalidOperationException("Snapshot key shader unavailable.");
            kernel = shader.FindKernel("PrepareSnapshotKeys");
            try
            {
                keys = Buffer(capacity); ids = Buffer(capacity); counts = Buffer(GpuSensorPipeline.FixedBinCount);
                BinOffsets = Buffer(GpuSensorPipeline.FixedBinCount + 1); BinnedIds = Buffer(capacity);
                Diagnostics = Buffer(2);
                binner = new GpuDirectSpatialBinner(capacity, GpuSensorPipeline.FixedBinCount, emitProfilerMarkers: false);
            }
            catch { Dispose(); throw; }
        }

        public void RecordUpdate(CommandBuffer commands, GraphicsBuffer inputSamples, GraphicsBuffer activeSlots)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorFullRebuildIndex));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            Validate(inputSamples, 16); Validate(activeSlots, 4);
            if (activeSlots == BinOffsets || activeSlots == BinnedIds || activeSlots == Diagnostics)
                throw new ArgumentException("Snapshot must not alias writable index buffers.");
            commands.BeginSample("Summit.SensorIndex/FullDirectReference");
            commands.SetComputeIntParam(shader, "_Capacity", Capacity);
            commands.SetComputeBufferParam(shader, kernel, "_Samples", inputSamples);
            commands.SetComputeBufferParam(shader, kernel, "_Active", activeSlots);
            commands.SetComputeBufferParam(shader, kernel, "_Keys", keys);
            commands.SetComputeBufferParam(shader, kernel, "_Ids", ids);
            commands.DispatchCompute(shader, kernel, (Capacity + 255) / 256, 1, 1);
            binner.Record(commands, keys, ids, counts, BinOffsets, BinnedIds,
                Diagnostics, Capacity, GpuSensorPipeline.FixedBinCount, Backend);
            commands.EndSample("Summit.SensorIndex/FullDirectReference");
        }
        private void Validate(GraphicsBuffer buffer, int stride)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 || buffer.stride != stride || buffer.count < Capacity)
                throw new ArgumentException("Snapshot has wrong shape/capacity.");
        }
        private static GraphicsBuffer Buffer(int count) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4);
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            keys?.Dispose(); ids?.Dispose(); counts?.Dispose(); BinOffsets?.Dispose();
            BinnedIds?.Dispose(); Diagnostics?.Dispose(); binner?.Dispose();
        }
    }
}
