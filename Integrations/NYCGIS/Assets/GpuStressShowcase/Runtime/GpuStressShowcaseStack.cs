using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Summit.GpuDeadlineScheduler;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuStressShowcaseStack : IDisposable
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private readonly GpuStressSensorWorkload sensor;
    private readonly GpuStressResidencyWorkload residency;
    private readonly GpuStressDeadlineWorkload deadline;
    private readonly int issueIntervalFrames;
    private bool collecting;
    private int scheduledIssueCount;
    private ulong issueFrameSequenceHash = FnvOffset;
    private ulong logicalStateSequenceHash = FnvOffset;
    private ulong logicalStateMask;
    private bool disposed;

    public GpuStressShowcaseStack(
        GpuStressShowcaseConfiguration configuration,
        GpuStressTuningDecision tuning)
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }
        if (tuning == null)
        {
            throw new ArgumentNullException(nameof(tuning));
        }

        bool baseline = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline;
        issueIntervalFrames = Math.Max(
            1, configuration.WorkloadIssueIntervalFrames);
        sensor = new GpuStressSensorWorkload(
            configuration.SensorElementCount,
            configuration.SensorCount,
            configuration.QueriesPerSensor,
            unchecked((uint)configuration.Seed),
            baseline ? GpuPrimitiveBackend.Portable : tuning.SensorBackend,
            baseline);
        try
        {
            residency = new GpuStressResidencyWorkload(
                configuration.ResidencyPointsPerPage,
                configuration.Seed,
                baseline);
            deadline = new GpuStressDeadlineWorkload(
                configuration.Seed,
                baseline);
        }
        catch
        {
            residency?.Dispose();
            sensor.Dispose();
            throw;
        }
    }

    public GpuStressSensorWorkload Sensor => sensor;
    public GpuStressResidencyWorkload Residency => residency;
    public GpuStressDeadlineWorkload Deadline => deadline;
    public int IssueIntervalFrames => issueIntervalFrames;
    public int LogicalStateCount =>
        GpuSensorDeterministicGenerator.StateCount;
    public int ScheduledIssueCount => scheduledIssueCount;
    public string IssueFrameSequenceHash =>
        issueFrameSequenceHash.ToString("X16", CultureInfo.InvariantCulture);
    public string LogicalStateSequenceHash =>
        logicalStateSequenceHash.ToString(
            "X16", CultureInfo.InvariantCulture);
    public int UniqueLogicalStateCount
    {
        get
        {
            return CountSetBits(logicalStateMask);
        }
    }

    public bool IsDrained
    {
        get
        {
            Poll();
            return !sensor.IsInFlight &&
                !residency.IsInFlight &&
                !deadline.IsInFlight;
        }
    }

    public void Tick(int logicalFrame)
    {
        ThrowIfDisposed();
        Poll();
        if ((logicalFrame % issueIntervalFrames) != 0)
        {
            return;
        }
        int issueOrdinal = logicalFrame / issueIntervalFrames;
        uint logicalState = unchecked(
            (uint)(issueOrdinal %
                GpuSensorDeterministicGenerator.StateCount));
        sensor.TryIssue(logicalState, logicalFrame);
        residency.TryIssue(issueOrdinal);
        deadline.TryIssue(logicalState);
        if (collecting)
        {
            scheduledIssueCount++;
            issueFrameSequenceHash = AppendHash(
                issueFrameSequenceHash,
                unchecked((uint)logicalFrame));
            logicalStateSequenceHash = AppendHash(
                logicalStateSequenceHash,
                logicalState);
            logicalStateMask |= 1UL << (int)logicalState;
        }
    }

    public void BeginMeasurement()
    {
        ThrowIfDisposed();
        scheduledIssueCount = 0;
        issueFrameSequenceHash = FnvOffset;
        logicalStateSequenceHash = FnvOffset;
        logicalStateMask = 0UL;
        sensor.BeginMeasurement();
        residency.BeginMeasurement();
        deadline.BeginMeasurement();
        collecting = true;
    }

    public void EndMeasurement()
    {
        collecting = false;
        sensor.EndMeasurement();
        residency.EndMeasurement();
        deadline.EndMeasurement();
    }

    public void Poll()
    {
        if (disposed)
        {
            return;
        }
        sensor.Poll();
        residency.Poll();
        deadline.Poll();
    }

    public bool TryIssueValidation(
        uint logicalState,
        int pathFrame)
    {
        ThrowIfDisposed();
        Poll();
        if (!IsDrained)
        {
            return false;
        }
        sensor.TryIssue(logicalState, pathFrame);
        residency.TryIssue(pathFrame);
        deadline.TryIssue(logicalState);
        return true;
    }

    public GpuStressValidationSnapshot CaptureValidation(
        uint logicalState)
    {
        ThrowIfDisposed();
        if (!IsDrained)
        {
            throw new InvalidOperationException(
                "GPU showcase validation requires a drained stack.");
        }
        string sensorHash = sensor.CaptureDigestHash(logicalState);
        GpuStressResidencyValidation residencyResult =
            residency.CaptureValidation();
        GpuStressDeadlineValidation deadlineResult =
            deadline.CaptureValidation(logicalState);
        bool passed = !string.IsNullOrWhiteSpace(sensorHash) &&
            residencyResult.Passed && deadlineResult.Passed;
        return new GpuStressValidationSnapshot(
            passed,
            passed
                ? "Sensor, residency, and deadline digests match their deterministic contracts."
                : "One or more GPU workload validation contracts failed.",
            sensorHash,
            residencyResult.Hash,
            deadlineResult.Hash,
            residencyResult.Message,
            deadlineResult.Message);
    }

    private static ulong AppendHash(ulong hash, uint value)
    {
        unchecked
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    private static int CountSetBits(ulong value)
    {
        int count = 0;
        while (value != 0UL)
        {
            value &= value - 1UL;
            count++;
        }
        return count;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        deadline?.Dispose();
        residency?.Dispose();
        sensor?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuStressShowcaseStack));
        }
    }
}

