using System;
using System.Collections.Generic;
using System.Linq;
using Summit.GpuDeadlineScheduler;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuDeadlineSchedulerBenchmarkAdapter : IDisposable
{
    private const int EstimatedMixesPerMicrosecond = 262144;
    public const string FifoCaseId =
        "deadline-scheduler/fifo-main-graphics-v1";
    public const string AsyncCaseId =
        "deadline-scheduler/least-slack-split-queue-v2";
    public const string DeadlineMainCaseId =
        "deadline-scheduler/least-slack-main-queue-v3";

    private readonly GpuDeadlineWorkload workload;
    private readonly GpuDeadlineJob[] jobs;
    private readonly CommandBuffer mainCommands;
    private readonly CommandBuffer copyCommands;
    private readonly CommandBuffer pressureCommands;
    private readonly CommandBuffer urgentCommands;
    private readonly CommandBuffer joinCommands;
    private readonly int pressureItems;
    private readonly int pressureIterations;
    private bool disposed;

    public GpuDeadlineSchedulerBenchmarkAdapter(
        int workItems,
        int criticalIterations,
        int normalIterations,
        int backgroundIterations,
        int pressureItems,
        int pressureIterations,
        int criticalDeadlineMicroseconds,
        int normalDeadlineMicroseconds,
        int backgroundDeadlineMicroseconds,
        int seed)
    {
        if (workItems < 256 || workItems > 1048576)
        {
            throw new ArgumentOutOfRangeException(nameof(workItems));
        }
        if (criticalIterations < 1 || normalIterations < 1 ||
            backgroundIterations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(criticalIterations));
        }
        if (pressureItems < 256 || pressureItems > 1048576)
        {
            throw new ArgumentOutOfRangeException(nameof(pressureItems));
        }
        if (pressureIterations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pressureIterations));
        }

        this.pressureItems = pressureItems;
        this.pressureIterations = pressureIterations;
        uint baseSeed = unchecked((uint)seed);
        jobs = new[]
        {
            CreateJob(
                100,
                0,
                GpuDeadlineClass.Background,
                backgroundDeadlineMicroseconds,
                backgroundIterations,
                workItems,
                baseSeed ^ 0xA1000001u),
            CreateJob(
                200,
                1,
                GpuDeadlineClass.Normal,
                normalDeadlineMicroseconds,
                normalIterations,
                workItems,
                baseSeed ^ 0xB2000002u),
            CreateJob(
                101,
                2,
                GpuDeadlineClass.Background,
                backgroundDeadlineMicroseconds,
                backgroundIterations,
                workItems,
                baseSeed ^ 0xA1000003u),
            CreateJob(
                300,
                3,
                GpuDeadlineClass.Critical,
                criticalDeadlineMicroseconds,
                criticalIterations,
                workItems,
                baseSeed ^ 0xC3000004u)
        };

        GpuDeadlineWorkload selectedWorkload = null;
        CommandBuffer selectedMain = null;
        CommandBuffer selectedCopy = null;
        CommandBuffer selectedPressure = null;
        CommandBuffer selectedUrgent = null;
        CommandBuffer selectedJoin = null;
        try
        {
            selectedWorkload = new GpuDeadlineWorkload(
                jobs.Length,
                workItems,
                pressureItems);
            selectedMain = CreateCommands("FIFO/MainGraphics");
            selectedCopy = CreateCommands("Async/CopyOnMainGraphics");
            selectedPressure = CreateCommands("Async/GraphicsPressure");
            selectedUrgent = CreateAsyncCommands("Async/Urgent");
            selectedJoin = CreateCommands("Async/JoinOnMainGraphics");
        }
        catch
        {
            selectedMain?.Dispose();
            selectedCopy?.Dispose();
            selectedPressure?.Dispose();
            selectedUrgent?.Dispose();
            selectedJoin?.Dispose();
            selectedWorkload?.Dispose();
            throw;
        }

        workload = selectedWorkload;
        mainCommands = selectedMain;
        copyCommands = selectedCopy;
        pressureCommands = selectedPressure;
        urgentCommands = selectedUrgent;
        joinCommands = selectedJoin;
    }

    public int JobCount => jobs.Length;

    public long ResidentBytes => workload.ResidentBytes;

    public bool SupportsAsyncCompute => SystemInfo.supportsAsyncCompute;

    public bool DedicatedCopyQueueClaim => false;

    public IReadOnlyList<GpuDeadlineJob> Jobs => jobs;

    public string VariantName(
        GpuDeadlineSchedulerBenchmarkVariant variant,
        bool optimizedUsesAsync)
    {
        return variant ==
            GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics
            ? "fifo-main-graphics"
            : optimizedUsesAsync
                ? "least-slack-async"
                : "least-slack-main";
    }

    public string CaseId(
        GpuDeadlineSchedulerBenchmarkVariant variant,
        bool optimizedUsesAsync)
    {
        return variant ==
            GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics
            ? FifoCaseId
            : optimizedUsesAsync
                ? AsyncCaseId
                : DeadlineMainCaseId;
    }

    public GpuDeadlineBenchmarkSubmission Submit(
        GpuDeadlineSchedulerBenchmarkVariant variant,
        uint logicalState,
        bool optimizedUsesAsync,
        Action<CommandBuffer> recordTimestampBegin,
        Action<CommandBuffer> recordTimestampEnd)
    {
        ThrowIfDisposed();
        GpuDeadlinePolicy policy = variant ==
            GpuDeadlineSchedulerBenchmarkVariant.FifoGraphics
            ? GpuDeadlinePolicy.FifoGraphics
            : GpuDeadlinePolicy.LeastSlackAsync;
        IReadOnlyList<GpuDeadlineDispatch> plan =
            GpuDeadlinePlanner.BuildPlan(
                jobs,
                policy,
                SupportsAsyncCompute);

        if (policy == GpuDeadlinePolicy.FifoGraphics)
        {
            return SubmitOnMainGraphics(
                plan,
                logicalState,
                recordTimestampBegin,
                recordTimestampEnd);
        }
        return optimizedUsesAsync && SupportsAsyncCompute
            ? SubmitDeadlineAwareAsync(
                plan,
                logicalState,
                recordTimestampBegin,
                recordTimestampEnd)
            : SubmitDeadlineAwareOnMain(
                plan,
                logicalState,
                recordTimestampBegin,
                recordTimestampEnd);
    }

    public GpuDeadlineValidationResult ValidateDigests(uint logicalState)
    {
        ThrowIfDisposed();
        uint hash = 2166136261u;
        for (int slot = 0; slot < jobs.Length; slot++)
        {
            var actual = new uint[4];
            workload.GetJobDigestBuffer(slot).GetData(actual);
            uint[] expected = GpuDeadlineWorkload.ExpectedDigest(
                jobs[slot],
                logicalState);
            for (int word = 0; word < expected.Length; word++)
            {
                if (actual[word] != expected[word])
                {
                    return new GpuDeadlineValidationResult(
                        false,
                        $"Job slot {slot} digest word {word} mismatched.",
                        hash.ToString("X8"),
                        checked((slot + 1) * GpuDeadlineWorkload.DigestStride));
                }
                hash = unchecked((hash ^ actual[word]) * 16777619u);
            }
        }
        return new GpuDeadlineValidationResult(
            true,
            "All four GPU task digests match the portable CPU oracle.",
            hash.ToString("X8"),
            checked(jobs.Length * GpuDeadlineWorkload.DigestStride));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        mainCommands.Dispose();
        copyCommands.Dispose();
        pressureCommands.Dispose();
        urgentCommands.Dispose();
        joinCommands.Dispose();
        workload.Dispose();
    }

    private GpuDeadlineBenchmarkSubmission SubmitOnMainGraphics(
        IReadOnlyList<GpuDeadlineDispatch> plan,
        uint logicalState,
        Action<CommandBuffer> recordTimestampBegin,
        Action<CommandBuffer> recordTimestampEnd)
    {
        mainCommands.Clear();
        mainCommands.name = "GPU.DeadlineScheduler/FIFO/MainGraphics";
        recordTimestampBegin?.Invoke(mainCommands);
        workload.RecordCopyStage(mainCommands);
        workload.RecordGraphicsPressure(
            mainCommands,
            pressureItems,
            pressureIterations,
            logicalState);

        var observations = new List<GpuDeadlineJobFence>(plan.Count);
        foreach (GpuDeadlineDispatch dispatch in plan)
        {
            int slot = SlotForJob(dispatch.Job.JobId);
            workload.RecordJob(
                mainCommands,
                dispatch.Job,
                slot,
                logicalState);
            GraphicsFence fence = CreateComputeFence(mainCommands);
            observations.Add(new GpuDeadlineJobFence(
                dispatch.Job,
                GpuDeadlineQueue.MainGraphics,
                fence));
        }
        recordTimestampEnd?.Invoke(mainCommands);
        GraphicsFence joinFence = CreateComputeFence(mainCommands);
        Graphics.ExecuteCommandBuffer(mainCommands);
        return new GpuDeadlineBenchmarkSubmission(
            observations.ToArray(),
            joinFence,
            usedAsyncCompute: false);
    }

    private GpuDeadlineBenchmarkSubmission SubmitDeadlineAwareAsync(
        IReadOnlyList<GpuDeadlineDispatch> plan,
        uint logicalState,
        Action<CommandBuffer> recordTimestampBegin,
        Action<CommandBuffer> recordTimestampEnd)
    {
        copyCommands.Clear();
        copyCommands.name =
            "GPU.DeadlineScheduler/Async/CopyOnMainGraphics";
        recordTimestampBegin?.Invoke(copyCommands);
        workload.RecordCopyStage(copyCommands);
        GraphicsFence copyFence = CreateComputeFence(copyCommands);

        PrepareAsyncCommands(urgentCommands, copyFence);

        pressureCommands.Clear();
        pressureCommands.name =
            "GPU.DeadlineScheduler/Async/MainBulkLane";
        workload.RecordGraphicsPressure(
            pressureCommands,
            pressureItems,
            pressureIterations,
            logicalState);

        var observations = new List<GpuDeadlineJobFence>(plan.Count);
        foreach (GpuDeadlineDispatch dispatch in plan)
        {
            bool deadlineLane = dispatch.Job.DeadlineClass !=
                GpuDeadlineClass.Background;
            CommandBuffer commands = deadlineLane
                ? urgentCommands
                : pressureCommands;
            GpuDeadlineQueue actualQueue = deadlineLane
                ? GpuDeadlineQueue.ComputeUrgent
                : GpuDeadlineQueue.MainGraphics;
            int slot = SlotForJob(dispatch.Job.JobId);
            workload.RecordJob(
                commands,
                dispatch.Job,
                slot,
                logicalState);
            GraphicsFence fence = CreateComputeFence(commands);
            observations.Add(new GpuDeadlineJobFence(
                dispatch.Job,
                actualQueue,
                fence));
        }

        joinCommands.Clear();
        joinCommands.name =
            "GPU.DeadlineScheduler/Async/JoinOnMainGraphics";
        foreach (GpuDeadlineJobFence observation in observations)
        {
            if (observation.Queue != GpuDeadlineQueue.MainGraphics)
            {
                joinCommands.WaitOnAsyncGraphicsFence(observation.Fence);
            }
        }
        recordTimestampEnd?.Invoke(joinCommands);
        GraphicsFence joinFence = CreateComputeFence(joinCommands);

        Graphics.ExecuteCommandBuffer(copyCommands);
        if (urgentCommands.sizeInBytes > 0)
        {
            Graphics.ExecuteCommandBufferAsync(
                urgentCommands,
                ComputeQueueType.Urgent);
        }
        Graphics.ExecuteCommandBuffer(pressureCommands);
        Graphics.ExecuteCommandBuffer(joinCommands);
        return new GpuDeadlineBenchmarkSubmission(
            observations.ToArray(),
            joinFence,
            usedAsyncCompute: true);
    }

    private GpuDeadlineBenchmarkSubmission SubmitDeadlineAwareOnMain(
        IReadOnlyList<GpuDeadlineDispatch> plan,
        uint logicalState,
        Action<CommandBuffer> recordTimestampBegin,
        Action<CommandBuffer> recordTimestampEnd)
    {
        mainCommands.Clear();
        mainCommands.name =
            "GPU.DeadlineScheduler/LeastSlack/MainGraphics";
        recordTimestampBegin?.Invoke(mainCommands);
        workload.RecordCopyStage(mainCommands);
        workload.RecordGraphicsPressure(
            mainCommands,
            pressureItems,
            pressureIterations,
            logicalState);

        var observations = new List<GpuDeadlineJobFence>(plan.Count);
        foreach (GpuDeadlineDispatch dispatch in plan)
        {
            int slot = SlotForJob(dispatch.Job.JobId);
            workload.RecordJob(
                mainCommands,
                dispatch.Job,
                slot,
                logicalState);
            GraphicsFence fence = CreateComputeFence(mainCommands);
            observations.Add(new GpuDeadlineJobFence(
                dispatch.Job,
                GpuDeadlineQueue.MainGraphics,
                fence));
        }
        recordTimestampEnd?.Invoke(mainCommands);
        GraphicsFence joinFence = CreateComputeFence(mainCommands);
        Graphics.ExecuteCommandBuffer(mainCommands);
        return new GpuDeadlineBenchmarkSubmission(
            observations.ToArray(),
            joinFence,
            usedAsyncCompute: false);
    }

    private static GpuDeadlineJob CreateJob(
        int id,
        int sequence,
        GpuDeadlineClass deadlineClass,
        int deadlineMicroseconds,
        int iterations,
        int workItems,
        uint seed)
    {
        int estimatedCost = Math.Max(
            1,
            checked(iterations * workItems /
                EstimatedMixesPerMicrosecond));
        return new GpuDeadlineJob(
            id,
            sequence,
            deadlineClass,
            deadlineMicroseconds,
            estimatedCost,
            workItems,
            iterations,
            seed);
    }

    private static CommandBuffer CreateCommands(string suffix)
    {
        return new CommandBuffer
        {
            name = "GPU.DeadlineScheduler/" + suffix
        };
    }

    private static CommandBuffer CreateAsyncCommands(string suffix)
    {
        CommandBuffer commands = CreateCommands(suffix);
        commands.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
        return commands;
    }

    private static void PrepareAsyncCommands(
        CommandBuffer commands,
        GraphicsFence copyFence)
    {
        commands.Clear();
        commands.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
        commands.WaitOnAsyncGraphicsFence(copyFence);
    }

    private int SlotForJob(int jobId)
    {
        for (int slot = 0; slot < jobs.Length; slot++)
        {
            if (jobs[slot].JobId == jobId)
            {
                return slot;
            }
        }
        throw new InvalidOperationException("Unknown job ID: " + jobId);
    }

    private static GraphicsFence CreateComputeFence(CommandBuffer commands)
    {
        return commands.CreateGraphicsFence(
            GraphicsFenceType.AsyncQueueSynchronisation,
            SynchronisationStageFlags.ComputeProcessing);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDeadlineSchedulerBenchmarkAdapter));
        }
    }
}

