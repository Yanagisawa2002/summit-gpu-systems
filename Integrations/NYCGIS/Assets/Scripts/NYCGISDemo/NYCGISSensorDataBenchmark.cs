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

public enum NYCGISSensorDataMode
{
    Off,
    Cpu,
    Gpu,
    CpuBev,
    GpuBev,
    CpuBevCost,
    GpuBevResident,
    CpuSpatialIndex,
    GpuSpatialBrute,
    GpuSpatialIndex
}

/// <summary>
/// Deterministic depth-to-point-cloud workload used by the full-city standalone benchmark.
/// CPU and GPU modes consume identical depth samples and produce identical float4 point records.
/// </summary>
public sealed class NYCGISSensorDataBenchmark : INYCGISSensorBenchmark
{
    private const int ReadbackRingSize = 3;
    private const int ComputeThreadGroupSize = 256;
    private const float CorrectnessTolerance = 0.0005f;
    private const string ComputeResourcePath = "NYCGISDemo/NYCGISSensorDepthToPointCloud";

    private readonly NYCGISSensorDataMode mode;
    private readonly int width;
    private readonly int height;
    private readonly int pointCount;
    private readonly float rateHz;
    private readonly float fx;
    private readonly float fy;
    private readonly float cx;
    private readonly float cy;
    private readonly double issuePeriodSeconds;
    private readonly List<float> hostWorkMilliseconds;
    private readonly List<float> completionLatencyMilliseconds;

    private NativeArray<float> depthSamples;
    private NativeArray<float4> cpuPoints;
    private Texture2D depthTexture;
    private ComputeShader computeShader;
    private int computeKernel = -1;
    private GraphicsBuffer[] gpuPointBuffers;
    private NativeArray<float4>[] readbackPoints;
    private AsyncGPUReadbackRequest[] readbackRequests;
    private bool[] readbackInFlight;
    private bool[] readbackMeasured;
    private double[] readbackIssueTimes;
    private int[] readbackReusableAfterFrame;

    private bool acceptingIssues;
    private bool measuring;
    private bool disposed;
    private double nextIssueTime;
    private int issuedFrames;
    private int completedFrames;
    private int droppedFrames;
    private int readbackErrors;
    private float maximumPointError;
    private double checksum;

