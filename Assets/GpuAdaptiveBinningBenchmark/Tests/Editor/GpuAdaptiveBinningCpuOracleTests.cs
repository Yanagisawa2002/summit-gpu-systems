using System;
using NUnit.Framework;

namespace Summit.GpuAdaptiveBinning.Benchmark.Tests
{
    public sealed class GpuAdaptiveBinningCpuOracleTests
    {
        private static readonly uint[] Keys =
        {
            2u,
            0u,
            2u,
            1u,
            0u,
            1u,
        };

        // The benchmark contract uses a unique source identity value=i.
        private static readonly uint[] Values =
        {
            0u,
            1u,
            2u,
            3u,
            4u,
            5u,
        };

        private static readonly uint[] Counts =
        {
            2u,
            2u,
            2u,
        };

        private static readonly uint[] Offsets =
        {
            0u,
            2u,
            4u,
            6u,
        };

        private static readonly uint[] CanonicalValues =
        {
            1u,
            4u,
            3u,
            5u,
            0u,
            2u,
        };

        private static readonly uint[] Diagnostics =
        {
            0u,
            0u,
        };

        private const string ExpectedSha256 =
            "summit.gpu-adaptive-binning.canonical-csr.sha256.v1:" +
            "EDB0FC01B40343AC8E1C59D88D1243F7" +
            "FE97A86343ED9F56F2F21D3EA8C0943E";

        private const string ExpectedFastHash =
            "summit.gpu-adaptive-binning.canonical-csr.fnv1a32.v1:" +
            "56E1F002";

        [Test]
        public void CanonicalHashIsVersionedFrozenAndOrderInsensitivePerBin()
        {
            var oracle = new GpuAdaptiveBinningCpuOracle(
                Keys,
                Values,
                3);
            var permutedWithinBins = new[]
            {
                4u,
                1u,
                5u,
                3u,
                2u,
                0u,
            };

            Assert.That(
                GpuAdaptiveBinningCpuOracle.CanonicalHashSchema,
                Is.EqualTo(
                    "summit.gpu-adaptive-binning." +
                    "canonical-csr.sha256.v1"));
            Assert.That(oracle.BinCount, Is.EqualTo(3));
            Assert.That(oracle.ValidCount, Is.EqualTo(6));
            Assert.That(oracle.InvalidKeyCount, Is.Zero);
            Assert.That(oracle.DiagnosticFlags, Is.Zero);
            Assert.That(oracle.ResultHash, Is.EqualTo(ExpectedSha256));
            Assert.That(
                oracle.FastResultHash,
                Is.EqualTo(ExpectedFastHash));
            Assert.That(
                oracle.Validate(
                    Counts,
                    Offsets,
                    permutedWithinBins,
                    Diagnostics,
                    out string message,
                    out string actualHash),
                Is.True,
                message);
            Assert.That(actualHash, Is.EqualTo(ExpectedSha256));
        }

        [Test]
        public void ValidationIgnoresOnlyTheUndefinedValueTail()
        {
            var oracle = new GpuAdaptiveBinningCpuOracle(
                Keys,
                Values,
                3);
            var valuesWithUndefinedTail = new[]
            {
                1u,
                4u,
                3u,
                5u,
                0u,
                2u,
                0xDEADBEEFu,
                0xBAADF00Du,
            };

            Assert.That(
                oracle.Validate(
                    Counts,
                    Offsets,
                    valuesWithUndefinedTail,
                    Diagnostics,
                    out string message,
                    out string actualHash),
                Is.True,
                message);
            Assert.That(actualHash, Is.EqualTo(ExpectedSha256));
        }

