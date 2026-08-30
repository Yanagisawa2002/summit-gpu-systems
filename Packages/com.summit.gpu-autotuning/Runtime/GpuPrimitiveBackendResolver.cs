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
            return TryResolveMeasured(
                workloadId,
                out GpuPrimitiveBackend backend)
                    ? backend
                    : GpuPrimitiveBackend.Auto;
        }

        /// <summary>
        /// Resolves an accepted choice only when the workload and exact device
        /// are both present in the measured profile.
        /// </summary>
        public bool TryResolveMeasured(
            string workloadId,
            out GpuPrimitiveBackend backend)
        {
            backend = GpuPrimitiveBackend.Auto;
            return profile != null &&
                profile.TryResolve(workloadId, device, out backend);
        }

        /// <summary>
        /// Resolves only holdout-accepted, exact-device profile choices. A
        /// missing or stale choice remains on Portable instead of delegating to
        /// capability-only Auto selection.
        /// </summary>
        public GpuPrimitiveBackend ResolveMeasuredOrPortable(string workloadId)
        {
            return TryResolveMeasured(
                workloadId,
                out GpuPrimitiveBackend resolved)
                    ? resolved
                    : GpuPrimitiveBackend.Portable;
        }
    }
}
