using System;
using System.Linq;
using NUnit.Framework;

namespace Summit.GpuDirectBinning.Tests
{
    public sealed class CpuDirectBinningOracleTests
    {
        [Test]
        public void EmptyInputProducesWellFormedEmptyCsr()
        {
            const int binCount = 17;
            var result = CpuDirectBinningOracle.Build(
                Array.Empty<uint>(),
                Array.Empty<uint>(),
                binCount);

            Assert.That(result.Counts, Is.EqualTo(new uint[binCount]));
            Assert.That(result.Offsets, Is.EqualTo(new uint[binCount + 1]));
            Assert.That(result.BinnedValues, Is.Empty);
            Assert.That(result.InvalidKeyCount, Is.Zero);
            Assert.That(result.ErrorFlags, Is.Zero);
        }

        [TestCaseSource(
            typeof(CpuDirectBinningOracle),
            nameof(CpuDirectBinningOracle.BoundarySizes))]
        public void ValidBoundaryInputsPreserveCountsOffsetsAndMembership(
            int elementCount)
        {
            const int binCount = 17;
            var keys = CpuDirectBinningOracle.CreateValidKeys(
                elementCount,
                binCount);
            var values = CpuDirectBinningOracle.CreatePayload(elementCount);
            var result = CpuDirectBinningOracle.Build(
                keys,
                values,
                binCount);

            AssertCsrInvariants(result, elementCount, binCount);
            Assert.That(result.InvalidKeyCount, Is.Zero);
            Assert.That(result.ErrorFlags, Is.Zero);
            Assert.That(
                result.BinnedValues.OrderBy(value => value),
                Is.EqualTo(values));
            AssertPayloadKeyAssociation(
                keys,
                result.BinnedValues,
                result.Offsets,
                result.Counts);
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(17)]
        [TestCase(257)]
        public void NonPowerOfTwoBinCountsProduceValidCsr(int binCount)
        {
            const int elementCount = 4097;
            var keys = CpuDirectBinningOracle.CreateValidKeys(
                elementCount,
                binCount);
            var values = CpuDirectBinningOracle.CreatePayload(elementCount);
            var result = CpuDirectBinningOracle.Build(
                keys,
                values,
                binCount);

            AssertCsrInvariants(result, elementCount, binCount);
            AssertPayloadKeyAssociation(
                keys,
                result.BinnedValues,
                result.Offsets,
                result.Counts);
        }

        [Test]
        public void MixedInvalidKeysAreExcludedAndReported()
        {
            const int elementCount = 257;
            const int binCount = 17;
            var keys = CpuDirectBinningOracle.CreateMixedValidityKeys(
                elementCount,
                binCount);
            var values = CpuDirectBinningOracle.CreatePayload(elementCount);
            var result = CpuDirectBinningOracle.Build(
                keys,
                values,
                binCount);
            var expectedInvalid = keys.Count(key => key >= binCount);

            AssertCsrInvariants(
                result,
                elementCount - expectedInvalid,
                binCount);
            Assert.That(
                result.InvalidKeyCount,
                Is.EqualTo((uint)expectedInvalid));
            Assert.That(
                result.ErrorFlags & CpuDirectBinningOracle.InvalidKeyErrorBit,
                Is.Not.Zero);
            Assert.That(
                result.BinnedValues.OrderBy(value => value),
                Is.EqualTo(
                    values.Where((_, index) => keys[index] < binCount)));
        }

        [Test]
        public void AllInvalidKeysProduceEmptyCsrAndDiagnostic()
        {
            const int elementCount = 65;
            const int binCount = 3;
            var keys = Enumerable.Repeat(
                (uint)binCount,
                elementCount).ToArray();
            var values = CpuDirectBinningOracle.CreatePayload(elementCount);
            var result = CpuDirectBinningOracle.Build(
                keys,
                values,
                binCount);

            AssertCsrInvariants(result, 0, binCount);
            Assert.That(result.BinnedValues, Is.Empty);
            Assert.That(
                result.InvalidKeyCount,
                Is.EqualTo((uint)elementCount));
            Assert.That(result.ErrorFlags, Is.EqualTo(1u));
        }

