using System;
using System.Collections.Generic;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using UnityEngine;
using UnityEngine.Rendering;
using Primitives = Summit.GpuPrimitives.GpuPrimitives;

namespace Summit.ExternalWorkloads
{
    /// <summary>Owns an opt-in source->SUMMIT CSR->conservative candidates->exact
    /// sphere filter->full CSR consumer. Record never executes/submits GPU work.
    /// Upload/Record/Dispose require the caller's previous completion fence.</summary>
    public sealed class GpuSphereWorkloadAdapter : IDisposable
    {
        public GraphicsBuffer Offsets { get; private set; }
        public GraphicsBuffer Ids { get; private set; }
        public int PointCount { get; private set; }
        public int QueryCount { get; private set; }
        public int PointCapacity { get; }
        public int QueryCapacity { get; }
        readonly ComputeShader shader;
        readonly int countKernel, finishKernel, scatterKernel;
        GpuSensorFullRebuildIndex index;
        Primitives primitives;
        GraphicsBuffer points, samples, active, spheres, cells, counts;
        bool disposed, uploaded;

        public GpuSphereWorkloadAdapter(int pointCapacity, int queryCapacity, bool allowUnmeasured)
        {
            if (!allowUnmeasured) throw new InvalidOperationException("External sphere adapter is opt-in and Unmeasured.");
            if (pointCapacity < 1 || pointCapacity > Primitives.MaxElementCount || queryCapacity < 1 || queryCapacity > 65535 ||
                (long)pointCapacity * queryCapacity > int.MaxValue / sizeof(uint)) throw new ArgumentOutOfRangeException("Bounded full-result capacities required.");
            PointCapacity = pointCapacity; QueryCapacity = queryCapacity;
            shader = Resources.Load<ComputeShader>("SummitExternalSphere");
            if (shader == null) throw new InvalidOperationException("External sphere shader not installed.");
            countKernel = shader.FindKernel("CountMatches"); finishKernel = shader.FindKernel("FinishOffsets"); scatterKernel = shader.FindKernel("ScatterMatches");
            if (!shader.IsSupported(countKernel) || !shader.IsSupported(finishKernel) || !shader.IsSupported(scatterKernel))
                throw new NotSupportedException("Sphere adapter kernels unsupported.");
            try
            {
                points = Buffer(pointCapacity, 16); samples = Buffer(pointCapacity, 16); active = Buffer(pointCapacity, 4);
                spheres = Buffer(queryCapacity, 16); cells = Buffer(queryCapacity, 32); counts = Buffer(queryCapacity, 4);
                Offsets = Buffer(queryCapacity + 1, 4); Ids = Buffer(checked(pointCapacity * queryCapacity), 4);
                index = new GpuSensorFullRebuildIndex(pointCapacity, GpuPrimitiveBackend.Portable);
                primitives = new Primitives(queryCapacity, emitProfilerMarkers: false);
            }
            catch { Dispose(); throw; }
        }
        public void Upload(SphereWorkloadDomain domain, SourcePoint[] sourcePoints, SourceSphere[] sourceSpheres)
        {
            Check(); uploaded = false;
            if (sourcePoints == null || sourceSpheres == null) throw new ArgumentNullException();
            if (sourcePoints.Length > PointCapacity || sourceSpheres.Length > QueryCapacity) throw new ArgumentOutOfRangeException("Snapshot exceeds capacity; no truncation is permitted.");
            var encoded = SphereWorkloadContract.Encode(domain, sourcePoints);
            var bounds = new CandidateCells[sourceSpheres.Length];
            for (int q = 0; q < bounds.Length; q++) bounds[q] = domain.Bounds(sourceSpheres[q]);
            var membership = new uint[PointCapacity];
            for (int i = 0; i < sourcePoints.Length; i++) membership[i] = 1;
            // Clear the full active mask on every replacement to remove unloaded entities.
            active.SetData(membership);
            if (sourcePoints.Length != 0) { points.SetData(sourcePoints); samples.SetData(encoded); }
            if (bounds.Length != 0) { spheres.SetData(sourceSpheres); cells.SetData(bounds); }
            PointCount = sourcePoints.Length; QueryCount = sourceSpheres.Length; uploaded = true;
        }
        public void Record(CommandBuffer commands)
        {
            Check(); if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (!uploaded) throw new InvalidOperationException("Upload a validated snapshot first.");
            commands.SetComputeIntParam(shader, "_PointCount", PointCount);
            commands.SetComputeIntParam(shader, "_QueryCount", QueryCount);
            if (QueryCount != 0)
            {
                index.RecordUpdate(commands, samples, active);
                Bind(commands, countKernel); Bind(commands, scatterKernel);
                commands.SetComputeBufferParam(shader, countKernel, "_Counts", counts);
                commands.DispatchCompute(shader, countKernel, (QueryCount + 63) / 64, 1, 1);
                primitives.RecordExclusiveScan(commands, counts, Offsets, QueryCount, GpuPrimitiveBackend.Portable);
            }
            commands.SetComputeBufferParam(shader, finishKernel, "_Counts", counts);
            commands.SetComputeBufferParam(shader, finishKernel, "_Offsets", Offsets);
            commands.DispatchCompute(shader, finishKernel, 1, 1, 1);
            if (QueryCount != 0)
            {
                commands.SetComputeBufferParam(shader, scatterKernel, "_Offsets", Offsets);
                commands.SetComputeBufferParam(shader, scatterKernel, "_Ids", Ids);
                commands.DispatchCompute(shader, scatterKernel, (QueryCount + 63) / 64, 1, 1);
            }
        }
        void Bind(CommandBuffer c, int kernel)
        {
            c.SetComputeBufferParam(shader, kernel, "_Points", points); c.SetComputeBufferParam(shader, kernel, "_Spheres", spheres);
            c.SetComputeBufferParam(shader, kernel, "_Cells", cells); c.SetComputeBufferParam(shader, kernel, "_BinOffsets", index.BinOffsets);
            c.SetComputeBufferParam(shader, kernel, "_BinnedIds", index.BinnedIds);
        }
        static GraphicsBuffer Buffer(int count, int stride) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        void Check() { if (disposed) throw new ObjectDisposedException(nameof(GpuSphereWorkloadAdapter)); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            index?.Dispose(); primitives?.Dispose(); points?.Dispose(); samples?.Dispose(); active?.Dispose();
            spheres?.Dispose(); cells?.Dispose(); counts?.Dispose(); Offsets?.Dispose(); Ids?.Dispose();
        }
    }
}
