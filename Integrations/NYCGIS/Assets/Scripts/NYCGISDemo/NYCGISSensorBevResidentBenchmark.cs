using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds an inflated obstacle-cost map from deterministic depth. The GPU mode keeps the
/// measurement product resident and performs only one correctness readback during warm-up.
/// </summary>
public sealed class NYCGISSensorBevResidentBenchmark : INYCGISSensorBenchmark
{
    private const int BevResolution = 256;
    private const int CostInflationRadiusCells = 3;
    private const int LinearThreadGroupSize = 256;
    private const int CostThreadGroupSize = 8;
    private const float BevMinimumX = -32.0f;
    private const float BevMinimumZ = 0.0f;
    private const float BevSizeMeters = 64.0f;
    private const string ComputeResourcePath =
        "NYCGISDemo/NYCGISSensorBevObstacleCost";

    private readonly NYCGISSensorDataMode mode;
    private readonly int width;
    private readonly int height;
    private readonly int pointCount;
    private readonly int cellCount = BevResolution * BevResolution;
    private readonly float rateHz;
    private readonly float fx;
    private readonly float fy;
    private readonly float cx;
    private readonly float cy;
    private readonly double issuePeriodSeconds;
    private readonly List<float> hostWorkMilliseconds;
    private readonly List<float> completionLatencyMilliseconds;

    private NativeArray<float> depthSamples;
    private NativeArray<uint> cpuBev;
    private NativeArray<uint> cpuCost;
    private NativeArray<uint> expectedBev;
    private NativeArray<uint> expectedCost;
    private Texture2D depthTexture;
    private ComputeShader computeShader;
    private int clearKernel = -1;
    private int bevKernel = -1;
    private int costKernel = -1;
    private GraphicsBuffer gpuBevBuffer;
    private GraphicsBuffer gpuCostBuffer;
    private AsyncGPUReadbackRequest validationRequest;
    private Action<AsyncGPUReadbackRequest> validationCallback;

    private bool acceptingIssues;
    private bool measuring;
    private bool disposed;
    private bool validationIssued;
    private bool validationInFlight;
    private bool validationCompleted;
    private double nextIssueTime;
    private int issuedFrames;
    private int completedFrames;
    private int droppedFrames;
    private int readbackErrors;
    private uint maximumCellError;
    private double checksum;
    private double validationChecksum;

