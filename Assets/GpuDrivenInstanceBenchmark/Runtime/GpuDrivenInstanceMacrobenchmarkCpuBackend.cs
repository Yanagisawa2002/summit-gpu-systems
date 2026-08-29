using System;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Burst-parallel, allocation-free CPU visibility and stable grouping baseline
/// used by the GPU-driven macrobenchmark.
/// </summary>
internal sealed class GpuDrivenInstanceMacrobenchmarkCpuBackend : IDisposable
{
    internal const int MaxInstancesPerDraw = 1023;

    private const int PairsPerChunk = 1024;

    private readonly int instanceCount;
    private readonly int viewCount;
    private readonly int drawGroupCount;
    private readonly int visibleBinCount;
    private readonly int pairCount;
    private readonly int chunkCount;
    private readonly NativeArray<GpuInstanceState> instances;
    private readonly NativeArray<Vector4> viewPlanes;
    private readonly NativeArray<Vector4> viewParameters;
    private readonly NativeArray<Matrix4x4> objectToWorld;
    private readonly NativeArray<int> pairKeys;
    private readonly NativeArray<int> chunkBinCountsAndCursors;
    private readonly NativeArray<int> maximumCounts;
    private readonly NativeArray<int> counts;
    private readonly NativeArray<int> offsets;
    private readonly NativeArray<uint> groupedIndices;
    private readonly NativeArray<Matrix4x4> groupedMatrices;
    private readonly long persistentNativeArrayPayloadBytes;
    private bool disposed;

    internal GpuDrivenInstanceMacrobenchmarkCpuBackend(
        GpuInstanceState[] instances,
        Vector4[] viewPlanes,
        Vector4[] viewParameters,
        int drawGroupCount,
        uint[] maximumCounts)
    {
        if (instances == null)
        {
            throw new ArgumentNullException(nameof(instances));
        }
        if (viewPlanes == null)
        {
            throw new ArgumentNullException(nameof(viewPlanes));
        }
        if (viewParameters == null)
        {
            throw new ArgumentNullException(nameof(viewParameters));
        }
        if (viewParameters.Length < 1 ||
            viewParameters.Length > GpuDrivenInstancePipeline.MaximumViewCount)
        {
            throw new ArgumentOutOfRangeException(nameof(viewParameters));
        }
        if (viewPlanes.Length !=
            viewParameters.Length *
            GpuDrivenInstancePipeline.FrustumPlaneCount)
        {
            throw new ArgumentException(
                "Exactly six planes are required per view.",
                nameof(viewPlanes));
        }
        if (drawGroupCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(drawGroupCount));
        }

        int selectedInstanceCount = instances.Length;
        int selectedViewCount = viewParameters.Length;
        int selectedVisibleBinCount = checked(
            selectedViewCount * drawGroupCount);
        int selectedPairCount = checked(
            selectedInstanceCount * selectedViewCount);
        int selectedChunkCount = DivideRoundUp(
            selectedPairCount,
            PairsPerChunk);
        if (maximumCounts == null ||
            maximumCounts.Length != selectedVisibleBinCount)
        {
            throw new ArgumentException(
                "One maximum count is required per visible bin.",
                nameof(maximumCounts));
        }

