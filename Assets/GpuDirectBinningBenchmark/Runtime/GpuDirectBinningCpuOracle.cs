using System;

internal sealed class GpuDirectBinningCpuOracle
{
    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;

    private readonly uint[] counts;
    private readonly uint[] offsets;
    private readonly uint[] canonicalValues;
    private readonly uint invalidKeyCount;
    private readonly uint diagnosticFlags;

    public GpuDirectBinningCpuOracle(
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
            throw new ArgumentException("Key and value lengths must match.");
        }
        if (binCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(binCount));
        }

        counts = new uint[binCount];
        for (int i = 0; i < keys.Length; i++)
        {
            uint key = keys[i];
            if (key >= (uint)binCount)
            {
                invalidKeyCount++;
                continue;
            }
            counts[key]++;
        }

        offsets = new uint[binCount + 1];
        for (int bin = 0; bin < binCount; bin++)
        {
            offsets[bin + 1] = checked(offsets[bin] + counts[bin]);
        }

        canonicalValues = new uint[offsets[binCount]];
        uint[] cursors = (uint[])offsets.Clone();
        for (int i = 0; i < keys.Length; i++)
        {
            uint key = keys[i];
            if (key >= (uint)binCount)
            {
                continue;
            }
            canonicalValues[cursors[key]++] = values[i];
        }
        for (int bin = 0; bin < binCount; bin++)
        {
            Array.Sort(
                canonicalValues,
                checked((int)offsets[bin]),
                checked((int)counts[bin]));
        }

        diagnosticFlags = invalidKeyCount == 0 ? 0u : 1u;
        ResultHash = ComputeHash(
            counts,
            offsets,
            canonicalValues,
            new[] { invalidKeyCount, diagnosticFlags });
    }

    public int BinCount => counts.Length;

    public int ValidCount => canonicalValues.Length;

    public uint InvalidKeyCount => invalidKeyCount;

    public string ResultHash { get; }

    public bool Validate(
        uint[] actualCounts,
        uint[] actualOffsets,
        uint[] actualValues,
        uint[] actualDiagnostics,
        out string message,
        out string actualHash)
    {
        if (actualCounts == null ||
            actualOffsets == null ||
            actualValues == null ||
            actualDiagnostics == null)
        {
            message = "Validation readback contains a null array.";
            actualHash = "unavailable";
            return false;
        }
        if (actualCounts.Length != counts.Length ||
            actualOffsets.Length != offsets.Length ||
            actualValues.Length < canonicalValues.Length ||
            actualDiagnostics.Length < 2)
        {
            message = "Validation readback has an unexpected length.";
            actualHash = "unavailable";
            return false;
        }

        int mismatch = FindMismatch(actualCounts, counts, counts.Length);
        if (mismatch >= 0)
        {
            message =
                $"Count mismatch at bin {mismatch}: GPU={actualCounts[mismatch]}, CPU={counts[mismatch]}.";
            actualHash = "unavailable";
            return false;
        }
        mismatch = FindMismatch(actualOffsets, offsets, offsets.Length);
        if (mismatch >= 0)
        {
            message =
                $"Offset mismatch at slot {mismatch}: GPU={actualOffsets[mismatch]}, CPU={offsets[mismatch]}.";
            actualHash = "unavailable";
            return false;
        }
        if (actualDiagnostics[0] != invalidKeyCount ||
            actualDiagnostics[1] != diagnosticFlags)
        {
            message =
                $"Diagnostics mismatch: GPU=({actualDiagnostics[0]},{actualDiagnostics[1]}), " +
                $"CPU=({invalidKeyCount},{diagnosticFlags}).";
            actualHash = "unavailable";
            return false;
        }

        uint[] canonicalActual = new uint[canonicalValues.Length];
        Array.Copy(actualValues, canonicalActual, canonicalActual.Length);
        for (int bin = 0; bin < counts.Length; bin++)
        {
            Array.Sort(
                canonicalActual,
                checked((int)offsets[bin]),
                checked((int)counts[bin]));
        }
        mismatch = FindMismatch(
            canonicalActual,
            canonicalValues,
            canonicalValues.Length);
        actualHash = ComputeHash(
            actualCounts,
            actualOffsets,
            canonicalActual,
            new[] { actualDiagnostics[0], actualDiagnostics[1] });
        if (mismatch >= 0)
        {
            message =
                $"Canonical membership mismatch at value slot {mismatch}: " +
                $"GPU={canonicalActual[mismatch]}, CPU={canonicalValues[mismatch]}.";
            return false;
        }
        if (!string.Equals(
                actualHash,
                ResultHash,
                StringComparison.Ordinal))
        {
            message =
                $"Canonical hash mismatch: GPU={actualHash}, CPU={ResultHash}.";
            return false;
        }

        message =
            "Counts, offsets, canonical per-bin membership, and diagnostics match the CPU oracle.";
        return true;
    }

    private static int FindMismatch(uint[] left, uint[] right, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }
        return -1;
    }

    private static string ComputeHash(params uint[][] arrays)
    {
        uint hash = FnvOffset;
        foreach (uint[] values in arrays)
        {
            for (int i = 0; i < values.Length; i++)
            {
                uint value = values[i];
                hash = (hash ^ (byte)value) * FnvPrime;
                hash = (hash ^ (byte)(value >> 8)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 16)) * FnvPrime;
                hash = (hash ^ (byte)(value >> 24)) * FnvPrime;
            }
        }
        return hash.ToString("X8");
    }
}
