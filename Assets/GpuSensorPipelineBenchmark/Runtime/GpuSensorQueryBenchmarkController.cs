using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Summit.GpuSensorPipeline;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorQueryBenchmark
{
    public sealed class GpuSensorQueryBenchmarkController : MonoBehaviour
    {
        [Serializable] public sealed class Configuration
        {
            public string mode, outputPath;
            public int warmup, samples;
            public int[] elementCounts;
            public string[] distributions;
        }
        [Serializable] public sealed class Measurement
        {
            public string distribution, backend, pairedBackend, status;
            public int elementCount, queryCount, round, order, sample, dispatches, sourceFrame, resultFrame;
            public long scratchBytes, recordAllocatedBytes;
            public ulong token, beginTicks, endTicks, frequency;
            public double gpuMs, recordCpuMs;
        }
        [Serializable] public sealed class Result
        {
            public int schemaVersion = 2;
            public string status = "working", error;
            public string mode, device, driver, graphicsApi, unityVersion;
            public string scope = "Query only: clear + full GPU work-list construction + indirect argument setup + point consumption/reduction; excludes index, uploads, oracle/readback and frame digest.";
            public string environment = "Windows x64 development Player native DX12 latency microbenchmark; synchronized validation between samples.";
            public string allocationScope = "Current-thread managed bytes during query command recording; excludes stopwatch construction, native markers, submission, validation and report construction.";
            public Configuration configuration;
            public GpuSensorRangeQuery[] queries;
            public List<Measurement> measurements = new List<Measurement>();
            public List<string> validatedOutputs = new List<string>();
        }
        private Configuration config;
        private Result result;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gpu-sensor-query-benchmark") < 0) return;
            var host = new GameObject("GPU Sensor Query Benchmark");
            DontDestroyOnLoad(host);
            host.AddComponent<GpuSensorQueryBenchmarkController>();
        }

        private IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            var run = Compare();
            while (true)
            {
                bool next;
                try { next = run.MoveNext(); }
                catch (Exception e)
                {
                    if (result != null) { result.status = "failed"; result.error = e.ToString(); Save(); }
                    UnityEngine.Debug.LogException(e);
                    (run as IDisposable)?.Dispose();
                    Application.Quit(2);
                    yield break;
                }
                if (!next) break;
                yield return run.Current;
            }
            (run as IDisposable)?.Dispose();
            Application.Quit(0);
        }

        private IEnumerator Compare()
        {
            config = JsonUtility.FromJson<Configuration>(File.ReadAllText(
                Environment.GetEnvironmentVariable("SUMMIT_QUERY_BENCHMARK_CONFIG")));
            Check(config.warmup >= 0 && config.samples > 0, "Invalid sample configuration.");
            result = new Result
            {
                configuration = config, mode = config.mode,
                device = SystemInfo.graphicsDeviceName, driver = SystemInfo.graphicsDeviceVersion,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), unityVersion = Application.unityVersion,
                queries = GpuSensorQueryFixtures.Queries()
            };
            Save();
            Check(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12, "DX12 is required.");
            Check(GpuSensorChunkedRangeQuery.SupportsWaveOperations, "All three requested candidates must be supported.");
            Check(GpuTimestampSession.TryCreate(out var timestamps, out var support), support.Message);
            int[][] orders = { new[] {0,1,2}, new[] {2,1,0}, new[] {1,2,0},
                new[] {0,2,1}, new[] {2,0,1}, new[] {1,0,2} };
            using (timestamps)
            using (var commands = new CommandBuffer())
            {
                timestamps.RecordFrequencyInitialization(commands);
                Graphics.ExecuteCommandBuffer(commands);
                yield return null;
                ulong tag = 1;
                foreach (string distribution in config.distributions)
                foreach (int count in config.elementCounts)
                {
                    var input = GpuSensorQueryFixtures.Samples(distribution, count);
                    var expected = GpuSensorQueryBenchmarkOracle.QueryAll(input, count, result.queries);
                    // One immutable CSR and identical buffers for every candidate.
                    using (var pipeline = GpuSensorQueryFixtures.Create(input, GpuSensorQueryBackend.CellSerial))
                    using (var chunks = new GpuSensorChunkedRangeQuery(count))
                    using (var waves = new GpuSensorChunkedRangeQuery(count, GpuSensorQueryBackend.PointChunksWave))
                    {
                        Check(GpuSensorQueryFixtures.Read(pipeline).SequenceEqual(expected), "Baseline oracle mismatch.");
                        for (int round = 0; round < orders.Length; round++)
                        for (int order = 0; order < 3; order++)
                        {
                            int candidate = orders[round][order];
                            var backend = (GpuSensorQueryBackend)candidate;
                            var consumer = candidate == 1 ? chunks : waves;
                            for (int sample = -config.warmup; sample < config.samples; sample++)
                            for (int control = 1; control >= 0; control--)
                            {
                                commands.Clear();
                                var flags = control == 1 ? GpuTimestampSampleFlags.EmptyScope : GpuTimestampSampleFlags.None;
                                Check(timestamps.Acquire(tag++, flags, Time.frameCount, out var token) == GpuTimestampStatus.Ready,
                                    "Timestamp acquisition failed.");
                                var scope = timestamps.GetScope(token);
                                scope.RecordBegin(commands);
                                var stopwatch = Stopwatch.StartNew();
                                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                                if (control == 0)
                                {
                                    if (candidate == 0) pipeline.RecordQueries(commands, count, 0, result.queries.Length);
                                    else consumer.Record(commands, pipeline.Samples, pipeline.BinOffsets,
                                        pipeline.BinnedIds, pipeline.Queries, pipeline.QueryDigests,
                                        count, 0, result.queries.Length);
                                }
                                long recordAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                                stopwatch.Stop();
                                scope.RecordEnd(commands);
                                Check(timestamps.MarkSubmitted(token) == GpuTimestampStatus.Ready, "Timestamp submission failed.");
                                Graphics.ExecuteCommandBuffer(commands);
                                GpuTimestampResult timing;
                                GpuTimestampStatus status;
                                double deadline = Time.realtimeSinceStartupAsDouble + 60;
                                while ((status = timestamps.TryConsume(token, Time.frameCount, out timing)) == GpuTimestampStatus.Pending)
                                {
                                    Check(Time.realtimeSinceStartupAsDouble < deadline, "Native timestamp timeout.");
                                    yield return null;
                                }
                                Check(status == GpuTimestampStatus.Ready, "Native timing failed: " + status);
                                if (control == 0)
                                    Check(GpuSensorQueryFixtures.Read(pipeline).SequenceEqual(expected),
                                        "Oracle mismatch: " + distribution + "/" + count + "/" + backend + "/" + sample);
                                if (sample >= 0)
                                    result.measurements.Add(new Measurement
                                    {
                                        distribution = distribution, elementCount = count,
                                        backend = control == 1 ? "EmptyControl" : backend.ToString(), pairedBackend = backend.ToString(),
                                        queryCount = result.queries.Length, round = round, order = order, sample = sample,
                                        gpuMs = timing.ElapsedMilliseconds, recordCpuMs = stopwatch.Elapsed.TotalMilliseconds,
                                        recordAllocatedBytes = recordAllocated,
                                        dispatches = control == 1 ? 0 : candidate == 0 ? 1 : result.queries.Length * 4,
                                        scratchBytes = control == 1 || candidate == 0 ? 0 : consumer.ScratchBytes,
                                        token = token.Value, sourceFrame = timing.SourceFrame, resultFrame = timing.ResultFrame,
                                        beginTicks = timing.BeginTicks, endTicks = timing.EndTicks,
                                        frequency = timing.TimestampFrequency, status = status.ToString()
                                    });
                            }
                        }
                        result.validatedOutputs.Add(distribution + "/" + count + ":" + JsonUtility.ToJson(new DigestArray { values = expected }));
                        Save();
                    }
                }
            }
            result.status = "complete";
            Save();
        }

        private void Save() => File.WriteAllText(config.outputPath, JsonUtility.ToJson(result, true));
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        [Serializable] private sealed class DigestArray { public GpuSensorQueryDigest[] values; }
    }
}
