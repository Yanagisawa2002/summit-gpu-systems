using System;
using System.Diagnostics;
using Summit.GpuDeadlineScheduler;

/// <summary>Identical offered work for pure fake-clock replay and the DX12 Player. Simulation times
/// are explicitly synthetic; they are never evidence for hardware policy promotion.</summary>
public static class GpuRuntimeSchedulerTrace
{
    public static readonly string[] Scenarios = { "bursts", "changing-costs", "dependencies", "overload", "background-progress" };
    public sealed class Batch
    {
        public long Release;
        public GpuRuntimeJob[] Jobs;
        public GpuJobDependency[] Edges;
    }
    [Serializable]
    public sealed class Row
    {
        public int jobId, deadlineClass, estimatedCostUs, actualCostUs;
        public long arrivalUs, admittedUs, submitUs, completeUs, planningTicks, planningAllocatedBytes;
        public double gpuDurationUs;
    }
    [Serializable]
    public sealed class Result
    {
        public string scenario, variant, timingDomain;
        public int offered, completed, backpressureAttempts, criticalCount, criticalMisses, backgroundCompleted, starvedBackground, acceptedCostSamples;
        public long makespanUs, maxBackgroundWaitUs, planningTicks, planningAllocatedBytes;
        public double criticalP99Us, criticalMissRate, gpuTimelineMakespanUs;
        public Row[] rows;
    }

    public static Batch[] Build(string scenario, int batchCount)
    {
        if (Array.IndexOf(Scenarios, scenario) < 0 || batchCount < 2 || batchCount > 10000) throw new ArgumentOutOfRangeException();
        var batches = new Batch[batchCount]; int id = 0;
        for (int batch = 0; batch < batchCount; batch++)
        {
            int count = scenario == "bursts" && batch % 4 == 0 ? 12 : scenario == "background-progress" ? (batch == 0 ? 2 : 1) : 4;
            long spacing = scenario == "overload" ? 20 : scenario == "background-progress" ? 30 : 120;
            var jobs = new GpuRuntimeJob[count];
            for (int j = 0; j < count; j++)
            {
                var kind = scenario == "background-progress" ? (batch == 0 && j == 0 ? GpuDeadlineClass.Background : GpuDeadlineClass.Critical)
                    : j == count - 1 ? GpuDeadlineClass.Critical : j % 2 == 0 ? GpuDeadlineClass.Background : GpuDeadlineClass.Normal;
                int cost = kind == GpuDeadlineClass.Critical ? 40 : kind == GpuDeadlineClass.Normal ? 80 : 120;
                if (scenario == "changing-costs" && batch >= batchCount / 2) cost *= 4;
                int deadline = kind == GpuDeadlineClass.Critical ? 400 : kind == GpuDeadlineClass.Normal ? 1500 : 1000000;
                jobs[j] = new GpuRuntimeJob(new GpuDeadlineJob(id, id, kind, deadline, 100, 256, cost, 0x12345678u), (int)kind); id++;
            }
            var edges = new GpuJobDependency[scenario == "dependencies" ? count - 1 : 0];
            for (int e = 0; e < edges.Length; e++) edges[e] = new GpuJobDependency(jobs[e].Job.JobId, jobs[e + 1].Job.JobId);
            batches[batch] = new Batch { Release = batch * spacing, Jobs = jobs, Edges = edges };
        }
        return batches;
    }

    public static GpuRuntimeCostEstimator CreateCosts()
    {
        var costs = new GpuRuntimeCostEstimator(3, 256, 5000000);
        for (int key = 0; key < 3; key++) costs.Configure(key, 1, 100, 1, 100000);
        return costs;
    }

    public static Result CreateResult(string scenario, bool fifo, Batch[] batches, string timingDomain)
    {
        int total = 0; for (int i = 0; i < batches.Length; i++) total += batches[i].Jobs.Length;
        var result = new Result { scenario = scenario, variant = fifo ? "fifo-bounded" : "runtime-aging-cost-dag",
            timingDomain = timingDomain, offered = total, rows = new Row[total] };
        foreach (Batch batch in batches) foreach (GpuRuntimeJob request in batch.Jobs)
            result.rows[request.Job.JobId] = new Row { jobId = request.Job.JobId, deadlineClass = (int)request.Job.DeadlineClass,
                actualCostUs = request.Job.Iterations, arrivalUs = batch.Release, completeUs = -1 };
        return result;
    }

