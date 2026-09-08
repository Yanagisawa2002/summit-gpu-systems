using System;
using System.Collections.Generic;

namespace Summit.GpuTimestamps
{
    // These types deliberately have no Unity, native plugin, clock, or counter calls.
    public enum ObservationScope
    {
        ExplicitGpuWork, EngineGpu, CpuMainThread, CpuRenderThread, CpuRecord,
        EngineCadence, OsPresentation, QueueCompletion, ManagedAllocationCurrentThread
    }
    public enum ObservationState { Available, Unavailable, NotRequested, Pending, Invalid }
    public enum ObservationUnit { Milliseconds, Bytes }

    public sealed class ObservationSource
    {
        public string SourceCommit { get; }
        public string BuildId { get; }
        public string DeviceDriverId { get; }
        public string Collector { get; }
        public string ClockDomain { get; }
        public string Queue { get; }
        public int ProcessId { get; }
        public uint DeviceGeneration { get; }

        public ObservationSource(string sourceCommit, string buildId, string deviceDriverId,
            string collector, string clockDomain, string queue, int processId, uint deviceGeneration = 0)
        {
            SourceCommit = Required(sourceCommit); BuildId = Required(buildId);
            DeviceDriverId = Required(deviceDriverId); Collector = Required(collector);
            ClockDomain = Required(clockDomain); Queue = Required(queue);
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            ProcessId = processId; DeviceGeneration = deviceGeneration;
        }
        internal static string Required(string value) => !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Observation identity must be explicit (use 'unknown' when unavailable).");
    }

    public sealed class Observation
    {
        public ObservationSource Source { get; }
        public string EventId { get; }
        public ObservationScope Scope { get; }
        public ObservationUnit Unit { get; }
        public ObservationState State { get; }
        public double? Value { get; }
        public string Reason { get; }
        public int SourceFrame { get; }
        public int ObservedFrame { get; }
        public ulong StartTicks { get; }
        public ulong EndTicks { get; }
        public ulong Frequency { get; }

        public Observation(ObservationSource source, string eventId, ObservationScope scope,
            ObservationUnit unit, ObservationState state, double? value, string reason,
            int sourceFrame, int observedFrame, ulong startTicks = 0, ulong endTicks = 0, ulong frequency = 0)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            EventId = ObservationSource.Required(eventId);
            if (!Enum.IsDefined(typeof(ObservationScope), scope) || !Enum.IsDefined(typeof(ObservationUnit), unit) ||
                !Enum.IsDefined(typeof(ObservationState), state)) throw new ArgumentException("Unknown observation enum.");
            if ((scope == ObservationScope.ManagedAllocationCurrentThread) != (unit == ObservationUnit.Bytes))
                throw new ArgumentException("Scope/unit mismatch.");
            if (sourceFrame < -1 || observedFrame < -1) throw new ArgumentOutOfRangeException(nameof(sourceFrame));
            if (state == ObservationState.Available)
            {
                if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value < 0)
                    throw new ArgumentException("Available observations require a finite nonnegative value.");
                if (sourceFrame < 0) throw new ArgumentException("Available observations require an attributed source frame.");
            }
            else if (value.HasValue || string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Missing/invalid observations require a reason and no numeric value.");
            if (frequency == 0 && (startTicks != 0 || endTicks != 0) || endTicks < startTicks)
                throw new ArgumentException("Invalid tick interval/frequency.");
            Scope = scope; Unit = unit; State = state; Value = value; Reason = reason ?? "";
            SourceFrame = sourceFrame; ObservedFrame = observedFrame;
            StartTicks = startTicks; EndTicks = endTicks; Frequency = frequency;
        }
    }

    public interface IObservationCollector
    {
        // Poll is injected. Implementations may replay data; the contract does not start capture.
        bool TryRead(out Observation observation);
    }

    public sealed class ReplayObservationCollector : IObservationCollector, IDisposable
    {
        readonly IEnumerator<Observation> rows;
        public ReplayObservationCollector(IEnumerable<Observation> source)
        { rows = (source ?? throw new ArgumentNullException(nameof(source))).GetEnumerator(); }
        public bool TryRead(out Observation observation)
        { bool next = rows.MoveNext(); observation = next ? rows.Current : null; return next; }
        public void Dispose() { rows.Dispose(); }
    }

    public sealed class ObservationLedger
    {
        readonly Dictionary<string, Observation> events = new Dictionary<string, Observation>();
        public IEnumerable<Observation> Rows => events.Values;
        public void Add(Observation row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            // Use length-delimited fields: exporter-owned IDs can contain delimiters.
            string key = Key(row.Source.SourceCommit) + Key(row.Source.BuildId) + row.Source.ProcessId + ":" +
                row.Source.DeviceGeneration + ":" + Key(row.Source.DeviceDriverId) + Key(row.Source.ClockDomain) + Key(row.Source.Collector) + Key(row.Source.Queue) +
                Key(row.EventId) + row.Scope;
            if (events.ContainsKey(key)) throw new ArgumentException("Duplicate observation identity.");
            events.Add(key, row);
        }
        static string Key(string value) => value.Length + ":" + value;
        public void Drain(IObservationCollector collector)
        { if (collector == null) throw new ArgumentNullException(nameof(collector)); while (collector.TryRead(out var row)) Add(row); }
    }

    public readonly struct FrameAnchor
    {
        public readonly int Frame;
        public readonly ulong UpdateTicks;
        public FrameAnchor(int frame, ulong updateTicks) { Frame = frame; UpdateTicks = updateTicks; }
    }

    public static class ObservationCorrelation
    {
        // Unity FTM QPC frame starts map to the first subsequent Update. No interpolation
        // across epochs/frequencies, no attribution to a delayed observation's frame.
        public static int FirstSubsequentUpdate(ulong frameStart, string sourceClock, ulong sourceFrequency,
            string anchorClock, ulong anchorFrequency, IReadOnlyList<FrameAnchor> anchors)
        {
            if (sourceClock != anchorClock || sourceFrequency == 0 || sourceFrequency != anchorFrequency ||
                frameStart == 0 || anchors == null || anchors.Count < 2) return -1;
            for (int i = 0; i < anchors.Count; i++)
                if (anchors[i].Frame < 0 || (i > 0 && (anchors[i].UpdateTicks <= anchors[i - 1].UpdateTicks ||
                    anchors[i].Frame <= anchors[i - 1].Frame))) return -1;
            // The first anchor has no preceding window, so starts before it are unknown.
            if (frameStart <= anchors[0].UpdateTicks) return -1;
            for (int i = 1; i < anchors.Count; i++) if (frameStart <= anchors[i].UpdateTicks) return anchors[i].Frame;
            return -1;
        }
    }
}
