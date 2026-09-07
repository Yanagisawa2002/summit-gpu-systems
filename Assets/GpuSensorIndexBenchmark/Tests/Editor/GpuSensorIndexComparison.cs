using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Pipeline = Summit.GpuSensorPipeline.GpuSensorPipeline;

namespace Summit.GpuSensorIndex.Benchmark.Tests
{
    /// <summary>
    /// Executable Editor/DX12 comparison, deliberately separate from formal Player
    /// performance evidence. Run via Tools/Run-GpuSensorIndexComparison.ps1.
    /// Native GPU timing fails closed; CPU synchronization is never called GPU time.
    /// </summary>
    public sealed class GpuSensorIndexComparison
    {
        [Serializable] public sealed class Row
        {
            public string scenario, backend, order;
            public int round, frame, capacity, staticSlots, changePercent, crossingPercent;
            public bool measured, forcedRebuild;
            public double gpuTotalMs, gpuIndexMs, gpuQueryMs, cpuRecordMs, cpuSubmitMs;
            public long indexResidentBytes, commonInputBytes, commonConsumerBytes;
            public long recordAllocatedBytes, submitAllocatedBytes;
            public uint activeCount, changedCount, rebuildReason, holes, csrExtent, inspectedSlots;
            public uint countDigest, xorDigest, sumDigest0, sumDigest1;
            public NativeSample[] nativeSamples;
        }
        [Serializable] public sealed class NativeSample
        {
            public ulong token, beginTicks, endTicks, frequency;
            public int sourceFrame, resultFrame;
            public string status;
            public double elapsedMs;
            public NativeSample(GpuTimestampResult r)
            {
                token = r.Token.Value; beginTicks = r.BeginTicks; endTicks = r.EndTicks;
                frequency = r.TimestampFrequency; sourceFrame = r.SourceFrame; resultFrame = r.ResultFrame;
                status = "Ready"; elapsedMs = r.ElapsedMilliseconds;
            }
        }
        [Serializable] public sealed class Report
        {
            public int schemaVersion = 2;
            public string evidenceClass = "editor-comparison-unpromoted";
            public string device, deviceVersion, unityVersion, graphicsApi, utc, sourceCommit;
            public string timingScope = "Native DX12 main-graphics-command-list: dirty/key detection + maintenance + fallback + consumer queries + frame digest. Snapshot uploads and correctness readback excluded.";
            public string allocationScope = "Current-thread managed bytes: record includes native marker recording, submit covers ExecuteCommandBuffer; excludes snapshots, token acquisition, correctness readback and report construction.";
            public bool formalPerformanceEvidence = false, allDigestsMatch;
            public uint timestampAbi;
            public List<Row> rows = new List<Row>();
            public List<NativeSample> emptyControls = new List<NativeSample>();
        }
        private sealed class Scenario
        {
            public string Name;
            public int Capacity, StaticSlots, Change, Crossing;
            public bool Concentrated, Teleport, Transitions, Lifecycle;
        }

