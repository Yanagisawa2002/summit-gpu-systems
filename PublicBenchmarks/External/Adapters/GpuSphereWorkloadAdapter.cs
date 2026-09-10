using System;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using UnityEngine;
using UnityEngine.Rendering;
using Primitives = Summit.GpuPrimitives.GpuPrimitives;

namespace Summit.ExternalWorkloads
{
    /// <summary>Owns an opt-in source->SUMMIT CSR->conservative candidates->exact
    /// sphere filter->full CSR consumer. Record never executes/submits GPU work.
    /// Call on Unity's main thread. Before replacing buffers or disposing, drain
    /// submitted work/readbacks and discard unsubmitted commands. Submit recorded
    /// buffers once, in order; do not replay them across snapshot generations.
    /// Upload/Record retain the original rebuild-per-call convenience behavior.</summary>
    public sealed class GpuSphereWorkloadAdapter : IDisposable
    {
        public GraphicsBuffer Offsets { get; private set; }
        public GraphicsBuffer Ids { get; private set; }
        public int PointCount { get; private set; }
        public int QueryCount { get; private set; }
        public int PointCapacity { get; }
        public int QueryCapacity { get; }
        public ulong PointGeneration { get; private set; }
        public ulong QueryGeneration { get; private set; }
        public bool IsIndexReady { get { Check(); return pointsUploaded && confirmedBuild != 0 && buildPointGeneration == PointGeneration; } }
        readonly ComputeShader shader;
        readonly int countKernel, finishKernel, scatterKernel;
        GpuSensorFullRebuildIndex index;
        Primitives primitives;
        GraphicsBuffer points, samples, active, spheres, cells, counts, buildStamp;
        readonly uint[] stampReadback = new uint[2];
        SphereWorkloadDomain domain;
        ulong buildSerial, recordedBuild, confirmedBuild, buildPointGeneration;
        GraphicsFence reusableFence;
        bool hasReusableFence, disposed, pointsUploaded, queriesUploaded;

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
                buildStamp = Buffer(2, 4); buildStamp.SetData(stampReadback);
                index = new GpuSensorFullRebuildIndex(pointCapacity, GpuPrimitiveBackend.Portable);
                primitives = new Primitives(queryCapacity, emitProfilerMarkers: false);
            }
            catch { Dispose(); throw; }
        }
        public void Upload(SphereWorkloadDomain domain, SourcePoint[] sourcePoints, SourceSphere[] sourceSpheres)
        {
            Check(); RequireReusableIdle(); InvalidatePoints();
            if (sourcePoints == null || sourceSpheres == null) throw new ArgumentNullException();
            if (sourcePoints.Length > PointCapacity || sourceSpheres.Length > QueryCapacity) throw new ArgumentOutOfRangeException("Snapshot exceeds capacity; no truncation is permitted.");
            var encoded = SphereWorkloadContract.Encode(domain, sourcePoints);
            var bounds = new CandidateCells[sourceSpheres.Length];
            for (int q = 0; q < bounds.Length; q++) bounds[q] = domain.Bounds(sourceSpheres[q]);
            UploadPointBuffers(domain, sourcePoints, encoded);
            UploadQueryBuffers(sourceSpheres, bounds);
        }

        /// <summary>Validate, encode and upload a new point snapshot once. Any
        /// domain/point replacement (including a rejected replacement) invalidates
        /// the prepared index and queries. Query-only updates use UploadQueries.</summary>
        public void UploadPoints(SphereWorkloadDomain domain, SourcePoint[] sourcePoints)
        {
            Check(); RequireReusableIdle(); InvalidatePoints();
            if (sourcePoints == null) throw new ArgumentNullException(nameof(sourcePoints));
            if (sourcePoints.Length > PointCapacity) throw new ArgumentOutOfRangeException(nameof(sourcePoints));
            var encoded = SphereWorkloadContract.Encode(domain, sourcePoints);
            UploadPointBuffers(domain, sourcePoints, encoded);
        }

        void UploadPointBuffers(SphereWorkloadDomain domain, SourcePoint[] sourcePoints, GpuSensorSample[] encoded)
        {
            var membership = new uint[PointCapacity];
            for (int i = 0; i < sourcePoints.Length; i++) membership[i] = 1;
            // Clear the full active mask on every replacement to remove unloaded entities.
            active.SetData(membership);
            if (sourcePoints.Length != 0) { points.SetData(sourcePoints); samples.SetData(encoded); }
            this.domain = domain; PointCount = sourcePoints.Length; pointsUploaded = true;
        }

        /// <summary>Replace one complete query batch without re-encoding points
        /// or rebuilding the index. A rejected batch invalidates queries only.
        /// The previous batch and all readbacks must have completed first.</summary>
        public void UploadQueries(SourceSphere[] sourceSpheres)
        {
            Check(); RequireReusableIdle();
            if (!pointsUploaded) throw new InvalidOperationException("Upload points and their domain first.");
            InvalidateQueries();
            if (sourceSpheres == null) throw new ArgumentNullException(nameof(sourceSpheres));
            if (sourceSpheres.Length > QueryCapacity) throw new ArgumentOutOfRangeException(nameof(sourceSpheres));
            var bounds = new CandidateCells[sourceSpheres.Length];
            for (int q = 0; q < bounds.Length; q++) bounds[q] = domain.Bounds(sourceSpheres[q]);
            UploadQueryBuffers(sourceSpheres, bounds);
        }

        void UploadQueryBuffers(SourceSphere[] sourceSpheres, CandidateCells[] bounds)
        {
            if (bounds.Length != 0) { spheres.SetData(sourceSpheres); cells.SetData(bounds); }
            QueryCount = sourceSpheres.Length; queriesUploaded = true;
        }

        /// <summary>Record one real full rebuild and a generation stamp after it.
        /// Recording alone never makes the index ready. Submit, then call
        /// CompleteIndexBuild before recording reusable queries.</summary>
        public GraphicsFence RecordIndexBuild(CommandBuffer commands)
        {
            Check(); ValidateCommands(commands); RequireReusableIdle(); RequireFences();
            if (!pointsUploaded) throw new InvalidOperationException("Upload points first.");
            confirmedBuild = recordedBuild = 0;
            ulong serial = checked(++buildSerial);
            index.RecordUpdate(commands, samples, active);
            commands.SetBufferData(buildStamp, new uint[] { (uint)serial, (uint)(serial >> 32) });
            reusableFence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            hasReusableFence = true; recordedBuild = serial; buildPointGeneration = PointGeneration;
            return reusableFence;
        }

        /// <summary>Synchronous eight-byte completion readback. Confirms that the
        /// current build command actually executed; a cleared/unsubmitted build
        /// cannot certify an old index. This does not submit commands. Async
        /// callers can first await the returned graphics fence to avoid blocking.</summary>
        public void CompleteIndexBuild()
        {
            Check();
            if (!pointsUploaded || recordedBuild == 0 || buildPointGeneration != PointGeneration)
                throw new InvalidOperationException("Record and submit the current point generation's index build first.");
            buildStamp.GetData(stampReadback);
            ulong completed = stampReadback[0] | ((ulong)stampReadback[1] << 32);
            if (completed != recordedBuild) throw new InvalidOperationException("Current index build has not executed; recorded or discarded commands are not a prepared index.");
            confirmedBuild = recordedBuild;
        }

        /// <summary>Record only the full-CSR query/count/scan/scatter path against
        /// a completed current index. Returns the fence for this batch; retain the
        /// owner through all output consumers/readbacks, then replace the batch.</summary>
        public GraphicsFence RecordQueries(CommandBuffer commands)
        {
            Check(); ValidateCommands(commands); RequireReusableIdle(); RequireFences();
            if (!IsIndexReady) throw new InvalidOperationException("Complete the current point/domain index build first.");
            if (!queriesUploaded) throw new InvalidOperationException("Upload a validated query batch first.");
            RecordQueryCore(commands);
            reusableFence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            hasReusableFence = true;
            return reusableFence;
        }
        public void Record(CommandBuffer commands)
        {
            Check(); ValidateCommands(commands); RequireReusableIdle();
            if (!pointsUploaded || !queriesUploaded) throw new InvalidOperationException("Upload a validated snapshot first.");
            // Legacy behavior: rebuild on every nonempty query call. It does not
            // certify an index for the separate reusable-query API.
            confirmedBuild = recordedBuild = 0;
            if (QueryCount != 0) index.RecordUpdate(commands, samples, active);
            RecordQueryCore(commands);
        }
        void RecordQueryCore(CommandBuffer commands)
        {
            commands.SetComputeIntParam(shader, "_PointCount", PointCount);
            commands.SetComputeIntParam(shader, "_QueryCount", QueryCount);
            if (QueryCount != 0)
            {
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
        void InvalidatePoints()
        {
            PointGeneration = checked(PointGeneration + 1);
            pointsUploaded = false; domain = null; PointCount = 0;
            confirmedBuild = recordedBuild = 0; InvalidateQueries();
        }
        void InvalidateQueries() { QueryGeneration = checked(QueryGeneration + 1); queriesUploaded = false; QueryCount = 0; }
        static void ValidateCommands(CommandBuffer commands) { if (commands == null) throw new ArgumentNullException(nameof(commands)); }
        static void RequireFences() { if (!SystemInfo.supportsGraphicsFence) throw new NotSupportedException("Prepared sphere batches require graphics fences; the legacy path remains available."); }
        void RequireReusableIdle()
        {
            if (hasReusableFence && !reusableFence.passed) throw new InvalidOperationException("Previous reusable work has not completed; submit/drain it or discard its unsubmitted command buffer.");
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
            spheres?.Dispose(); cells?.Dispose(); counts?.Dispose(); Offsets?.Dispose(); Ids?.Dispose(); buildStamp?.Dispose();
        }
    }
}
