using System;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    [Serializable]
    public sealed class GpuAutotuneProfile
    {
        public const int CurrentSchemaVersion = 2;
        private static readonly string[] LegacyCandidateIds = { "Portable", "WaveOps" };

        public int schemaVersion = CurrentSchemaVersion;
        public string generatedUtc = string.Empty;
        public GpuCalibrationEnvironment environment;
        public string sourceCommit = string.Empty;
        public string calibrationProtocol = string.Empty;
        public GpuDeviceFingerprint device = new GpuDeviceFingerprint();
        public GpuAutotuneWorkloadSelection[] workloads =
            Array.Empty<GpuAutotuneWorkloadSelection>();

        public bool TryResolve(
            string workloadId,
            GpuDeviceFingerprint currentDevice,
            out GpuPrimitiveBackend backend)
        {
            // Legacy callers cannot establish an independent build identity.
            backend = GpuPrimitiveBackend.Auto;
            return false;
        }

        public bool IsCompatible(GpuDeviceFingerprint currentDevice,
            GpuCalibrationEnvironment currentEnvironment) =>
            schemaVersion == CurrentSchemaVersion && device != null &&
            device.Equals(currentDevice) && environment != null &&
            environment.Matches(currentEnvironment);

        public bool TryResolve(string workloadId, GpuDeviceFingerprint currentDevice,
            GpuCalibrationEnvironment currentEnvironment, out GpuPrimitiveBackend backend)
        {
            backend = GpuPrimitiveBackend.Auto;
            if (!TryResolveCandidate(workloadId, currentDevice, currentEnvironment,
                LegacyCandidateIds, out string candidate, out _)) return false;
            return Enum.TryParse(candidate, false, out backend);
        }

        /// <summary>Candidate IDs are opaque, versioned identifiers. The caller passes only
        /// candidates executable by its current primitive implementation and capabilities.</summary>
        public bool TryResolveCandidate(string workloadId, GpuDeviceFingerprint currentDevice,
            GpuCalibrationEnvironment currentEnvironment,
            System.Collections.Generic.IReadOnlyList<string> supportedCandidateIds,
            out string candidateId, out string reason)
        {
            candidateId = null;
            reason = "incompatible-calibration";
            if (!IsCompatible(currentDevice, currentEnvironment)) return false;
            reason = "unknown-workload";
            if (string.IsNullOrWhiteSpace(workloadId) || workloads == null) return false;
            GpuAutotuneWorkloadSelection found = null;
            foreach (var row in workloads)
            {
                if (row == null || !string.Equals(row.workloadId, workloadId,
                    StringComparison.Ordinal)) continue;
                if (found != null) { reason = "duplicate-workload"; return false; }
                found = row;
            }
            if (found == null) return false;
            reason = "unvalidated-candidate";
            if (!found.accepted) return false;
            string selected = string.IsNullOrEmpty(found.selectedCandidateId)
                ? found.selectedBackend : found.selectedCandidateId;
            reason = "unsupported-candidate";
            if (supportedCandidateIds != null)
                for (int i = 0; i < supportedCandidateIds.Count; i++)
                    if (!string.IsNullOrWhiteSpace(selected) && string.Equals(selected,
                        supportedCandidateIds[i], StringComparison.Ordinal))
                    { candidateId = selected; reason = "validated-candidate"; return true; }
            return false;
        }
    }

    [Serializable]
    public sealed class GpuAutotuneWorkloadSelection
    {
        public string workloadId = string.Empty;
        public string baselineBackend = "Portable";
        public string selectedCandidateId = string.Empty;
        public string selectedBackend = "Auto";
        public bool accepted;
        public int calibrationSamplesPerCandidate;
        public double baselineMedianMs;
        public double selectedMedianMs;
        public double baselineP99Ms;
        public double selectedP99Ms;
        public double calibrationImprovementPercent;
    }
}
