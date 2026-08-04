using System;
using System.Linq;
using NUnit.Framework;

namespace Summit.GpuAdaptiveBinning.Benchmark.Tests
{
    public sealed class GpuAdaptiveBinningInputDistributionTests
    {
        private const int ElementCount = 4097;
        private const int BinCount = 256;
        private const int Seed = 20260730;

        [TestCase("uniform", "39A24E40")]
        [TestCase("hotset4", "356529B5")]
        [TestCase("hotset16", "37D175F1")]
        [TestCase("singlebin", "64DE37EF")]
        public void EveryDistributionIsDeterministicValidAndFrozen(
            string distribution,
            string expectedHash)
        {
            var firstKeys = new uint[ElementCount];
            var firstValues = new uint[ElementCount];
            var secondKeys = new uint[ElementCount];
            var secondValues = new uint[ElementCount];

            GpuAdaptiveBinningInputGenerator.Populate(
                firstKeys,
                firstValues,
                BinCount,
                distribution,
                Seed);
            GpuAdaptiveBinningInputGenerator.Populate(
                secondKeys,
                secondValues,
                BinCount,
                distribution.ToUpperInvariant(),
                Seed);

            Assert.That(
                GpuAdaptiveBinningInputGenerator.GeneratorContract,
                Is.EqualTo("gpu-adaptive-binning-input-v3"));
            Assert.That(secondKeys, Is.EqualTo(firstKeys));
            Assert.That(secondValues, Is.EqualTo(firstValues));
            Assert.That(
                firstKeys.All(key => key < (uint)BinCount),
                Is.True);
            AssertSequentialUniqueValues(firstValues);
            Assert.That(
                ComputeInputHash(firstKeys, firstValues),
                Is.EqualTo(expectedHash));
        }

        [Test]
        public void UniformCoversTheFrozenPowerOfTwoDomain()
        {
            uint[] keys = PopulateKeys("uniform");

            Assert.That(keys.Min(), Is.Zero);
            Assert.That(keys.Max(), Is.EqualTo(255u));
            Assert.That(
                keys.Distinct().Count(),
                Is.EqualTo(BinCount));
        }

        [TestCase(1, "uniform")]
        [TestCase(3, "uniform")]
        [TestCase(17, "uniform")]
        [TestCase(257, "uniform")]
        [TestCase(17, "hotset4")]
        [TestCase(17, "hotset16")]
        [TestCase(1, "singlebin")]
        [TestCase(17, "singlebin")]
        public void NonPowerDomainsAreDeterministicAndValid(
            int binCount,
            string distribution)
        {
            var firstKeys = new uint[ElementCount];
            var firstValues = new uint[ElementCount];
            var secondKeys = new uint[ElementCount];
            var secondValues = new uint[ElementCount];

            GpuAdaptiveBinningInputGenerator.Populate(
                firstKeys,
                firstValues,
                binCount,
                distribution,
                Seed);
            GpuAdaptiveBinningInputGenerator.Populate(
                secondKeys,
                secondValues,
                binCount,
                distribution,
                Seed);

            Assert.That(secondKeys, Is.EqualTo(firstKeys));
            Assert.That(secondValues, Is.EqualTo(firstValues));
            Assert.That(
                firstKeys.All(key => key < (uint)binCount),
                Is.True);
            AssertSequentialUniqueValues(firstValues);
        }

        [TestCase("hotset4", 4, 3592)]
        [TestCase("hotset16", 16, 3622)]
        public void HotsetDistributionsHaveFrozenSevenEighthsSkew(
            string distribution,
            int hotBinCount,
            int expectedHotElementCount)
        {
            uint[] keys = PopulateKeys(distribution);

            Assert.That(keys.Min(), Is.Zero);
            Assert.That(keys.Max(), Is.EqualTo(255u));
            Assert.That(
                keys.Count(key => key < (uint)hotBinCount),
                Is.EqualTo(expectedHotElementCount));
            Assert.That(
                expectedHotElementCount,
                Is.GreaterThan(ElementCount * 3 / 4));
            Assert.That(
                expectedHotElementCount,
                Is.LessThan(ElementCount));
        }

