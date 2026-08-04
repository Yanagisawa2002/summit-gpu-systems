using System;
using System.Linq;
using NUnit.Framework;

namespace Summit.GpuDirectBinning.Benchmark.Tests
{
    public sealed class GpuDirectBinningInputDistributionTests
    {
        private const int ElementCount = 4097;
        private const int BinCount = 256;
        private const int Seed = 20260730;

        [Test]
        public void UniformInputIsDeterministicInRangeAndMatchesFrozenHash()
        {
            var firstKeys = new uint[ElementCount];
            var firstValues = new uint[ElementCount];
            var secondKeys = new uint[ElementCount];
            var secondValues = new uint[ElementCount];

            GpuDirectBinningInputGenerator.Populate(
                firstKeys,
                firstValues,
                BinCount,
                "uniform",
                Seed);
            GpuDirectBinningInputGenerator.Populate(
                secondKeys,
                secondValues,
                BinCount,
                "UNIFORM",
                Seed);

            Assert.That(secondKeys, Is.EqualTo(firstKeys));
            Assert.That(secondValues, Is.EqualTo(firstValues));
            Assert.That(firstKeys.Min(), Is.Zero);
            Assert.That(firstKeys.Max(), Is.EqualTo(255u));
            Assert.That(
                firstKeys.All(key => key < BinCount),
                Is.True);
            AssertSequentialValues(firstValues);
            Assert.That(
                firstKeys.Take(16),
                Is.EqualTo(new uint[]
                {
                    4u,
                    138u,
                    197u,
                    245u,
                    162u,
                    87u,
                    35u,
                    49u,
                    153u,
                    57u,
                    177u,
                    10u,
                    9u,
                    177u,
                    164u,
                    33u,
                }));
            Assert.That(
                ComputeInputHash(firstKeys, firstValues),
                Is.EqualTo("39A24E40"));
        }

        [Test]
        public void HotsetInputIsDeterministicSkewedAndMatchesFrozenHash()
        {
            var firstKeys = new uint[ElementCount];
            var firstValues = new uint[ElementCount];
            var secondKeys = new uint[ElementCount];
            var secondValues = new uint[ElementCount];

            GpuDirectBinningInputGenerator.Populate(
                firstKeys,
                firstValues,
                BinCount,
                "hotset16",
                Seed);
            GpuDirectBinningInputGenerator.Populate(
                secondKeys,
                secondValues,
                BinCount,
                "HOTSET16",
                Seed);

            Assert.That(secondKeys, Is.EqualTo(firstKeys));
            Assert.That(secondValues, Is.EqualTo(firstValues));
            Assert.That(firstKeys.Min(), Is.Zero);
            Assert.That(firstKeys.Max(), Is.EqualTo(255u));
            Assert.That(
                firstKeys.All(key => key < BinCount),
                Is.True);
            Assert.That(
                firstKeys.Count(key => key < 16u),
                Is.EqualTo(3622));
            AssertSequentialValues(firstValues);
            Assert.That(
                firstKeys.Take(16),
                Is.EqualTo(new uint[]
                {
                    0u,
                    1u,
                    8u,
                    14u,
                    4u,
                    10u,
                    4u,
                    6u,
                    3u,
                    7u,
                    6u,
                    1u,
                    1u,
                    6u,
                    4u,
                    4u,
                }));
            Assert.That(
                ComputeInputHash(firstKeys, firstValues),
                Is.EqualTo("37D175F1"));
        }

        private static void AssertSequentialValues(uint[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                Assert.That(values[index], Is.EqualTo((uint)index));
            }
        }

        private static string ComputeInputHash(params uint[][] arrays)
        {
            const uint fnvOffset = 2166136261u;
            const uint fnvPrime = 16777619u;
            uint hash = fnvOffset;
            unchecked
            {
                foreach (var values in arrays)
                {
                    foreach (var value in values)
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
