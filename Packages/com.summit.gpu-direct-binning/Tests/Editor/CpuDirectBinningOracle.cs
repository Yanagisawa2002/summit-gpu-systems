using System;

namespace Summit.GpuDirectBinning.Tests
{
    internal sealed class CpuDirectBinningResult
    {
        internal CpuDirectBinningResult(
            uint[] counts,
            uint[] offsets,
            uint[] binnedValues,
            uint invalidKeyCount,
            uint errorFlags)
        {
            Counts = counts;
            Offsets = offsets;
            BinnedValues = binnedValues;
            InvalidKeyCount = invalidKeyCount;
            ErrorFlags = errorFlags;
        }

        internal uint[] Counts { get; }

        internal uint[] Offsets { get; }

        internal uint[] BinnedValues { get; }

        internal uint InvalidKeyCount { get; }

        internal uint ErrorFlags { get; }

        internal int ValidCount => BinnedValues.Length;
    }

    /// <summary>
    /// Independent CPU reference for unordered uint key/value binning into CSR.
    /// It intentionally does not mirror the GPU kernels line by line.
    /// </summary>
    internal static class CpuDirectBinningOracle
    {
        internal const uint InvalidKeyErrorBit = 1u;

        internal static readonly int[] BoundarySizes =
        {
            0,
            1,
            31,
            32,
            33,
            63,
            64,
            65,
            127,
            128,
            129,
            255,
            256,
            257,
            4095,
            4096,
            4097,
        };

        internal static uint[] CreatePayload(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var values = new uint[length];
            for (var index = 0; index < length; index++)
            {
                values[index] = (uint)index;
            }

            return values;
        }

        internal static uint[] CreateValidKeys(int length, int binCount)
        {
            ValidateShape(length, binCount);
            var keys = new uint[length];
            for (var index = 0; index < length; index++)
            {
                var value = unchecked((uint)index * 747796405u + 2891336453u);
                value ^= value >> 16;
                value *= 2246822519u;
                value ^= value >> 13;
                keys[index] = value % (uint)binCount;
            }

            return keys;
        }

        internal static uint[] CreateMixedValidityKeys(int length, int binCount)
        {
            var keys = CreateValidKeys(length, binCount);
            for (var index = 0; index < keys.Length; index++)
            {
                if (index % 5 == 0)
                {
                    keys[index] = unchecked((uint)binCount + (uint)(index % 7));
                }
            }

            return keys;
        }

        internal static CpuDirectBinningResult Build(
            uint[] keys,
            uint[] values,
            int binCount)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (keys.Length != values.Length)
            {
                throw new ArgumentException(
                    "Keys and values must have the same length.");
            }

            if (binCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(binCount));
            }

            var counts = new uint[binCount];
            uint invalidKeyCount = 0u;
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                if (key < (uint)binCount)
                {
                    counts[key]++;
                }
                else
                {
                    invalidKeyCount++;
                }
            }

            var offsets = new uint[binCount + 1];
            uint validCount = 0u;
            for (var bin = 0; bin < binCount; bin++)
            {
                offsets[bin] = validCount;
                validCount = unchecked(validCount + counts[bin]);
            }

            offsets[binCount] = validCount;
            var writeHeads = (uint[])offsets.Clone();
            var binnedValues = new uint[checked((int)validCount)];
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                if (key >= (uint)binCount)
                {
                    continue;
                }

                binnedValues[writeHeads[key]++] = values[index];
            }

            return new CpuDirectBinningResult(
                counts,
                offsets,
                binnedValues,
                invalidKeyCount,
                invalidKeyCount == 0u ? 0u : InvalidKeyErrorBit);
        }

        internal static uint[] CanonicalizeBins(
            uint[] binnedValues,
            uint[] offsets,
            uint[] counts)
        {
            if (binnedValues == null)
            {
                throw new ArgumentNullException(nameof(binnedValues));
            }

            if (offsets == null)
            {
                throw new ArgumentNullException(nameof(offsets));
            }

            if (counts == null)
            {
                throw new ArgumentNullException(nameof(counts));
            }

            if (offsets.Length != counts.Length + 1)
            {
                throw new ArgumentException(
                    "Offsets must contain one terminal entry after all bins.");
            }

            var canonical = (uint[])binnedValues.Clone();
            for (var bin = 0; bin < counts.Length; bin++)
            {
                var start = checked((int)offsets[bin]);
                var count = checked((int)counts[bin]);
                if (start < 0 ||
                    count < 0 ||
                    start > canonical.Length ||
                    count > canonical.Length - start ||
                    offsets[bin + 1] != offsets[bin] + counts[bin])
                {
                    throw new ArgumentException(
                        $"Invalid CSR range for bin {bin}.");
                }

                Array.Sort(canonical, start, count);
            }

            if (offsets[offsets.Length - 1] != (uint)canonical.Length)
            {
                throw new ArgumentException(
                    "The terminal CSR offset must equal the value count.");
            }

            return canonical;
        }

        private static void ValidateShape(int length, int binCount)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (binCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(binCount));
            }
        }
    }
}