internal sealed class GpuStressSensorWorkload : IDisposable
{
    private readonly int elementCount;
    private readonly int sensorCount;
    private readonly int queriesPerSensor;
    private readonly uint seed;
    private readonly bool baseline;
    private readonly GpuSensorPipeline pipeline;
    private readonly GpuSensorSample[] samples;
    private readonly uint[] keys;
    private readonly CommandBuffer commands;
    private readonly List<float> cpuProducerSamples =
        new List<float>(1024);
    private GraphicsFence fence;
    private bool inFlight;
    private bool collecting;
    private int submittedFrame;
    private int lastCompletedFrame = -1;
    private bool disposed;

    public GpuStressSensorWorkload(
        int elementCount,
        int sensorCount,
        int queriesPerSensor,
        uint seed,
        GpuPrimitiveBackend backend,
        bool baseline)
    {
        this.elementCount = elementCount;
        this.sensorCount = sensorCount;
        this.queriesPerSensor = queriesPerSensor;
        this.seed = seed;
        this.baseline = baseline;
        int queryCount = checked(sensorCount * queriesPerSensor);
        pipeline = new GpuSensorPipeline(
            elementCount,
            queryCount,
            backend,
            emitProfilerMarkers: false);
        uint[] stableIds = new uint[elementCount];
        for (int index = 0; index < stableIds.Length; index++)
        {
            stableIds[index] = unchecked((uint)index);
        }
        pipeline.SetStableIds(stableIds);
        pipeline.SetQueries(BuildQueries(queryCount, seed));
        if (baseline)
        {
            samples = new GpuSensorSample[elementCount];
            keys = new uint[elementCount];
        }
        commands = new CommandBuffer
        {
            name = baseline
                ? "GPU.StressShowcase/A/CPUUploadPerSensorRebuild"
                : "GPU.StressShowcase/B/GpuProducerSharedCSR"
        };
        Backend = backend;
    }

    public GpuPrimitiveBackend Backend { get; }
    public bool IsInFlight => inFlight;
    public int Submitted { get; private set; }
    public int Dropped { get; private set; }
    public long LogicalUploadBytes { get; private set; }
    public int IndexBuildsPerUpdate => baseline ? sensorCount : 1;
    public int DataAgeFrames { get; private set; }
    public double CpuProducerAverageMs =>
        GpuStressShowcaseMath.Average(cpuProducerSamples);

    public void BeginMeasurement()
    {
        Submitted = 0;
        Dropped = 0;
        LogicalUploadBytes = 0L;
        DataAgeFrames = 0;
        cpuProducerSamples.Clear();
        collecting = true;
    }

    public void EndMeasurement()
    {
        collecting = false;
    }

