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
        ScatterDestinationOutOfRange = 1u << 1,

        /// <summary>
        /// A GPU-resident prefix count exceeded the binner's fixed element
        /// capacity. The precounted indirect path writes a zero-X dispatch.
        /// </summary>
        PrecountedElementCountOutOfRange = 1u << 2,

        /// <summary>
        /// Producer-provided bin counts did not describe the GPU-resident
        /// dense prefix. This includes a terminal sum mismatch, a bin range
        /// outside capacity, or more scattered keys than a bin declared.
        /// </summary>
        PrecountedCountMismatch = 1u << 3,

        /// <summary>
        /// The GPU prefix would require more than 65,535 compute groups in
        /// one dispatch dimension. The indirect path writes a zero-X
        /// dispatch to preserve the D3D12 dispatch contract.
        /// </summary>
        IndirectDispatchDimensionOutOfRange = 1u << 4
    }
}
