using System;

namespace Summit.GpuSensorPipeline.Tests
{
    /// <summary>
    /// Keeps grouped assertion call sites source-compatible with the NUnit
    /// version embedded by this Unity project.
    /// </summary>
    internal static class GpuSensorTestAssert
    {
        public static void Multiple(Action assertions)
        {
            if (assertions == null)
            {
                throw new ArgumentNullException(nameof(assertions));
            }

            assertions();
        }
    }
}