    public bool TryIssue(uint logicalState, int logicalFrame)
    {
        ThrowIfDisposed();
        Poll();
        if (inFlight)
        {
            if (collecting)
            {
                Dropped++;
                DataAgeFrames = lastCompletedFrame < 0
                    ? logicalFrame + 1
                    : logicalFrame - lastCompletedFrame;
            }
            return false;
        }

        commands.Clear();
        long producerStart = Stopwatch.GetTimestamp();
        if (baseline)
        {
            GpuSensorDeterministicGenerator.Populate(
                samples,
                keys,
                elementCount,
                seed,
                logicalState);
            if (collecting)
            {
                cpuProducerSamples.Add((float)ElapsedMilliseconds(
                    producerStart));
            }
            pipeline.RecordCpuProducedRebuiltPerSensor(
                commands,
                samples,
                keys,
                elementCount,
                sensorCount,
                queriesPerSensor,
                logicalState);
        }
        else
        {
            pipeline.RecordGpuProducedSharedSensorIndex(
                commands,
                seed,
                logicalState,
                elementCount,
                sensorCount,
                queriesPerSensor);
        }
        fence = commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
        Graphics.ExecuteCommandBuffer(commands);
        inFlight = true;
        submittedFrame = logicalFrame;
        if (collecting)
        {
            Submitted++;
            if (baseline)
            {
                LogicalUploadBytes = checked(
                    LogicalUploadBytes +
                    (long)elementCount *
                    (GpuSensorPipeline.SampleStride +
                     GpuSensorPipeline.UintStride));
            }
        }
        return true;
    }

    public void Poll()
    {
        if (!inFlight || !fence.passed)
        {
            return;
        }
        inFlight = false;
        lastCompletedFrame = submittedFrame;
        DataAgeFrames = 0;
    }

    public string CaptureDigestHash(uint logicalState)
    {
        ThrowIfDisposed();
        if (inFlight)
        {
            throw new InvalidOperationException(
                "Sensor digest cannot be read while work is in flight.");
        }
        var digest = new GpuSensorQueryDigest[1];
        pipeline.FrameDigest.GetData(
            digest,
            0,
            checked((int)logicalState),
            1);
        GpuSensorQueryDigest value = digest[0];
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:X8}{1:X8}{2:X8}{3:X8}",
            value.Count,
            value.XorHash,
            value.SumHash0,
            value.SumHash1);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        commands.Dispose();
        pipeline.Dispose();
    }

    private static GpuSensorRangeQuery[] BuildQueries(
        int count,
        uint seed)
    {
        var result = new GpuSensorRangeQuery[count];
        for (int index = 0; index < count; index++)
        {
            uint ordinal = unchecked((uint)index);
            uint x = GpuSensorDeterministicGenerator.Mix(
                seed ^ ordinal ^ 0xB5297A4Du) &
                GpuSensorDeterministicGenerator.CoordinateMask;
            uint y = GpuSensorDeterministicGenerator.Mix(
                seed ^ ordinal ^ 0x68E31DA4u) &
                GpuSensorDeterministicGenerator.CoordinateMask;
            uint z = GpuSensorDeterministicGenerator.Mix(
                seed ^ ordinal ^ 0x1B56C4E9u) &
                GpuSensorDeterministicGenerator.CoordinateMask;
            result[index] = new GpuSensorRangeQuery(x, y, z, 2047u);
        }
        return result;
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
                nameof(GpuStressSensorWorkload));
        }
    }
}

internal sealed class GpuStressResidencyWorkload : IDisposable
{
    private const int VirtualPageCount = 64 * 64;
    private const int PhysicalSlotCount = 512;
    private readonly GpuResidencyBenchmarkAdapter adapter;
    private readonly GpuResidencyBenchmarkVariant variant;
    private GraphicsFence fence;
    private bool inFlight;
    private bool collecting;
    private bool disposed;
    private long hitCount;
    private long requestCount;

    public GpuStressResidencyWorkload(
        int pointsPerPage,
        int seed,
        bool baseline)
    {
        adapter = new GpuResidencyBenchmarkAdapter(
            VirtualPageCount,
            PhysicalSlotCount,
            pointsPerPage,
            seed);
        variant = baseline
            ? GpuResidencyBenchmarkVariant.RebuildVisibleSet
            : GpuResidencyBenchmarkVariant.PersistentLru;
        adapter.Reset(variant);
        PointsPerPage = pointsPerPage;
    }

