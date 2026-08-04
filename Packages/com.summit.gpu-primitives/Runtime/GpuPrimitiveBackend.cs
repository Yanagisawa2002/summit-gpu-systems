namespace Summit.GpuPrimitives
{
    /// <summary>
    /// Selects the implementation used when GPU work is recorded.
    /// </summary>
    public enum GpuPrimitiveBackend
    {
        /// <summary>
        /// Select WaveOps when the active graphics API advertises Shader Model 6,
        /// otherwise select the portable implementation.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Shader Model 5 group-shared and atomic implementation.
        /// </summary>
        Portable = 1,

        /// <summary>
        /// Shader Model 6 implementation using wave prefix and vote operations.
        /// </summary>
        WaveOps = 2
    }
}