    public static Result Simulate(string scenario, int batchCount, bool fifo)
    {
        Batch[] batches = Build(scenario, batchCount);
        Result result = CreateResult(scenario, fifo, batches, "synthetic-fake-clock-microseconds");
        var costs = CreateCosts(); var scheduler = new GpuRuntimeScheduler(64, 5000, 1000, 1, costs, fifo);
        var sampleTokens = new long[result.offered]; var sampleReady = new long[result.offered];
        long now = 0; int nextBatch = 0, nextSample = 0, submitted = 0;
        var sampleDurations = new int[result.offered];
        while (result.completed < result.offered)
        {
            while (nextSample < submitted && sampleReady[nextSample] <= now)
            { if (!fifo && costs.TryUpdate(sampleTokens[nextSample], now, sampleDurations[nextSample], true)) result.acceptedCostSamples++; nextSample++; }
            while (nextBatch < batches.Length && batches[nextBatch].Release <= now)
            {
                Batch batch = batches[nextBatch];
                var status = scheduler.TryAdmit(batch.Jobs, batch.Jobs.Length, batch.Edges, batch.Edges.Length, now);
                if (status == GpuAdmissionResult.Capacity || status == GpuAdmissionResult.CostBudget) { result.backpressureAttempts++; break; }
                if (status != GpuAdmissionResult.Accepted) throw new InvalidOperationException(status.ToString());
                foreach (var request in batch.Jobs) result.rows[request.Job.JobId].admittedUs = now;
                nextBatch++;
            }
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread(), beforeTicks = Stopwatch.GetTimestamp();
            bool ready = scheduler.TryPrepare(now, false, default, out var dispatch);
            long ticks = Stopwatch.GetTimestamp() - beforeTicks, bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            if (!ready)
            {
                if (nextBatch >= batches.Length || batches[nextBatch].Release <= now) throw new InvalidOperationException("Replay made no progress.");
                now = batches[nextBatch].Release; continue;
            }
            Row row = result.rows[dispatch.Job.JobId]; row.submitUs = now; row.estimatedCostUs = dispatch.EstimatedCostMicroseconds;
            row.planningTicks = ticks; row.planningAllocatedBytes = bytes;
            scheduler.MarkSubmitted(dispatch); sampleTokens[submitted] = scheduler.BeginCostSample(dispatch, now);
            now += row.actualCostUs; row.completeUs = now; sampleReady[submitted] = now + 200;
            sampleDurations[submitted++] = row.actualCostUs;
            scheduler.TryComplete(dispatch.Slot, dispatch.Ticket); scheduler.ReleaseCompleted(); result.completed++;
        }
        Finish(result, 1000); return result;
    }

    public static void Finish(Result result, long starvationThresholdUs)
    {
        var critical = new double[result.offered]; int criticalCount = 0;
        foreach (Row row in result.rows)
        {
            result.planningTicks += row.planningTicks; result.planningAllocatedBytes += row.planningAllocatedBytes;
            result.makespanUs = Math.Max(result.makespanUs, row.completeUs);
            if (row.deadlineClass == (int)GpuDeadlineClass.Critical && row.completeUs >= 0)
            {
                critical[criticalCount++] = row.completeUs - row.arrivalUs;
                if (row.completeUs - row.arrivalUs > 400) result.criticalMisses++;
            }
            if (row.deadlineClass == (int)GpuDeadlineClass.Background)
            {
                if (row.completeUs >= 0) result.backgroundCompleted++;
                long wait = row.completeUs < 0 ? long.MaxValue : row.submitUs - row.arrivalUs;
                result.maxBackgroundWaitUs = Math.Max(result.maxBackgroundWaitUs, wait);
                if (wait > starvationThresholdUs) result.starvedBackground++;
            }
        }
        result.criticalCount = criticalCount;
        if (criticalCount > 0)
        {
            Array.Sort(critical, 0, criticalCount); result.criticalP99Us = critical[(int)Math.Ceiling(criticalCount * 0.99) - 1];
            result.criticalMissRate = (double)result.criticalMisses / criticalCount;
        }
    }
}