    public NYCGISSensorDataBenchmark(
        NYCGISSensorDataMode mode,
        int width,
        int height,
        float rateHz,
        int measurementCapacity)
    {
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

        if (mode == NYCGISSensorDataMode.Off)
        {
            return;
        }

        depthSamples = new NativeArray<float>(
            pointCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        PopulateDeterministicDepth();

        if (mode == NYCGISSensorDataMode.Cpu)
        {
            cpuPoints = new NativeArray<float4>(
                pointCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            return;
        }

        InitializeGpuResources();
    }

    public NYCGISSensorDataMode Mode => mode;

    public string ModeName => mode.ToString().ToLowerInvariant();

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
        mode == NYCGISSensorDataMode.Off ||
        (completedFrames > 0 &&
         readbackErrors == 0 &&
         (mode != NYCGISSensorDataMode.Gpu || maximumPointError <= CorrectnessTolerance));

    public static NYCGISSensorDataMode ParseMode(string value)
    {
        if (string.Equals(value, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.Cpu;
        }
        if (string.Equals(value, "gpu", StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.Gpu;
        }
        if (string.Equals(value, "cpu-bev", StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.CpuBev;
        }
        if (string.Equals(value, "gpu-bev", StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.GpuBev;
        }
        if (string.Equals(value, "cpu-bev-cost", StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.CpuBevCost;
        }
        if (string.Equals(value, "gpu-bev-resident", StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.GpuBevResident;
        }
        if (string.Equals(
                value,
                "cpu-spatial-index",
                StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.CpuSpatialIndex;
        }
        if (string.Equals(
                value,
                "gpu-spatial-brute",
                StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.GpuSpatialBrute;
        }
        if (string.Equals(
                value,
                "gpu-spatial-index",
                StringComparison.OrdinalIgnoreCase))
        {
            return NYCGISSensorDataMode.GpuSpatialIndex;
        }
        return NYCGISSensorDataMode.Off;
    }

    public void BeginWarmup(double now)
    {
        measuring = false;
        acceptingIssues = mode != NYCGISSensorDataMode.Off;
        nextIssueTime = now;
    }

    public void BeginMeasurement(double now)
    {
        issuedFrames = 0;
        completedFrames = 0;
        droppedFrames = 0;
        readbackErrors = 0;
        maximumPointError = 0.0f;
        checksum = 0.0;
        hostWorkMilliseconds.Clear();
        completionLatencyMilliseconds.Clear();
        measuring = true;
        acceptingIssues = mode != NYCGISSensorDataMode.Off;
        nextIssueTime = now;
    }

    public void Tick(double now)
    {
        PollReadbacks(now);
        if (!acceptingIssues || mode == NYCGISSensorDataMode.Off || now + 1.0e-9 < nextIssueTime)
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

        if (mode == NYCGISSensorDataMode.Cpu)
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
        long outputBytesPerFrame = (long)pointCount * 16L;
        double millionPointsPerSecond =
            completedFrames * (double)pointCount / elapsed / 1_000_000.0;
        double outputGiBPerSecond =
            completedFrames * (double)outputBytesPerFrame / elapsed / (1024.0 * 1024.0 * 1024.0);

        report.AppendLine($"sensorMode={ModeName}");
        report.AppendLine($"sensorWidth={width}");
        report.AppendLine($"sensorHeight={height}");
        report.AppendLine($"sensorRateHz={rateHz:F1}");
        report.AppendLine($"sensorPointCount={pointCount}");
        report.AppendLine($"sensorOutputBytesPerFrame={outputBytesPerFrame}");
        report.AppendLine($"sensorReadbackRingSize={(mode == NYCGISSensorDataMode.Gpu ? ReadbackRingSize : 0)}");
        report.AppendLine($"sensorIssuedFrames={issuedFrames}");
        report.AppendLine($"sensorCompletedFrames={completedFrames}");
        report.AppendLine($"sensorDroppedFrames={droppedFrames}");
        report.AppendLine($"sensorReadbackErrors={readbackErrors}");
        report.AppendLine($"sensorMillionPointsPerSecond={millionPointsPerSecond:F3}");
        report.AppendLine($"sensorOutputGiBPerSecond={outputGiBPerSecond:F3}");
        report.AppendLine($"sensorMaximumPointError={maximumPointError:F8}");
        report.AppendLine($"sensorCorrectnessTolerance={CorrectnessTolerance:F8}");
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

        if (gpuPointBuffers != null)
        {
            for (int i = 0; i < gpuPointBuffers.Length; i++)
            {
                gpuPointBuffers[i]?.Dispose();
            }
        }
        if (readbackPoints != null)
        {
            for (int i = 0; i < readbackPoints.Length; i++)
            {
                if (readbackPoints[i].IsCreated)
                {
                    readbackPoints[i].Dispose();
                }
            }
        }
        if (cpuPoints.IsCreated)
        {
            cpuPoints.Dispose();
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

    private void InitializeGpuResources()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
        {
            throw new NotSupportedException(
                "GPU sensor benchmark requires compute shaders and AsyncGPUReadback.");
        }

        computeShader = Resources.Load<ComputeShader>(ComputeResourcePath);
        if (computeShader == null)
        {
            throw new InvalidOperationException(
                "GPU sensor benchmark compute shader is missing at Resources/" +
                ComputeResourcePath + ".");
        }
        computeKernel = computeShader.FindKernel("DepthToPointCloud");

        depthTexture = new Texture2D(width, height, TextureFormat.RFloat, false, true)
        {
            name = "NYCGIS Sensor Benchmark Depth",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        depthTexture.SetPixelData(depthSamples, 0);
        depthTexture.Apply(false, true);

        computeShader.SetTexture(computeKernel, "_SensorDepth", depthTexture);
        computeShader.SetInt("_SensorWidth", width);
        computeShader.SetInt("_SensorPointCount", pointCount);
        computeShader.SetVector("_SensorIntrinsics", new Vector4(fx, fy, cx, cy));

        gpuPointBuffers = new GraphicsBuffer[ReadbackRingSize];
        readbackPoints = new NativeArray<float4>[ReadbackRingSize];
        readbackRequests = new AsyncGPUReadbackRequest[ReadbackRingSize];
        readbackInFlight = new bool[ReadbackRingSize];
        readbackMeasured = new bool[ReadbackRingSize];
        readbackIssueTimes = new double[ReadbackRingSize];
        readbackReusableAfterFrame = new int[ReadbackRingSize];
        for (int i = 0; i < ReadbackRingSize; i++)
        {
            gpuPointBuffers[i] = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                pointCount,
                16);
            readbackPoints[i] = new NativeArray<float4>(
                pointCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }
    }

    private void IssueCpuWork()
    {
        long start = Stopwatch.GetTimestamp();
        DepthToPointCloudJob job = new DepthToPointCloudJob
        {
            Depth = depthSamples,
            Points = cpuPoints,
            Width = width,
            Fx = fx,
            Fy = fy,
            Cx = cx,
            Cy = cy
        };
        job.Schedule(pointCount, ComputeThreadGroupSize).Complete();
        float elapsedMilliseconds = ElapsedMilliseconds(start);

        if (!measuring)
        {
            return;
        }

        issuedFrames++;
        completedFrames++;
        hostWorkMilliseconds.Add(elapsedMilliseconds);
        completionLatencyMilliseconds.Add(elapsedMilliseconds);
        int checksumIndex = (issuedFrames * 7919) % pointCount;
        checksum += cpuPoints[checksumIndex].z;
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
        computeShader.SetBuffer(computeKernel, "_SensorPoints", gpuPointBuffers[slot]);
        computeShader.Dispatch(
            computeKernel,
            Mathf.CeilToInt(pointCount / (float)ComputeThreadGroupSize),
            1,
            1);
        readbackRequests[slot] = AsyncGPUReadback.RequestIntoNativeArray(
            ref readbackPoints[slot],
            gpuPointBuffers[slot]);
        readbackInFlight[slot] = true;
        readbackMeasured[slot] = measuring;
        readbackIssueTimes[slot] = now;
        float elapsedMilliseconds = ElapsedMilliseconds(start);

        if (measuring)
        {
            issuedFrames++;
            hostWorkMilliseconds.Add(elapsedMilliseconds);
        }
    }

    private int FindAvailableReadbackSlot()
    {
        for (int i = 0; i < ReadbackInFlightLength(); i++)
        {
            if (!readbackInFlight[i] &&
                Time.frameCount >= readbackReusableAfterFrame[i])
            {
                return i;
            }
        }
        return -1;
    }

    private int ReadbackInFlightLength()
    {
        return readbackInFlight != null ? readbackInFlight.Length : 0;
    }

    private void PollReadbacks(double now)
    {
        for (int slot = 0; slot < ReadbackInFlightLength(); slot++)
        {
            if (!readbackInFlight[slot] || !readbackRequests[slot].done)
            {
                continue;
            }

            readbackInFlight[slot] = false;
            readbackReusableAfterFrame[slot] = Time.frameCount + 2;
            if (!readbackMeasured[slot])
            {
                continue;
            }

            if (readbackRequests[slot].hasError)
            {
                readbackErrors++;
                continue;
            }

            completedFrames++;
            completionLatencyMilliseconds.Add(
                (float)Math.Max(0.0, (now - readbackIssueTimes[slot]) * 1000.0));
            ValidateReadback(readbackPoints[slot], completedFrames);
        }
    }

    private void ValidateReadback(NativeArray<float4> points, int completionOrdinal)
    {
        int sampleCount = Math.Min(2048, pointCount);
        int stride = Math.Max(1, pointCount / sampleCount);
        float localMaximumError = 0.0f;
        for (int index = 0; index < pointCount; index += stride)
        {
            float depth = depthSamples[index];
            int x = index % width;
            int y = index / width;
            float4 expected = new float4(
                (x - cx) * depth / fx,
                (y - cy) * depth / fy,
                depth,
                1.0f);
            float4 actual = points[index];
            localMaximumError = math.max(
                localMaximumError,
                math.cmax(math.abs(actual - expected)));
        }
        maximumPointError = Mathf.Max(maximumPointError, localMaximumError);
        int checksumIndex = (completionOrdinal * 7919) % pointCount;
        checksum += points[checksumIndex].z;
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
    private struct DepthToPointCloudJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Depth;
        [WriteOnly] public NativeArray<float4> Points;
        public int Width;
        public float Fx;
        public float Fy;
        public float Cx;
        public float Cy;

        public void Execute(int index)
        {
            int x = index % Width;
            int y = index / Width;
            float depth = Depth[index];
            Points[index] = new float4(
                (x - Cx) * depth / Fx,
                (y - Cy) * depth / Fy,
                depth,
                1.0f);
        }
    }
}
