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
    }
}