internal readonly struct GpuDeadlineJobFence
{
    public GpuDeadlineJobFence(
        GpuDeadlineJob job,
        GpuDeadlineQueue queue,
        GraphicsFence fence)
    {
        Job = job;
        Queue = queue;
        Fence = fence;
    }

    public GpuDeadlineJob Job { get; }

    public GpuDeadlineQueue Queue { get; }

    public GraphicsFence Fence { get; }
}

internal sealed class GpuDeadlineBenchmarkSubmission
{
    public GpuDeadlineBenchmarkSubmission(
        GpuDeadlineJobFence[] jobFences,
        GraphicsFence joinFence,
        bool usedAsyncCompute)
    {
        JobFences = jobFences ?? throw new ArgumentNullException(
            nameof(jobFences));
        JoinFence = joinFence;
        UsedAsyncCompute = usedAsyncCompute;
    }

    public GpuDeadlineJobFence[] JobFences { get; }

    public GraphicsFence JoinFence { get; }

    public bool UsedAsyncCompute { get; }
}

internal readonly struct GpuDeadlineValidationResult
{
    public GpuDeadlineValidationResult(
        bool passed,
        string message,
        string resultHash,
        int readbackBytes)
    {
        Passed = passed;
        Message = message;
        ResultHash = resultHash;
        ReadbackBytes = readbackBytes;
    }

    public bool Passed { get; }

    public string Message { get; }

    public string ResultHash { get; }

    public int ReadbackBytes { get; }
}
