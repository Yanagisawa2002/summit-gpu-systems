using System;

namespace Summit.GpuPrimitives.Tests
{
    internal static class CpuPrimitiveOracle
    {
        internal static readonly int[] BoundarySizes =
        {
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

        internal static uint[] CreateKeys(int length, uint bucketCount = 257u)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (bucketCount == 0u)
            {
                throw new ArgumentOutOfRangeException(nameof(bucketCount));
            }

            var keys = new uint[length];
            for (var index = 0; index < length; index++)
            {
                // Deliberately non-monotonic, duplicate-heavy, and deterministic.
                var value = unchecked((uint)index * 747796405u + 2891336453u);
                value ^= value >> 16;
                value *= 2246822519u;
                value ^= value >> 13;
                keys[index] = value % bucketCount;
            }

            return keys;
        }

        internal static uint[] CreateRadixKeys(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var duplicateSet = new[]
            {
                0u,
                1u,
                0x00010000u,
                0x7fffffffu,
                0x80000000u,
                0xffff0000u,
                uint.MaxValue,
            };
            var keys = new uint[length];
            for (var index = 0; index < length; index++)
            {
                var value = unchecked((uint)index * 747796405u + 2891336453u);
                value ^= value >> 16;
                value *= 2246822519u;
                value ^= value >> 13;
                keys[index] = index % 5 == 0
                    ? duplicateSet[(index / 5) % duplicateSet.Length]
                    : value;
            }

            return keys;
        }

        internal static uint[] CreatePayload(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var payload = new uint[length];
            for (var index = 0; index < length; index++)
            {
                payload[index] = (uint)index;
            }

            return payload;
        }

        internal static uint[] Histogram(uint[] keys, int bucketCount)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            if (bucketCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bucketCount));
            }

            var histogram = new uint[bucketCount];
            foreach (var key in keys)
            {
                if (key >= (uint)bucketCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(keys),
                        $"Key {key} is outside the histogram range [0, {bucketCount}).");
                }

                histogram[key]++;
            }

            return histogram;
        }

        internal static uint[] ExclusiveScan(uint[] input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var output = new uint[input.Length];
            uint running = 0u;
            for (var index = 0; index < input.Length; index++)
            {
                output[index] = running;
                running = unchecked(running + input[index]);
            }

            return output;
        }

        internal static bool[] CreateKeepMask(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var keep = new bool[length];
            for (var index = 0; index < length; index++)
            {
                // Mix long and short runs and force both boundary elements to be kept.
                keep[index] = index == 0 ||
                              index == length - 1 ||
                              index % 3 == 1 ||
                              ((index / 17) & 1) == 1;
            }

            return keep;
        }

        internal static uint[] StableCompact(uint[] values, bool[] keep)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (keep == null)
            {
                throw new ArgumentNullException(nameof(keep));
            }

            if (values.Length != keep.Length)
            {
                throw new ArgumentException("Values and keep mask must have the same length.");
            }

            var count = 0;
            foreach (var item in keep)
            {
                if (item)
                {
                    count++;
                }
            }

            var output = new uint[count];
            var writeIndex = 0;
            for (var index = 0; index < values.Length; index++)
            {
                if (keep[index])
                {
                    output[writeIndex++] = values[index];
                }
            }

            return output;
        }

        internal static (uint[] Keys, uint[] Payload) StableRadixSort(
            uint[] inputKeys,
            uint[] inputPayload,
            int bitsPerPass = 4)
        {
            if (inputKeys == null)
            {
                throw new ArgumentNullException(nameof(inputKeys));
            }

            if (inputPayload == null)
            {
                throw new ArgumentNullException(nameof(inputPayload));
            }

            if (inputKeys.Length != inputPayload.Length)
            {
                throw new ArgumentException("Keys and payload must have the same length.");
            }

            if (bitsPerPass <= 0 || bitsPerPass > 16 || 32 % bitsPerPass != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bitsPerPass),
                    "bitsPerPass must be a positive divisor of 32 and no greater than 16.");
            }

            var keysA = (uint[])inputKeys.Clone();
            var payloadA = (uint[])inputPayload.Clone();
            var keysB = new uint[inputKeys.Length];
            var payloadB = new uint[inputPayload.Length];
            var radix = 1 << bitsPerPass;
            var digitMask = (uint)(radix - 1);

            for (var shift = 0; shift < 32; shift += bitsPerPass)
            {
                var counts = new int[radix];
                for (var index = 0; index < keysA.Length; index++)
                {
                    counts[(keysA[index] >> shift) & digitMask]++;
                }

                var offsets = new int[radix];
                for (var digit = 1; digit < radix; digit++)
                {
                    offsets[digit] = offsets[digit - 1] + counts[digit - 1];
                }

                for (var index = 0; index < keysA.Length; index++)
                {
                    var digit = (keysA[index] >> shift) & digitMask;
                    var destination = offsets[digit]++;
                    keysB[destination] = keysA[index];
                    payloadB[destination] = payloadA[index];
                }

                (keysA, keysB) = (keysB, keysA);
                (payloadA, payloadB) = (payloadB, payloadA);
            }

            return (keysA, payloadA);
        }
    }
}
