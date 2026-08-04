using System.Linq;
using NUnit.Framework;

namespace Summit.GpuPrimitives.Tests
{
    public sealed class CpuPrimitiveOracleTests
    {
        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void HistogramAndExclusiveScanPreserveTotal(int elementCount)
        {
            const int bucketCount = 257;
            var keys = CpuPrimitiveOracle.CreateKeys(elementCount, bucketCount);
            var histogram = CpuPrimitiveOracle.Histogram(keys, bucketCount);
            var offsets = CpuPrimitiveOracle.ExclusiveScan(histogram);

            Assert.That(histogram.Sum(value => (long)value), Is.EqualTo(elementCount));
            Assert.That(offsets[0], Is.Zero);

            for (var index = 1; index < offsets.Length; index++)
            {
                Assert.That(
                    offsets[index],
                    Is.EqualTo(offsets[index - 1] + histogram[index - 1]),
                    $"Exclusive scan mismatch at bucket {index} for N={elementCount}.");
            }

            Assert.That(
                offsets[offsets.Length - 1] + histogram[histogram.Length - 1],
                Is.EqualTo((uint)elementCount));
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void StableCompactionPreservesInputOrder(int elementCount)
        {
            var payload = CpuPrimitiveOracle.CreatePayload(elementCount);
            var keep = CpuPrimitiveOracle.CreateKeepMask(elementCount);
            var expected = payload.Where((_, index) => keep[index]).ToArray();
            var actual = CpuPrimitiveOracle.StableCompact(payload, keep);

            Assert.That(actual, Is.EqualTo(expected));
            for (var index = 1; index < actual.Length; index++)
            {
                Assert.That(actual[index], Is.GreaterThan(actual[index - 1]));
            }
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void StableRadixSortProducesOrderedPermutation(int elementCount)
        {
            var keys = CpuPrimitiveOracle.CreateRadixKeys(elementCount);
            var payload = CpuPrimitiveOracle.CreatePayload(elementCount);
            var result = CpuPrimitiveOracle.StableRadixSort(keys, payload);

            Assert.That(result.Keys, Has.Length.EqualTo(elementCount));
            Assert.That(result.Payload.OrderBy(value => value), Is.EqualTo(payload));

            for (var index = 1; index < result.Keys.Length; index++)
            {
                Assert.That(result.Keys[index], Is.GreaterThanOrEqualTo(result.Keys[index - 1]));
                if (result.Keys[index] == result.Keys[index - 1])
                {
                    Assert.That(
                        result.Payload[index],
                        Is.GreaterThan(result.Payload[index - 1]),
                        $"Sort is not stable at output {index} for N={elementCount}.");
                }
            }

            for (var index = 0; index < result.Keys.Length; index++)
            {
                Assert.That(result.Keys[index], Is.EqualTo(keys[result.Payload[index]]));
            }
        }
    }
}
