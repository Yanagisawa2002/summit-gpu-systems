namespace Summit.GpuResidencyManager
{
    public readonly struct GpuPageRequest
    {
        public GpuPageRequest(int virtualPage, int priority = 0, bool prefetch = false)
        { VirtualPage = virtualPage; Priority = priority; Prefetch = prefetch; }
        public int VirtualPage { get; }
        public int Priority { get; }
        public bool Prefetch { get; }
    }
}
