using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Summit.ExternalWorkloads;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Summit.WholeTaskMD
{
    [Serializable] internal sealed class Configuration
    {
        public string arm, input, output;
        public int warmups = 3, measured = 5;
        public bool diagnostic;
    }
    [Serializable] internal sealed class Row
    {
        public int step, points, batches, ids;
        public string phase;
        public double allocationMs, firstUseMs, hostWallMs, encodeUploadMs, indexMs, recordMs, submitMs, waitMs, readbackMs, aggregateMs, consumeMs;
        public double gpuMs, gpuEmptyMs;
        public bool gpuAvailable, verified;
        public double[] stateMaxAbs;
    }
    [Serializable] internal sealed class Report
    {
        public string status = "running", error, arm, input, device, graphicsApi, unityVersion, processor, timestampStatus;
        public string residencyBoundary = "CPU snapshot to complete state ready in backend memory; full CPU CSR remains necessary for legacy/reuse consumer";
        public bool diagnostic;
        public long worstCaseResultBytes;
        public List<Row> rows = new List<Row>();
        public List<RawTimestamp> timestamps = new List<RawTimestamp>();
    }
    [Serializable] internal sealed class RawTimestamp
    {
        public ulong token, userTag, beginTicks, endTicks, elapsedTicks, frequency, fence;
        public uint nativeFlags, deviceGeneration;
        public int sourceFrame, resultFrame;
        public double milliseconds;
        public string purpose;
    }
    public sealed class MolecularPlayer : MonoBehaviour
    {
        Configuration config;
        Report report;
        GpuTimestampSession timestamps;
        ulong tag;
        static long Tick() => Stopwatch.GetTimestamp();
        static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-md-config");
            if (at >= 0) new GameObject("Whole task molecular dynamics").AddComponent<MolecularPlayer>().config = JsonUtility.FromJson<Configuration>(File.ReadAllText(args[at + 1]));
        }
        void Start()
        {
            try
            {
                if (config == null) throw new ArgumentException("Missing configuration");
                if (Directory.Exists(config.output)) throw new IOException("Keep previous output; new directory required");
                Directory.CreateDirectory(config.output);
                report = new Report { arm = config.arm, input = config.input, diagnostic = config.diagnostic,
                    device = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceVersion, unityVersion = Application.unityVersion,
                    processor = SystemInfo.processorType };
                if (config.diagnostic)
                {
                    if (GpuTimestampSession.TryCreate(out timestamps, out var support))
                    {
                        using var commands = new CommandBuffer(); timestamps.RecordFrequencyInitialization(commands); Graphics.ExecuteCommandBuffer(commands);
                        report.timestampStatus = "available";
                    }
                    else report.timestampStatus = support.Availability.ToString();
                }
                else report.timestampStatus = "disabled in performance processes";
                Save(); Run(); report.status = "completed"; Save(); timestamps?.Dispose(); Application.Quit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (report != null) { report.status = "failed"; report.error = e.ToString(); Save(); }
                timestamps?.Dispose(); Application.Quit(2);
            }
        }
        void Save() => File.WriteAllText(Path.Combine(config.output, "result.json"), JsonUtility.ToJson(report, true));
        GpuTimestampToken Begin(CommandBuffer commands, bool empty = false)
        {
            if (timestamps == null) return default;
            var status = timestamps.Acquire(++tag, empty ? GpuTimestampSampleFlags.EmptyScope : 0, Time.frameCount, out var token);
            if (status != GpuTimestampStatus.Ready) throw new InvalidOperationException("Timestamp acquire " + status);
            timestamps.Scopes[token.ScopeIndex].RecordBegin(commands); return token;
        }
        void End(CommandBuffer commands, GpuTimestampToken token)
        { if (token.IsValid) timestamps.Scopes[token.ScopeIndex].RecordEnd(commands); }
        void Submitted(GpuTimestampToken token)
        { if (token.IsValid && timestamps.MarkSubmitted(token) != GpuTimestampStatus.Ready) throw new InvalidOperationException("Timestamp submission"); }
        double ConsumeTimestamp(GpuTimestampToken token)
        {
            if (!token.IsValid) return 0;
            long start = Tick();
            while (true)
            {
                var status = timestamps.TryConsume(token, Time.frameCount, out var result);
                if (status == GpuTimestampStatus.Ready)
                {
                    report.timestamps.Add(new RawTimestamp { token = token.Value, userTag = token.UserTag,
                        beginTicks = result.BeginTicks, endTicks = result.EndTicks, elapsedTicks = result.ElapsedTicks,
                        frequency = result.TimestampFrequency, fence = result.FenceValue, nativeFlags = result.NativeFlags,
                        deviceGeneration = result.DeviceGeneration, sourceFrame = result.SourceFrame, resultFrame = result.ResultFrame,
                        milliseconds = result.ElapsedMilliseconds, purpose = token.Flags == GpuTimestampSampleFlags.EmptyScope ? "empty-control" : "index-or-query" });
                    return result.ElapsedMilliseconds;
                }
                if (status != GpuTimestampStatus.Pending || Ms(start, Tick()) > 10000) throw new InvalidOperationException("Timestamp result " + status);
                Thread.Yield();
            }
        }
        static void Wait(GraphicsFence fence)
        {
            long start = Tick();
            while (!fence.passed)
            {
                if (Ms(start, Tick()) > 10000) throw new TimeoutException("GPU completion fence");
                Thread.Yield();
            }
        }
        void EmptyScope(Row row)
        {
            if (timestamps == null) return;
            using var c = new CommandBuffer(); var token = Begin(c, true); End(c, token);
            var fence = c.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            Graphics.ExecuteCommandBuffer(c); Submitted(token); Wait(fence); row.gpuEmptyMs = ConsumeTimestamp(token);
        }
        void Run()
        {
            if (config.arm != "legacy" && config.arm != "reuse") throw new ArgumentException("Unknown baseline arm");
            var input = Input.Read(config.input); var expected = Csr.Read(Path.ChangeExtension(config.input, ".csr"));
            var expectedState = input.MembershipOnly ? null : State.Read(Path.ChangeExtension(config.input, ".state"));
            int n = input.Points.Length, capacity = Math.Min(128, n);
            if ((long)n * capacity * 4 > 64L * 1024 * 1024) throw new InvalidOperationException("Explicit 64 MiB result-buffer budget exceeded");
            report.worstCaseResultBytes = (long)n * capacity * 4; Save();
            long allocated = Tick();
            using var adapter = new GpuSphereWorkloadAdapter(n, capacity, true);
            using var commands = new CommandBuffer();
            double allocationMs = Ms(allocated, Tick());
            for (int step = -config.warmups; step < config.measured; step++)
            {
                var row = new Row { step = step, phase = step < 0 ? "warmup" : "measured", points = n,
                    batches = (n + capacity - 1) / capacity, allocationMs = allocationMs, gpuAvailable = timestamps != null };
                EmptyScope(row);
                long start = Tick(), phase = start;
                var domain = input.Domain(); var ids = new List<uint>(); var offsets = new uint[n + 1];
                if (config.arm == "reuse")
                {
                    adapter.UploadPoints(domain, input.Points); long uploaded = Tick(); row.encodeUploadMs += Ms(phase, uploaded);
                    commands.Clear(); var token = Begin(commands); adapter.RecordIndexBuild(commands); End(commands, token);
                    var fence = config.diagnostic ? commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations) : default;
                    long recorded = Tick(); row.recordMs += Ms(uploaded, recorded);
                    Graphics.ExecuteCommandBuffer(commands); Submitted(token); long submitted = Tick(); row.submitMs += Ms(recorded, submitted);
                    if (config.diagnostic) Wait(fence); long waited = Tick(); row.waitMs += Ms(submitted, waited);
                    adapter.CompleteIndexBuild(); long completed = Tick(); row.readbackMs += Ms(waited, completed); row.indexMs = Ms(uploaded, completed);
                    row.gpuMs += ConsumeTimestamp(token);
                }
                for (int first = 0; first < n; first += capacity)
                {
                    int count = Math.Min(capacity, n - first); phase = Tick();
                    var spheres = new SourceSphere[count];
                    for (int i = 0; i < count; i++) { var p = input.Points[first + i]; spheres[i] = new SourceSphere(p.X, p.Y, p.Z, input.Radius); }
                    if (config.arm == "reuse") adapter.UploadQueries(spheres); else adapter.Upload(domain, input.Points, spheres);
                    long uploaded = Tick(); row.encodeUploadMs += Ms(phase, uploaded);
                    commands.Clear(); var token = Begin(commands);
                    if (config.arm == "reuse") adapter.RecordQueries(commands); else adapter.Record(commands);
                    End(commands, token);
                    var fence = config.diagnostic ? commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations) : default;
                    long recorded = Tick(); row.recordMs += Ms(uploaded, recorded);
                    Graphics.ExecuteCommandBuffer(commands); Submitted(token); long submitted = Tick(); row.submitMs += Ms(recorded, submitted);
                    if (config.diagnostic) Wait(fence); long waited = Tick(); row.waitMs += Ms(submitted, waited);
                    var batchOffsets = new uint[count + 1]; adapter.Offsets.GetData(batchOffsets, 0, 0, count + 1);
                    if (batchOffsets[count] > (long)n * count) throw new InvalidDataException("Result overflow");
                    var batchIds = new uint[batchOffsets[count]];
                    if (batchIds.Length > 0) adapter.Ids.GetData(batchIds, 0, 0, batchIds.Length);
                    long readback = Tick(); row.readbackMs += Ms(waited, readback);
                    // The upstream callback excludes just the original particle ID.
                    for (int q = 0; q < count; q++)
                    {
                        for (uint at = batchOffsets[q]; at < batchOffsets[q + 1]; at++) if (batchIds[at] != first + q) ids.Add(batchIds[at]);
                        offsets[first + q + 1] = checked((uint)ids.Count);
                    }
                    row.aggregateMs += Ms(readback, Tick()); row.gpuMs += ConsumeTimestamp(token);
                }
                phase = Tick(); var actual = new Csr { Offsets = offsets, Ids = ids.ToArray() }; row.aggregateMs += Ms(phase, Tick());
                phase = Tick(); var state = input.MembershipOnly ? null : State.Consume(input, actual); long end = Tick();
                row.consumeMs = Ms(phase, end); row.hostWallMs = Ms(start, end); row.ids = actual.Ids.Length;
                if (step == -config.warmups) row.firstUseMs = Ms(allocated, end);
                // Full equality is outside the primary interval, never a count/checksum substitute.
                actual.Equal(expected); row.stateMaxAbs = state?.Equal(expectedState); row.verified = true;
                report.rows.Add(row); Save();
                if (step == 0 || step == config.measured - 1)
                {
                    actual.Write(Path.Combine(config.output, "step-" + step + ".csr"));
                    state?.Write(Path.Combine(config.output, "step-" + step + ".state"));
                }
            }
        }
    }
}
