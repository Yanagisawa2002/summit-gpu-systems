namespace Summit.GpuPrimitives
{
    /// <summary>
    /// Selects the implementation used when GPU work is recorded.
    /// </summary>
    public enum GpuPrimitiveBackend
    {
        /// <summary>
        /// Select the operation's conservative automatic backend. Most
        /// primitives use WaveOps when Shader Model 6 is available; histogram
        /// stays Portable because its best backend depends on input contention.
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
