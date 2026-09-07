using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    public sealed class GpuPrimitiveBackendResolver
    {
        private readonly GpuAutotuneProfile profile;
        private readonly GpuDeviceFingerprint device;
        private readonly GpuCalibrationEnvironment environment;

        public GpuPrimitiveBackendResolver(
            GpuAutotuneProfile profile,
            GpuDeviceFingerprint device = null,
            GpuCalibrationEnvironment environment = null)
        {
            this.profile = profile;
            this.environment = environment?.Copy();
            this.device = device?.Copy() ?? GpuDeviceFingerprint.Capture();
        }

        public GpuPrimitiveBackend Resolve(string workloadId)
        {
            return profile != null &&
                profile.TryResolve(workloadId, device, environment, out GpuPrimitiveBackend backend)
                    ? backend
                    : GpuPrimitiveBackend.Auto;
        }
    }
}