    public int PointsPerPage { get; }
    public bool IsInFlight => inFlight;
    public int Submitted { get; private set; }
    public int Dropped { get; private set; }
    public long LogicalUploadBytes { get; private set; }
    public int LastUploadPages { get; private set; }
    public double HitRate => requestCount > 0
        ? hitCount / (double)requestCount
        : 0.0;
    public double PlanningAverageMs { get; private set; }
    public double StagingAverageMs { get; private set; }
    private double planningTotal;
    private double stagingTotal;

    public void BeginMeasurement()
    {
        Submitted = 0;
        Dropped = 0;
        LogicalUploadBytes = 0L;
        LastUploadPages = 0;
        hitCount = 0L;
        requestCount = 0L;
        planningTotal = 0.0;
        stagingTotal = 0.0;
        PlanningAverageMs = 0.0;
        StagingAverageMs = 0.0;
        collecting = true;
    }

    public void EndMeasurement()
    {
        collecting = false;
    }

    public bool TryIssue(int pathFrame)
    {
        ThrowIfDisposed();
        Poll();
        if (inFlight)
        {
            if (collecting)
            {
                Dropped++;
            }
            return false;
        }
        GpuResidencyFramePreparation preparation =
            adapter.PrepareFrame(variant, pathFrame);
        CommandBuffer commands = adapter.FrameCommands;
        commands.Clear();
        commands.name = variant ==
            GpuResidencyBenchmarkVariant.RebuildVisibleSet
                ? "GPU.StressShowcase/A/RebuildVisiblePages"
                : "GPU.StressShowcase/B/PersistentLruDelta";
        adapter.RecordFrame(preparation);
        fence = commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
        Graphics.ExecuteCommandBuffer(commands);
        inFlight = true;
        LastUploadPages = preparation.Plan.UploadCount;
        if (collecting)
        {
            Submitted++;
            LogicalUploadBytes = checked(
                LogicalUploadBytes + preparation.TotalUploadBytes);
            hitCount += preparation.Plan.HitCount;
            requestCount += preparation.Plan.RequestedPages.Length;
            planningTotal += preparation.PlanningMs;
            stagingTotal += preparation.StagingMs;
            PlanningAverageMs = planningTotal / Submitted;
            StagingAverageMs = stagingTotal / Submitted;
        }
        return true;
    }

    public void Poll()
    {
        if (inFlight && fence.passed)
        {
            inFlight = false;
        }
    }

    public GpuStressResidencyValidation CaptureValidation()
    {
        ThrowIfDisposed();
        GpuResidencyValidationResult result =
            adapter.ValidateLastFrame();
        return new GpuStressResidencyValidation(
            result.Passed,
            result.ResultHash,
            result.Message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        adapter.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuStressResidencyWorkload));
        }
    }
}

internal sealed class GpuStressDeadlineWorkload : IDisposable
{
    private readonly GpuDeadlineSchedulerBenchmarkAdapter adapter;
    private readonly GpuDeadlineSchedulerBenchmarkVariant variant;
    private readonly List<float> criticalLatencySamples =
        new List<float>(1024);
    private GpuDeadlineBenchmarkSubmission submission;
    private bool[] observed;
    private long releaseTicks;
    private uint submittedState;
    private bool collecting;
    private bool disposed;

    public GpuStressDeadlineWorkload(int seed, bool baseline)
    {
        adapter = new GpuDeadlineSchedulerBenchmarkAdapter(
            workItems: 262144,
            criticalIterations: 1024,
            normalIterations: 2048,
            backgroundIterations: 4096,
            pressureItems: 524288,
            pressureIterations: 2048,
            criticalDeadlineMicroseconds: 8000,
            normalDeadlineMicroseconds: 16000,
            backgroundDeadlineMicroseconds: 50000,
            seed: seed);
        variant = baseline
            ? GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics
            : GpuDeadlineSchedulerBenchmarkVariant.DeadlineAwareAdaptive;
        PolicyName = baseline
            ? "FIFO main graphics"
            : "least-slack main queue (AMD async calibration rejected)";
    }

    public bool IsInFlight => submission != null;
    public int Submitted { get; private set; }
    public int Dropped { get; private set; }
    public int CriticalLateObservations { get; private set; }
    public double LatestCriticalLatencyMs { get; private set; }
    public string PolicyName { get; }
    public double CriticalLatencyAverageMs =>
        GpuStressShowcaseMath.Average(criticalLatencySamples);
    public double CriticalLatencyP99Ms =>
        GpuStressShowcaseMath.Percentile(
            criticalLatencySamples,
            0.99);

