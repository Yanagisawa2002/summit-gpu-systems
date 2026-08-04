using System;

internal static class GpuDirectBinningInputGenerator
{
    public static void Populate(
        uint[] keys,
        uint[] values,
        int binCount,
        string distribution,
        int seed)
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
                "Key and value arrays must have the same length.");
        }
        if (binCount < 1 || (binCount & (binCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "Bin count must be a positive power of two.");
        }

        bool uniform = string.Equals(
            distribution,
            "uniform",
            StringComparison.OrdinalIgnoreCase);
        bool hotset = string.Equals(
            distribution,
            "hotset16",
            StringComparison.OrdinalIgnoreCase);
        if (!uniform && !hotset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(distribution),
                distribution,
                "Supported distributions are uniform and hotset16.");
        }
        if (hotset && binCount < 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "hotset16 requires at least 16 bins.");
        }

        uint mask = checked((uint)(binCount - 1));
        for (int i = 0; i < keys.Length; i++)
        {
            uint hash = Mix32((uint)i ^ unchecked((uint)seed));
            keys[i] = uniform
                ? hash & mask
                : (hash & 7u) != 0u
                    ? (hash >> 3) & 15u
                    : (hash >> 3) & mask;
            values[i] = (uint)i;
        }
    }

    private static uint Mix32(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }
}