        [UnityTest]
        public IEnumerator Compare()
        {
            string output = Environment.GetEnvironmentVariable("SUMMIT_INDEX_COMPARISON_OUTPUT");
            if (string.IsNullOrEmpty(output)) Assert.Ignore("Use the dedicated comparison runner to opt in.");
            Assert.That(SystemInfo.graphicsDeviceType, Is.EqualTo(GraphicsDeviceType.Direct3D12));
            bool smoke = Environment.GetEnvironmentVariable("SUMMIT_INDEX_COMPARISON_MODE") != "matrix";
            int samples = ReadInt("SUMMIT_INDEX_COMPARISON_SAMPLES", smoke ? 2 : 60);
            int warmup = ReadInt("SUMMIT_INDEX_COMPARISON_WARMUP", smoke ? 1 : 12);
            int rounds = ReadInt("SUMMIT_INDEX_COMPARISON_ROUNDS", smoke ? 1 : 4);
            var report = new Report {
                device = SystemInfo.graphicsDeviceName, deviceVersion = SystemInfo.graphicsDeviceVersion,
                unityVersion = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                utc = DateTime.UtcNow.ToString("O"), sourceCommit = Environment.GetEnvironmentVariable("SUMMIT_INDEX_SOURCE_COMMIT") };
            foreach (var plugin in UnityEditor.PluginImporter.GetAllImporters())
                if (plugin.assetPath.Contains("SummitGpuTimestamps"))
                {
                    UnityEngine.Debug.Log("Timestamp plugin: " + plugin.assetPath + " Editor=" + plugin.GetCompatibleWithEditor() +
                        " CPU=" + plugin.GetEditorData("CPU") + " OS=" + plugin.GetEditorData("OS"));
                    Assert.That(plugin.GetCompatibleWithEditor(), Is.True, "Timestamp plugin must be enabled for Windows Editor.");
                }
            Assert.That(GpuTimestampSession.TryCreate(out var timestamps, out var support), Is.True, support.Availability + ": " + support.Message);
            using (timestamps)
            using (var commands = new CommandBuffer { name = "SensorIndex/Comparison" })
            {
                report.timestampAbi = support.AbiVersion;
                timestamps.RecordFrequencyInitialization(commands);
                Graphics.ExecuteCommandBuffer(commands); commands.Clear();
                yield return null;
                int serial = 0;
                foreach (var scenario in Scenarios(smoke))
                for (int round = 0; round < rounds; round++)
                {
                    // Raw control overhead is disclosed and never subtracted.
                    Assert.That(timestamps.Acquire((ulong)(serial * 3 + 1), GpuTimestampSampleFlags.EmptyScope,
                        serial, out var control), Is.EqualTo(GpuTimestampStatus.Ready));
                    commands.Clear();
                    timestamps.Scopes[control.ScopeIndex].RecordBegin(commands);
                    timestamps.Scopes[control.ScopeIndex].RecordEnd(commands);
                    Assert.That(timestamps.MarkSubmitted(control), Is.EqualTo(GpuTimestampStatus.Ready));
                    Graphics.ExecuteCommandBuffer(commands);
                    var controlWait = Stopwatch.StartNew();
                    while (true)
                    {
                        var status = timestamps.TryConsume(control, serial + 1, out var result);
                        if (status == GpuTimestampStatus.Ready) { report.emptyControls.Add(new NativeSample(result)); break; }
                        Assert.That(status, Is.EqualTo(GpuTimestampStatus.Pending));
                        Assert.That(controlWait.Elapsed.TotalSeconds, Is.LessThan(20));
                        yield return null;
                    }
                    serial++;
                    // One pair per round, AB/BA alternating. Fresh indices and trace
                    // arrays make initial/fallback state identical between paired runs.
                    GpuSensorQueryDigest[][] paired = new GpuSensorQueryDigest[2][];
                    for (int orderIndex = 0; orderIndex < 2; orderIndex++)
                    {
                        bool incremental = (round + orderIndex) % 2 == 1;
                        using (var data = new Data(scenario, incremental))
                        {
                            var captures = new GpuSensorQueryDigest[(warmup + samples) * data.Queries.Length];
                            for (int frame = 0; frame < warmup + samples; frame++)
                            {
                                data.Advance(frame);
                                // Upload completion precedes native begin and is outside
                                // both arms. Both arms see the exact same snapshot sequence.
                                data.Input.SetData(data.Samples); data.Flags.SetData(data.Active);
                                bool force = scenario.Transitions && frame % 3 == 0;
                                var tokens = new GpuTimestampToken[3];
                                for (int i = 0; i < 3; i++)
                                    Assert.That(timestamps.Acquire((ulong)(serial * 3 + i + 1), GpuTimestampSampleFlags.None,
                                        serial, out tokens[i]), Is.EqualTo(GpuTimestampStatus.Ready));
                                commands.Clear();
                                var timer = Stopwatch.StartNew();
                                long recordAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                                timestamps.Scopes[tokens[0].ScopeIndex].RecordBegin(commands);
                                timestamps.Scopes[tokens[1].ScopeIndex].RecordBegin(commands);
                                data.RecordIndex(commands, force);
                                timestamps.Scopes[tokens[1].ScopeIndex].RecordEnd(commands);
                                timestamps.Scopes[tokens[2].ScopeIndex].RecordBegin(commands);
                                data.RecordQueries(commands);
                                timestamps.Scopes[tokens[2].ScopeIndex].RecordEnd(commands);
                                timestamps.Scopes[tokens[0].ScopeIndex].RecordEnd(commands);
                                long recordAllocated = GC.GetAllocatedBytesForCurrentThread() - recordAllocatedBefore;
                                double recordMs = timer.Elapsed.TotalMilliseconds;
                                foreach (var token in tokens)
                                    Assert.That(timestamps.MarkSubmitted(token), Is.EqualTo(GpuTimestampStatus.Ready));
                                timer.Restart();
                                long submitAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                                Graphics.ExecuteCommandBuffer(commands);
                                long submitAllocated = GC.GetAllocatedBytesForCurrentThread() - submitAllocatedBefore;
                                double submitMs = timer.Elapsed.TotalMilliseconds;
                                var elapsed = new double[3];
                                var native = new NativeSample[3];
                                for (int i = 0; i < 3; i++)
                                {
                                    var deadline = Stopwatch.StartNew();
                                    while (true)
                                    {
                                        var status = timestamps.TryConsume(tokens[i], serial + 1, out var result);
                                        if (status == GpuTimestampStatus.Ready) { elapsed[i] = result.ElapsedMilliseconds; native[i] = new NativeSample(result); break; }
                                        Assert.That(status, Is.EqualTo(GpuTimestampStatus.Pending), "Native timing failed closed.");
                                        Assert.That(deadline.Elapsed.TotalSeconds, Is.LessThan(20), "Native timestamp timeout.");
                                        yield return null;
                                    }
                                }
                                // All query outputs are validated outside the interval.
                                var actual = new GpuSensorQueryDigest[data.Queries.Length];
                                data.Consumer.QueryDigests.GetData(actual);
                                Assert.That(actual, Is.EqualTo(data.Oracle()), scenario.Name + " frame " + frame);
                                Array.Copy(actual, 0, captures, frame * actual.Length, actual.Length);
                                uint[] state = new uint[16];
                                if (incremental) data.Incremental.Diagnostics.GetData(state);
                                else { state[9] = data.ActiveCount; state[10] = data.ActiveCount; }
                                var digest = new GpuSensorQueryDigest[Pipeline.FrameDigestCount];
                                data.Consumer.FrameDigest.GetData(digest);
                                report.rows.Add(new Row {
                                    scenario = scenario.Name, backend = incremental ? "incremental-reserved-csr" : "full-direct-waveops",
                                    order = round % 2 == 0 ? "AB" : "BA", round = round, frame = frame,
                                    measured = frame >= warmup, forcedRebuild = incremental && force,
                                    capacity = scenario.Capacity, staticSlots = scenario.StaticSlots,
                                    changePercent = scenario.Change, crossingPercent = scenario.Crossing,
                                    gpuTotalMs = elapsed[0], gpuIndexMs = elapsed[1], gpuQueryMs = elapsed[2],
                                    cpuRecordMs = recordMs, cpuSubmitMs = submitMs,
                                    recordAllocatedBytes = recordAllocated, submitAllocatedBytes = submitAllocated,
                                    indexResidentBytes = data.IndexBytes, commonInputBytes = scenario.Capacity * 20L,
                                    commonConsumerBytes = data.Consumer.ResidentBytes,
                                    activeCount = state[9], changedCount = state[3], rebuildReason = state[8], holes = state[2],
                                    csrExtent = state[10], inspectedSlots = incremental ? state[14] : (uint)scenario.Capacity,
                                    countDigest = digest[0].Count, xorDigest = digest[0].XorHash,
                                    sumDigest0 = digest[0].SumHash0, sumDigest1 = digest[0].SumHash1, nativeSamples = native });
                                serial++;
                            }
                            paired[incremental ? 1 : 0] = captures;
                        }
                    }
                    Assert.That(paired[1], Is.EqualTo(paired[0]), scenario.Name + " paired outputs");
                }
            }
            report.allDigestsMatch = true;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, JsonUtility.ToJson(report, true));
        }

