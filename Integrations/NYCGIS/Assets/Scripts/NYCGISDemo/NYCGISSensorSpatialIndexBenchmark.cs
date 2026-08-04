using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds and queries a deterministic sensor-space spatial index. The indexed GPU path keeps
/// Morton pairs, radix scratch, cell ranges, queries, and results resident on the GPU. The
/// brute-force GPU path is the algorithmic A/B baseline; the Burst CPU path is the oracle and
/// portability fallback.
/// </summary>
public sealed class NYCGISSensorSpatialIndexBenchmark : INYCGISSensorBenchmark
{
    private const int GridResolution = 64;
    private const int CellCount = GridResolution * GridResolution * GridResolution;
    private const int QueryRadiusCells = 3;
    private const int LinearThreadGroupSize = 256;
    private const int QueryThreadGroupSize = 128;
    private const int RadixBitsPerPass = 4;
    private const int RadixPasses = 5;
    private const int RadixBinCount = 1 << RadixBitsPerPass;
    private const int ValidationWordCount = 8;
    private const string ComputeResourcePath =
        "NYCGISDemo/NYCGISGpuSpatialIndex";

    private readonly NYCGISSensorDataMode mode;
    private readonly int width;
    private readonly int height;
    private readonly int pointCount;
    private readonly int groupCount;
    private readonly int queryCount;
    private readonly float rateHz;
    private readonly double issuePeriodSeconds;
    private readonly List<float> hostWorkMilliseconds;
    private readonly List<float> completionLatencyMilliseconds;

    private NativeArray<float> depthSamples;
    private NativeArray<ulong> cpuPairs;
    private NativeArray<uint> cpuCellStarts;
    private NativeArray<uint> cpuCellEnds;
    private NativeArray<uint4> queries;
    private NativeArray<uint> cpuQueryCounts;
    private NativeArray<uint> expectedQueryCounts;
    private NativeArray<uint> cpuUniqueCellCount;

    private Texture2D depthTexture;
    private ComputeShader computeShader;
    private GraphicsBuffer pairBufferA;
    private GraphicsBuffer pairBufferB;
    private GraphicsBuffer groupHistogramBuffer;
    private GraphicsBuffer groupOffsetBuffer;
    private GraphicsBuffer cellStartBuffer;
    private GraphicsBuffer cellEndBuffer;
    private GraphicsBuffer queryBuffer;
    private GraphicsBuffer expectedQueryBuffer;
    private GraphicsBuffer queryResultBuffer;
    private GraphicsBuffer validationBuffer;
    private CommandBuffer measurementCommands;
    private CommandBuffer validationCommands;
    private AsyncGPUReadbackRequest validationRequest;
    private Action<AsyncGPUReadbackRequest> validationCallback;

    private bool acceptingIssues;
    private bool measuring;
    private bool disposed;
    private bool validationIssued;
    private bool validationInFlight;
    private bool validationCompleted;
    private bool validationReadbackFailed;
    private double nextIssueTime;
    private int issuedFrames;
    private int completedFrames;
    private int droppedFrames;
    private int readbackErrors;
    private int expectedUniqueCellCount;
    private uint expectedIndexXor;
    private uint expectedIndexSum;
    private uint sortedOrderViolations;
    private uint cellRangeViolations;
    private uint queryMismatchCount;
    private uint observedIndexXor;
    private uint observedIndexSum;
    private uint observedUniqueCellCount;
    private uint boundsViolations;
    private ulong expectedQueryHitCount;
    private ulong resultHash;
    private double checksum;

    public NYCGISSensorSpatialIndexBenchmark(
        NYCGISSensorDataMode mode,
        int width,
        int height,
        float rateHz,
        int queryCount,
        int measurementCapacity)
    {
        if (mode != NYCGISSensorDataMode.CpuSpatialIndex &&
            mode != NYCGISSensorDataMode.GpuSpatialBrute &&
            mode != NYCGISSensorDataMode.GpuSpatialIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "A spatial-index benchmark mode is required.");
        }

        this.mode = mode;
        this.width = Mathf.Max(1, width);
        this.height = Mathf.Max(1, height);
        pointCount = checked(this.width * this.height);
        groupCount = Mathf.CeilToInt(
            pointCount / (float)LinearThreadGroupSize);
        this.rateHz = Mathf.Max(1.0f, rateHz);
        this.queryCount = Mathf.Clamp(queryCount, 16, 4096);
        issuePeriodSeconds = 1.0 / this.rateHz;
        hostWorkMilliseconds =
            new List<float>(Mathf.Max(16, measurementCapacity));
        completionLatencyMilliseconds =
            new List<float>(Mathf.Max(16, measurementCapacity));

