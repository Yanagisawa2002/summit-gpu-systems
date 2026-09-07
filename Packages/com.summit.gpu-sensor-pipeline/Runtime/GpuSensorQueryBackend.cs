namespace Summit.GpuSensorPipeline
{
    /// <summary>Explicit query candidates; CellSerial remains the measured baseline.</summary>
    public enum GpuSensorQueryBackend
    {
        CellSerial = 0,
        PointChunks = 1,
        PointChunksWave = 2
    }
}