        [Test]
        public void CanonicalResultHandlesSeedSelectedNonzeroSingleBin()
        {
            const int elementCount = 8;
            const int binCount = 16;
            const int seed = 20261001;
            var keys = new uint[elementCount];
            var values = new uint[elementCount];
            GpuAdaptiveBinningInputGenerator.Populate(
                keys,
                values,
                binCount,
                "singlebin",
                seed);

            uint selectedBin = unchecked((uint)seed) % binCount;
            Assert.That(selectedBin, Is.EqualTo(9u));
            Assert.That(keys, Is.All.EqualTo(selectedBin));

            var expectedCounts = new uint[binCount];
            expectedCounts[selectedBin] = elementCount;
            var expectedOffsets = new uint[binCount + 1];
            for (int bin = (int)selectedBin + 1;
                bin < expectedOffsets.Length;
                bin++)
            {
                expectedOffsets[bin] = elementCount;
            }
            var expectedValues = new uint[elementCount];
            for (uint index = 0; index < elementCount; index++)
            {
                expectedValues[index] = index;
            }

            var oracle = new GpuAdaptiveBinningCpuOracle(
                keys,
                values,
                binCount);
            Assert.That(oracle.ValidCount, Is.EqualTo(elementCount));
            Assert.That(oracle.InvalidKeyCount, Is.Zero);
            Assert.That(
                oracle.Validate(
                    expectedCounts,
                    expectedOffsets,
                    expectedValues,
                    Diagnostics,
                    out string message,
                    out string actualHash),
                Is.True,
                message);
            Assert.That(
                actualHash,
                Is.EqualTo(oracle.ResultHash));
        }

        [Test]
        public void ValidationRejectsEveryCsrAndDiagnosticCorruptionClass()
        {
            var oracle = new GpuAdaptiveBinningCpuOracle(
                Keys,
                Values,
                3);

            var corruptCounts = (uint[])Counts.Clone();
            corruptCounts[0]++;
            AssertRejected(
                oracle,
                corruptCounts,
                Offsets,
                CanonicalValues,
                Diagnostics,
                "Count mismatch");

            var corruptOffsets = (uint[])Offsets.Clone();
            corruptOffsets[1]--;
            AssertRejected(
                oracle,
                Counts,
                corruptOffsets,
                CanonicalValues,
                Diagnostics,
                "Offset mismatch");

            var corruptValues = (uint[])CanonicalValues.Clone();
            uint temporary = corruptValues[0];
            corruptValues[0] = corruptValues[2];
            corruptValues[2] = temporary;
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                corruptValues,
                Diagnostics,
                "Canonical membership mismatch");

            var corruptInvalidCount = (uint[])Diagnostics.Clone();
            corruptInvalidCount[0] = 1u;
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                CanonicalValues,
                corruptInvalidCount,
                "Diagnostics mismatch");

            var corruptFlags = (uint[])Diagnostics.Clone();
            corruptFlags[1] = 1u;
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                CanonicalValues,
                corruptFlags,
                "Diagnostics mismatch");
        }

        [Test]
        public void ValidationRejectsNullOrMalformedReadbackLengths()
        {
            var oracle = new GpuAdaptiveBinningCpuOracle(
                Keys,
                Values,
                3);

            AssertRejected(
                oracle,
                null,
                Offsets,
                CanonicalValues,
                Diagnostics,
                "null array");
            AssertRejected(
                oracle,
                new uint[Counts.Length - 1],
                Offsets,
                CanonicalValues,
                Diagnostics,
                "unexpected length");
            AssertRejected(
                oracle,
                Counts,
                new uint[Offsets.Length - 1],
                CanonicalValues,
                Diagnostics,
                "unexpected length");
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                new uint[CanonicalValues.Length - 1],
                Diagnostics,
                "unexpected length");
            AssertRejected(
                oracle,
                Counts,
                Offsets,
                CanonicalValues,
                new uint[3],
                "unexpected length");
        }

        [Test]
        public void ConstructorRejectsInvalidArrayContracts()
        {
            Assert.Throws<ArgumentNullException>(
                () => new GpuAdaptiveBinningCpuOracle(
                    null,
                    Values,
                    3));
            Assert.Throws<ArgumentNullException>(
                () => new GpuAdaptiveBinningCpuOracle(
                    Keys,
                    null,
                    3));
            Assert.Throws<ArgumentException>(
                () => new GpuAdaptiveBinningCpuOracle(
                    Keys,
                    new uint[Keys.Length - 1],
                    3));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuAdaptiveBinningCpuOracle(
                    Keys,
                    Values,
                    0));
        }

        private static void AssertRejected(
            GpuAdaptiveBinningCpuOracle oracle,
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
                    out string message,
                    out _),
                Is.False);
            StringAssert.Contains(expectedMessage, message);
        }
    }
}
