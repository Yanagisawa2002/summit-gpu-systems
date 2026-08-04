using System;
using System.Collections.Generic;
using System.Linq;

namespace Summit.GpuAdaptiveBinning.Tests
{
    internal static class GpuAdaptiveBinningTestOracle
    {
        internal sealed class Result
        {
            internal uint[] Counts;
            internal uint[] Offsets;
            internal uint[] Values;
            internal uint InvalidKeyCount;
        }

        internal static uint[] CreateValidKeys(
            int elementCount,
            int binCount,
            uint seed = 0x9e3779b9u)
        {
            var keys = new uint[elementCount];
            uint state = seed;
            for (int index = 0; index < elementCount; index++)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                keys[index] = state % (uint)binCount;
            }
            return keys;
        }

        internal static uint[] CreateValues(int elementCount)
        {
            return Enumerable.Range(0, elementCount)
                .Select(index => (uint)index)
                .ToArray();
        }

        internal static Result Build(
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
                    "Key and value lengths must match.");
            }
            if (binCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(binCount));
            }

            var bins = new List<uint>[binCount];
            for (int bin = 0; bin < binCount; bin++)
            {
                bins[bin] = new List<uint>();
            }

            uint invalid = 0u;
            for (int index = 0; index < keys.Length; index++)
            {
                uint key = keys[index];
                if (key < (uint)binCount)
                {
                    bins[key].Add(values[index]);
                }
                else
                {
                    invalid++;
                }
            }

            var counts = new uint[binCount];
            var offsets = new uint[binCount + 1];
            int validCount = 0;
            for (int bin = 0; bin < binCount; bin++)
            {
                counts[bin] = (uint)bins[bin].Count;
                offsets[bin] = (uint)validCount;
                validCount += bins[bin].Count;
            }
            offsets[binCount] = (uint)validCount;

            var output = new uint[validCount];
            int destination = 0;
            for (int bin = 0; bin < binCount; bin++)
            {
                bins[bin].CopyTo(output, destination);
                destination += bins[bin].Count;
            }

            return new Result
            {
                Counts = counts,
                Offsets = offsets,
                Values = output,
                InvalidKeyCount = invalid,
            };
        }

        internal static uint[] Canonicalize(
            uint[] values,
            uint[] counts,
            uint[] offsets)
        {
            int validCount = checked((int)offsets[offsets.Length - 1]);
            var canonical = new uint[validCount];
            for (int bin = 0; bin < counts.Length; bin++)
            {
                int start = checked((int)offsets[bin]);
                int count = checked((int)counts[bin]);
                Array.Copy(values, start, canonical, start, count);
                Array.Sort(canonical, start, count);
            }
            return canonical;
        }
    }
}