        private static int ReadInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

        private static IEnumerable<Scenario> Scenarios(bool smoke)
        {
            if (!smoke)
                foreach (int n in new[] { 262144, 1048576 })
                foreach (int change in GpuSensorIndexUpdateTrace.Rates)
                foreach (int crossing in GpuSensorIndexUpdateTrace.Rates)
                    yield return new Scenario { Name = $"rates-n{n}-c{change}-x{crossing}", Capacity = n, Change = change, Crossing = crossing };
            int size = smoke ? 257 : 262144;
            yield return new Scenario { Name = "static90", Capacity = size, StaticSlots = size * 9 / 10, Change = 100, Crossing = 20 };
            yield return new Scenario { Name = "hotspot", Capacity = size, Concentrated = true, Change = 100, Crossing = 100 };
            yield return new Scenario { Name = "teleports-transitions", Capacity = size, Change = 100, Crossing = 100, Teleport = true, Transitions = true };
            yield return new Scenario { Name = "lifecycle", Capacity = size, Change = 5, Crossing = 20, Lifecycle = true };
        }

        private sealed class Data : IDisposable
        {
            public readonly GpuSensorSample[] Samples;
            public readonly uint[] Active;
            public readonly GraphicsBuffer Input, Flags;
            public readonly GpuSensorIncrementalIndex Incremental;
            public readonly GpuSensorFullRebuildIndex Full;
            public readonly Pipeline Consumer;
            public readonly GpuSensorRangeQuery[] Queries = {
                new GpuSensorRangeQuery(32768, 32768, 32768, 65535),
                new GpuSensorRangeQuery(512, 512, 512, 511),
                new GpuSensorRangeQuery(1024, 512, 512, 1),
                new GpuSensorRangeQuery(40000, 40000, 40000, 2) };
            private readonly Scenario scenario;
            public long IndexBytes => Incremental?.ResidentBytes ?? Full.ResidentBytes;
            public uint ActiveCount { get { uint total = 0; foreach (uint a in Active) if (a == 1) total++; return total; } }
            public Data(Scenario scenario, bool incremental)
            {
                this.scenario = scenario;
                Samples = new GpuSensorSample[scenario.Capacity]; Active = new uint[scenario.Capacity];
                GpuSensorIndexUpdateTrace.Initialize(Samples, Active, scenario.Concentrated);
                Input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, scenario.Capacity, 16);
                Flags = new GraphicsBuffer(GraphicsBuffer.Target.Structured, scenario.Capacity, 4);
                if (incremental) Incremental = new GpuSensorIncrementalIndex(scenario.Capacity, scenario.StaticSlots);
                else Full = new GpuSensorFullRebuildIndex(scenario.Capacity, GpuPrimitiveBackend.WaveOps);
                Consumer = new Pipeline(scenario.Capacity, Queries.Length, GpuPrimitiveBackend.Portable, false);
                Consumer.SetQueries(Queries);
            }
            public void Advance(int frame)
            {
                if (frame == 0) return;
                GpuSensorIndexUpdateTrace.Advance(Samples, Active, scenario.StaticSlots,
                    scenario.Transitions && frame % 3 == 2 ? 0 : scenario.Change,
                    scenario.Crossing, frame, scenario.Teleport);
                if (scenario.Lifecycle)
                {
                    int slot = ((frame - 1) / 2) % (Samples.Length - 1);
                    Active[slot] = frame % 2 == 0 ? 1u : 0u;
                    Active[Samples.Length - 1] = 1; // Highest stable ID always remains query-visible.
                }
            }
            public void RecordIndex(CommandBuffer c, bool force)
            {
                if (Incremental != null) Incremental.RecordUpdate(c, Input, Flags, forceRebuild: force);
                else Full.RecordUpdate(c, Input, Flags);
            }
            public void RecordQueries(CommandBuffer c)
            {
                Consumer.RecordExternalIndexQueries(c, Incremental?.Samples ?? Input,
                    Incremental?.BinOffsets ?? Full.BinOffsets, Incremental?.BinnedIds ?? Full.BinnedIds,
                    Samples.Length, Queries.Length, 0);
            }
            public GpuSensorQueryDigest[] Oracle()
            {
                var output = new GpuSensorQueryDigest[Queries.Length];
                unchecked
                {
                    for (int q = 0; q < Queries.Length; q++)
                    {
                        var query = Queries[q]; uint r = query.Radius;
                        uint minX = query.CenterX > r ? query.CenterX - r : 0;
                        uint minY = query.CenterY > r ? query.CenterY - r : 0;
                        uint minZ = query.CenterZ > r ? query.CenterZ - r : 0;
                        uint maxX = Math.Min(65535u, query.CenterX + r), maxY = Math.Min(65535u, query.CenterY + r), maxZ = Math.Min(65535u, query.CenterZ + r);
                        for (uint id = 0; id < Samples.Length; id++)
                        {
                            var s = Samples[id];
                            if (Active[id] != 1 || s.X < minX || s.X > maxX || s.Y < minY || s.Y > maxY || s.Z < minZ || s.Z > maxZ) continue;
                            uint h = Mix(Mix(Mix(Mix(Mix(id ^ 0x85ebca6bu) ^ s.X) ^ s.Y) ^ s.Z) ^ s.Payload);
                            output[q].Count++; output[q].XorHash ^= h; output[q].SumHash0 += h;
                            output[q].SumHash1 += Mix(h ^ id ^ 0x27d4eb2fu);
                        }
                    }
                }
                return output;
            }
            private static uint Mix(uint v)
            {
                unchecked { v ^= v >> 16; v *= 0x7feb352du; v ^= v >> 15; v *= 0x846ca68bu; return v ^ (v >> 16); }
            }
            public void Dispose()
            {
                Consumer.Dispose(); Incremental?.Dispose(); Full?.Dispose(); Input.Dispose(); Flags.Dispose();
            }
        }
    }
}
