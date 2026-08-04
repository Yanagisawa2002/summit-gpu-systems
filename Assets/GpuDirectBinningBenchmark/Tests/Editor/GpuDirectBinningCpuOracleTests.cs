using NUnit.Framework;

namespace Summit.GpuDirectBinning.Benchmark.Tests
{
    public sealed class GpuDirectBinningCpuOracleTests
    {
        private static readonly uint[] Keys =
        {
            2u,
            0u,
            2u,
            1u,
            0u,
            3u,
        };

        private static readonly uint[] Values =
        {
            20u,
            10u,
            21u,
            30u,
            11u,
            99u,
        };

        private static readonly uint[] Counts =
        {
            2u,
            1u,
            2u,
        };

        private static readonly uint[] Offsets =
        {
            0u,
            2u,
            3u,
            5u,
        };

        private static readonly uint[] Diagnostics =
        {
            1u,
            1u,
        };

        [Test]
        public void ValidationIsOrderInsensitiveWithinEachCsrBin()
        {
            var oracle = new GpuDirectBinningCpuOracle(
                Keys,
                Values,
                3);
            var firstOrder = new[]
            {
                11u,
                10u,
                30u,
                21u,
                20u,
            };
            var secondOrder = new[]
            {
                10u,
                11u,
                30u,
                20u,
                21u,
            };

            Assert.That(oracle.ValidCount, Is.EqualTo(5));
            Assert.That(oracle.InvalidKeyCount, Is.EqualTo(1u));
            Assert.That(
                oracle.Validate(
                    Counts,
                    Offsets,
                    firstOrder,
                    Diagnostics,
                    out var firstMessage,
                    out var firstHash),
                Is.True,
                firstMessage);
            Assert.That(
                oracle.Validate(
                    Counts,
                    Offsets,
                    secondOrder,
                    Diagnostics,
                    out var secondMessage,
                    out var secondHash),
                Is.True,
                secondMessage);
            Assert.That(firstHash, Is.EqualTo(oracle.ResultHash));
            Assert.That(secondHash, Is.EqualTo(oracle.ResultHash));
            Assert.That(secondHash, Is.EqualTo(firstHash));
        }

        [Test]
        public void ValidationRejectsEveryCsrAndDiagnosticCorruptionClass()
        {
            var oracle = new GpuDirectBinningCpuOracle(
                Keys,
                Values,
                3);
            var canonicalValues = new[]
            {
                10u,
                11u,
                30u,
                20u,
                21u,
            };

            var corruptCounts = (uint[])Counts.Clone();
            corruptCounts[0]++;
            AssertRejected(
                oracle,
                corruptCounts,
                Offsets,
                canonicalValues,
                Diagnostics,
                "Count mismatch");

            var corruptOffsets = (uint[])Offsets.Clone();
            corruptOffsets[1]--;
            AssertRejected(
                oracle,
                Counts,
                corruptOffsets,
                canonicalValues,
                Diagnostics,
                "Offset mismatch");

            var corruptValues = (uint[])canonicalValues.Clone();
            corruptValues[0] = uint.MaxValue;
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                corruptValues,
                Diagnostics,
                "Canonical membership mismatch");

            var corruptDiagnostics = (uint[])Diagnostics.Clone();
            corruptDiagnostics[0] = 0u;
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                canonicalValues,
                corruptDiagnostics,
                "Diagnostics mismatch");
        }

        private static void AssertRejected(
            GpuDirectBinningCpuOracle oracle,
            uint[] counts,
            uint[] offsets,
            uint[] values,
            uint[] diagnostics,
            string expectedMessage)
        {
            Assert.That(
                oracle.Validate(
                    counts,
                    offsets,
                    values,
                    diagnostics,
                    out var message,
                    out _),
                Is.False);
            StringAssert.Contains(expectedMessage, message);
        }
    }
}
