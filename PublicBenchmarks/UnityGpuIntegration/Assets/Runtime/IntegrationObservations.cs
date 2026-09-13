using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Summit.GpuTimestamps;

namespace Summit.PublicIntegration
{
    // Adapts existing recorded rows at checkpoints; never starts a collector or fills
    // missing engine/presentation data from explicit command-buffer GPU scopes.
    public static class IntegrationObservations
    {
        public static void Export(IntegrationResult result, TextWriter writer)
        {
            ObservationCsv.Write(writer, Enumerate(result));
        }
        public static IEnumerable<Observation> Enumerate(IntegrationResult result)
        {
            var anchors = new List<FrameAnchor>();
            foreach (var frame in result.processFrames)
                if (frame.qpc > 0) anchors.Add(new FrameAnchor(frame.unityFrame, (ulong)frame.qpc));
            ObservationSource Source(string collector, string clock, string queue, uint generation = 0) =>
                new ObservationSource(Identity(result.config.sourceSha), Identity(result.buildGuid),
                    Identity(result.config.observationDeviceDriverId), collector, clock, queue, result.processId, generation);
            var engine = Source("Unity.FrameTimingManager", "windows-qpc", "engine");
            foreach (var row in result.engineTimings)
            {
                int frame = ObservationCorrelation.FirstSubsequentUpdate(row.frameStartTimestamp, "windows-qpc",
                    result.engineCpuTimerFrequency, "windows-qpc", (ulong)Math.Max(0, result.qpcFrequency), anchors);
                string id = row.frameStartTimestamp.ToString();
                yield return Duration(engine, id, ObservationScope.EngineGpu, row.gpuFrameMs, frame, row.observedUnityFrame);
                yield return Duration(engine, id, ObservationScope.CpuMainThread, row.mainThreadMs, frame, row.observedUnityFrame);
                yield return Duration(engine, id, ObservationScope.CpuRenderThread, row.renderThreadMs, frame, row.observedUnityFrame);
            }
            if (result.engineTimings.Count == 0)
                yield return new Observation(engine, "engine-capability", ObservationScope.EngineGpu,
                    ObservationUnit.Milliseconds, ObservationState.Unavailable, null, "no-engine-timing-records", -1, -1);
            var cadence = Source("Unity.Update-interval", "unity-realtime", "cpu-main");
            foreach (var row in result.processFrames)
                yield return Duration(cadence, row.unityFrame.ToString(), ObservationScope.EngineCadence,
                    row.engineIntervalMs, row.unityFrame, row.unityFrame);
            for (int run = 0; run < result.runs.Count; run++)
            foreach (var row in result.runs[run].frames ?? Array.Empty<IntegrationFrame>())
            {
                if (row == null || row.qpcStart <= 0) continue;
                string id = run + ":" + row.frame;
                var record = Source("Unity.CommandBuffer-record", "stopwatch-relative", "cpu-main");
                yield return Duration(record, id, ObservationScope.CpuRecord, row.recordCpuMs, row.unityFrame, row.unityFrame);
                bool allocationValid = result.allocationCounterAvailable && row.recordAllocatedBytes >= 0;
                yield return new Observation(record, id, ObservationScope.ManagedAllocationCurrentThread, ObservationUnit.Bytes,
                    allocationValid ? ObservationState.Available : ObservationState.Unavailable,
                    allocationValid ? (double?)row.recordAllocatedBytes : null,
                    allocationValid ? "" : result.allocationCounterReason, row.unityFrame, row.unityFrame);
                foreach (var named in new[] { ("scene", row.sceneGpu), ("index", row.indexGpu), ("query", row.queryGpu) })
                {
                    var n = named.Item2;
                    var state = n.status == "NotRequested" ? ObservationState.NotRequested :
                        n.status == "Ready" && n.frequency > 0 && n.endTicks >= n.beginTicks ? ObservationState.Available : ObservationState.Pending;
                    bool available = state == ObservationState.Available;
                    yield return new Observation(Source("SummitNative/" + named.Item1, "gpu-queue-ticks", "direct-main", n.deviceGeneration),
                        id + ":" + n.token, ObservationScope.ExplicitGpuWork, ObservationUnit.Milliseconds, state,
                        available ? (double?)n.milliseconds : null, available ? "" : n.status,
                        available ? n.sourceFrame : row.unityFrame, n.resultFrame,
                        available ? n.beginTicks : 0, available ? n.endTicks : 0, available ? n.frequency : 0);
                }
            }
            // Presence of cpuTimePresentCalled or Present wait is not proof of displayed frames.
            yield return new Observation(Source("none", "unavailable", "display"), "presentation-capability",
                ObservationScope.OsPresentation, ObservationUnit.Milliseconds, ObservationState.Unavailable,
                null, "no-OS-presentation-source-attached", -1, -1);
        }
        static Observation Duration(ObservationSource source, string id, ObservationScope scope,
            double ms, int frame, int observed)
        {
            bool valid = frame >= 0 && ms > 0 && !double.IsNaN(ms) && !double.IsInfinity(ms);
            return new Observation(source, id, scope, ObservationUnit.Milliseconds,
                valid ? ObservationState.Available : ObservationState.Unavailable, valid ? (double?)ms : null,
                valid ? "" : frame < 0 ? "unattributed-clock-or-frame" : "missing-or-zero-duration", frame, observed);
        }
        static string Identity(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
    internal sealed class UnityManagedAllocationCounter : IManagedAllocationCounter
    {
        public string Scope => AllocationCounterCapability.CurrentThreadScope;
        public int ThreadId => Thread.CurrentThread.ManagedThreadId;
        public bool TryRead(out long bytes)
        {
            try { bytes = GC.GetAllocatedBytesForCurrentThread(); return bytes >= 0; }
            catch (NotSupportedException) { bytes = -1; return false; }
        }
    }
}
