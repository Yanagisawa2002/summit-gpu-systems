using NUnit.Framework;
using Summit.GpuDrivenInstances;
using UnityEngine;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceInputGeneratorTests
    {
        [TestCase("visible5", 5)]
        [TestCase("visible25", 25)]
        [TestCase("visible75", 75)]
        [TestCase("visible100", 100)]
        public void VisibilityCellsProduceExactDeterministicCounts(
            string visibility,
            int expectedVisible)
        {
            var instances = new GpuInstanceState[100];
            var planes = new Vector4[12];
            var views = new Vector4[2];
            var draws = new GpuDrawTemplate[8];

            int actualVisible = GpuDrivenInstanceInputGenerator.Populate(
                instances,
                planes,
                views,
                draws,
                visibility,
                20260829);

            Assert.That(actualVisible, Is.EqualTo(expectedVisible));
            for (int index = 0; index < instances.Length; index++)
            {
                float x = instances[index].PositionRadius.x;
                Assert.That(
                    index < expectedVisible ? x < 100f : x >= 1000f,
                    Is.True,
                    $"Unexpected visibility placement at {index}.");
                Assert.That(instances[index].ViewMask, Is.EqualTo(3u));
                Assert.That(instances[index].LodCount, Is.EqualTo(1u));
                Assert.That(instances[index].DrawGroupBase, Is.LessThan(8u));
            }
        }

        [Test]
        public void UnsupportedVisibilityCellIsRejected()
        {
            Assert.That(
                () => GpuDrivenInstanceInputGenerator.ParseVisibilityPercent(
                    "visible50"),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }
    }
}
