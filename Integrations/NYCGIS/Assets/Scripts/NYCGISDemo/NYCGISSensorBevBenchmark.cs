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
/// Converts deterministic depth into a compact bird's-eye-view occupancy-count grid.
/// The workload processes every source depth sample but returns only a 256 KiB grid.
/// </summary>
public sealed class NYCGISSensorBevBenchmark : INYCGISSensorBenchmark
{
    private const int BevResolution = 256;
    private const int ReadbackRingSize = 3;
    private const int ThreadGroupSize = 256;
    private const float BevMinimumX = -32.0f;
    private const float BevMinimumZ = 0.0f;
    private const float BevSizeMeters = 64.0f;
    private const string ComputeResourcePath = "NYCGISDemo/NYCGISSensorDepthToBev";

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
    private NativeArray<uint> expectedBev;
    private Texture2D depthTexture;
    private ComputeShader computeShader;
    private int clearKernel = -1;
    private int bevKernel = -1;
    private GraphicsBuffer[] gpuBevBuffers;
    private AsyncGPUReadbackRequest[] readbackRequests;
    private Action<AsyncGPUReadbackRequest>[] readbackCallbacks;
    private bool[] readbackInFlight;
    private bool[] readbackMeasured;
    private double[] readbackIssueTimes;
    private int[] readbackReusableAfterFrame;

    private bool acceptingIssues;
    private bool measuring;
    private bool disposed;
    private bool gpuValidationCompleted;
    private double nextIssueTime;
    private int issuedFrames;
    private int completedFrames;
    private int droppedFrames;
    private int readbackErrors;
    private uint maximumCellError;
    private double checksum;