        [Test]
        public void OneBinContentionPreservesEveryPayload()
        {
            const int elementCount = 4097;
            var keys = new uint[elementCount];
            var values = CpuDirectBinningOracle.CreatePayload(elementCount);
            var result = CpuDirectBinningOracle.Build(keys, values, 1);

            Assert.That(
                result.Counts,
                Is.EqualTo(new[] { (uint)elementCount }));
            Assert.That(
                result.Offsets,
                Is.EqualTo(new[] { 0u, (uint)elementCount }));
            Assert.That(
                CpuDirectBinningOracle.CanonicalizeBins(
                    result.BinnedValues,
                    result.Offsets,
                    result.Counts),
                Is.EqualTo(values));
        }

        [Test]
        public void PerBinCanonicalizationIgnoresUnspecifiedScatterOrder()
        {
            var counts = new[] { 2u, 3u };
            var offsets = new[] { 0u, 2u, 5u };
            var first = new[] { 7u, 2u, 11u, 3u, 5u };
            var second = new[] { 2u, 7u, 5u, 11u, 3u };

            Assert.That(
                CpuDirectBinningOracle.CanonicalizeBins(
                    first,
                    offsets,
                    counts),
                Is.EqualTo(
                    CpuDirectBinningOracle.CanonicalizeBins(
                        second,
                        offsets,
                        counts)));
        }

        [Test]
        public void OracleRejectsMalformedInputs()
        {
            Assert.Throws<ArgumentNullException>(
                () => CpuDirectBinningOracle.Build(
                    null,
                    Array.Empty<uint>(),
                    1));
            Assert.Throws<ArgumentNullException>(
                () => CpuDirectBinningOracle.Build(
                    Array.Empty<uint>(),
                    null,
                    1));
            Assert.Throws<ArgumentException>(
                () => CpuDirectBinningOracle.Build(
                    new uint[1],
                    new uint[2],
                    1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CpuDirectBinningOracle.Build(
                    Array.Empty<uint>(),
                    Array.Empty<uint>(),
                    0));
            Assert.Throws<ArgumentException>(
                () => CpuDirectBinningOracle.CanonicalizeBins(
                    new uint[1],
                    new[] { 0u, 1u },
                    new[] { 0u }));
        }

        internal static void AssertCsrInvariants(
            CpuDirectBinningResult result,
            int expectedValidCount,
            int binCount)
        {
            Assert.That(result.Counts, Has.Length.EqualTo(binCount));
            Assert.That(result.Offsets, Has.Length.EqualTo(binCount + 1));
            Assert.That(result.Offsets[0], Is.Zero);
            Assert.That(result.ValidCount, Is.EqualTo(expectedValidCount));
            Assert.That(
                result.Counts.Sum(count => (long)count),
                Is.EqualTo(expectedValidCount));

            for (var bin = 0; bin < binCount; bin++)
            {
                Assert.That(
                    result.Offsets[bin + 1],
                    Is.EqualTo(result.Offsets[bin] + result.Counts[bin]),
                    $"Malformed CSR range at bin {bin}.");
            }

            Assert.That(
                result.Offsets[binCount],
                Is.EqualTo((uint)expectedValidCount));
        }

        internal static void AssertPayloadKeyAssociation(
            uint[] inputKeys,
            uint[] binnedValues,
            uint[] offsets,
            uint[] counts)
        {
            for (var bin = 0; bin < counts.Length; bin++)
            {
                var start = checked((int)offsets[bin]);
                var end = checked(start + (int)counts[bin]);
                for (var index = start; index < end; index++)
                {
                    var payload = binnedValues[index];
                    Assert.That(
                        payload,
                        Is.LessThan((uint)inputKeys.Length));
                    Assert.That(
                        inputKeys[payload],
                        Is.EqualTo((uint)bin),
                        $"Payload {payload} was scattered into bin {bin}.");
                }
            }
        }
    }
}