    public void BeginMeasurement()
    {
        Submitted = 0;
        Dropped = 0;
        CriticalLateObservations = 0;
        LatestCriticalLatencyMs = 0.0;
        criticalLatencySamples.Clear();
        collecting = true;
    }

    public void EndMeasurement()
    {
        collecting = false;
    }

    public bool TryIssue(uint logicalState)
    {
        ThrowIfDisposed();
        Poll();
        if (submission != null)
        {
            if (collecting)
            {
                Dropped++;
            }
            return false;
        }
        releaseTicks = Stopwatch.GetTimestamp();
        submission = adapter.Submit(
            variant,
            logicalState,
            optimizedUsesAsync: false,
            recordTimestampBegin: null,
            recordTimestampEnd: null);
        observed = new bool[submission.JobFences.Length];
        submittedState = logicalState;
        if (collecting)
        {
            Submitted++;
        }
        return true;
    }

    public void Poll()
    {
        if (submission == null)
        {
            return;
        }
        for (int index = 0; index < submission.JobFences.Length; index++)
        {
            GpuDeadlineJobFence jobFence = submission.JobFences[index];
            if (observed[index] || !jobFence.Fence.passed)
            {
                continue;
            }
            observed[index] = true;
            double latency = ElapsedMilliseconds(releaseTicks);
            if (jobFence.Job.DeadlineClass == GpuDeadlineClass.Critical)
            {
                LatestCriticalLatencyMs = latency;
                if (collecting)
                {
                    criticalLatencySamples.Add((float)latency);
                    if (latency * 1000.0 >
                        jobFence.Job.RelativeDeadlineMicroseconds)
                    {
                        CriticalLateObservations++;
                    }
                }
            }
        }
        if (!submission.JoinFence.passed)
        {
            return;
        }
        submission = null;
        observed = null;
    }

    public GpuStressDeadlineValidation CaptureValidation(
        uint logicalState)
    {
        ThrowIfDisposed();
        if (submission != null)
        {
            throw new InvalidOperationException(
                "Deadline digest cannot be read while work is in flight.");
        }
        if (submittedState != logicalState)
        {
            return new GpuStressDeadlineValidation(
                false,
                string.Empty,
                "Final deadline logical state does not match validation state.");
        }
        GpuDeadlineValidationResult result =
            adapter.ValidateDigests(logicalState);
        return new GpuStressDeadlineValidation(
            result.Passed,
            result.ResultHash,
            result.Message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        adapter.Dispose();
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
                nameof(GpuStressDeadlineWorkload));
        }
    }
}

internal readonly struct GpuStressResidencyValidation
{
    public GpuStressResidencyValidation(
        bool passed,
        string hash,
        string message)
    {
        Passed = passed;
        Hash = hash ?? string.Empty;
        Message = message ?? string.Empty;
    }

    public bool Passed { get; }
    public string Hash { get; }
    public string Message { get; }
}

internal readonly struct GpuStressDeadlineValidation
{
    public GpuStressDeadlineValidation(
        bool passed,
        string hash,
        string message)
    {
        Passed = passed;
        Hash = hash ?? string.Empty;
        Message = message ?? string.Empty;
    }

    public bool Passed { get; }
    public string Hash { get; }
    public string Message { get; }
}

internal readonly struct GpuStressValidationSnapshot
{
    public GpuStressValidationSnapshot(
        bool passed,
        string message,
        string sensorHash,
        string residencyHash,
        string deadlineHash,
        string residencyMessage,
        string deadlineMessage)
    {
        Passed = passed;
        Message = message ?? string.Empty;
        SensorHash = sensorHash ?? string.Empty;
        ResidencyHash = residencyHash ?? string.Empty;
        DeadlineHash = deadlineHash ?? string.Empty;
        ResidencyMessage = residencyMessage ?? string.Empty;
        DeadlineMessage = deadlineMessage ?? string.Empty;
    }

    public bool Passed { get; }
    public string Message { get; }
    public string SensorHash { get; }
    public string ResidencyHash { get; }
    public string DeadlineHash { get; }
    public string ResidencyMessage { get; }
    public string DeadlineMessage { get; }
}