    public NYCGISSensorBevBenchmark(
        NYCGISSensorDataMode mode,
        int width,
        int height,
        float rateHz,
        int measurementCapacity)
    {
        if (mode != NYCGISSensorDataMode.CpuBev &&
            mode != NYCGISSensorDataMode.GpuBev)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "BEV mode is required.");
        }

        this.mode = mode;
        this.width = Mathf.Max(1, width);
        this.height = Mathf.Max(1, height);
        pointCount = checked(this.width * this.height);
        this.rateHz = Mathf.Max(1.0f, rateHz);
        issuePeriodSeconds = 1.0 / this.rateHz;
        hostWorkMilliseconds = new List<float>(Mathf.Max(16, measurementCapacity));
        completionLatencyMilliseconds = new List<float>(Mathf.Max(16, measurementCapacity));

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

        if (mode == NYCGISSensorDataMode.CpuBev)
        {
            cpuBev = new NativeArray<uint>(
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
            BuildExpectedBev(expectedBev);
            InitializeGpuResources();
        }
    }

    public bool HasPendingReadbacks
    {
        get
        {
            if (readbackInFlight == null)
            {
                return false;
            }
            for (int i = 0; i < readbackInFlight.Length; i++)
            {
                if (readbackInFlight[i])
                {
                    return true;
                }
            }
            return false;
        }
    }

    public bool Passed =>
        completedFrames > 0 &&
        readbackErrors == 0 &&
        (mode != NYCGISSensorDataMode.GpuBev ||
         (gpuValidationCompleted && maximumCellError == 0u));

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
        maximumCellError = 0u;
        checksum = 0.0;
        gpuValidationCompleted = false;
        hostWorkMilliseconds.Clear();
        completionLatencyMilliseconds.Clear();
        measuring = true;
        acceptingIssues = true;
        nextIssueTime = now;
    }

    public void Tick(double now)
    {
        PollReadbacks(now);
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

        if (mode == NYCGISSensorDataMode.CpuBev)
        {
            IssueCpuWork();
        }
        else
        {
            IssueGpuWork(now);
        }
    }

    public void StopIssuing()
    {
        acceptingIssues = false;
    }

    public void PollOnly(double now)
    {
        PollReadbacks(now);
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

        report.AppendLine($"sensorMode={ModeName()}");
        report.AppendLine("sensorProduct=bev-occupancy-count");
        report.AppendLine($"sensorWidth={width}");
        report.AppendLine($"sensorHeight={height}");
        report.AppendLine($"sensorRateHz={rateHz:F1}");
        report.AppendLine($"sensorPointCount={pointCount}");
        report.AppendLine($"sensorBevResolution={BevResolution}");
        report.AppendLine($"sensorOutputBytesPerFrame={outputBytesPerFrame}");
        report.AppendLine(
            $"sensorReadbackRingSize={(mode == NYCGISSensorDataMode.GpuBev ? ReadbackRingSize : 0)}");
        report.AppendLine($"sensorIssuedFrames={issuedFrames}");
        report.AppendLine($"sensorCompletedFrames={completedFrames}");
        report.AppendLine($"sensorDroppedFrames={droppedFrames}");
        report.AppendLine($"sensorReadbackErrors={readbackErrors}");
        report.AppendLine($"sensorMillionPointsPerSecond={millionPointsPerSecond:F3}");
        report.AppendLine($"sensorOutputGiBPerSecond={outputGiBPerSecond:F3}");
        report.AppendLine($"sensorMaximumPointError={maximumCellError}");
        report.AppendLine("sensorCorrectnessTolerance=0");
        report.AppendLine($"sensorCorrectnessPassed={(Passed ? 1 : 0)}");
        report.AppendLine($"sensorChecksum={checksum:F6}");
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

        if (readbackRequests != null)
        {
            for (int i = 0; i < readbackRequests.Length; i++)
            {
                if (readbackInFlight[i] && !readbackRequests[i].done)
                {
                    readbackRequests[i].WaitForCompletion();
                }
            }
        }

        if (gpuBevBuffers != null)
        {
            for (int i = 0; i < gpuBevBuffers.Length; i++)
            {
                gpuBevBuffers[i]?.Dispose();
            }
        }
        if (cpuBev.IsCreated)
        {
            cpuBev.Dispose();
        }
        if (expectedBev.IsCreated)
        {
            expectedBev.Dispose();
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
        return mode == NYCGISSensorDataMode.CpuBev ? "cpu-bev" : "gpu-bev";
    }

    private void PopulateDeterministicDepth()
    {
        for (int index = 0; index < pointCount; index++)
        {
            int x = index % width;
            int y = index / width;
            float slope = x * 0.0015f + y * 0.0025f;
            float surface = 0.35f * Mathf.Sin(x * 0.013f) * Mathf.Cos(y * 0.017f);
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
            int cellX = Mathf.FloorToInt((pointX - BevMinimumX) * inverseCellSize);
            int cellZ = Mathf.FloorToInt((depth - BevMinimumZ) * inverseCellSize);
            if (cellX >= 0 && cellX < BevResolution &&
                cellZ >= 0 && cellZ < BevResolution)
            {
                int cellIndex = cellZ * BevResolution + cellX;
                output[cellIndex] = output[cellIndex] + 1u;
            }
        }
    }

    private void InitializeGpuResources()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
        {
            throw new NotSupportedException(
                "GPU BEV benchmark requires compute shaders and AsyncGPUReadback.");
        }

        computeShader = Resources.Load<ComputeShader>(ComputeResourcePath);
        if (computeShader == null)
        {
            throw new InvalidOperationException(
                "GPU BEV benchmark compute shader is missing at Resources/" +
                ComputeResourcePath + ".");
        }
        clearKernel = computeShader.FindKernel("ClearBev");
        bevKernel = computeShader.FindKernel("DepthToBev");

        depthTexture = new Texture2D(width, height, TextureFormat.RFloat, false, true)
        {
            name = "NYCGIS Sensor BEV Benchmark Depth",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        depthTexture.SetPixelData(depthSamples, 0);
        depthTexture.Apply(false, true);

        computeShader.SetTexture(bevKernel, "_SensorDepth", depthTexture);
        computeShader.SetInt("_SensorWidth", width);
        computeShader.SetInt("_SensorPointCount", pointCount);
        computeShader.SetInt("_SensorBevCellCount", cellCount);
        computeShader.SetInt("_SensorBevResolution", BevResolution);
        computeShader.SetVector("_SensorIntrinsics", new Vector4(fx, fy, cx, cy));
        float inverseCellSize = BevResolution / BevSizeMeters;
        computeShader.SetVector(
            "_SensorBevBounds",
            new Vector4(
                BevMinimumX,
                BevMinimumZ,
                inverseCellSize,
                inverseCellSize));

        gpuBevBuffers = new GraphicsBuffer[ReadbackRingSize];
        readbackRequests = new AsyncGPUReadbackRequest[ReadbackRingSize];
        readbackCallbacks = new Action<AsyncGPUReadbackRequest>[ReadbackRingSize];
        readbackInFlight = new bool[ReadbackRingSize];
        readbackMeasured = new bool[ReadbackRingSize];
        readbackIssueTimes = new double[ReadbackRingSize];
        readbackReusableAfterFrame = new int[ReadbackRingSize];
        for (int i = 0; i < ReadbackRingSize; i++)
        {
            gpuBevBuffers[i] = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                cellCount,
                sizeof(uint));
            int capturedSlot = i;
            readbackCallbacks[i] = request => CompleteReadback(capturedSlot, request);
        }
    }

    private void IssueCpuWork()
    {
        long start = Stopwatch.GetTimestamp();
        DepthToBevJob job = new DepthToBevJob
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
        job.Schedule().Complete();
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
        checksum += cpuBev[checksumIndex];
    }

    private void IssueGpuWork(double now)
    {
        int slot = FindAvailableReadbackSlot();
        if (slot < 0)
        {
            if (measuring)
            {
                droppedFrames++;
            }
            return;
        }

        long start = Stopwatch.GetTimestamp();
        computeShader.SetBuffer(clearKernel, "_SensorBev", gpuBevBuffers[slot]);
        computeShader.Dispatch(
            clearKernel,
            Mathf.CeilToInt(cellCount / (float)ThreadGroupSize),
            1,
            1);
        computeShader.SetBuffer(bevKernel, "_SensorBev", gpuBevBuffers[slot]);
        computeShader.Dispatch(
            bevKernel,
            Mathf.CeilToInt(pointCount / (float)ThreadGroupSize),
            1,
            1);
        readbackInFlight[slot] = true;
        readbackMeasured[slot] = measuring;
        readbackIssueTimes[slot] = now;
        readbackRequests[slot] = AsyncGPUReadback.Request(
            gpuBevBuffers[slot],
            readbackCallbacks[slot]);
        float elapsedMilliseconds = ElapsedMilliseconds(start);

        if (measuring)
        {
            issuedFrames++;
            hostWorkMilliseconds.Add(elapsedMilliseconds);
        }
    }

    private int FindAvailableReadbackSlot()
    {
        for (int i = 0; i < readbackInFlight.Length; i++)
        {
            if (!readbackInFlight[i] &&
                Time.frameCount >= readbackReusableAfterFrame[i])
            {
                return i;
            }
        }
        return -1;
    }

    private void PollReadbacks(double now)
    {
        // Completion is handled by one preallocated callback per ring slot. Polling can miss
        // Unity's one-frame result window when explicit camera renders dominate the frame.
    }

    private void CompleteReadback(int slot, AsyncGPUReadbackRequest request)
    {
        readbackInFlight[slot] = false;
        readbackReusableAfterFrame[slot] = Time.frameCount + 2;
        if (!readbackMeasured[slot])
        {
            return;
        }
        if (request.hasError)
        {
            readbackErrors++;
            return;
        }

        completedFrames++;
        completionLatencyMilliseconds.Add(
            (float)Math.Max(
                0.0,
                (Time.realtimeSinceStartupAsDouble - readbackIssueTimes[slot]) * 1000.0));
        NativeArray<uint> completedBev = request.GetData<uint>();
        if (!gpuValidationCompleted)
        {
            ValidateReadback(completedBev);
            gpuValidationCompleted = true;
        }
        int checksumIndex = (completedFrames * 7919) % cellCount;
        checksum += completedBev[checksumIndex];
    }

    private void ValidateReadback(NativeArray<uint> actual)
    {
        uint localMaximum = 0u;
        for (int index = 0; index < cellCount; index++)
        {
            uint expected = expectedBev[index];
            uint observed = actual[index];
            uint difference = observed >= expected
                ? observed - expected
                : expected - observed;
            localMaximum = Math.Max(localMaximum, difference);
        }
        maximumCellError = Math.Max(maximumCellError, localMaximum);
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
                int cellX = (int)Math.Floor((pointX - MinimumX) * InverseCellSize);
                int cellZ = (int)Math.Floor((depth - MinimumZ) * InverseCellSize);
                if (cellX >= 0 && cellX < Resolution &&
                    cellZ >= 0 && cellZ < Resolution)
                {
                    int cellIndex = cellZ * Resolution + cellX;
                    Bev[cellIndex] = Bev[cellIndex] + 1u;
                }
            }
        }
    }
}
