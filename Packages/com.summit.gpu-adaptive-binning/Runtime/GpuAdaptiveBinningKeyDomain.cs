namespace Summit.GpuAdaptiveBinning
{
    /// <summary>
    /// Declares whether GPU keys have been validated by an upstream producer.
    /// </summary>
    public enum GpuAdaptiveBinningKeyDomain
    {
        Untrusted = 0,
        GuaranteedInRange = 1,
    }
}
