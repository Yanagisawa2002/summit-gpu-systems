using System;

internal static class GpuAdaptiveBinningInputGenerator
{
    public const string GeneratorContract =
        "gpu-adaptive-binning-input-v3";
    public const string UniformDistribution = "uniform";
    public const string Hotset4Distribution = "hotset4";
    public const string Hotset16Distribution = "hotset16";
    public const string SingleBinDistribution = "singlebin";

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
        if (ReferenceEquals(keys, values))
        {
            throw new ArgumentException(
                "Key and value arrays must be distinct.");
        }
        if (keys.Length != values.Length)
        {
            throw new ArgumentException(
                "Key and value arrays must have the same length.");
        }
        if (binCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "Bin count must be positive.");
        }

        bool uniform = IsDistribution(
            distribution,
            UniformDistribution);
        bool hotset4 = IsDistribution(
            distribution,
            Hotset4Distribution);
        bool hotset16 = IsDistribution(
            distribution,
            Hotset16Distribution);
        bool singleBin = IsDistribution(
            distribution,
            SingleBinDistribution);
        if (!uniform && !hotset4 && !hotset16 && !singleBin)
        {
            throw new ArgumentOutOfRangeException(
                nameof(distribution),
                distribution,
                "Supported distributions are uniform, hotset4, " +
                "hotset16, and singlebin.");
        }
        if (hotset4 && binCount < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "hotset4 requires at least 4 bins.");
        }
        if (hotset16 && binCount < 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                "hotset16 requires at least 16 bins.");
        }

        uint binRange = checked((uint)binCount);
        // singlebin stays a one-bin workload, but its occupied bin is bound
        // to the input seed. Converting the signed seed to its unsigned bit
        // pattern before modulo is the complete versioned mapping contract;
        // no statistical distribution property is implied.
        uint singleBinKey = unchecked((uint)seed) % binRange;
        for (int index = 0; index < keys.Length; index++)
        {
            uint hash = Mix32(
                (uint)index ^ unchecked((uint)seed));
            if (uniform)
            {
                keys[index] = hash % binRange;
            }
            else if (hotset4)
            {
                keys[index] = HotsetKey(hash, binRange, 4u);
            }
            else if (hotset16)
            {
                keys[index] = HotsetKey(hash, binRange, 16u);
            }
            else
            {
                keys[index] = singleBinKey;
            }

            // A unique, source-stable value makes a canonical membership
            // comparison detect loss, duplication, and cross-bin movement.
            values[index] = (uint)index;
        }
    }

    private static bool IsDistribution(
        string actual,
        string expected)
    {
        return string.Equals(
            actual,
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static uint HotsetKey(
        uint hash,
        uint binRange,
        uint hotsetRange)
    {
        uint keyBits = hash >> 3;
        return (hash & 7u) != 0u
            ? keyBits % hotsetRange
            : keyBits % binRange;
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
