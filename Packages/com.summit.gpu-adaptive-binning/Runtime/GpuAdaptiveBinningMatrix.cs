using System;
using System.Collections.Generic;
using System.Diagnostics;
using Summit.GpuAutotuning;
using Summit.GpuPrimitives;

namespace Summit.GpuAdaptiveBinning
{
    /// <summary>Exact, versioned producer features. No interpolation between measured cells.</summary>
    [Serializable]
    public struct GpuAdaptiveBinningFeatures : IEquatable<GpuAdaptiveBinningFeatures>
    {
        public string workloadId;
        public int elementCount;
        public int binCount;
        public GpuAdaptiveBinningWorkloadConcentration concentration;
        public int occupiedBinCount;
        public int maximumBinOccupancy;
        public uint singleBinKey;

        public bool IsValid => !string.IsNullOrWhiteSpace(workloadId) && elementCount > 0 &&
            binCount > 0 && occupiedBinCount > 0 && occupiedBinCount <= Math.Min(elementCount, binCount) &&
            maximumBinOccupancy > 0 && maximumBinOccupancy <= elementCount &&
            (long)maximumBinOccupancy * occupiedBinCount >= elementCount &&
            (long)maximumBinOccupancy + occupiedBinCount - 1 <= elementCount &&
            (concentration == GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed
                ? occupiedBinCount == 1 && maximumBinOccupancy == elementCount && singleBinKey < binCount
                : (concentration == GpuAdaptiveBinningWorkloadConcentration.Hotset ||
                   concentration == GpuAdaptiveBinningWorkloadConcentration.General) &&
                  occupiedBinCount > 1 && singleBinKey == 0);