        [Test]
        public void SingleBinUsesTheDocumentedSeedSelectedBin()
        {
            uint[] keys = PopulateKeys("singlebin");
            uint expectedBin = unchecked((uint)Seed) % (uint)BinCount;

            Assert.That(expectedBin, Is.Not.Zero);
            Assert.That(expectedBin, Is.LessThan((uint)BinCount));
            Assert.That(keys, Is.All.EqualTo(expectedBin));
            Assert.That(keys.Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void SingleBinIsReproducibleAndFormalSeedsSelectDistinctC16Bins()
        {
            const int formalBinCount = 16;
            const int firstFormalSeed = 20261001;
            const int secondFormalSeed = 20261002;
            var firstKeys = new uint[ElementCount];
            var firstValues = new uint[ElementCount];
            var repeatedKeys = new uint[ElementCount];
            var repeatedValues = new uint[ElementCount];
            var secondKeys = new uint[ElementCount];
            var secondValues = new uint[ElementCount];

            GpuAdaptiveBinningInputGenerator.Populate(
                firstKeys,
                firstValues,
                formalBinCount,
                "singlebin",
                firstFormalSeed);
            GpuAdaptiveBinningInputGenerator.Populate(
                repeatedKeys,
                repeatedValues,
                formalBinCount,
                "SINGLEBIN",
                firstFormalSeed);
            GpuAdaptiveBinningInputGenerator.Populate(
                secondKeys,
                secondValues,
                formalBinCount,
                "singlebin",
                secondFormalSeed);

            Assert.That(repeatedKeys, Is.EqualTo(firstKeys));
            Assert.That(repeatedValues, Is.EqualTo(firstValues));
            Assert.That(firstKeys, Is.All.EqualTo(9u));
            Assert.That(secondKeys, Is.All.EqualTo(10u));
            Assert.That(firstKeys[0], Is.Not.EqualTo(secondKeys[0]));
            Assert.That(
                firstKeys.All(key => key < formalBinCount),
                Is.True);
            Assert.That(
                secondKeys.All(key => key < formalBinCount),
                Is.True);
            Assert.That(secondValues, Is.EqualTo(firstValues));
            AssertSequentialUniqueValues(firstValues);
        }

        [Test]
        public void SeedChangesEveryDistributionButNotUniqueValues()
        {
            foreach (string distribution in new[]
            {
                "uniform",
                "hotset4",
                "hotset16",
                "singlebin",
            })
            {
                var firstKeys = new uint[ElementCount];
                var firstValues = new uint[ElementCount];
                var secondKeys = new uint[ElementCount];
                var secondValues = new uint[ElementCount];
                GpuAdaptiveBinningInputGenerator.Populate(
                    firstKeys,
                    firstValues,
                    BinCount,
                    distribution,
                    Seed);
                GpuAdaptiveBinningInputGenerator.Populate(
                    secondKeys,
                    secondValues,
                    BinCount,
                    distribution,
                    Seed + 1);

                Assert.That(secondKeys, Is.Not.EqualTo(firstKeys));
                Assert.That(secondValues, Is.EqualTo(firstValues));
                AssertSequentialUniqueValues(secondValues);
            }
        }

        [Test]
        public void GeneratorRejectsAmbiguousOrUnsupportedContracts()
        {
            var keys = new uint[8];
            var values = new uint[8];

            Assert.Throws<ArgumentNullException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    null,
                    values,
                    16,
                    "uniform",
                    Seed));
            Assert.Throws<ArgumentNullException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    null,
                    16,
                    "uniform",
                    Seed));
            Assert.Throws<ArgumentException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    keys,
                    16,
                    "uniform",
                    Seed));
            Assert.Throws<ArgumentException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    new uint[7],
                    16,
                    "uniform",
                    Seed));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    values,
                    0,
                    "uniform",
                    Seed));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    values,
                    16,
                    null,
                    Seed));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    values,
                    16,
                    "zipf",
                    Seed));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    values,
                    2,
                    "hotset4",
                    Seed));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuAdaptiveBinningInputGenerator.Populate(
                    keys,
                    values,
                    8,
                    "hotset16",
                    Seed));
        }

        private static uint[] PopulateKeys(string distribution)
        {
            var keys = new uint[ElementCount];
            var values = new uint[ElementCount];
            GpuAdaptiveBinningInputGenerator.Populate(
                keys,
                values,
                BinCount,
                distribution,
                Seed);
            return keys;
        }

        private static void AssertSequentialUniqueValues(uint[] values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                Assert.That(
                    values[index],
                    Is.EqualTo((uint)index));
            }
            Assert.That(
                values.Distinct().Count(),
                Is.EqualTo(values.Length));
        }

        private static string ComputeInputHash(
            params uint[][] arrays)
        {
            const uint fnvOffset = 2166136261u;
            const uint fnvPrime = 16777619u;
            uint hash = fnvOffset;
            unchecked
            {
                foreach (uint[] values in arrays)
                {
                    foreach (uint value in values)
                    {
                        hash = (hash ^ (byte)value) * fnvPrime;
                        hash = (hash ^ (byte)(value >> 8)) * fnvPrime;
                        hash = (hash ^ (byte)(value >> 16)) * fnvPrime;
                        hash = (hash ^ (byte)(value >> 24)) * fnvPrime;
                    }
                }
            }
            return hash.ToString("X8");
        }
    }
}
