using System;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    [Serializable]
    public sealed class GpuAutotuneProfile
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public string generatedUtc = string.Empty;
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
            backend = GpuPrimitiveBackend.Auto;
            if (schemaVersion != CurrentSchemaVersion ||
                device == null ||
                !device.Equals(currentDevice) ||
                string.IsNullOrWhiteSpace(workloadId) ||
                workloads == null)
            {
                return false;
            }
            for (int i = 0; i < workloads.Length; i++)
            {
                GpuAutotuneWorkloadSelection selection = workloads[i];
                if (selection != null &&
                    selection.accepted &&
                    string.Equals(selection.workloadId, workloadId,
                        StringComparison.Ordinal) &&
                    Enum.TryParse(selection.selectedBackend, true, out backend))
                {
                    return backend == GpuPrimitiveBackend.Portable ||
                        backend == GpuPrimitiveBackend.WaveOps;
                }
            }
            backend = GpuPrimitiveBackend.Auto;
            return false;
        }
    }

    [Serializable]
    public sealed class GpuAutotuneWorkloadSelection
    {
        public string workloadId = string.Empty;
        public string baselineBackend = "Portable";
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