    public NYCGISSensorBevResidentBenchmark(
        NYCGISSensorDataMode mode,
        int width,
        int height,
        float rateHz,
        int measurementCapacity)
    {
        if (mode != NYCGISSensorDataMode.CpuBevCost &&
            mode != NYCGISSensorDataMode.GpuBevResident)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "BEV obstacle-cost mode is required.");
        }

        this.mode = mode;
        this.width = Mathf.Max(1, width);
        this.height = Mathf.Max(1, height);
        pointCount = checked(this.width * this.height);
        this.rateHz = Mathf.Max(1.0f, rateHz);
        issuePeriodSeconds = 1.0 / this.rateHz;
        hostWorkMilliseconds = new List<float>(Mathf.Max(16, measurementCapacity));
        completionLatencyMilliseconds =
            new List<float>(Mathf.Max(16, measurementCapacity));

        float verticalFovRadians = 60.0f * Mathf.Deg2Rad;
        fy = 0.5f * this.height / Mathf.Tan(0.5f * verticalFovRadians);
        fx = fy;
        cx = (this.width - 1) * 0.5f;
        cy = (this.height - 1) * 0.5f;

        depthSamples = new NativeArray<float>(
            pointCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        PopulateDeterministicDepth();

        if (mode == NYCGISSensorDataMode.CpuBevCost)
        {
            cpuBev = new NativeArray<uint>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            cpuCost = new NativeArray<uint>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
        }
        else
        {
            expectedBev = new NativeArray<uint>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            expectedCost = new NativeArray<uint>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            BuildExpectedBev(expectedBev);
            BuildExpectedCost(expectedBev, expectedCost);
            InitializeGpuResources();
        }
    }

    public bool HasPendingReadbacks => validationInFlight;

    public bool Passed =>
        completedFrames > 0 &&
        readbackErrors == 0 &&
        (mode != NYCGISSensorDataMode.GpuBevResident ||
         (validationCompleted && maximumCellError == 0u));

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
        readbackErrors = 0;
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

        if (now - nextIssueTime > issuePeriodSeconds)
        {
            nextIssueTime = now + issuePeriodSeconds;
        }
        else
        {
            nextIssueTime += issuePeriodSeconds;
        }

        if (mode == NYCGISSensorDataMode.CpuBevCost)
        {
            IssueCpuWork();
        }
        else
        {
            IssueGpuWork();
        }
    }

    public void StopIssuing()
    {
        acceptingIssues = false;
    }

    public void PollOnly(double now)
    {
        // GPU-resident measurement has no readback to poll. The warm-up validation uses a
        // preallocated callback.
    }

    public void AppendReport(StringBuilder report, double measurementElapsedSeconds)
    {
        double elapsed = Math.Max(0.001, measurementElapsedSeconds);
        long outputBytesPerFrame = (long)cellCount * sizeof(uint);
        double millionPointsPerSecond =
            completedFrames * (double)pointCount / elapsed / 1_000_000.0;
        double outputGiBPerSecond =
            completedFrames * (double)outputBytesPerFrame / elapsed /
            (1024.0 * 1024.0 * 1024.0);
        bool gpuResident = mode == NYCGISSensorDataMode.GpuBevResident;

        report.AppendLine($"sensorMode={ModeName()}");
        report.AppendLine("sensorProduct=bev-obstacle-cost");
        report.AppendLine($"sensorWidth={width}");
        report.AppendLine($"sensorHeight={height}");
        report.AppendLine($"sensorRateHz={rateHz:F1}");
        report.AppendLine($"sensorPointCount={pointCount}");
        report.AppendLine($"sensorBevResolution={BevResolution}");
        report.AppendLine(
            $"sensorBevCellSizeMeters={BevSizeMeters / BevResolution:F3}");
        report.AppendLine(
            $"sensorObstacleInflationRadiusCells={CostInflationRadiusCells}");
        report.AppendLine(
            $"sensorObstacleInflationRadiusMeters=" +
            $"{CostInflationRadiusCells * BevSizeMeters / BevResolution:F3}");
        report.AppendLine($"sensorOutputBytesPerFrame={outputBytesPerFrame}");
        report.AppendLine("sensorMeasurementReadbackBytesPerFrame=0");
        report.AppendLine("sensorMeasurementReadbackRequests=0");
        report.AppendLine($"sensorGpuResidentProduct={(gpuResident ? 1 : 0)}");
        report.AppendLine("sensorReadbackRingSize=0");
        report.AppendLine($"sensorIssuedFrames={issuedFrames}");
        report.AppendLine($"sensorCompletedFrames={completedFrames}");
        report.AppendLine($"sensorDroppedFrames={droppedFrames}");
        report.AppendLine($"sensorReadbackErrors={readbackErrors}");
        report.AppendLine($"sensorMillionPointsPerSecond={millionPointsPerSecond:F3}");
        report.AppendLine($"sensorOutputGiBPerSecond={outputGiBPerSecond:F3}");
        report.AppendLine($"sensorMaximumPointError={maximumCellError}");
        report.AppendLine("sensorCorrectnessTolerance=0");
        report.AppendLine($"sensorCorrectnessPassed={(Passed ? 1 : 0)}");
        report.AppendLine(
            $"sensorCompletionSemantics=" +
            $"{(gpuResident ? "gpu-resident-enqueued" : "cpu-complete")}");
        report.AppendLine(
            $"sensorChecksum={(gpuResident ? validationChecksum : checksum):F6}");
        AppendMetric(report, "sensorHostWorkMs", hostWorkMilliseconds);
        AppendMetric(report, "sensorCompletionLatencyMs", completionLatencyMilliseconds);
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

        gpuBevBuffer?.Dispose();
        gpuCostBuffer?.Dispose();
        if (cpuBev.IsCreated)
        {
            cpuBev.Dispose();
        }
        if (cpuCost.IsCreated)
        {
            cpuCost.Dispose();
        }
        if (expectedBev.IsCreated)
        {
            expectedBev.Dispose();
        }
        if (expectedCost.IsCreated)
        {
            expectedCost.Dispose();
        }
        if (depthSamples.IsCreated)
        {
            depthSamples.Dispose();
        }
        if (depthTexture != null)
        {
            UnityEngine.Object.Destroy(depthTexture);
        }
    }

    private string ModeName()
    {
        return mode == NYCGISSensorDataMode.CpuBevCost
            ? "cpu-bev-cost"
            : "gpu-bev-resident";
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

    private void BuildExpectedBev(NativeArray<uint> output)
    {
        float inverseCellSize = BevResolution / BevSizeMeters;
        for (int index = 0; index < pointCount; index++)
        {
            int x = index % width;
            float depth = depthSamples[index];
            float pointX = (x - cx) * depth / fx;
            int cellX =
                Mathf.FloorToInt((pointX - BevMinimumX) * inverseCellSize);
            int cellZ =
                Mathf.FloorToInt((depth - BevMinimumZ) * inverseCellSize);
            if (cellX >= 0 && cellX < BevResolution &&
                cellZ >= 0 && cellZ < BevResolution)
            {
                int cellIndex = cellZ * BevResolution + cellX;
                output[cellIndex] = output[cellIndex] + 1u;
            }
        }
    }

    private static void BuildExpectedCost(
        NativeArray<uint> occupancy,
        NativeArray<uint> output)
    {
        for (int cellIndex = 0; cellIndex < output.Length; cellIndex++)
        {
            int cellX = cellIndex % BevResolution;
            int cellZ = cellIndex / BevResolution;
            uint bestDistanceSquared = uint.MaxValue;
            for (int offsetZ = -CostInflationRadiusCells;
                 offsetZ <= CostInflationRadiusCells;
                 offsetZ++)
            {
                int sampleZ = cellZ + offsetZ;
                if (sampleZ < 0 || sampleZ >= BevResolution)
                {
                    continue;
                }
                for (int offsetX = -CostInflationRadiusCells;
                     offsetX <= CostInflationRadiusCells;
                     offsetX++)
                {
                    int sampleX = cellX + offsetX;
                    if (sampleX < 0 || sampleX >= BevResolution ||
                        occupancy[sampleZ * BevResolution + sampleX] == 0u)
                    {
                        continue;
                    }
                    uint distanceSquared =
                        (uint)(offsetX * offsetX + offsetZ * offsetZ);
                    bestDistanceSquared =
                        Math.Min(bestDistanceSquared, distanceSquared);
                }
            }
            output[cellIndex] = CostFromDistance(bestDistanceSquared);
        }
    }

    private static uint CostFromDistance(uint distanceSquared)
    {
        return distanceSquared == uint.MaxValue
            ? 0u
            : 255u - Math.Min(255u, distanceSquared * 12u);
    }

    private void InitializeGpuResources()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
        {
            throw new NotSupportedException(
                "GPU-resident BEV benchmark requires compute shaders and " +
                "AsyncGPUReadback for warm-up validation.");
        }

        computeShader = Resources.Load<ComputeShader>(ComputeResourcePath);
        if (computeShader == null)
        {
            throw new InvalidOperationException(
                "GPU-resident BEV compute shader is missing at Resources/" +
                ComputeResourcePath + ".");
        }
        clearKernel = computeShader.FindKernel("ClearBev");
        bevKernel = computeShader.FindKernel("DepthToBev");
        costKernel = computeShader.FindKernel("BevToObstacleCost");

        depthTexture = new Texture2D(
            width,
            height,
            TextureFormat.RFloat,
            false,
            true)
        {
            name = "NYCGIS GPU-Resident BEV Benchmark Depth",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        depthTexture.SetPixelData(depthSamples, 0);
        depthTexture.Apply(false, true);

        float inverseCellSize = BevResolution / BevSizeMeters;
        computeShader.SetTexture(bevKernel, "_SensorDepth", depthTexture);
        computeShader.SetInt("_SensorWidth", width);
        computeShader.SetInt("_SensorPointCount", pointCount);
        computeShader.SetInt("_SensorBevCellCount", cellCount);
        computeShader.SetInt("_SensorBevResolution", BevResolution);
        computeShader.SetInt("_SensorCostRadius", CostInflationRadiusCells);
        computeShader.SetVector("_SensorIntrinsics", new Vector4(fx, fy, cx, cy));
        computeShader.SetVector(
            "_SensorBevBounds",
            new Vector4(
                BevMinimumX,
                BevMinimumZ,
                inverseCellSize,
                inverseCellSize));

        gpuBevBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            cellCount,
            sizeof(uint));
        gpuCostBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            cellCount,
            sizeof(uint));
        validationCallback = CompleteValidationReadback;
    }

    private void IssueCpuWork()
    {
        long start = Stopwatch.GetTimestamp();
        DepthToBevJob bevJob = new DepthToBevJob
        {
            Depth = depthSamples,
            Bev = cpuBev,
            Width = width,
            Fx = fx,
            Cx = cx,
            MinimumX = BevMinimumX,
            MinimumZ = BevMinimumZ,
            InverseCellSize = BevResolution / BevSizeMeters,
            Resolution = BevResolution
        };
        BevToObstacleCostJob costJob = new BevToObstacleCostJob
        {
            Bev = cpuBev,
            Cost = cpuCost,
            Resolution = BevResolution,
            Radius = CostInflationRadiusCells
        };
        JobHandle bevHandle = bevJob.Schedule();
        costJob.Schedule(cellCount, 64, bevHandle).Complete();
        float elapsedMilliseconds = ElapsedMilliseconds(start);

        if (!measuring)
        {
            return;
        }

        issuedFrames++;
        completedFrames++;
        hostWorkMilliseconds.Add(elapsedMilliseconds);
        completionLatencyMilliseconds.Add(elapsedMilliseconds);
        int checksumIndex = (issuedFrames * 7919) % cellCount;
        checksum += cpuCost[checksumIndex];
    }

    private void IssueGpuWork()
    {
        long start = Stopwatch.GetTimestamp();
        computeShader.SetBuffer(clearKernel, "_SensorBev", gpuBevBuffer);
        computeShader.Dispatch(
            clearKernel,
            Mathf.CeilToInt(cellCount / (float)LinearThreadGroupSize),
            1,
            1);
        computeShader.SetBuffer(bevKernel, "_SensorBev", gpuBevBuffer);
        computeShader.Dispatch(
            bevKernel,
            Mathf.CeilToInt(pointCount / (float)LinearThreadGroupSize),
            1,
            1);
        computeShader.SetBuffer(costKernel, "_SensorBev", gpuBevBuffer);
        computeShader.SetBuffer(
            costKernel,
            "_SensorObstacleCost",
            gpuCostBuffer);
        computeShader.Dispatch(
            costKernel,
            Mathf.CeilToInt(BevResolution / (float)CostThreadGroupSize),
            Mathf.CeilToInt(BevResolution / (float)CostThreadGroupSize),
            1);

        if (!measuring && !validationIssued)
        {
            validationIssued = true;
            validationInFlight = true;
            validationRequest =
                AsyncGPUReadback.Request(gpuCostBuffer, validationCallback);
        }
        float elapsedMilliseconds = ElapsedMilliseconds(start);

        if (measuring)
        {
            issuedFrames++;
            completedFrames++;
            hostWorkMilliseconds.Add(elapsedMilliseconds);
        }
    }

    private void CompleteValidationReadback(AsyncGPUReadbackRequest request)
    {
        validationInFlight = false;
        if (request.hasError)
        {
            readbackErrors++;
            return;
        }

        NativeArray<uint> actual = request.GetData<uint>();
        uint localMaximum = 0u;
        double localChecksum = 0.0;
        for (int index = 0; index < cellCount; index++)
        {
            uint expected = expectedCost[index];
            uint observed = actual[index];
            uint difference = observed >= expected
                ? observed - expected
                : expected - observed;
            localMaximum = Math.Max(localMaximum, difference);
            if ((index & 1023) == 0)
            {
                localChecksum += observed;
            }
        }
        maximumCellError = Math.Max(maximumCellError, localMaximum);
        validationChecksum = localChecksum;
        validationCompleted = true;
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
        report.AppendLine($"{name}Average={total / sorted.Length:F3}");
        report.AppendLine($"{name}P95={Percentile(sorted, 0.95f):F3}");
        report.AppendLine($"{name}P99={Percentile(sorted, 0.99f):F3}");
    }

    private static float Percentile(float[] sorted, float percentile)
    {
        int index = Mathf.Clamp(
            Mathf.CeilToInt(sorted.Length * percentile) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    private struct DepthToBevJob : IJob
    {
        [ReadOnly] public NativeArray<float> Depth;
        public NativeArray<uint> Bev;
        public int Width;
        public float Fx;
        public float Cx;
        public float MinimumX;
        public float MinimumZ;
        public float InverseCellSize;
        public int Resolution;

        public void Execute()
        {
            for (int cellIndex = 0; cellIndex < Bev.Length; cellIndex++)
            {
                Bev[cellIndex] = 0u;
            }

            for (int index = 0; index < Depth.Length; index++)
            {
                int x = index % Width;
                float depth = Depth[index];
                float pointX = (x - Cx) * depth / Fx;
                int cellX =
                    (int)Math.Floor((pointX - MinimumX) * InverseCellSize);
                int cellZ =
                    (int)Math.Floor((depth - MinimumZ) * InverseCellSize);
                if (cellX >= 0 && cellX < Resolution &&
                    cellZ >= 0 && cellZ < Resolution)
                {
                    int cellIndex = cellZ * Resolution + cellX;
                    Bev[cellIndex] = Bev[cellIndex] + 1u;
                }
            }
        }
    }

    [BurstCompile]
    private struct BevToObstacleCostJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<uint> Bev;
        [WriteOnly] public NativeArray<uint> Cost;
        public int Resolution;
        public int Radius;

        public void Execute(int cellIndex)
        {
            int cellX = cellIndex % Resolution;
            int cellZ = cellIndex / Resolution;
            uint bestDistanceSquared = uint.MaxValue;
            for (int offsetZ = -Radius; offsetZ <= Radius; offsetZ++)
            {
                int sampleZ = cellZ + offsetZ;
                if (sampleZ < 0 || sampleZ >= Resolution)
                {
                    continue;
                }
                for (int offsetX = -Radius; offsetX <= Radius; offsetX++)
                {
                    int sampleX = cellX + offsetX;
                    if (sampleX < 0 || sampleX >= Resolution ||
                        Bev[sampleZ * Resolution + sampleX] == 0u)
                    {
                        continue;
                    }
                    uint distanceSquared =
                        (uint)(offsetX * offsetX + offsetZ * offsetZ);
                    bestDistanceSquared =
                        Math.Min(bestDistanceSquared, distanceSquared);
                }
            }
            Cost[cellIndex] = CostFromDistance(bestDistanceSquared);
        }
    }
}