        public bool Equals(GpuAdaptiveBinningFeatures other) =>
            string.Equals(workloadId, other.workloadId, StringComparison.Ordinal) &&
            elementCount == other.elementCount && binCount == other.binCount &&
            concentration == other.concentration && occupiedBinCount == other.occupiedBinCount &&
            maximumBinOccupancy == other.maximumBinOccupancy && singleBinKey == other.singleBinKey;
        public override bool Equals(object obj) => obj is GpuAdaptiveBinningFeatures other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = workloadId == null ? 0 : StringComparer.Ordinal.GetHashCode(workloadId);
                h = h * 31 + elementCount; h = h * 31 + binCount;
                h = h * 31 + (int)concentration; h = h * 31 + occupiedBinCount;
                h = h * 31 + maximumBinOccupancy; return h * 31 + (int)singleBinKey;
            }
        }

        /// <summary>Exact CPU-side feature gathering using caller-owned histogram scratch.
        /// The caller must upload/use these same keys; GPU-only input needs producer evidence
        /// or separately measured readback and must not use stale features.</summary>
        public static bool TryGather(uint[] keys, int count, int bins, string workloadId,
            GpuAdaptiveBinningWorkloadConcentration concentration, int[] histogram,
            out GpuAdaptiveBinningFeatures features)
        {
            features = default;
            if (keys == null || count <= 0 || count > keys.Length || bins <= 0 ||
                histogram == null || histogram.Length < bins) return false;
            Array.Clear(histogram, 0, bins);
            int occupied = 0, maximum = 0; uint single = 0;
            for (int i = 0; i < count; i++)
            {
                uint key = keys[i];
                if (key >= bins) return false;
                int occupancy = ++histogram[key];
                if (occupancy == 1) { occupied++; single = key; }
                maximum = Math.Max(maximum, occupancy);
            }
            features = new GpuAdaptiveBinningFeatures { workloadId = workloadId,
                elementCount = count, binCount = bins, concentration = concentration,
                occupiedBinCount = occupied, maximumBinOccupancy = maximum,
                singleBinKey = occupied == 1 ? single : 0 };
            return features.IsValid;
        }
    }

    [Serializable]
    public sealed class GpuAdaptiveBinningMatrixRow
    {
        public GpuAdaptiveBinningFeatures features;
        public GpuAdaptiveBinningBackend backend;
        public string primitiveCandidateId;
        public bool validationPassed;
        public string evidenceId;
        public int calibrationSamples;
    }

    [Serializable]
    public sealed class GpuAdaptiveBinningMatrixDocument
    {
        public const int CurrentSchemaVersion = 3;
        public int schemaVersion = CurrentSchemaVersion;
        public string matrixId;
        public int revision;
        public string phase; // discovery or frozen; only frozen may select.
        public string calibrationRunId;
        public int[] calibrationSeeds;
        public GpuDeviceFingerprint device;
        public GpuCalibrationEnvironment environment;
        public bool profilerMarkersEnabled;
        public GpuAdaptiveBinningMatrixRow[] rows;
    }

    /// <summary>Immutable runtime snapshot. Changes to the loaded document cannot alter a live selector.</summary>
    public sealed class GpuAdaptiveBinningMatrix
    {
        private readonly Dictionary<GpuAdaptiveBinningFeatures, Entry> cells =
            new Dictionary<GpuAdaptiveBinningFeatures, Entry>();
        private readonly GpuDeviceFingerprint device;
        private readonly GpuCalibrationEnvironment environment;
        public bool IsValid { get; }
        public string MatrixId { get; }
        public int Revision { get; }
        public bool ProfilerMarkersEnabled { get; }
        public int CellCount => cells.Count;

        private readonly struct Entry
        {
            public Entry(GpuAdaptiveBinningMatrixRow row)
            { Backend = row.backend; Candidate = row.primitiveCandidateId; Validated = row.validationPassed &&
                !string.IsNullOrWhiteSpace(row.evidenceId) && row.calibrationSamples >= 3; }
            public GpuAdaptiveBinningBackend Backend { get; }
            public string Candidate { get; }
            public bool Validated { get; }
        }

        public GpuAdaptiveBinningMatrix(GpuAdaptiveBinningMatrixDocument document)
        {
            if (document == null) return;
            MatrixId = document.matrixId; Revision = document.revision;
            ProfilerMarkersEnabled = document.profilerMarkersEnabled;
            device = document.device?.Copy(); environment = document.environment?.Copy();
            if (document.schemaVersion != GpuAdaptiveBinningMatrixDocument.CurrentSchemaVersion ||
                document.phase != "frozen" || string.IsNullOrWhiteSpace(MatrixId) || Revision < 1 ||
                string.IsNullOrWhiteSpace(document.calibrationRunId) || device == null ||
                !device.Equals(device) || device.vendorId != 0x1002 || device.deviceId != 0x7551 ||
                device.graphicsApi != "Direct3D12" || environment == null || !environment.IsValid ||
                document.rows == null || document.rows.Length == 0 || document.rows.Length > 65536) return;
            foreach (var row in document.rows)
            {
                if (row == null || !row.features.IsValid || cells.ContainsKey(row.features) ||
                    string.IsNullOrWhiteSpace(row.primitiveCandidateId) ||
                    (row.backend != GpuAdaptiveBinningBackend.Direct && row.backend != GpuAdaptiveBinningBackend.Radix)) return;
                cells.Add(row.features, new Entry(row));
            }
            IsValid = true;
        }

        internal GpuAdaptiveBinningSelectionReason Lookup(in GpuAdaptiveBinningFeatures features,
            GpuDeviceFingerprint currentDevice, GpuCalibrationEnvironment currentEnvironment,
            string candidateId, bool profilerMarkersEnabled, out GpuAdaptiveBinningBackend backend)
        {
            backend = GpuAdaptiveBinningBackend.Direct;
            if (!IsValid) return GpuAdaptiveBinningSelectionReason.InvalidMatrix;
            if (!device.Equals(currentDevice) || !environment.Matches(currentEnvironment))
                return GpuAdaptiveBinningSelectionReason.IncompatibleEnvironment;
            if (ProfilerMarkersEnabled != profilerMarkersEnabled)
                return GpuAdaptiveBinningSelectionReason.IncompatibleInstrumentation;
            if (!cells.TryGetValue(features, out Entry entry)) return GpuAdaptiveBinningSelectionReason.UnknownCell;
            if (!entry.Validated) return GpuAdaptiveBinningSelectionReason.UnvalidatedCell;
            if (!string.Equals(entry.Candidate, candidateId, StringComparison.Ordinal))
                return GpuAdaptiveBinningSelectionReason.IncompatibleCandidate;
            backend = entry.Backend;
            return GpuAdaptiveBinningSelectionReason.CalibratedCell;
        }
    }

    public enum GpuAdaptiveBinningSelectionReason
    {
        CalibratedCell, HysteresisPending, UntrustedKeys, InvalidFeatures, InvalidMatrix,
        IncompatibleEnvironment, IncompatibleInstrumentation, UnknownCell, UnvalidatedCell,
        IncompatibleCandidate
    }

    public readonly struct GpuAdaptiveBinningDecision
    {
        internal GpuAdaptiveBinningDecision(GpuAdaptiveBinningBackend backend,
            GpuAdaptiveBinningSelectionReason reason, bool switched, long selectorTicks, long stateTicks)
        { Backend = backend; Reason = reason; Switched = switched;
          SelectorCpuTicks = selectorTicks; SwitchStateCpuTicks = stateTicks; }
        public GpuAdaptiveBinningBackend Backend { get; }
        public GpuAdaptiveBinningSelectionReason Reason { get; }
        public bool Switched { get; }
        public long SelectorCpuTicks { get; }
        public long SwitchStateCpuTicks { get; }
    }

    /// <summary>One selector per ordered input stream. Non-thread-safe. Radix promotion requires
    /// consecutive evidence for the same cell. Direct fallback is immediate and resets pending state;
    /// hysteresis never retains Radix on unknown, unvalidated or incompatible input.</summary>
    public sealed class GpuAdaptiveBinningStableSelector
    {
        private readonly GpuAdaptiveBinningMatrix matrix;
        private readonly GpuDeviceFingerprint device;
        private readonly GpuCalibrationEnvironment environment;
        private readonly int promotionCount;
        private GpuAdaptiveBinningFeatures pending;
        private int consecutive;
        private GpuAdaptiveBinningBackend current;

        public GpuAdaptiveBinningStableSelector(GpuAdaptiveBinningMatrix matrix,
            GpuDeviceFingerprint currentDevice, GpuCalibrationEnvironment currentEnvironment,
            int consecutiveRadixObservations = 3)
        {
            if (consecutiveRadixObservations < 1) throw new ArgumentOutOfRangeException(nameof(consecutiveRadixObservations));
            this.matrix = matrix; device = currentDevice?.Copy(); environment = currentEnvironment?.Copy();
            promotionCount = consecutiveRadixObservations;
        }
        public void Reset() { consecutive = 0; pending = default; current = GpuAdaptiveBinningBackend.Direct; }

        public GpuAdaptiveBinningDecision Select(in GpuAdaptiveBinningFeatures features,
            int elementCount, int binCount, GpuAdaptiveBinningKeyDomain keyDomain,
            string primitiveCandidateId, bool profilerMarkersEnabled)
        {
            long start = Stopwatch.GetTimestamp();
            GpuAdaptiveBinningBackend desired = GpuAdaptiveBinningBackend.Direct;
            GpuAdaptiveBinningSelectionReason reason;
            if (keyDomain != GpuAdaptiveBinningKeyDomain.GuaranteedInRange)
                reason = GpuAdaptiveBinningSelectionReason.UntrustedKeys;
            else if (!features.IsValid || features.elementCount != elementCount || features.binCount != binCount)
                reason = GpuAdaptiveBinningSelectionReason.InvalidFeatures;
            else if (matrix == null) reason = GpuAdaptiveBinningSelectionReason.InvalidMatrix;
            else reason = matrix.Lookup(in features, device, environment, primitiveCandidateId,
                profilerMarkersEnabled, out desired);
            long stateStart = Stopwatch.GetTimestamp();
            if (desired == GpuAdaptiveBinningBackend.Radix)
            {
                consecutive = pending.Equals(features) ? (consecutive < promotionCount ? consecutive + 1 : promotionCount) : 1;
                pending = features;
                if (consecutive < promotionCount && current != GpuAdaptiveBinningBackend.Radix)
                { desired = GpuAdaptiveBinningBackend.Direct; reason = GpuAdaptiveBinningSelectionReason.HysteresisPending; }
            }
            else { consecutive = 0; pending = default; }
            bool switched = current != desired; current = desired;
            long end = Stopwatch.GetTimestamp();
            return new GpuAdaptiveBinningDecision(desired, reason, switched, end - start, end - stateStart);
        }
    }
}