        int totalCapacity = 0;
        for (int bin = 0; bin < maximumCounts.Length; bin++)
        {
            if (maximumCounts[bin] > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumCounts));
            }
            totalCapacity = checked(
                totalCapacity + (int)maximumCounts[bin]);
        }

        NativeArray<GpuInstanceState> selectedInstances = default;
        NativeArray<Vector4> selectedViewPlanes = default;
        NativeArray<Vector4> selectedViewParameters = default;
        NativeArray<Matrix4x4> selectedObjectToWorld = default;
        NativeArray<int> selectedPairKeys = default;
        NativeArray<int> selectedChunkBinCountsAndCursors = default;
        NativeArray<int> selectedMaximumCounts = default;
        NativeArray<int> selectedCounts = default;
        NativeArray<int> selectedOffsets = default;
        NativeArray<uint> selectedGroupedIndices = default;
        NativeArray<Matrix4x4> selectedGroupedMatrices = default;
        try
        {
            selectedInstances = new NativeArray<GpuInstanceState>(
                instances,
                Allocator.Persistent);
            selectedViewPlanes = new NativeArray<Vector4>(
                viewPlanes,
                Allocator.Persistent);
            selectedViewParameters = new NativeArray<Vector4>(
                viewParameters,
                Allocator.Persistent);
            selectedObjectToWorld = new NativeArray<Matrix4x4>(
                selectedInstanceCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            for (int index = 0; index < selectedInstanceCount; index++)
            {
                GpuInstanceState instance = instances[index];
                Vector3 position = new Vector3(
                    instance.PositionRadius.x,
                    instance.PositionRadius.y,
                    instance.PositionRadius.z);
                float diameter = instance.PositionRadius.w * 2.0f;
                selectedObjectToWorld[index] = Matrix4x4.TRS(
                    position,
                    Quaternion.identity,
                    new Vector3(diameter, diameter, diameter));
            }

            selectedPairKeys = new NativeArray<int>(
                selectedPairCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            selectedChunkBinCountsAndCursors = new NativeArray<int>(
                checked(selectedChunkCount * selectedVisibleBinCount),
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            selectedMaximumCounts = new NativeArray<int>(
                selectedVisibleBinCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            for (int bin = 0; bin < selectedVisibleBinCount; bin++)
            {
                selectedMaximumCounts[bin] = checked((int)maximumCounts[bin]);
            }
            selectedCounts = new NativeArray<int>(
                selectedVisibleBinCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            selectedOffsets = new NativeArray<int>(
                checked(selectedVisibleBinCount + 1),
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            selectedGroupedIndices = new NativeArray<uint>(
                totalCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            selectedGroupedMatrices = new NativeArray<Matrix4x4>(
                totalCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }
        catch
        {
            DisposeIfCreated(ref selectedGroupedMatrices);
            DisposeIfCreated(ref selectedGroupedIndices);
            DisposeIfCreated(ref selectedOffsets);
            DisposeIfCreated(ref selectedCounts);
            DisposeIfCreated(ref selectedMaximumCounts);
            DisposeIfCreated(ref selectedChunkBinCountsAndCursors);
            DisposeIfCreated(ref selectedPairKeys);
            DisposeIfCreated(ref selectedObjectToWorld);
            DisposeIfCreated(ref selectedViewParameters);
            DisposeIfCreated(ref selectedViewPlanes);
            DisposeIfCreated(ref selectedInstances);
            throw;
        }

        instanceCount = selectedInstanceCount;
        viewCount = selectedViewCount;
        this.drawGroupCount = drawGroupCount;
        visibleBinCount = selectedVisibleBinCount;
        pairCount = selectedPairCount;
        chunkCount = selectedChunkCount;
        this.instances = selectedInstances;
        this.viewPlanes = selectedViewPlanes;
        this.viewParameters = selectedViewParameters;
        objectToWorld = selectedObjectToWorld;
        pairKeys = selectedPairKeys;
        chunkBinCountsAndCursors = selectedChunkBinCountsAndCursors;
        this.maximumCounts = selectedMaximumCounts;
        counts = selectedCounts;
        offsets = selectedOffsets;
        groupedIndices = selectedGroupedIndices;
        groupedMatrices = selectedGroupedMatrices;
        persistentNativeArrayPayloadBytes = checked(
            NativeArrayPayloadBytes(this.instances) +
            NativeArrayPayloadBytes(this.viewPlanes) +
            NativeArrayPayloadBytes(this.viewParameters) +
            NativeArrayPayloadBytes(objectToWorld) +
            NativeArrayPayloadBytes(pairKeys) +
            NativeArrayPayloadBytes(chunkBinCountsAndCursors) +
            NativeArrayPayloadBytes(this.maximumCounts) +
            NativeArrayPayloadBytes(counts) +
            NativeArrayPayloadBytes(offsets) +
            NativeArrayPayloadBytes(groupedIndices) +
            NativeArrayPayloadBytes(groupedMatrices));
    }

    internal int InstanceCount => instanceCount;

    internal int ViewCount => viewCount;

    internal int DrawGroupCount => drawGroupCount;

    internal int VisibleBinCount => visibleBinCount;

    internal int PairCount => pairCount;

    /// <summary>
    /// Logical payload bytes across every persistent NativeArray owned by this
    /// backend. Allocator metadata, alignment, and native container headers are
    /// intentionally excluded.
    /// </summary>
    internal long PersistentNativeArrayPayloadBytes =>
        persistentNativeArrayPayloadBytes;

    internal int GroupedCapacity
    {
        get
        {
            ThrowIfDisposed();
            return groupedIndices.Length;
        }
    }

    internal int VisiblePairCount { get; private set; }

    internal int DrawCallCount { get; private set; }

    internal NativeArray<int> Counts
    {
        get
        {
            ThrowIfDisposed();
            return counts;
        }
    }

    /// <summary>
    /// Actual compacted bin offsets, including one terminal offset.
    /// </summary>
    internal NativeArray<int> BinOffsets
    {
        get
        {
            ThrowIfDisposed();
            return offsets;
        }
    }

    internal NativeArray<uint> GroupedIndices
    {
        get
        {
            ThrowIfDisposed();
            return groupedIndices;
        }
    }

    /// <summary>
    /// Matrices in the same compacted order as <see cref="GroupedIndices"/>.
    /// The array can be passed directly to Graphics.RenderMeshInstanced with
    /// the start/count returned by the batch helpers below.
    /// </summary>
    internal NativeArray<Matrix4x4> GroupedMatrices
    {
        get
        {
            ThrowIfDisposed();
            return groupedMatrices;
        }
    }

    /// <summary>
    /// Executes parallel classification and deterministic count/prefix/scatter
    /// without managed allocation. Contiguous chunks own disjoint histogram
    /// and scatter ranges, so order matches the serial oracle exactly.
    /// </summary>
    internal void CullAndPack()
    {
        ThrowIfDisposed();
        VisiblePairCount = 0;
        DrawCallCount = 0;

        JobHandle classify = new ClassifyAndCountChunksJob
        {
            InstanceCount = instanceCount,
            PairCount = pairCount,
            DrawGroupCount = drawGroupCount,
            VisibleBinCount = visibleBinCount,
            Instances = instances,
            ViewPlanes = viewPlanes,
            ViewParameters = viewParameters,
            PairKeys = pairKeys,
            ChunkBinCounts = chunkBinCountsAndCursors,
        }.Schedule(chunkCount, 1);

        JobHandle prefix = new PrefixCountsJob
        {
            ChunkCount = chunkCount,
            VisibleBinCount = visibleBinCount,
            ChunkBinCountsAndCursors = chunkBinCountsAndCursors,
            Counts = counts,
            Offsets = offsets,
        }.Schedule(classify);
        prefix.Complete();

        for (int bin = 0; bin < visibleBinCount; bin++)
        {
            if (counts[bin] > maximumCounts[bin])
            {
                throw new InvalidOperationException(
                    "CPU visibility exceeded the frozen capacity of bin " +
                    bin + ".");
            }
        }
        int visiblePairs = offsets[visibleBinCount];
        if (visiblePairs > groupedIndices.Length)
        {
            throw new InvalidOperationException(
                "CPU visibility exceeded the frozen grouped capacity.");
        }

        JobHandle scatter = new StableScatterChunksJob
        {
            InstanceCount = instanceCount,
            PairCount = pairCount,
            VisibleBinCount = visibleBinCount,
            PairKeys = pairKeys,
            ChunkBinCursors = chunkBinCountsAndCursors,
            ObjectToWorld = objectToWorld,
            GroupedIndices = groupedIndices,
            GroupedMatrices = groupedMatrices,
        }.Schedule(chunkCount, 1);
        scatter.Complete();

        int draws = 0;
        for (int bin = 0; bin < visibleBinCount; bin++)
        {
            draws = checked(
                draws + DivideRoundUp(counts[bin], MaxInstancesPerDraw));
        }
        VisiblePairCount = visiblePairs;
        DrawCallCount = draws;
    }

    internal int GetBatchCount(int bin)
    {
        ValidateBin(bin);
        return DivideRoundUp(counts[bin], MaxInstancesPerDraw);
    }

    internal int GetBatchStartInstance(int bin, int batch)
    {
        ValidateBatch(bin, batch);
        return checked(
            offsets[bin] + batch * MaxInstancesPerDraw);
    }

    internal int GetBatchInstanceCount(int bin, int batch)
    {
        ValidateBatch(bin, batch);
        int consumed = checked(batch * MaxInstancesPerDraw);
        return Math.Min(
            MaxInstancesPerDraw,
            counts[bin] - consumed);
    }

    internal bool Validate(
        GpuDrivenInstanceExpectedResult expected,
        out string message)
    {
        ThrowIfDisposed();
        if (expected == null)
        {
            throw new ArgumentNullException(nameof(expected));
        }
        if (expected.Counts.Length != visibleBinCount ||
            expected.Offsets.Length != visibleBinCount + 1)
        {
            message = "Expected count/offset shape does not match visible bins.";
            return false;
        }
        if (expected.GroupedInstanceIndices.Length != VisiblePairCount)
        {
            message = "Expected grouped stream length does not match.";
            return false;
        }

        for (int bin = 0; bin < visibleBinCount; bin++)
        {
            if (counts[bin] != checked((int)expected.Counts[bin]))
            {
                message = "CPU count mismatch in bin " + bin + ".";
                return false;
            }
            if (offsets[bin] != checked((int)expected.Offsets[bin]))
            {
                message = "CPU offset mismatch in bin " + bin + ".";
                return false;
            }
            int expectedOffset = checked((int)expected.Offsets[bin]);
            for (int slot = 0; slot < counts[bin]; slot++)
            {
                uint actual = groupedIndices[offsets[bin] + slot];
                uint wanted =
                    expected.GroupedInstanceIndices[expectedOffset + slot];
                if (actual != wanted)
                {
                    message =
                        "CPU membership mismatch in bin " + bin +
                        " at slot " + slot + ".";
                    return false;
                }
            }
        }
        if (offsets[visibleBinCount] !=
            checked((int)expected.Offsets[visibleBinCount]))
        {
            message = "CPU terminal offset mismatch.";
            return false;
        }
        message = "CPU counts, offsets, and stable order match the oracle.";
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        groupedMatrices.Dispose();
        groupedIndices.Dispose();
        offsets.Dispose();
        counts.Dispose();
        maximumCounts.Dispose();
        chunkBinCountsAndCursors.Dispose();
        pairKeys.Dispose();
        objectToWorld.Dispose();
        viewParameters.Dispose();
        viewPlanes.Dispose();
        instances.Dispose();
    }

    private void ValidateBin(int bin)
    {
        ThrowIfDisposed();
        if ((uint)bin >= (uint)visibleBinCount)
        {
            throw new ArgumentOutOfRangeException(nameof(bin));
        }
    }

    private void ValidateBatch(int bin, int batch)
    {
        ValidateBin(bin);
        int batchCount = DivideRoundUp(
            counts[bin],
            MaxInstancesPerDraw);
        if ((uint)batch >= (uint)batchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(batch));
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDrivenInstanceMacrobenchmarkCpuBackend));
        }
    }

    private static void DisposeIfCreated<T>(ref NativeArray<T> array)
        where T : struct
    {
        if (array.IsCreated)
        {
            array.Dispose();
        }
        array = default;
    }

    private static long NativeArrayPayloadBytes<T>(NativeArray<T> array)
        where T : struct
    {
        return checked(
            (long)array.Length * UnsafeUtility.SizeOf<T>());
    }

    private static int DivideRoundUp(int value, int divisor)
    {
        return value <= 0
            ? 0
            : checked((value + divisor - 1) / divisor);
    }

    [BurstCompile(
        FloatMode = FloatMode.Strict,
        FloatPrecision = FloatPrecision.Standard)]
    private struct ClassifyAndCountChunksJob : IJobParallelFor
    {
        internal int InstanceCount;
        internal int PairCount;
        internal int DrawGroupCount;
        internal int VisibleBinCount;

        [ReadOnly]
        internal NativeArray<GpuInstanceState> Instances;

        [ReadOnly]
        internal NativeArray<Vector4> ViewPlanes;

        [ReadOnly]
        internal NativeArray<Vector4> ViewParameters;

        [NativeDisableParallelForRestriction]
        internal NativeArray<int> PairKeys;

        [NativeDisableParallelForRestriction]
        internal NativeArray<int> ChunkBinCounts;

        public void Execute(int chunkIndex)
        {
            int histogramBase = chunkIndex * VisibleBinCount;
            for (int bin = 0; bin < VisibleBinCount; bin++)
            {
                ChunkBinCounts[histogramBase + bin] = 0;
            }

            int pairStart = chunkIndex * PairsPerChunk;
            int pairEnd = math.min(pairStart + PairsPerChunk, PairCount);
            for (int pairIndex = pairStart;
                 pairIndex < pairEnd;
                 pairIndex++)
            {
                int viewIndex = pairIndex / InstanceCount;
                int instanceIndex = pairIndex - viewIndex * InstanceCount;
                int key = Classify(
                    Instances[instanceIndex],
                    viewIndex,
                    DrawGroupCount,
                    ViewPlanes,
                    ViewParameters);
                PairKeys[pairIndex] = key;
                if (key >= 0)
                {
                    int histogramIndex = histogramBase + key;
                    ChunkBinCounts[histogramIndex] =
                        ChunkBinCounts[histogramIndex] + 1;
                }
            }
        }

        private static int Classify(
            GpuInstanceState instance,
            int viewIndex,
            int drawGroupCount,
            NativeArray<Vector4> planes,
            NativeArray<Vector4> views)
        {
            if (!IsValid(instance, drawGroupCount))
            {
                return -1;
            }

            Vector4 view = views[viewIndex];
            if (!(view.w > 0.0f))
            {
                return -1;
            }
            uint viewBit = 1u << viewIndex;
            if ((instance.ViewMask & viewBit) == 0u ||
                !IsSphereVisible(instance, viewIndex, planes))
            {
                return -1;
            }

            float dx = instance.PositionRadius.x - view.x;
            float dy = instance.PositionRadius.y - view.y;
            float dz = instance.PositionRadius.z - view.z;
            float scaledDistance =
                math.sqrt(dx * dx + dy * dy + dz * dz) * view.w;
            int selectedLod = SelectLod(instance, scaledDistance);
            if (selectedLod < 0)
            {
                return -1;
            }
            int drawGroup = checked(
                (int)instance.DrawGroupBase + selectedLod);
            return checked(
                viewIndex * drawGroupCount + drawGroup);
        }

        private static bool IsValid(
            GpuInstanceState instance,
            int drawGroupCount)
        {
            if (!(instance.PositionRadius.w >= 0.0f))
            {
                return false;
            }

            uint lodCount = instance.LodCount;
            if (lodCount < 1u ||
                lodCount > GpuInstanceState.MaximumLodCount)
            {
                return false;
            }
            float previous = 0.0f;
            for (int lod = 0; lod < (int)lodCount; lod++)
            {
                float distance = LodDistance(instance.LodDistances, lod);
                if (!(distance > 0.0f) || distance < previous)
                {
                    return false;
                }
                previous = distance;
            }
            return instance.DrawGroupBase < (uint)drawGroupCount &&
                lodCount <=
                (uint)drawGroupCount - instance.DrawGroupBase;
        }

        private static bool IsSphereVisible(
            GpuInstanceState instance,
            int viewIndex,
            NativeArray<Vector4> planes)
        {
            int planeBase =
                viewIndex * GpuDrivenInstancePipeline.FrustumPlaneCount;
            for (int planeIndex = 0;
                 planeIndex < GpuDrivenInstancePipeline.FrustumPlaneCount;
                 planeIndex++)
            {
                Vector4 plane = planes[planeBase + planeIndex];
                float distance =
                    plane.x * instance.PositionRadius.x +
                    plane.y * instance.PositionRadius.y +
                    plane.z * instance.PositionRadius.z +
                    plane.w;
                if (distance < -instance.PositionRadius.w)
                {
                    return false;
                }
            }
            return true;
        }

        private static int SelectLod(
            GpuInstanceState instance,
            float scaledDistance)
        {
            for (int lod = 0; lod < (int)instance.LodCount; lod++)
            {
                if (scaledDistance <=
                    LodDistance(instance.LodDistances, lod))
                {
                    return lod;
                }
            }
            return -1;
        }

        private static float LodDistance(Vector4 distances, int lod)
        {
            switch (lod)
            {
                case 0:
                    return distances.x;
                case 1:
                    return distances.y;
                case 2:
                    return distances.z;
                case 3:
                    return distances.w;
                default:
                    return 0.0f;
            }
        }
    }

    [BurstCompile]
    private struct PrefixCountsJob : IJob
    {
        internal int ChunkCount;
        internal int VisibleBinCount;
        internal NativeArray<int> ChunkBinCountsAndCursors;
        internal NativeArray<int> Counts;
        internal NativeArray<int> Offsets;

        public void Execute()
        {
            int total = 0;
            for (int bin = 0; bin < VisibleBinCount; bin++)
            {
                int binCount = 0;
                for (int chunk = 0; chunk < ChunkCount; chunk++)
                {
                    binCount += ChunkBinCountsAndCursors[
                        chunk * VisibleBinCount + bin];
                }
                Counts[bin] = binCount;
                Offsets[bin] = total;
                total += binCount;
            }
            Offsets[VisibleBinCount] = total;

            for (int bin = 0; bin < VisibleBinCount; bin++)
            {
                int cursor = Offsets[bin];
                for (int chunk = 0; chunk < ChunkCount; chunk++)
                {
                    int index = chunk * VisibleBinCount + bin;
                    int chunkCount = ChunkBinCountsAndCursors[index];
                    ChunkBinCountsAndCursors[index] = cursor;
                    cursor += chunkCount;
                }
            }
        }
    }

    [BurstCompile]
    private struct StableScatterChunksJob : IJobParallelFor
    {
        internal int InstanceCount;
        internal int PairCount;
        internal int VisibleBinCount;

        [ReadOnly]
        internal NativeArray<int> PairKeys;

        [NativeDisableParallelForRestriction]
        internal NativeArray<int> ChunkBinCursors;

        [ReadOnly]
        internal NativeArray<Matrix4x4> ObjectToWorld;

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        internal NativeArray<uint> GroupedIndices;

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        internal NativeArray<Matrix4x4> GroupedMatrices;

        public void Execute(int chunkIndex)
        {
            int pairStart = chunkIndex * PairsPerChunk;
            int pairEnd = math.min(pairStart + PairsPerChunk, PairCount);
            int cursorBase = chunkIndex * VisibleBinCount;
            for (int pairIndex = pairStart;
                 pairIndex < pairEnd;
                 pairIndex++)
            {
                int key = PairKeys[pairIndex];
                if (key < 0)
                {
                    continue;
                }
                int cursorIndex = cursorBase + key;
                int outputIndex = ChunkBinCursors[cursorIndex];
                ChunkBinCursors[cursorIndex] = outputIndex + 1;
                int instanceIndex = pairIndex % InstanceCount;
                GroupedIndices[outputIndex] = checked((uint)instanceIndex);
                GroupedMatrices[outputIndex] = ObjectToWorld[instanceIndex];
            }
        }
    }
}
