using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;
using Pipeline = Summit.GpuSensorPipeline.GpuSensorPipeline;

namespace Summit.UnifiedMicrobenchmark
{
    // Explicit opt-in Release Player latency experiment. No synchronous GPU readback.
    public sealed class UnifiedMicrobenchmarkController : MonoBehaviour
    {
        [Serializable] public sealed class Configuration
        {
            public string phase, outputPath, sourceCommit;
            public int processIndex, warmup = 8, querySamples = 30, indexSamples = 24;
        }
        [Serializable] public sealed class NativeSample
        {
            public ulong token, beginTicks, endTicks, frequency;
            public int sourceFrame, resultFrame;
            public double elapsedMs;
            public string status;
            public NativeSample(GpuTimestampResult r)
            {
                token = r.Token.Value; beginTicks = r.BeginTicks; endTicks = r.EndTicks;
                frequency = r.TimestampFrequency; sourceFrame = r.SourceFrame; resultFrame = r.ResultFrame;
                elapsedMs = r.ElapsedMilliseconds; status = "Ready";
            }
        }
        [Serializable] public sealed class Row
        {
            public string kind, scenario, backend;
            public int process, block, order, frame, capacity, queryCount, dispatches;
            public int indexRecordedDispatches, detectionDispatchSlots, queryMembershipCapacity;
            public uint inspectedSlots, maintenanceSlots, nonemptyIndexDispatches;
            public bool measured;
            public double gpuTotalMs, gpuIndexMs, gpuQueryMs, cpuRecordMs, cpuIndexRecordMs, cpuQueryRecordMs;
            public double cpuSubmitMs, uploadCpuMs, asyncRequestCpuMs, asyncWaitWallMs, readbackCopyCpuMs, nativeWaitWallMs;
            public double traceCpuMs;
            public long recordAllocatedBytes, submitAllocatedBytes, indexResidentBytes, queryScratchBytes;
            public long commonInputBytes, commonConsumerBytes;
            public uint[] indexState;
            public GpuSensorQueryDigest[] queryDigests;
            public NativeSample[] native;
        }
        [Serializable] public sealed class Setup
        {
            public string scenario, backend, stage;
            public int block;
            public double cpuMs;
        }
        [Serializable] public sealed class Report
        {
            public int schemaVersion = 1;
            public string status = "working", error, device, driver, unityVersion, graphicsApi, utc;
            public string queryFixture = GpuSensorBenchmarkFixtures.QueryFixtureId;
            public string indexFixture = GpuSensorBenchmarkFixtures.IndexFixtureId;
            public string environment = "Windows x64 non-development Player, DX12 native timestamps; serialized latency microbenchmark with asynchronous output capture after each operation.";
            public string scope = "Query: all query dispatches, no index or frame digest. Index: complete maintenance plus CellSerial consumer and frame digest, including all reset, indirect arguments and fallback dispatches. Empty markers never subtracted.";
            public string exclusions = "Uploads, resource setup, CPU oracle, async readback request/copy and all GPU-result waits reported separately. Readback occurs after timed scope. These are latency experiments, not sustained throughput or presented-frame timings.";
            public Configuration configuration;
            public uint timestampAbi;
            public List<Row> rows = new List<Row>();
            public List<Setup> setup = new List<Setup>();
            public List<string> orderSchedule = new List<string>();
            public List<string> validation = new List<string>();
        }
        private Configuration config;
        private Report report;
        private GpuTimestampSession timestamps;
        private ulong tag = 1;
        private static readonly int[] Counts = { 4097, 65541, 262145 };
        private static readonly string[] Distributions = { "sparse", "uniform", "hotspot", "single-cell" };
        private static readonly int[][] QueryOrders = { new[] {0,1,3,2}, new[] {1,2,0,3}, new[] {2,3,1,0}, new[] {3,0,2,1} };
        private static readonly int[][] IndexOrders = { new[] {0,1,2}, new[] {0,2,1}, new[] {1,0,2}, new[] {1,2,0}, new[] {2,0,1}, new[] {2,1,0} };
        private static readonly string[] IndexNames = { "full-direct-waveops", "incremental-original", "incremental-gpu-driven" };
        private static readonly GpuSensorRangeQuery[] IndexQueries = {
            new GpuSensorRangeQuery(32768,32768,32768,65535), new GpuSensorRangeQuery(512,512,512,511),
            new GpuSensorRangeQuery(1024,512,512,1), new GpuSensorRangeQuery(40000,40000,40000,2) };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-summit-unified-microbenchmark") < 0) return;
            var go = new GameObject("Unified microbenchmark"); DontDestroyOnLoad(go);
            go.AddComponent<UnifiedMicrobenchmarkController>();
        }
        private IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            var run = Run().GetEnumerator();
            while (true)
            {
                bool next;
                try { next = run.MoveNext(); }
                catch (Exception e)
                {
                    if (report != null) { report.status = "failed"; report.error = e.ToString(); Save(); }
                    UnityEngine.Debug.LogException(e); run.Dispose(); Application.Quit(2); yield break;
                }
                if (!next) break;
                yield return run.Current;
            }
            run.Dispose(); Application.Quit(0);
        }
        private IEnumerable<object> Run()
        {
            config = JsonUtility.FromJson<Configuration>(File.ReadAllText(Environment.GetEnvironmentVariable("SUMMIT_UNIFIED_CONFIG")));
            Check(config.phase == "validation" || config.phase == "diagnostic" || config.phase == "formal", "Unknown phase.");
            Check(config.warmup > 0 && config.querySamples > 0 && config.indexSamples > 0, "Invalid counts.");
            if (config.phase == "formal" || config.phase == "validation")
                Check(config.warmup == 8 && config.querySamples == 30 && config.indexSamples == 24 && config.processIndex >= 0 && config.processIndex < 5, "Frozen protocol mismatch.");
            report = new Report { configuration = config, device = SystemInfo.graphicsDeviceName,
                driver = SystemInfo.graphicsDeviceVersion, unityVersion = Application.unityVersion,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O") };
            Save();
            Check(!UnityEngine.Debug.isDebugBuild, "A non-development Player is required.");
            Check(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12, "DX12 required.");
            Check(GpuTimestampSession.TryCreate(out timestamps, out var support), support.Message);
            report.timestampAbi = support.AbiVersion;
            using (timestamps)
            using (var c = new CommandBuffer())
            {
                timestamps.RecordFrequencyInitialization(c); Graphics.ExecuteCommandBuffer(c); c.Clear(); yield return null;
                int caseId = 0;
                foreach (string distribution in Distributions)
                foreach (int n in Counts)
                {
                    string name = distribution + "-n" + n;
                    var timer = Stopwatch.StartNew();
                    var samples = GpuSensorBenchmarkFixtures.Samples(distribution, n);
                    var queries = GpuSensorBenchmarkFixtures.Queries();
                    var expected = GpuSensorBenchmarkOracle.QueryAll(samples, n, queries);
                    AddSetup(name, "common", -1, "fixture-and-independent-oracle", timer.Elapsed.TotalMilliseconds);
                    timer.Restart();
                    using (var data = new QueryData(samples))
                    {
                        AddSetup(name, "common", -1, "resources-upload-and-CSR-setup", timer.Elapsed.TotalMilliseconds);
                        int[] blocks = Shuffled(QueryOrders.Length, (uint)(0x713ba121 + config.processIndex * 997 + caseId++));
                        if (config.phase != "formal") blocks = new[] { 0 };
                        for (int block = 0; block < blocks.Length; block++)
                        {
                            int[] order = QueryOrders[blocks[block]];
                            report.orderSchedule.Add(name + "/" + block + ":" + string.Join(",", order));
                            foreach (object wait in EmptyControl(c, name, block)) yield return wait;
                            for (int o = 0; o < order.Length; o++)
                            for (int f = 0; f < config.warmup + config.querySamples; f++)
                            {
                                int arm = order[o];
                                var row = new Row { kind = "query", scenario = name, backend = ((GpuSensorQueryBackend)arm).ToString(),
                                    process = config.processIndex, block = block, order = o, frame = f, measured = f >= config.warmup,
                                    capacity = n, queryCount = queries.Length, dispatches = arm == 0 ? 1 : arm == 3 ? 2 : queries.Length * 4,
                                    queryScratchBytes = data.Scratch(arm), queryMembershipCapacity = n,
                                    commonConsumerBytes = data.Source.ResidentBytes };
                                foreach (object wait in Measure(c, row, null, () => data.Record(c, arm), data.Source.QueryDigests, null, expected)) yield return wait;
                            }
                            Save();
                        }
                    }
                    report.validation.Add(name + ": all arms matched independent oracle"); Save();
                }
                foreach (var scenario in Scenarios())
                {
                    var timer = Stopwatch.StartNew();
                    var expected = ExpectedTrace(scenario, config.warmup + config.indexSamples);
                    AddSetup(scenario.Name, "common", -1, "fixture-and-independent-oracle", timer.Elapsed.TotalMilliseconds);
                    int[] blocks = Shuffled(IndexOrders.Length, (uint)(0x61db2391 + config.processIndex * 997 + caseId++));
                    if (config.phase != "formal") blocks = new[] { 0 };
                    for (int block = 0; block < blocks.Length; block++)
                    {
                        int[] order = IndexOrders[blocks[block]];
                        report.orderSchedule.Add(scenario.Name + "/" + block + ":" + string.Join(",", order));
                        foreach (object wait in EmptyControl(c, scenario.Name, block)) yield return wait;
                        for (int o = 0; o < order.Length; o++)
                        {
                            int arm = order[o]; timer.Restart();
                            using (var data = new IndexData(scenario, arm))
                            {
                                AddSetup(scenario.Name, IndexNames[arm], block, "resources-and-initialization", timer.Elapsed.TotalMilliseconds);
                                for (int f = 0; f < config.warmup + config.indexSamples; f++)
                                {
                                    timer.Restart(); Advance(scenario, data.Samples, data.Active, f);
                                    double traceMs = timer.Elapsed.TotalMilliseconds;
                                    timer.Restart(); data.Input.SetData(data.Samples); data.Flags.SetData(data.Active);
                                    var row = new Row { kind = "index", scenario = scenario.Name, backend = IndexNames[arm],
                                        process = config.processIndex, block = block, order = o, frame = f, measured = f >= config.warmup,
                                        capacity = scenario.Capacity, queryCount = IndexQueries.Length,
                                        dispatches = arm == 0 ? 13 : 15, indexResidentBytes = data.IndexBytes,
                                        indexRecordedDispatches = arm == 0 ? 11 : 13, detectionDispatchSlots = scenario.Capacity,
                                        queryMembershipCapacity = scenario.Capacity * (arm == 0 ? 1 : 3),
                                        commonInputBytes = scenario.Capacity * 20L, commonConsumerBytes = data.Consumer.ResidentBytes,
                                        traceCpuMs = traceMs,
                                        uploadCpuMs = timer.Elapsed.TotalMilliseconds };
                                    foreach (object wait in Measure(c, row, () => data.RecordIndex(c), () => data.RecordQueries(c),
                                        data.Consumer.QueryDigests, data.Incremental?.Diagnostics, expected[f])) yield return wait;
                                }
                            }
                        }
                        Save();
                    }
                    report.validation.Add(scenario.Name + ": all trace frames and arms matched independent oracle"); Save();
                }
            }
            report.status = "complete"; Save();
        }
        private IEnumerable<object> Measure(CommandBuffer c, Row row, Action index, Action query,
            GraphicsBuffer output, GraphicsBuffer diagnostics, GpuSensorQueryDigest[] expected)
        {
            int scopeCount = index == null ? 1 : 3;
            var tokens = new GpuTimestampToken[scopeCount];
            for (int i = 0; i < scopeCount; i++)
                Check(timestamps.Acquire(tag++, GpuTimestampSampleFlags.None, Time.frameCount, out tokens[i]) == GpuTimestampStatus.Ready, "Acquire failed.");
            c.Clear();
            var timer = Stopwatch.StartNew(); long allocated = GC.GetAllocatedBytesForCurrentThread();
            timestamps.GetScope(tokens[0]).RecordBegin(c);
            if (index != null)
            {
                timestamps.GetScope(tokens[1]).RecordBegin(c);
                long before = Stopwatch.GetTimestamp(); index(); row.cpuIndexRecordMs = Ms(Stopwatch.GetTimestamp() - before);
                timestamps.GetScope(tokens[1]).RecordEnd(c);
                timestamps.GetScope(tokens[2]).RecordBegin(c);
            }
            long queryBefore = Stopwatch.GetTimestamp(); query(); row.cpuQueryRecordMs = Ms(Stopwatch.GetTimestamp() - queryBefore);
            if (index != null) timestamps.GetScope(tokens[2]).RecordEnd(c);
            timestamps.GetScope(tokens[0]).RecordEnd(c);
            row.recordAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            row.cpuRecordMs = timer.Elapsed.TotalMilliseconds;
            foreach (var token in tokens) Check(timestamps.MarkSubmitted(token) == GpuTimestampStatus.Ready, "Submit marker failed.");
            timer.Restart(); allocated = GC.GetAllocatedBytesForCurrentThread(); Graphics.ExecuteCommandBuffer(c);
            row.submitAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated; row.cpuSubmitMs = timer.Elapsed.TotalMilliseconds;
            // Ordered asynchronous capture after the complete timed operation; no GetData synchronization.
            timer.Restart(); var request = AsyncGPUReadback.Request(output);
            var stateRequest = diagnostics == null ? default(AsyncGPUReadbackRequest) : AsyncGPUReadback.Request(diagnostics);
            row.asyncRequestCpuMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            while (!request.done || (diagnostics != null && !stateRequest.done))
            { Check(timer.Elapsed.TotalSeconds < 60, "Async capture timeout."); yield return null; }
            row.asyncWaitWallMs = timer.Elapsed.TotalMilliseconds;
            Check(!request.hasError && (diagnostics == null || !stateRequest.hasError), "Async capture failed.");
            timer.Restart(); row.queryDigests = request.GetData<GpuSensorQueryDigest>().ToArray();
            row.indexState = diagnostics == null ? new uint[0] : stateRequest.GetData<uint>().ToArray();
            row.readbackCopyCpuMs = timer.Elapsed.TotalMilliseconds;
            if (row.kind == "index")
            {
                bool full = row.backend == "full-direct-waveops";
                bool driven = row.backend == "incremental-gpu-driven";
                row.inspectedSlots = full ? (uint)row.capacity : row.indexState[14];
                // Logical slot inspections in remove/insert; separately disclose
                // full-capacity detection dispatch and recorded empty dispatches.
                row.maintenanceSlots = full ? (uint)row.capacity * 2u : row.indexState[8] != 0 ? 0u :
                    2u * (driven ? row.indexState[3] : (uint)row.capacity);
                row.nonemptyIndexDispatches = full ? 11u : driven ? row.indexState[15] : 13u;
            }
            Check(row.queryDigests.SequenceEqual(expected), "Oracle mismatch: " + row.scenario + "/" + row.backend + "/" + row.frame);
            timer.Restart(); row.native = new NativeSample[scopeCount];
            for (int i = 0; i < scopeCount; i++)
            {
                while (true)
                {
                    var status = timestamps.TryConsume(tokens[i], Time.frameCount, out var timing);
                    if (status == GpuTimestampStatus.Ready) { row.native[i] = new NativeSample(timing); break; }
                    Check(status == GpuTimestampStatus.Pending && timer.Elapsed.TotalSeconds < 60, "Native timestamp failed: " + status);
                    yield return null;
                }
            }
            row.nativeWaitWallMs = timer.Elapsed.TotalMilliseconds;
            row.gpuTotalMs = row.native[0].elapsedMs;
            row.gpuIndexMs = index == null ? 0 : row.native[1].elapsedMs;
            row.gpuQueryMs = index == null ? row.gpuTotalMs : row.native[2].elapsedMs;
            report.rows.Add(row); c.Clear(); yield return null;
        }
        private IEnumerable<object> EmptyControl(CommandBuffer c, string scenario, int block)
        {
            Check(timestamps.Acquire(tag++, GpuTimestampSampleFlags.EmptyScope, Time.frameCount, out var token) == GpuTimestampStatus.Ready, "Empty acquire failed.");
            c.Clear(); timestamps.GetScope(token).RecordBegin(c); timestamps.GetScope(token).RecordEnd(c);
            Check(timestamps.MarkSubmitted(token) == GpuTimestampStatus.Ready, "Empty submit failed."); Graphics.ExecuteCommandBuffer(c);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var status = timestamps.TryConsume(token, Time.frameCount, out var r);
                if (status == GpuTimestampStatus.Ready)
                {
                    report.rows.Add(new Row { kind = "control", scenario = scenario, backend = "EmptyControl", process = config.processIndex,
                        block = block, gpuTotalMs = r.ElapsedMilliseconds, nativeWaitWallMs = timer.Elapsed.TotalMilliseconds,
                        native = new[] { new NativeSample(r) } }); break;
                }
                Check(status == GpuTimestampStatus.Pending && timer.Elapsed.TotalSeconds < 60, "Empty timestamp failure."); yield return null;
            }
            c.Clear(); yield return null;
        }
        private sealed class QueryData : IDisposable
        {
            public readonly Pipeline Source;
            private readonly GpuSensorChunkedRangeQuery chunks, waves;
            private readonly GpuSensorBatchedRangeQuery batch;
            private readonly int n;
            public QueryData(GpuSensorSample[] samples)
            {
                n = samples.Length;
                Source = new Pipeline(n, 9, GpuPrimitiveBackend.Portable, false);
                Source.Samples.SetData(samples); Source.SetQueries(GpuSensorBenchmarkFixtures.Queries());
                var keys = new uint[n]; var ids = new uint[n];
                for (int i = 0; i < n; i++) { keys[i] = GpuSensorDeterministicGenerator.ComputeKey(samples[i]); ids[i] = (uint)i; }
                Source.SetStableIds(ids);
                using (var c = new CommandBuffer()) { Source.RecordCpuProduced(c, samples, keys, n, 9, 0); Graphics.ExecuteCommandBuffer(c); }
                chunks = new GpuSensorChunkedRangeQuery(n); waves = new GpuSensorChunkedRangeQuery(n, GpuSensorQueryBackend.PointChunksWave);
                batch = new GpuSensorBatchedRangeQuery(n);
            }
            public long Scratch(int arm) => arm == 1 ? chunks.ScratchBytes : arm == 2 ? waves.ScratchBytes : 0;
            public void Record(CommandBuffer c, int arm)
            {
                if (arm == 0) Source.RecordQueries(c, n, 0, 9);
                else if (arm == 3) batch.Record(c, Source.Samples, Source.BinOffsets, Source.BinnedIds, Source.Queries, Source.QueryDigests, n, 0, 9);
                else (arm == 1 ? chunks : waves).Record(c, Source.Samples, Source.BinOffsets, Source.BinnedIds, Source.Queries, Source.QueryDigests, n, 0, 9);
            }
            public void Dispose() { batch.Dispose(); waves.Dispose(); chunks.Dispose(); Source.Dispose(); }
        }
        private sealed class Scenario { public string Name; public int Capacity = 262144, Static, Change, Crossing; public bool Lifecycle; }
        private static IEnumerable<Scenario> Scenarios()
        {
            yield return new Scenario { Name = "rates-n262144-c0-x0" };
            yield return new Scenario { Name = "rates-n262144-c1-x0", Change = 1 };
            yield return new Scenario { Name = "rates-n262144-c1-x1", Change = 1, Crossing = 1 };
            yield return new Scenario { Name = "rates-n262144-c100-x100", Change = 100, Crossing = 100 };
            yield return new Scenario { Name = "static90", Static = 262144 * 9 / 10, Change = 100, Crossing = 20 };
            yield return new Scenario { Name = "lifecycle", Change = 5, Crossing = 20, Lifecycle = true };
        }
        private static void Advance(Scenario s, GpuSensorSample[] samples, uint[] active, int frame)
        {
            if (frame == 0) return;
            GpuSensorIndexUpdateTrace.Advance(samples, active, s.Static, s.Change, s.Crossing, frame);
            if (s.Lifecycle) { active[((frame - 1) / 2) % (samples.Length - 1)] = frame % 2 == 0 ? 1u : 0u; active[samples.Length - 1] = 1; }
        }
        private static GpuSensorQueryDigest[][] ExpectedTrace(Scenario scenario, int frames)
        {
            var samples = new GpuSensorSample[scenario.Capacity]; var active = new uint[scenario.Capacity];
            GpuSensorIndexUpdateTrace.Initialize(samples, active, false);
            var output = new GpuSensorQueryDigest[frames][];
            for (int f = 0; f < frames; f++) { Advance(scenario, samples, active, f); output[f] = GpuSensorBenchmarkOracle.QueryAll(samples, samples.Length, IndexQueries, active); }
            return output;
        }
        private sealed class IndexData : IDisposable
        {
            public readonly GpuSensorSample[] Samples;
            public readonly uint[] Active;
            public readonly GraphicsBuffer Input, Flags;
            public readonly GpuSensorIncrementalIndex Incremental;
            public readonly GpuSensorFullRebuildIndex Full;
            public readonly Pipeline Consumer;
            public long IndexBytes => Incremental?.ResidentBytes ?? Full.ResidentBytes;
            public IndexData(Scenario scenario, int arm)
            {
                Samples = new GpuSensorSample[scenario.Capacity]; Active = new uint[scenario.Capacity];
                GpuSensorIndexUpdateTrace.Initialize(Samples, Active, false);
                Input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, scenario.Capacity, 16);
                Flags = new GraphicsBuffer(GraphicsBuffer.Target.Structured, scenario.Capacity, 4);
                if (arm == 0) Full = new GpuSensorFullRebuildIndex(scenario.Capacity, GpuPrimitiveBackend.WaveOps);
                else Incremental = new GpuSensorIncrementalIndex(scenario.Capacity, scenario.Static,
                    executionMode: arm == 1 ? GpuSensorIndexExecutionMode.Original : GpuSensorIndexExecutionMode.GpuDriven);
                Consumer = new Pipeline(scenario.Capacity, IndexQueries.Length, GpuPrimitiveBackend.Portable, false);
                Consumer.SetQueries(IndexQueries);
            }
            public void RecordIndex(CommandBuffer c) { if (Full != null) Full.RecordUpdate(c, Input, Flags); else Incremental.RecordUpdate(c, Input, Flags); }
            public void RecordQueries(CommandBuffer c) => Consumer.RecordExternalIndexQueries(c, Incremental?.Samples ?? Input,
                Incremental?.BinOffsets ?? Full.BinOffsets, Incremental?.BinnedIds ?? Full.BinnedIds, Samples.Length, IndexQueries.Length, 0);
            public void Dispose() { Consumer.Dispose(); Incremental?.Dispose(); Full?.Dispose(); Input.Dispose(); Flags.Dispose(); }
        }
        private static int[] Shuffled(int n, uint seed)
        {
            var values = Enumerable.Range(0, n).ToArray();
            for (int i = n - 1; i > 0; i--) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; int j = (int)(seed % (i + 1)); int t = values[i]; values[i] = values[j]; values[j] = t; }
            return values;
        }
        private void AddSetup(string scenario, string backend, int block, string stage, double ms) =>
            report.setup.Add(new Setup { scenario = scenario, backend = backend, block = block, stage = stage, cpuMs = ms });
        private void Save() => File.WriteAllText(config.outputPath, JsonUtility.ToJson(report, true));
        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
