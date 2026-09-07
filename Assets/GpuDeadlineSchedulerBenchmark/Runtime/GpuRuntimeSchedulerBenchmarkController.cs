using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Summit.GpuDeadlineScheduler;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Dynamic FIFO/runtime comparison. Main-queue only until workload-specific async calibration
/// supplies fresh evidence. Timestamp samples measure GPU dispatches; latency measures CPU-observed fences.</summary>
public sealed class GpuRuntimeSchedulerBenchmarkController : MonoBehaviour
{
    private string output;
    private int batchCount;
    private int rounds;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-gpu-runtime-scheduler-output");
        if (index < 0) return;
        var host = new GameObject("GPU Runtime Scheduler Benchmark"); DontDestroyOnLoad(host);
        var controller = host.AddComponent<GpuRuntimeSchedulerBenchmarkController>();
        controller.output = args[index + 1];
        index = Array.IndexOf(args, "-gpu-runtime-scheduler-batches");
        controller.batchCount = index < 0 ? 16 : int.Parse(args[index + 1]);
        index = Array.IndexOf(args, "-gpu-runtime-scheduler-rounds");
        controller.rounds = index < 0 ? 1 : int.Parse(args[index + 1]);
    }

    private IEnumerator Start()
    {
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Application.runInBackground = true;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "environment.json"), JsonUtility.ToJson(new EnvironmentRow {
            gpu = SystemInfo.graphicsDeviceName, graphicsVersion = SystemInfo.graphicsDeviceVersion,
            unity = Application.unityVersion, copyQueueClaim = false, asyncAdmitted = false }, true));
        // Guard nested iterators so failures produce nonzero process exit and a reviewable error.
        var stack = new System.Collections.Generic.Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count > 0)
        {
            object current = null; Exception failure = null;
            try { if (!stack.Peek().MoveNext()) { stack.Pop(); continue; } current = stack.Peek().Current; }
            catch (Exception exception) { failure = exception; }
            if (failure != null)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), failure.ToString());
                UnityEngine.Debug.LogException(failure); Application.Quit(1); yield break;
            }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
        File.WriteAllText(Path.Combine(output, "completed.txt"), "All offered jobs completed; per-job GPU digests validated in separate untimed replay.\n");
        Application.Quit(0);
    }

    private IEnumerator Run()
    {
        if (!GpuTimestampSession.TryCreate(out var timestamps, out var support)) throw new InvalidOperationException(support.Message);
        using (timestamps)
        {
            using (var commands = new CommandBuffer())
            { timestamps.RecordFrequencyInitialization(commands); Graphics.ExecuteCommandBuffer(commands); }
            yield return null;
            foreach (string scenario in GpuRuntimeSchedulerTrace.Scenarios)
                for (int round = 0; round < rounds; round++)
                    for (int order = 0; order < 2; order++)
                    {
                        bool fifo = (round + order) % 2 == 0;
                        yield return RunCase(timestamps, scenario, fifo, round, true);
                        yield return RunCase(timestamps, scenario, fifo, round, false);
                    }
        }
    }

    private IEnumerator RunCase(GpuTimestampSession timestamps, string scenario, bool fifo, int round, bool validation)
    {
        var batches = GpuRuntimeSchedulerTrace.Build(scenario, batchCount);
        var result = GpuRuntimeSchedulerTrace.CreateResult(scenario, fifo, batches, "cpu-fence-observed-latency-and-native-dx12-gpu-duration");
        var costs = GpuRuntimeSchedulerTrace.CreateCosts();
        var scheduler = new GpuRuntimeScheduler(64, 5000, 1000, 2, costs, fifo);
        var tokens = new GpuTimestampToken[64]; var costTokens = new long[64]; var submittedAtFrame = new int[64];
        var active = new bool[64]; var dispatches = new GpuRuntimeDispatch[64];
        int nextBatch = 0;
        var watch = Stopwatch.StartNew();
        long Now() => (long)(watch.ElapsedTicks * (1000000.0 / Stopwatch.Frequency));
        using (var workload = new GpuDeadlineWorkload(64, 256, 256))
        using (var executor = new GpuRuntimeQueueExecutor(scheduler))
        using (var setup = new CommandBuffer())
        {
            workload.RecordCopyStage(setup); Graphics.ExecuteCommandBuffer(setup);
            // A distinct outer scope measures GPU timeline span, including intentional idle/backpressure gaps.
            Require(timestamps.Acquire(ulong.MaxValue, GpuTimestampSampleFlags.None, Time.frameCount, out var whole));
            Require(timestamps.MarkSubmitted(whole));
            setup.Clear(); timestamps.GetScope(whole).RecordBegin(setup); Graphics.ExecuteCommandBuffer(setup);
            Action<CommandBuffer, GpuRuntimeDispatch> record = (commands, dispatch) =>
            {
                Require(timestamps.Acquire((ulong)dispatch.Job.JobId + 1, GpuTimestampSampleFlags.None, Time.frameCount, out var token));
                tokens[dispatch.Slot] = token; Require(timestamps.MarkSubmitted(token));
                timestamps.GetScope(token).RecordBegin(commands);
                workload.RecordJob(commands, dispatch.Job, dispatch.Slot, 19);
                timestamps.GetScope(token).RecordEnd(commands);
            };
            while (result.completed < result.offered)
            {
                if (watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("Runtime scheduler trace timed out.");
                long now = Now(); executor.PollCompletions();
                for (int slot = 0; slot < 64; slot++)
                {
                    if (!active[slot]) continue;
                    var row = result.rows[dispatches[slot].Job.JobId];
                    if (scheduler.State(slot) == GpuRuntimeJobState.Completed && row.completeUs < 0) row.completeUs = now;
                    // Deliberately consume at least two frames later to exercise delayed valid samples.
                    if (row.completeUs < 0 || Time.frameCount < submittedAtFrame[slot] + 2) continue;
                    var status = timestamps.TryConsume(tokens[slot], Time.frameCount, out var sample);
                    if (status == GpuTimestampStatus.Pending) continue;
                    Require(status); row.gpuDurationUs = sample.ElapsedMilliseconds * 1000;
                    if (!fifo && costs.TryUpdate(costTokens[slot], now, row.gpuDurationUs, true)) result.acceptedCostSamples++;
                    if (validation)
                    {
                        var digest = new uint[4]; workload.GetJobDigestBuffer(slot).GetData(digest);
                        var expected = GpuDeadlineWorkload.ExpectedDigest(dispatches[slot].Job, 19);
                        for (int word = 0; word < 4; word++) if (digest[word] != expected[word]) throw new InvalidOperationException("GPU digest mismatch.");
                    }
                    active[slot] = false; result.completed++;
                }
                // Keep completed slots until all delayed samples have been consumed, avoiding slot/token aliasing.
                bool anyActive = false; for (int i = 0; i < 64; i++) anyActive |= active[i];
                if (!anyActive) scheduler.ReleaseCompleted();
                while (nextBatch < batches.Length && batches[nextBatch].Release <= now)
                {
                    var batch = batches[nextBatch];
                    var status = scheduler.TryAdmit(batch.Jobs, batch.Jobs.Length, batch.Edges, batch.Edges.Length, now);
                    if (status == GpuAdmissionResult.Capacity || status == GpuAdmissionResult.CostBudget) { result.backpressureAttempts++; break; }
                    if (status != GpuAdmissionResult.Accepted) throw new InvalidOperationException(status.ToString());
                    foreach (var request in batch.Jobs) result.rows[request.Job.JobId].admittedUs = now;
                    nextBatch++;
                }
                // A completed slot still carrying a delayed token must not be submitted a second time.
                while (scheduler.InFlightCount < 2)
                {
                    long bytes = GC.GetAllocatedBytesForCurrentThread(), ticks = Stopwatch.GetTimestamp();
                    bool ready = scheduler.TryPrepare(now, false, default, out _);
                    ticks = Stopwatch.GetTimestamp() - ticks; bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    if (!ready) break;
                    if (!executor.SubmitNext(now, record, out var dispatch)) throw new InvalidOperationException("Submission readiness changed.");
                    int slot = dispatch.Slot;
                    active[slot] = true; dispatches[slot] = dispatch; submittedAtFrame[slot] = Time.frameCount;
                    costTokens[slot] = scheduler.BeginCostSample(dispatch, now);
                    var row = result.rows[dispatch.Job.JobId]; row.submitUs = now; row.estimatedCostUs = dispatch.EstimatedCostMicroseconds;
                    row.planningTicks = ticks; row.planningAllocatedBytes = bytes;
                }
                yield return null;
            }
            setup.Clear(); executor.RecordJoin(setup); timestamps.GetScope(whole).RecordEnd(setup); Graphics.ExecuteCommandBuffer(setup);
            while (true)
            {
                var status = timestamps.TryConsume(whole, Time.frameCount, out var sample);
                if (status == GpuTimestampStatus.Pending) { if (watch.Elapsed.TotalSeconds > 120) throw new TimeoutException(); yield return null; continue; }
                Require(status); result.gpuTimelineMakespanUs = sample.ElapsedMilliseconds * 1000; break;
            }
        }
        GpuRuntimeSchedulerTrace.Finish(result, 1000);
        File.WriteAllText(Path.Combine(output, scenario + "-r" + round + "-" + result.variant + (validation ? "-validation" : "-measured") + ".json"), JsonUtility.ToJson(result, true));
    }

    private static void Require(GpuTimestampStatus status)
    { if (status != GpuTimestampStatus.Ready) throw new InvalidOperationException("Native timestamp gate: " + status); }

    [Serializable]
    private sealed class EnvironmentRow { public string gpu, graphicsVersion, unity; public bool copyQueueClaim, asyncAdmitted; }
}