        depthSamples = new NativeArray<float>(
            pointCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        cpuPairs = new NativeArray<ulong>(
            pointCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        cpuCellStarts = new NativeArray<uint>(
            CellCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        cpuCellEnds = new NativeArray<uint>(
            CellCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        queries = new NativeArray<uint4>(
            this.queryCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        cpuQueryCounts = new NativeArray<uint>(
            this.queryCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        expectedQueryCounts = new NativeArray<uint>(
            this.queryCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        cpuUniqueCellCount = new NativeArray<uint>(
            1,
            Allocator.Persistent,
            NativeArrayOptions.ClearMemory);

        PopulateDeterministicDepth();
        PopulateDeterministicQueries();
        RunCpuIndexAndQueries();
        CaptureExpectedResults();

        if (IsGpuMode)
        {
            InitializeGpuResources();
        }
    }

    private bool IsGpuMode =>
        mode == NYCGISSensorDataMode.GpuSpatialBrute ||
        mode == NYCGISSensorDataMode.GpuSpatialIndex;

    private bool IsIndexedGpuMode =>
        mode == NYCGISSensorDataMode.GpuSpatialIndex;

    public bool HasPendingReadbacks => validationInFlight;

    public bool Passed
    {
        get
        {
            if (completedFrames <= 0 || readbackErrors != 0)
            {
                return false;
            }
            if (!IsGpuMode)
            {
                return true;
            }
            if (!validationCompleted ||
                validationReadbackFailed ||
                queryMismatchCount != 0u)
            {
                return false;
            }
            if (!IsIndexedGpuMode)
            {
                return true;
            }
            return
                sortedOrderViolations == 0u &&
                cellRangeViolations == 0u &&
                boundsViolations == 0u &&
                observedIndexXor == expectedIndexXor &&
                observedIndexSum == expectedIndexSum &&
                observedUniqueCellCount == (uint)expectedUniqueCellCount;
        }
    }

    public void BeginWarmup(double now)
    {
        measuring = false;
        acceptingIssues = true;
        nextIssueTime = now;
    }

    public void BeginMeasurement(double now)
    {
        issuedFrames = 0;
        completedFrames = 0;
        droppedFrames = 0;
        checksum = 0.0;
        hostWorkMilliseconds.Clear();
        completionLatencyMilliseconds.Clear();
        measuring = true;
        acceptingIssues = true;
        nextIssueTime = now;
    }

    public void Tick(double now)
    {
        if (!acceptingIssues || now + 1.0e-9 < nextIssueTime)
        {
            return;
        }

        double lateness = now - nextIssueTime;
        if (lateness >= issuePeriodSeconds)
        {
            int missed = Mathf.Max(
                1,
                Mathf.FloorToInt((float)(lateness / issuePeriodSeconds)));
            if (measuring)
            {
                droppedFrames += missed;
            }
            nextIssueTime = now + issuePeriodSeconds;
        }
        else
        {
            nextIssueTime += issuePeriodSeconds;
        }

        if (IsGpuMode)
        {
            IssueGpuWork();
        }
        else
        {
            IssueCpuWork();
        }
    }

    public void StopIssuing()
    {
        acceptingIssues = false;
    }

    public void PollOnly(double now)
    {
        // Measurement has no readback. Warm-up validation completes through a preallocated
        // AsyncGPUReadback callback.
    }

    public void AppendReport(
        StringBuilder report,
        double measurementElapsedSeconds)
    {
        double elapsed = Math.Max(0.001, measurementElapsedSeconds);
        long outputBytesPerFrame = (long)queryCount * sizeof(uint);
        long indexBytes = EstimateResidentBytes();
        double millionPointsPerSecond =
            completedFrames * (double)pointCount / elapsed / 1_000_000.0;
        double outputGiBPerSecond =
            completedFrames * (double)outputBytesPerFrame / elapsed /
            (1024.0 * 1024.0 * 1024.0);
        double updatesPerSecond = completedFrames / elapsed;
        double queriesPerSecond = completedFrames * (double)queryCount / elapsed;
        ulong bruteCandidates =
            (ulong)pointCount * (ulong)queryCount;
        ulong candidateTests = mode == NYCGISSensorDataMode.GpuSpatialBrute
            ? bruteCandidates
            : expectedQueryHitCount;
        double candidateReductionPercent = bruteCandidates == 0
            ? 0.0
            : (1.0 - candidateTests / (double)bruteCandidates) * 100.0;

        report.AppendLine($"sensorMode={ModeName()}");
        report.AppendLine("sensorProduct=spatial-range-query");
        report.AppendLine($"sensorWidth={width}");
        report.AppendLine($"sensorHeight={height}");
        report.AppendLine($"sensorRateHz={rateHz:F1}");
        report.AppendLine($"sensorPointCount={pointCount}");
        report.AppendLine($"sensorOutputBytesPerFrame={outputBytesPerFrame}");
        report.AppendLine("sensorMeasurementReadbackBytesPerFrame=0");
        report.AppendLine("sensorMeasurementReadbackRequests=0");
        report.AppendLine($"sensorGpuResidentProduct={(IsGpuMode ? 1 : 0)}");
        report.AppendLine("sensorReadbackRingSize=0");
        report.AppendLine($"sensorIssuedFrames={issuedFrames}");
        report.AppendLine($"sensorCompletedFrames={completedFrames}");
        report.AppendLine($"sensorDroppedFrames={droppedFrames}");
        report.AppendLine($"sensorReadbackErrors={readbackErrors}");
        report.AppendLine(
            $"sensorMillionPointsPerSecond={millionPointsPerSecond:F3}");
        report.AppendLine($"sensorOutputGiBPerSecond={outputGiBPerSecond:F6}");
        report.AppendLine($"sensorMaximumPointError={queryMismatchCount}");
        report.AppendLine("sensorCorrectnessTolerance=0");
        report.AppendLine($"sensorCorrectnessPassed={(Passed ? 1 : 0)}");
        report.AppendLine(
            $"sensorCompletionSemantics=" +
            $"{(IsGpuMode ? "gpu-resident-enqueued" : "cpu-complete")}");
        report.AppendLine($"sensorChecksum={checksum:F6}");
        report.AppendLine($"sensorSpatialAlgorithm={AlgorithmName()}");
        report.AppendLine("sensorSpatialCoordinateSpace=sensor-frustum-voxel");
        report.AppendLine($"sensorSpatialGridResolution={GridResolution}");
        report.AppendLine($"sensorSpatialCellCount={CellCount}");
        report.AppendLine($"sensorSpatialQueryCount={queryCount}");
        report.AppendLine(
            $"sensorSpatialQueryRadiusCells={QueryRadiusCells}");
        report.AppendLine(
            $"sensorSpatialExpectedUniqueCellCount={expectedUniqueCellCount}");
        report.AppendLine(
            $"sensorSpatialExpectedQueryHitCount={expectedQueryHitCount}");
        report.AppendLine(
            $"sensorSpatialRadixBitsPerPass=" +
            $"{(IsIndexedGpuMode ? RadixBitsPerPass : 0)}");
        report.AppendLine(
            $"sensorSpatialRadixPasses=" +
            $"{(IsIndexedGpuMode ? RadixPasses : 0)}");
        report.AppendLine(
            $"sensorSpatialRadixHistogram=" +
            $"{(IsIndexedGpuMode ? "wave-aggregated" : "not-applicable")}");
        report.AppendLine(
            $"sensorSpatialRadixScatter=" +
            $"{(IsIndexedGpuMode ? "wave-prefix-stable" : "not-applicable")}");
        report.AppendLine(
            $"sensorSpatialShaderModel=" +
            $"{(IsIndexedGpuMode ? "6.0-dxc" : "5.0-compatible")}");
        report.AppendLine($"sensorSpatialResidentBytes={indexBytes}");
        report.AppendLine(
            $"sensorSpatialWarmupValidationReadbackBytes=" +
            $"{(IsGpuMode ? ValidationWordCount * sizeof(uint) : 0)}");
        report.AppendLine(
            "sensorSpatialMeasurementReadbackBytesPerFrame=0");
        report.AppendLine(
            $"sensorSpatialCandidateTestsPerUpdate={candidateTests}");
        report.AppendLine(
            $"sensorSpatialCandidateCellVisitsPerUpdate=" +
            $"{queryCount * 343}");
        report.AppendLine(
            $"sensorSpatialCandidateReductionPercent=" +
            $"{candidateReductionPercent:F6}");
        report.AppendLine(
            $"sensorSpatialUpdatesPerSecond={updatesPerSecond:F3}");
        report.AppendLine(
            $"sensorSpatialQueriesPerSecond={queriesPerSecond:F3}");
        report.AppendLine(
            $"sensorSpatialSortedOrderViolations={sortedOrderViolations}");
        report.AppendLine(
            $"sensorSpatialCellRangeViolations={cellRangeViolations}");
        report.AppendLine(
            $"sensorSpatialQueryMismatchCount={queryMismatchCount}");
        report.AppendLine(
            $"sensorSpatialBoundsViolations={boundsViolations}");
        report.AppendLine(
            $"sensorSpatialIndexXorExpected={expectedIndexXor}");
        report.AppendLine(
            $"sensorSpatialIndexXorObserved={observedIndexXor}");
        report.AppendLine(
            $"sensorSpatialIndexSumExpected={expectedIndexSum}");
        report.AppendLine(
            $"sensorSpatialIndexSumObserved={observedIndexSum}");
        report.AppendLine(
            $"sensorSpatialUniqueCellsObserved={observedUniqueCellCount}");
        report.AppendLine($"sensorSpatialResultHash={resultHash}");
        report.AppendLine(
            "sensorSpatialGpuMarkers=" +
            "Spatial/Build,Spatial/MortonEncode,Spatial/RadixSort," +
            "Spatial/CellRanges,Spatial/IndexedQuery,Spatial/BruteForceQuery");
        AppendMetric(report, "sensorHostWorkMs", hostWorkMilliseconds);
        AppendMetric(
            report,
            "sensorCompletionLatencyMs",
            completionLatencyMilliseconds);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        acceptingIssues = false;

        if (validationInFlight && !validationRequest.done)
        {
            validationRequest.WaitForCompletion();
        }
        validationInFlight = false;

        measurementCommands?.Release();
        validationCommands?.Release();
        pairBufferA?.Dispose();
        pairBufferB?.Dispose();
        groupHistogramBuffer?.Dispose();
        groupOffsetBuffer?.Dispose();
        cellStartBuffer?.Dispose();
        cellEndBuffer?.Dispose();
        queryBuffer?.Dispose();
        expectedQueryBuffer?.Dispose();
        queryResultBuffer?.Dispose();
        validationBuffer?.Dispose();

        if (depthSamples.IsCreated)
        {
            depthSamples.Dispose();
        }
        if (cpuPairs.IsCreated)
        {
            cpuPairs.Dispose();
        }
        if (cpuCellStarts.IsCreated)
        {
            cpuCellStarts.Dispose();
        }
        if (cpuCellEnds.IsCreated)
        {
            cpuCellEnds.Dispose();
        }
        if (queries.IsCreated)
        {
            queries.Dispose();
        }
        if (cpuQueryCounts.IsCreated)
        {
            cpuQueryCounts.Dispose();
        }
        if (expectedQueryCounts.IsCreated)
        {
            expectedQueryCounts.Dispose();
        }
        if (cpuUniqueCellCount.IsCreated)
        {
            cpuUniqueCellCount.Dispose();
        }
        if (depthTexture != null)
        {
            UnityEngine.Object.Destroy(depthTexture);
        }
    }

    private string ModeName()
    {
        switch (mode)
        {
            case NYCGISSensorDataMode.CpuSpatialIndex:
                return "cpu-spatial-index";
            case NYCGISSensorDataMode.GpuSpatialBrute:
                return "gpu-spatial-brute";
            default:
                return "gpu-spatial-index";
        }
    }

    private string AlgorithmName()
    {
        switch (mode)
        {
            case NYCGISSensorDataMode.CpuSpatialIndex:
                return "burst-morton-sort-uniform-grid";
            case NYCGISSensorDataMode.GpuSpatialBrute:
                return "gpu-parallel-brute-force";
            default:
                return "gpu-wave-radix-morton-uniform-grid";
        }
    }

    private void PopulateDeterministicDepth()
    {
        for (int index = 0; index < pointCount; index++)
        {
            int x = index % width;
            int y = index / width;
            float slope = x * 0.0015f + y * 0.0025f;
            float surface =
                0.35f * Mathf.Sin(x * 0.013f) * Mathf.Cos(y * 0.017f);
            depthSamples[index] = 8.0f + slope + surface;
        }
    }

    private void PopulateDeterministicQueries()
    {
        uint state = 0x9e3779b9u;
        int span = GridResolution - QueryRadiusCells * 2;
        for (int index = 0; index < queryCount; index++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            uint x = (uint)(QueryRadiusCells + state % (uint)span);
            state = unchecked(state * 1664525u + 1013904223u);
            uint y = (uint)(QueryRadiusCells + state % (uint)span);
            state = unchecked(state * 1664525u + 1013904223u);
            uint z = (uint)(QueryRadiusCells + state % (uint)span);
            queries[index] = new uint4(
                x,
                y,
                z,
                QueryRadiusCells);
        }
    }

    private void CaptureExpectedResults()
    {
        expectedUniqueCellCount = (int)cpuUniqueCellCount[0];
        expectedQueryHitCount = 0;
        resultHash = 1469598103934665603ul;
        for (int index = 0; index < queryCount; index++)
        {
            uint count = cpuQueryCounts[index];
            expectedQueryCounts[index] = count;
            expectedQueryHitCount += count;
            resultHash ^= count;
            resultHash *= 1099511628211ul;
        }

        expectedIndexXor = 0u;
        expectedIndexSum = 0u;
        for (uint index = 0u; index < (uint)pointCount; index++)
        {
            expectedIndexXor ^= index;
            expectedIndexSum = unchecked(expectedIndexSum + index);
        }
    }

    private float RunCpuIndexAndQueries()
    {
        long start = Stopwatch.GetTimestamp();
        GenerateMortonPairsJob generateJob = new GenerateMortonPairsJob
        {
            Depth = depthSamples,
            Pairs = cpuPairs,
            Width = width,
            Height = height,
            GridResolution = GridResolution
        };
        generateJob.Schedule(pointCount, LinearThreadGroupSize).Complete();
        cpuPairs.Sort();

        BuildCellRangesJob buildJob = new BuildCellRangesJob
        {
            Pairs = cpuPairs,
            CellStarts = cpuCellStarts,
            CellEnds = cpuCellEnds,
            UniqueCellCount = cpuUniqueCellCount
        };
        JobHandle buildHandle = buildJob.Schedule();
        QueryCellRangesJob queryJob = new QueryCellRangesJob
        {
            Queries = queries,
            CellStarts = cpuCellStarts,
            CellEnds = cpuCellEnds,
            QueryCounts = cpuQueryCounts
        };
        queryJob.Schedule(queryCount, 16, buildHandle).Complete();
        return ElapsedMilliseconds(start);
    }

    private void IssueCpuWork()
    {
        float elapsedMilliseconds = RunCpuIndexAndQueries();
        if (!measuring)
        {
            return;
        }

        issuedFrames++;
        completedFrames++;
        hostWorkMilliseconds.Add(elapsedMilliseconds);
        completionLatencyMilliseconds.Add(elapsedMilliseconds);
        checksum += cpuQueryCounts[(issuedFrames * 7919) % queryCount];
    }

    private void IssueGpuWork()
    {
        bool runValidation = !validationIssued;
        long start = Stopwatch.GetTimestamp();
        Graphics.ExecuteCommandBuffer(
            runValidation ? validationCommands : measurementCommands);

        if (runValidation)
        {
            validationIssued = true;
            validationInFlight = true;
            validationRequest =
                AsyncGPUReadback.Request(validationBuffer, validationCallback);
        }
        float elapsedMilliseconds = ElapsedMilliseconds(start);

        if (measuring)
        {
            issuedFrames++;
            completedFrames++;
            hostWorkMilliseconds.Add(elapsedMilliseconds);
            checksum += expectedQueryCounts[(issuedFrames * 7919) % queryCount];
        }
    }

    private void InitializeGpuResources()
    {
        if (!SystemInfo.supportsComputeShaders ||
            !SystemInfo.supportsAsyncGPUReadback)
        {
            throw new NotSupportedException(
                "GPU spatial-index benchmark requires compute shaders and " +
                "AsyncGPUReadback for warm-up validation.");
        }

        computeShader = Resources.Load<ComputeShader>(ComputeResourcePath);
        if (computeShader == null)
        {
            throw new InvalidOperationException(
                "GPU spatial-index compute shader is missing at Resources/" +
                ComputeResourcePath + ".");
        }

        depthTexture = new Texture2D(
            width,
            height,
            TextureFormat.RFloat,
            false,
            true)
        {
            name = "NYCGIS GPU Spatial Index Depth",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        depthTexture.SetPixelData(depthSamples, 0);
        depthTexture.Apply(false, true);

        queryBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            queryCount,
            sizeof(uint) * 4);
        expectedQueryBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            queryCount,
            sizeof(uint));
        queryResultBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            queryCount,
            sizeof(uint));
        validationBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            ValidationWordCount,
            sizeof(uint));
        queryBuffer.SetData(queries);
        expectedQueryBuffer.SetData(expectedQueryCounts);

        if (IsIndexedGpuMode)
        {
            pairBufferA = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                pointCount,
                sizeof(uint) * 2);
            pairBufferB = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                pointCount,
                sizeof(uint) * 2);
            groupHistogramBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                groupCount * RadixBinCount,
                sizeof(uint));
            groupOffsetBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                groupCount * RadixBinCount,
                sizeof(uint));
            cellStartBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                CellCount,
                sizeof(uint));
            cellEndBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                CellCount,
                sizeof(uint));
        }

        validationCallback = CompleteValidationReadback;
        measurementCommands = BuildGpuCommandBuffer(false);
        validationCommands = BuildGpuCommandBuffer(true);
    }

    private CommandBuffer BuildGpuCommandBuffer(bool validate)
    {
        CommandBuffer commands = new CommandBuffer
        {
            name = validate
                ? "NYCGIS Spatial Index Warm-up Validation"
                : "NYCGIS Spatial Index Measurement"
        };
        SetCommonComputeParameters(commands, validate);
        commands.BeginSample("Spatial/Build");

        if (validate)
        {
            int clearValidationKernel =
                computeShader.FindKernel("ClearValidation");
            commands.SetComputeBufferParam(
                computeShader,
                clearValidationKernel,
                "_SpatialValidation",
                validationBuffer);
            commands.DispatchCompute(
                computeShader,
                clearValidationKernel,
                1,
                1,
                1);
        }

        if (IsIndexedGpuMode)
        {
            RecordIndexedBuildAndQuery(commands, validate);
        }
        else
        {
            RecordBruteForceQuery(commands, validate);
        }

        commands.EndSample("Spatial/Build");
        return commands;
    }

    private void SetCommonComputeParameters(
        CommandBuffer commands,
        bool validate)
    {
        commands.SetComputeIntParam(
            computeShader,
            "_SensorWidth",
            width);
        commands.SetComputeIntParam(
            computeShader,
            "_SensorHeight",
            height);
        commands.SetComputeIntParam(
            computeShader,
            "_SensorPointCount",
            pointCount);
        commands.SetComputeIntParam(
            computeShader,
            "_SpatialGridResolution",
            GridResolution);
        commands.SetComputeIntParam(
            computeShader,
            "_SpatialCellCount",
            CellCount);
        commands.SetComputeIntParam(
            computeShader,
            "_SpatialGroupCount",
            groupCount);
        commands.SetComputeIntParam(
            computeShader,
            "_SpatialQueryCount",
            queryCount);
        commands.SetComputeIntParam(
            computeShader,
            "_SpatialValidationEnabled",
            validate ? 1 : 0);
    }

    private void RecordBruteForceQuery(
        CommandBuffer commands,
        bool validate)
    {
        int kernel = computeShader.FindKernel("BruteForceQueries");
        commands.BeginSample("Spatial/BruteForceQuery");
        commands.SetComputeTextureParam(
            computeShader,
            kernel,
            "_SensorDepth",
            depthTexture);
        BindQueryBuffers(commands, kernel);
        commands.DispatchCompute(
            computeShader,
            kernel,
            queryCount,
            1,
            1);
        commands.EndSample("Spatial/BruteForceQuery");
    }

    private void RecordIndexedBuildAndQuery(
        CommandBuffer commands,
        bool validate)
    {
        int generateKernel = computeShader.FindKernel("GenerateMortonPairs");
        int histogramKernel = computeShader.FindKernel("RadixHistogram");
        int scanKernel = computeShader.FindKernel("RadixScanOffsets");
        int scatterKernel = computeShader.FindKernel("RadixScatterStable");
        int clearRangesKernel = computeShader.FindKernel("ClearCellRanges");
        int buildRangesKernel = computeShader.FindKernel("BuildCellRanges");
        int queryKernel = computeShader.FindKernel("QueryCellRanges");
        int validateKernel = computeShader.FindKernel("ValidateIndex");

        commands.BeginSample("Spatial/MortonEncode");
        commands.SetComputeTextureParam(
            computeShader,
            generateKernel,
            "_SensorDepth",
            depthTexture);
        commands.SetComputeBufferParam(
            computeShader,
            generateKernel,
            "_SpatialPairsGenerated",
            pairBufferA);
        commands.DispatchCompute(
            computeShader,
            generateKernel,
            groupCount,
            1,
            1);
        commands.EndSample("Spatial/MortonEncode");

        GraphicsBuffer input = pairBufferA;
        GraphicsBuffer output = pairBufferB;
        commands.BeginSample("Spatial/RadixSort");
        for (int pass = 0; pass < RadixPasses; pass++)
        {
            commands.SetComputeIntParam(
                computeShader,
                "_SpatialRadixShift",
                pass * RadixBitsPerPass);
            commands.SetComputeBufferParam(
                computeShader,
                histogramKernel,
                "_SpatialPairsIn",
                input);
            commands.SetComputeBufferParam(
                computeShader,
                histogramKernel,
                "_SpatialGroupHistograms",
                groupHistogramBuffer);
            commands.DispatchCompute(
                computeShader,
                histogramKernel,
                groupCount,
                1,
                1);

            commands.SetComputeBufferParam(
                computeShader,
                scanKernel,
                "_SpatialGroupHistograms",
                groupHistogramBuffer);
            commands.SetComputeBufferParam(
                computeShader,
                scanKernel,
                "_SpatialGroupOffsets",
                groupOffsetBuffer);
            commands.DispatchCompute(
                computeShader,
                scanKernel,
                1,
                1,
                1);

            commands.SetComputeBufferParam(
                computeShader,
                scatterKernel,
                "_SpatialPairsIn",
                input);
            commands.SetComputeBufferParam(
                computeShader,
                scatterKernel,
                "_SpatialPairsOut",
                output);
            commands.SetComputeBufferParam(
                computeShader,
                scatterKernel,
                "_SpatialGroupOffsets",
                groupOffsetBuffer);
            commands.DispatchCompute(
                computeShader,
                scatterKernel,
                groupCount,
                1,
                1);

            GraphicsBuffer swap = input;
            input = output;
            output = swap;
        }
        commands.EndSample("Spatial/RadixSort");

        commands.BeginSample("Spatial/CellRanges");
        commands.SetComputeBufferParam(
            computeShader,
            clearRangesKernel,
            "_SpatialCellStarts",
            cellStartBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            clearRangesKernel,
            "_SpatialCellEnds",
            cellEndBuffer);
        commands.DispatchCompute(
            computeShader,
            clearRangesKernel,
            Mathf.CeilToInt(CellCount / (float)LinearThreadGroupSize),
            1,
            1);

        commands.SetComputeBufferParam(
            computeShader,
            buildRangesKernel,
            "_SpatialPairsIn",
            input);
        commands.SetComputeBufferParam(
            computeShader,
            buildRangesKernel,
            "_SpatialCellStarts",
            cellStartBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            buildRangesKernel,
            "_SpatialCellEnds",
            cellEndBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            buildRangesKernel,
            "_SpatialValidation",
            validationBuffer);
        commands.DispatchCompute(
            computeShader,
            buildRangesKernel,
            groupCount,
            1,
            1);
        commands.EndSample("Spatial/CellRanges");

        commands.BeginSample("Spatial/IndexedQuery");
        commands.SetComputeBufferParam(
            computeShader,
            queryKernel,
            "_SpatialCellStarts",
            cellStartBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            queryKernel,
            "_SpatialCellEnds",
            cellEndBuffer);
        BindQueryBuffers(commands, queryKernel);
        commands.DispatchCompute(
            computeShader,
            queryKernel,
            queryCount,
            1,
            1);
        commands.EndSample("Spatial/IndexedQuery");

        if (validate)
        {
            commands.SetComputeBufferParam(
                computeShader,
                validateKernel,
                "_SpatialPairsIn",
                input);
            commands.SetComputeBufferParam(
                computeShader,
                validateKernel,
                "_SpatialCellStarts",
                cellStartBuffer);
            commands.SetComputeBufferParam(
                computeShader,
                validateKernel,
                "_SpatialCellEnds",
                cellEndBuffer);
            commands.SetComputeBufferParam(
                computeShader,
                validateKernel,
                "_SpatialValidation",
                validationBuffer);
            commands.DispatchCompute(
                computeShader,
                validateKernel,
                groupCount,
                1,
                1);
        }
    }

    private void BindQueryBuffers(CommandBuffer commands, int kernel)
    {
        commands.SetComputeBufferParam(
            computeShader,
            kernel,
            "_SpatialQueries",
            queryBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            kernel,
            "_SpatialExpectedQueryCounts",
            expectedQueryBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            kernel,
            "_SpatialQueryCounts",
            queryResultBuffer);
        commands.SetComputeBufferParam(
            computeShader,
            kernel,
            "_SpatialValidation",
            validationBuffer);
    }

    private void CompleteValidationReadback(
        AsyncGPUReadbackRequest request)
    {
        validationInFlight = false;
        if (request.hasError)
        {
            validationReadbackFailed = true;
            readbackErrors++;
            return;
        }

        NativeArray<uint> words = request.GetData<uint>();
        if (words.Length < ValidationWordCount)
        {
            validationReadbackFailed = true;
            readbackErrors++;
            return;
        }

        sortedOrderViolations = words[0];
        cellRangeViolations = words[1];
        queryMismatchCount = words[2];
        observedIndexXor = words[3];
        observedIndexSum = words[4];
        observedUniqueCellCount = words[5];
        boundsViolations = words[6];
        validationCompleted = true;
    }

    private long EstimateResidentBytes()
    {
        long bytes =
            (long)queryCount * sizeof(uint) * 4 +
            (long)queryCount * sizeof(uint) * 3 +
            (long)ValidationWordCount * sizeof(uint);
        if (!IsIndexedGpuMode)
        {
            return bytes;
        }
        bytes +=
            (long)pointCount * sizeof(uint) * 4 +
            (long)groupCount * RadixBinCount * sizeof(uint) * 2 +
            (long)CellCount * sizeof(uint) * 2;
        return bytes;
    }

    private static float ElapsedMilliseconds(long startTimestamp)
    {
        return (float)(
            (Stopwatch.GetTimestamp() - startTimestamp) *
            (1000.0 / Stopwatch.Frequency));
    }

    private static void AppendMetric(
        StringBuilder report,
        string name,
        List<float> values)
    {
        if (values == null || values.Count == 0)
        {
            report.AppendLine(name + "Average=unavailable");
            report.AppendLine(name + "P95=unavailable");
            report.AppendLine(name + "P99=unavailable");
            return;
        }

        float[] sorted = values.ToArray();
        Array.Sort(sorted);
        double total = 0.0;
        for (int i = 0; i < sorted.Length; i++)
        {
            total += sorted[i];
        }
        report.AppendLine(
            $"{name}Average={total / sorted.Length:F3}");
        report.AppendLine(
            $"{name}P95={Percentile(sorted, 0.95f):F3}");
        report.AppendLine(
            $"{name}P99={Percentile(sorted, 0.99f):F3}");
    }

    private static float Percentile(
        float[] sorted,
        float percentile)
    {
        int index = Mathf.Clamp(
            Mathf.CeilToInt(sorted.Length * percentile) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private static uint ExpandMortonBits(uint value)
    {
        value &= 0x000003ffu;
        value = (value | (value << 16)) & 0x030000ffu;
        value = (value | (value << 8)) & 0x0300f00fu;
        value = (value | (value << 4)) & 0x030c30c3u;
        value = (value | (value << 2)) & 0x09249249u;
        return value;
    }

    private static uint Morton3D(uint x, uint y, uint z)
    {
        return
            ExpandMortonBits(x) |
            (ExpandMortonBits(y) << 1) |
            (ExpandMortonBits(z) << 2);
    }

    [BurstCompile(
        FloatMode = FloatMode.Fast,
        FloatPrecision = FloatPrecision.Standard)]
    private struct GenerateMortonPairsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Depth;
        [WriteOnly] public NativeArray<ulong> Pairs;
        public int Width;
        public int Height;
        public int GridResolution;

        public void Execute(int pointIndex)
        {
            int x = pointIndex % Width;
            int y = pointIndex / Width;
            int cellX = math.min(
                x * GridResolution / Width,
                GridResolution - 1);
            int cellY = math.min(
                y * GridResolution / Height,
                GridResolution - 1);
            int cellZ = math.clamp(
                (int)math.floor((Depth[pointIndex] - 4.0f) * 4.0f),
                0,
                GridResolution - 1);
            uint code = Morton3D(
                (uint)cellX,
                (uint)cellY,
                (uint)cellZ);
            Pairs[pointIndex] =
                ((ulong)code << 32) | (uint)pointIndex;
        }
    }

    [BurstCompile]
    private struct BuildCellRangesJob : IJob
    {
        [ReadOnly] public NativeArray<ulong> Pairs;
        public NativeArray<uint> CellStarts;
        public NativeArray<uint> CellEnds;
        public NativeArray<uint> UniqueCellCount;

        public void Execute()
        {
            for (int cellIndex = 0;
                 cellIndex < CellStarts.Length;
                 cellIndex++)
            {
                CellStarts[cellIndex] = uint.MaxValue;
                CellEnds[cellIndex] = 0u;
            }

            uint uniqueCount = 0u;
            for (int sortedIndex = 0;
                 sortedIndex < Pairs.Length;
                 sortedIndex++)
            {
                uint code = (uint)(Pairs[sortedIndex] >> 32);
                bool startsCell =
                    sortedIndex == 0 ||
                    (uint)(Pairs[sortedIndex - 1] >> 32) != code;
                bool endsCell =
                    sortedIndex + 1 == Pairs.Length ||
                    (uint)(Pairs[sortedIndex + 1] >> 32) != code;
                if (startsCell)
                {
                    CellStarts[(int)code] = (uint)sortedIndex;
                    uniqueCount++;
                }
                if (endsCell)
                {
                    CellEnds[(int)code] = (uint)(sortedIndex + 1);
                }
            }
            UniqueCellCount[0] = uniqueCount;
        }
    }

    [BurstCompile]
    private struct QueryCellRangesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<uint4> Queries;
        [ReadOnly] public NativeArray<uint> CellStarts;
        [ReadOnly] public NativeArray<uint> CellEnds;
        [WriteOnly] public NativeArray<uint> QueryCounts;

        public void Execute(int queryIndex)
        {
            uint4 query = Queries[queryIndex];
            int radius = (int)query.w;
            uint count = 0u;
            for (int z = (int)query.z - radius;
                 z <= (int)query.z + radius;
                 z++)
            {
                for (int y = (int)query.y - radius;
                     y <= (int)query.y + radius;
                     y++)
                {
                    for (int x = (int)query.x - radius;
                         x <= (int)query.x + radius;
                         x++)
                    {
                        uint code = Morton3D(
                            (uint)x,
                            (uint)y,
                            (uint)z);
                        uint start = CellStarts[(int)code];
                        uint end = CellEnds[(int)code];
                        if (start != uint.MaxValue && end >= start)
                        {
                            count += end - start;
                        }
                    }
                }
            }
            QueryCounts[queryIndex] = count;
        }
    }
}
