namespace Summit.GpuAdaptiveBinning
{
    /// <summary>
    /// Explicit spatial-binning implementation selection.
    /// </summary>
    public enum GpuAdaptiveBinningBackend
    {
        Direct = 0,
        Radix = 1,
    }
}
