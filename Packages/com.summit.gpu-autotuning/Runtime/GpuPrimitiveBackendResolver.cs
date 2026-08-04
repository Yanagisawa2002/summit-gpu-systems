using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    public sealed class GpuPrimitiveBackendResolver
    {
        private readonly GpuAutotuneProfile profile;
        private readonly GpuDeviceFingerprint device;

        public GpuPrimitiveBackendResolver(
            GpuAutotuneProfile profile,
            GpuDeviceFingerprint device = null)
        {
            this.profile = profile;
            this.device = device ?? GpuDeviceFingerprint.Capture();
        }

        public GpuPrimitiveBackend Resolve(string workloadId)
        {
            return profile != null &&
                profile.TryResolve(workloadId, device, out GpuPrimitiveBackend backend)
                    ? backend
                    : GpuPrimitiveBackend.Auto;
        }
    }
}
