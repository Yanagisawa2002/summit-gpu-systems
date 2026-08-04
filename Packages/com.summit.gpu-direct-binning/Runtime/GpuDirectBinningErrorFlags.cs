using System;

namespace Summit.GpuDirectBinning
{
    /// <summary>
    /// GPU-written diagnostic bits for one direct-binning recording.
    /// </summary>
    [Flags]
    public enum GpuDirectBinningErrorFlags : uint
    {
        None = 0u,

        /// <summary>
        /// At least one input key was outside the requested bin domain.
        /// Invalid inputs are counted and excluded from the CSR payload.
        /// </summary>
        InvalidKeyEncountered = 1u << 0,

        /// <summary>
        /// Atomic scatter produced a destination outside the logical input
        /// count. This indicates a broken count/scan/scatter invariant.
        /// </summary>
        ScatterDestinationOutOfRange = 1u << 1
    }
}
