using System;

namespace Summit.GpuDrivenInstances
{
    [Flags]
    public enum GpuDrivenInstanceErrorFlags : uint
    {
        None = 0u,
        InvalidRadius = 1u << 0,
        InvalidLodContract = 1u << 1,
        InvalidDrawGroupRange = 1u << 2,
        InvalidViewLodScale = 1u << 3,

        /// <summary>
        /// Cluster descriptors do not form one exact, contiguous cover of the
        /// active instance prefix, exceed the 64-instance limit, or contain a
        /// non-finite or negative-radius coarse bound.
        /// </summary>
        InvalidClusterContract = 1u << 4,

        /// <summary>
        /// Dense visible-pair production exceeded its declared capacity.
        /// This cannot occur when the active counts and cluster contract are
        /// valid, and therefore indicates a producer invariant failure.
        /// </summary>
        HierarchicalPairCapacityExceeded = 1u << 5,

        /// <summary>
        /// The pre-counted scan/scatter stage rejected an inconsistent GPU
        /// count, bin-count sum, dispatch dimension, key, or destination.
        /// </summary>
        HierarchicalBinningInvariantViolation = 1u << 6,
    }
}
