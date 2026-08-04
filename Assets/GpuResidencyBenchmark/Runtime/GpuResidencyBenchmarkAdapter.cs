using System;
using System.Diagnostics;
using Summit.GpuResidencyManager;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuResidencyBenchmarkAdapter : IDisposable
{
    public const string RebuildCaseId =
        "gpu-residency/rebuild-visible-set-v1";
    public const string PersistentCaseId =
        "gpu-residency/persistent-lru-delta-v1";
    public const int GridWidth = 64;
    public const int WindowWidth = 16;
    public const int RequestedPageCount = WindowWidth * WindowWidth;

    private readonly uint seed;
    private readonly GpuPointPageCache cache;
    private readonly GpuPageResidencyPlanner rebuildPlanner;
    private readonly GpuPageResidencyPlanner persistentPlanner;
    private readonly GpuPointPageValue[] backingStore;
    private readonly GpuPointPageValue[] uploadStaging;
    private readonly int[] requestedPages;
    private readonly CommandBuffer resetCommands;
    private readonly CommandBuffer frameCommands;
    private GpuResidencyFramePlan lastPlan;
    private bool disposed;

    public GpuResidencyBenchmarkAdapter(
        int virtualPageCount,
        int physicalSlotCount,
        int pointsPerPage,
        int seed)
    {
        if (virtualPageCount != GridWidth * GridWidth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualPageCount),
                "The deterministic path requires a 64 x 64 virtual grid.");
        }
        if (physicalSlotCount < RequestedPageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalSlotCount));
        }

        VirtualPageCount = virtualPageCount;
        PhysicalSlotCount = physicalSlotCount;
        PointsPerPage = pointsPerPage;
        this.seed = unchecked((uint)seed);
        GpuPointPageCache selectedCache = null;
        CommandBuffer selectedReset = null;
        CommandBuffer selectedFrame = null;
        try
        {
            selectedCache = new GpuPointPageCache(
                virtualPageCount,
                physicalSlotCount,
                pointsPerPage,
                RequestedPageCount);
            selectedReset = new CommandBuffer
            {
                name = "GPU.Residency/ResetPageTable"
            };
            selectedFrame = new CommandBuffer
            {
                name = "GPU.Residency/Frame"
            };
        }
        catch
        {
            selectedReset?.Dispose();
            selectedFrame?.Dispose();
            selectedCache?.Dispose();
            throw;
        }

        cache = selectedCache;
        resetCommands = selectedReset;
        frameCommands = selectedFrame;
        rebuildPlanner = new GpuPageResidencyPlanner(
            virtualPageCount,
            physicalSlotCount,
            RequestedPageCount,
            GpuResidencyPolicy.RebuildVisibleSet);
        persistentPlanner = new GpuPageResidencyPlanner(
            virtualPageCount,
            physicalSlotCount,
            RequestedPageCount,
            GpuResidencyPolicy.PersistentLru);
        backingStore = new GpuPointPageValue[
            checked(virtualPageCount * pointsPerPage)];
        uploadStaging = new GpuPointPageValue[
            checked(RequestedPageCount * pointsPerPage)];
        requestedPages = new int[RequestedPageCount];
        PopulateBackingStore();
    }

    public int VirtualPageCount { get; }
    public int PhysicalSlotCount { get; }
    public int PointsPerPage { get; }
    public long GpuResidentBytes => cache.ResidentBytes;
    public long CpuBackingStoreBytes =>
        checked((long)backingStore.Length * GpuPointPageCache.PointStride);
    public long CpuStagingBytes =>
        checked((long)uploadStaging.Length * GpuPointPageCache.PointStride);

    public CommandBuffer FrameCommands => frameCommands;

    public string VariantName(GpuResidencyBenchmarkVariant variant)
    {
        return variant == GpuResidencyBenchmarkVariant.RebuildVisibleSet
            ? "rebuild-visible-set"
            : "persistent-lru-delta";
    }

    public string CaseId(GpuResidencyBenchmarkVariant variant)
    {
        return variant == GpuResidencyBenchmarkVariant.RebuildVisibleSet
            ? RebuildCaseId
            : PersistentCaseId;
    }

    public void Reset(GpuResidencyBenchmarkVariant variant)
    {
        ThrowIfDisposed();
        Planner(variant).Reset();
        lastPlan = null;
        resetCommands.Clear();
        cache.RecordReset(resetCommands);
        Graphics.ExecuteCommandBuffer(resetCommands);
    }

    public GpuResidencyFramePreparation PrepareFrame(
        GpuResidencyBenchmarkVariant variant,
        int pathFrame)
    {
        ThrowIfDisposed();
        BuildRequests(pathFrame);
        long planStart = Stopwatch.GetTimestamp();
        GpuResidencyFramePlan plan = Planner(variant).PlanFrame(
            requestedPages,
            pathFrame);
        double planningMs = ElapsedMilliseconds(planStart);

        long stagingStart = Stopwatch.GetTimestamp();
        for (int upload = 0; upload < plan.UploadCount; upload++)
        {
            int virtualPage = checked((int)plan.Uploads[upload].VirtualPage);
            Array.Copy(
                backingStore,
                checked(virtualPage * PointsPerPage),
                uploadStaging,
                checked(upload * PointsPerPage),
                PointsPerPage);
        }
        double stagingMs = ElapsedMilliseconds(stagingStart);
        lastPlan = plan;
        return new GpuResidencyFramePreparation(
            plan,
            planningMs,
            stagingMs,
            checked((long)plan.UploadCount * PointsPerPage *
                GpuPointPageCache.PointStride),
            checked((long)plan.UploadCount *
                GpuPointPageCache.UploadStride),
            checked((long)plan.DeltaCount *
                GpuPointPageCache.DeltaStride),
            checked((long)plan.RequestedPages.Length * sizeof(uint)));
    }

    public double RecordFrame(GpuResidencyFramePreparation preparation)
    {
        ThrowIfDisposed();
        long start = Stopwatch.GetTimestamp();
        cache.RecordFrame(
            frameCommands,
            preparation.Plan,
            uploadStaging);
        return ElapsedMilliseconds(start);
    }

    public GpuResidencyValidationResult ValidateLastFrame()
    {
        ThrowIfDisposed();
        if (lastPlan == null)
        {
            throw new InvalidOperationException(
                "No residency frame has been recorded.");
        }
        var actual = new GpuPageDigest[lastPlan.RequestedPages.Length];
        cache.PageDigests.GetData(actual);
        uint hash = 2166136261u;
        for (int index = 0; index < actual.Length; index++)
        {
            uint page = checked((uint)lastPlan.RequestedPages[index]);
            GpuPageDigest expected = GpuPointPageGenerator.Digest(
                page,
                PointsPerPage,
                seed);
            if (!DigestEquals(actual[index], expected))
            {
                return new GpuResidencyValidationResult(
                    false,
                    "GPU page digest mismatch at request " + index + ".",
                    hash.ToString("X8"),
                    checked(actual.Length * GpuPointPageCache.DigestStride));
            }
            hash = HashDigest(hash, actual[index]);
        }
        return new GpuResidencyValidationResult(
            true,
            "All visible page digests match the CPU backing-store oracle.",
            hash.ToString("X8"),
            checked(actual.Length * GpuPointPageCache.DigestStride));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        resetCommands.Dispose();
        frameCommands.Dispose();
        cache.Dispose();
    }

    private void PopulateBackingStore()
    {
        for (uint page = 0u;
            page < unchecked((uint)VirtualPageCount);
            page++)
        {
            int baseIndex = checked((int)page * PointsPerPage);
            for (uint point = 0u;
                point < unchecked((uint)PointsPerPage);
                point++)
            {
                backingStore[baseIndex + checked((int)point)] =
                    GpuPointPageGenerator.Generate(page, point, seed);
            }
        }
    }

    private void BuildRequests(int pathFrame)
    {
        int epoch = pathFrame / 128;
        int local = pathFrame % 128;
        int baseX = (local + epoch * 23) & (GridWidth - 1);
        int baseY = (epoch * 17 + local / 8) & (GridWidth - 1);
        int index = 0;
        for (int y = 0; y < WindowWidth; y++)
        {
            int pageY = (baseY + y) & (GridWidth - 1);
            for (int x = 0; x < WindowWidth; x++)
            {
                int pageX = (baseX + x) & (GridWidth - 1);
                requestedPages[index++] = pageY * GridWidth + pageX;
            }
        }
    }

    private GpuPageResidencyPlanner Planner(
        GpuResidencyBenchmarkVariant variant)
    {
        return variant == GpuResidencyBenchmarkVariant.RebuildVisibleSet
            ? rebuildPlanner
            : persistentPlanner;
    }

    private static bool DigestEquals(
        GpuPageDigest left,
        GpuPageDigest right)
    {
        return left.X == right.X && left.Y == right.Y &&
            left.Z == right.Z && left.W == right.W;
    }

    private static uint HashDigest(uint hash, GpuPageDigest value)
    {
        hash = unchecked((hash ^ value.X) * 16777619u);
        hash = unchecked((hash ^ value.Y) * 16777619u);
        hash = unchecked((hash ^ value.Z) * 16777619u);
        return unchecked((hash ^ value.W) * 16777619u);
    }

    private static double ElapsedMilliseconds(long start)
    {
        return (Stopwatch.GetTimestamp() - start) *
            (1000.0 / Stopwatch.Frequency);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuResidencyBenchmarkAdapter));
        }
    }
}

