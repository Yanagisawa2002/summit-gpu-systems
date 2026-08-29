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
            int observedVisible = 0;
            for (int index = 0; index < instances.Length; index++)
            {
                float x = instances[index].PositionRadius.x;
                bool isVisible = x < 100f;
                bool isRejected = x >= 1000f;
                Assert.That(
                    isVisible || isRejected,
                    Is.True,
                    $"Unexpected visibility placement at {index}.");
                if (isVisible)
                {
                    observedVisible++;
                }
                Assert.That(instances[index].ViewMask, Is.EqualTo(3u));
                Assert.That(instances[index].LodCount, Is.EqualTo(1u));
                Assert.That(instances[index].DrawGroupBase, Is.LessThan(8u));
            }
            Assert.That(observedVisible, Is.EqualTo(expectedVisible));
        }

        [Test]
        public void VisibleMembershipIsSeededExactAndDispersed()
        {
            bool[] first = GenerateVisibleMembership(200, 20260829);
            bool[] repeat = GenerateVisibleMembership(200, 20260829);
            bool[] differentSeed = GenerateVisibleMembership(200, 20260830);

            Assert.That(repeat, Is.EqualTo(first));
            Assert.That(differentSeed, Is.Not.EqualTo(first));
            Assert.That(
                System.Array.FindAll(first, value => value),
                Has.Length.EqualTo(50));
            int transitions = 0;
            for (int index = 1; index < first.Length; index++)
            {
                if (first[index] != first[index - 1])
                {
                    transitions++;
                }
            }
            Assert.That(transitions, Is.GreaterThan(2));
        }

        [Test]
        public void UnsupportedVisibilityCellIsRejected()
        {
            Assert.That(
                () => GpuDrivenInstanceInputGenerator.ParseVisibilityPercent(
                    "visible50"),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        private static bool[] GenerateVisibleMembership(int count, int seed)
        {
            var instances = new GpuInstanceState[count];
            var planes = new Vector4[6];
            var views = new Vector4[1];
            var draws = new GpuDrawTemplate[8];
            int visible = GpuDrivenInstanceInputGenerator.Populate(
                instances,
                planes,
                views,
                draws,
                "visible25",
                seed);
            Assert.That(visible, Is.EqualTo(count / 4));

            var membership = new bool[count];
            for (int index = 0; index < count; index++)
            {
                membership[index] =
                    instances[index].PositionRadius.x < 100f;
            }
            return membership;
        }
    }
}
