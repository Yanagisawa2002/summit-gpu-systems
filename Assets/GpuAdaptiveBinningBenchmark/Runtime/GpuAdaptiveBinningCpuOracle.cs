using System;
using System.Security.Cryptography;
using System.Text;

internal sealed class GpuAdaptiveBinningCpuOracle
{
    public const string CanonicalHashSchema =
        "summit.gpu-adaptive-binning.canonical-csr.sha256.v1";
    public const string FastHashSchema =
        "summit.gpu-adaptive-binning.canonical-csr.fnv1a32.v1";

    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;
    private const int HashBufferSize = 4096;

    private readonly uint[] counts;
    private readonly uint[] offsets;
    private readonly uint[] canonicalValues;
    private readonly uint invalidKeyCount;
    private readonly uint diagnosticFlags;

    public GpuAdaptiveBinningCpuOracle(
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
        for (int index = 0; index < keys.Length; index++)
        {
            uint key = keys[index];
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
            offsets[bin + 1] =
                checked(offsets[bin] + counts[bin]);
        }

        canonicalValues =
            new uint[checked((int)offsets[binCount])];
        uint[] cursors = (uint[])offsets.Clone();
        for (int index = 0; index < keys.Length; index++)
        {
            uint key = keys[index];
            if (key >= (uint)binCount)
            {
                continue;
            }
            canonicalValues[cursors[key]++] = values[index];
        }
        CanonicalizeValues(canonicalValues, offsets, counts);

        diagnosticFlags = invalidKeyCount == 0 ? 0u : 1u;
        uint[] diagnostics = { invalidKeyCount, diagnosticFlags };
        ResultHash = ComputeCanonicalSha256(
            counts,
            offsets,
            canonicalValues,
            diagnostics);
        FastResultHash = ComputeCanonicalFastHash(
            counts,
            offsets,
            canonicalValues,
            diagnostics);
    }

    public int BinCount => counts.Length;

    public int ValidCount => canonicalValues.Length;

    public uint InvalidKeyCount => invalidKeyCount;

    public uint DiagnosticFlags => diagnosticFlags;

    /// <summary>
    /// Authoritative, versioned SHA-256 over counts, offsets, canonical
    /// per-bin membership, and diagnostics. Array lengths and uint values
    /// are serialized explicitly in little-endian order.
    /// </summary>
    public string ResultHash { get; }