internal sealed class GpuResidencyFramePreparation
{
    public GpuResidencyFramePreparation(
        GpuResidencyFramePlan plan,
        double planningMs,
        double stagingMs,
        long pointPayloadBytes,
        long descriptorBytes,
        long deltaBytes,
        long requestBytes)
    {
        Plan = plan;
        PlanningMs = planningMs;
        StagingMs = stagingMs;
        PointPayloadBytes = pointPayloadBytes;
        DescriptorBytes = descriptorBytes;
        DeltaBytes = deltaBytes;
        RequestBytes = requestBytes;
    }

    public GpuResidencyFramePlan Plan { get; }
    public double PlanningMs { get; }
    public double StagingMs { get; }
    public long PointPayloadBytes { get; }
    public long DescriptorBytes { get; }
    public long DeltaBytes { get; }
    public long RequestBytes { get; }
    public long TotalUploadBytes => checked(
        PointPayloadBytes + DescriptorBytes + DeltaBytes + RequestBytes);
}

internal readonly struct GpuResidencyValidationResult
{
    public GpuResidencyValidationResult(
        bool passed,
        string message,
        string hash,
        int readbackBytes)
    {
        Passed = passed;
        Message = message;
        ResultHash = hash;
        ReadbackBytes = readbackBytes;
    }

    public bool Passed { get; }
    public string Message { get; }
    public string ResultHash { get; }
    public int ReadbackBytes { get; }
}