    /// <summary>
    /// Non-authoritative checksum useful for quick diagnostics only.
    /// ResultHash remains the evidence identity.
    /// </summary>
    public string FastResultHash { get; }

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
            actualDiagnostics.Length != 2)
        {
            message = "Validation readback has an unexpected length.";
            actualHash = "unavailable";
            return false;
        }

        int mismatch = FindMismatch(
            actualCounts,
            counts,
            counts.Length);
        if (mismatch >= 0)
        {
            message =
                $"Count mismatch at bin {mismatch}: " +
                $"GPU={actualCounts[mismatch]}, CPU={counts[mismatch]}.";
            actualHash = "unavailable";
            return false;
        }

        mismatch = FindMismatch(
            actualOffsets,
            offsets,
            offsets.Length);
        if (mismatch >= 0)
        {
            message =
                $"Offset mismatch at slot {mismatch}: " +
                $"GPU={actualOffsets[mismatch]}, CPU={offsets[mismatch]}.";
            actualHash = "unavailable";
            return false;
        }

        if (actualDiagnostics[0] != invalidKeyCount ||
            actualDiagnostics[1] != diagnosticFlags)
        {
            message =
                $"Diagnostics mismatch: " +
                $"GPU=({actualDiagnostics[0]},{actualDiagnostics[1]}), " +
                $"CPU=({invalidKeyCount},{diagnosticFlags}).";
            actualHash = "unavailable";
            return false;
        }

        // Only [0, ValidCount) is defined. Any allocation tail beyond the
        // terminal CSR offset is intentionally excluded from validation.
        uint[] canonicalActual =
            new uint[canonicalValues.Length];
        Array.Copy(
            actualValues,
            canonicalActual,
            canonicalActual.Length);
        CanonicalizeValues(canonicalActual, offsets, counts);

        mismatch = FindMismatch(
            canonicalActual,
            canonicalValues,
            canonicalValues.Length);
        uint[] diagnostics =
        {
            actualDiagnostics[0],
            actualDiagnostics[1],
        };
        actualHash = ComputeCanonicalSha256(
            actualCounts,
            actualOffsets,
            canonicalActual,
            diagnostics);
        if (mismatch >= 0)
        {
            message =
                $"Canonical membership mismatch at value slot {mismatch}: " +
                $"GPU={canonicalActual[mismatch]}, " +
                $"CPU={canonicalValues[mismatch]}.";
            return false;
        }
        if (!string.Equals(
                actualHash,
                ResultHash,
                StringComparison.Ordinal))
        {
            message =
                $"Canonical hash mismatch: " +
                $"GPU={actualHash}, CPU={ResultHash}.";
            return false;
        }

        message =
            "Counts, offsets, canonical per-bin membership, and " +
            "diagnostics match the CPU oracle.";
        return true;
    }

    private static void CanonicalizeValues(
        uint[] values,
        uint[] csrOffsets,
        uint[] csrCounts)
    {
        for (int bin = 0; bin < csrCounts.Length; bin++)
        {
            Array.Sort(
                values,
                checked((int)csrOffsets[bin]),
                checked((int)csrCounts[bin]));
        }
    }

    private static int FindMismatch(
        uint[] left,
        uint[] right,
        int length)
    {
        for (int index = 0; index < length; index++)
        {
            if (left[index] != right[index])
            {
                return index;
            }
        }
        return -1;
    }

    private static string ComputeCanonicalSha256(
        params uint[][] arrays)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] schemaBytes = Encoding.ASCII.GetBytes(
                CanonicalHashSchema + "\0");
            byte[] hashBuffer = new byte[HashBufferSize];
            sha256.TransformBlock(
                schemaBytes,
                0,
                schemaBytes.Length,
                hashBuffer,
                0);

            int bufferedByteCount = 0;
            AppendUInt32(
                sha256,
                hashBuffer,
                ref bufferedByteCount,
                checked((uint)arrays.Length));
            for (int arrayIndex = 0;
                 arrayIndex < arrays.Length;
                 arrayIndex++)
            {
                uint[] values = arrays[arrayIndex];
                AppendUInt32(
                    sha256,
                    hashBuffer,
                    ref bufferedByteCount,
                    checked((uint)values.Length));
                for (int valueIndex = 0;
                     valueIndex < values.Length;
                     valueIndex++)
                {
                    AppendUInt32(
                        sha256,
                        hashBuffer,
                        ref bufferedByteCount,
                        values[valueIndex]);
                }
            }
            FlushHashBuffer(
                sha256,
                hashBuffer,
                ref bufferedByteCount);
            sha256.TransformFinalBlock(
                Array.Empty<byte>(),
                0,
                0);
            return CanonicalHashSchema + ":" +
                ToUpperHex(sha256.Hash);
        }
    }

    private static string ComputeCanonicalFastHash(
        params uint[][] arrays)
    {
        uint hash = FnvOffset;
        byte[] schemaBytes = Encoding.ASCII.GetBytes(
            FastHashSchema + "\0");
        for (int index = 0;
             index < schemaBytes.Length;
             index++)
        {
            AppendFastHashByte(ref hash, schemaBytes[index]);
        }

        AppendFastHashUInt32(
            ref hash,
            checked((uint)arrays.Length));
        for (int arrayIndex = 0;
             arrayIndex < arrays.Length;
             arrayIndex++)
        {
            uint[] values = arrays[arrayIndex];
            AppendFastHashUInt32(
                ref hash,
                checked((uint)values.Length));
            for (int valueIndex = 0;
                 valueIndex < values.Length;
                 valueIndex++)
            {
                AppendFastHashUInt32(
                    ref hash,
                    values[valueIndex]);
            }
        }
        return FastHashSchema + ":" + hash.ToString("X8");
    }

    private static void AppendUInt32(
        SHA256 sha256,
        byte[] buffer,
        ref int bufferedByteCount,
        uint value)
    {
        if (bufferedByteCount > buffer.Length - sizeof(uint))
        {
            FlushHashBuffer(
                sha256,
                buffer,
                ref bufferedByteCount);
        }

        buffer[bufferedByteCount++] = (byte)value;
        buffer[bufferedByteCount++] = (byte)(value >> 8);
        buffer[bufferedByteCount++] = (byte)(value >> 16);
        buffer[bufferedByteCount++] = (byte)(value >> 24);
    }

    private static void FlushHashBuffer(
        SHA256 sha256,
        byte[] buffer,
        ref int bufferedByteCount)
    {
        if (bufferedByteCount == 0)
        {
            return;
        }

        sha256.TransformBlock(
            buffer,
            0,
            bufferedByteCount,
            buffer,
            0);
        bufferedByteCount = 0;
    }

    private static void AppendFastHashUInt32(
        ref uint hash,
        uint value)
    {
        AppendFastHashByte(ref hash, (byte)value);
        AppendFastHashByte(ref hash, (byte)(value >> 8));
        AppendFastHashByte(ref hash, (byte)(value >> 16));
        AppendFastHashByte(ref hash, (byte)(value >> 24));
    }

    private static void AppendFastHashByte(
        ref uint hash,
        byte value)
    {
        unchecked
        {
            hash = (hash ^ value) * FnvPrime;
        }
    }

    private static string ToUpperHex(byte[] bytes)
    {
        const string digits = "0123456789ABCDEF";
        char[] result = new char[checked(bytes.Length * 2)];
        for (int index = 0; index < bytes.Length; index++)
        {
            result[index * 2] = digits[bytes[index] >> 4];
            result[index * 2 + 1] = digits[bytes[index] & 15];
        }
        return new string(result);
    }
}
