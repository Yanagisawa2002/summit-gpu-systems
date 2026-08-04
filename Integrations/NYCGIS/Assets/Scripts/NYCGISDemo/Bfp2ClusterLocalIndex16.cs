using System;

/// <summary>
/// Encodes the BFP2 source-index stream as two cluster-local uint16 indices per uint.
/// The original uint32 stream remains available as the portable fallback.
/// </summary>
public static class Bfp2ClusterLocalIndex16
{
    public const int ClusterWordsPerRecord = 20;

    public static bool TryEncode(
        uint[] sourceIndices,
        uint[] clusterWords,
        out uint[] packedIndexWords,
        out uint[] clusterBaseVertices,
        out uint maximumClusterIndexSpan,
        out string error)
    {
        packedIndexWords = null;
        clusterBaseVertices = null;
        maximumClusterIndexSpan = 0u;
        error = string.Empty;

        if (sourceIndices == null || clusterWords == null)
        {
            error = "Source indices or cluster records are missing.";
            return false;
        }
        if (clusterWords.Length % ClusterWordsPerRecord != 0)
        {
            error = "Cluster word count is not aligned to the 20-word BFP2 record.";
            return false;
        }

        int clusterCount = clusterWords.Length / ClusterWordsPerRecord;
        uint[] packed = new uint[(sourceIndices.Length + 1) / 2];
        uint[] bases = new uint[clusterCount];
        int previousEnd = 0;

        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
        {
            int clusterWord = clusterIndex * ClusterWordsPerRecord;
            uint firstIndexWord = clusterWords[clusterWord + 0];
            uint indexCountWord = clusterWords[clusterWord + 1];
            ulong endWord = (ulong)firstIndexWord + indexCountWord;
            if (firstIndexWord > int.MaxValue ||
                indexCountWord > int.MaxValue ||
                endWord > (ulong)sourceIndices.Length)
            {
                error = "Cluster " + clusterIndex + " has an out-of-range source-index interval.";
                return false;
            }

            int firstIndex = (int)firstIndexWord;
            int indexCount = (int)indexCountWord;
            int endIndex = firstIndex + indexCount;
            if (firstIndex < previousEnd)
            {
                error = "Cluster " + clusterIndex + " overlaps a previous source-index interval.";
                return false;
            }
            if (indexCount % 3 != 0)
            {
                error = "Cluster " + clusterIndex + " does not contain complete triangles.";
                return false;
            }
            previousEnd = endIndex;

            if (indexCount == 0)
            {
                bases[clusterIndex] = 0u;
                continue;
            }

            uint minimumIndex = uint.MaxValue;
            uint maximumIndex = 0u;
            for (int sourceIndex = firstIndex; sourceIndex < endIndex; sourceIndex++)
            {
                uint value = sourceIndices[sourceIndex];
                minimumIndex = Math.Min(minimumIndex, value);
                maximumIndex = Math.Max(maximumIndex, value);
            }

            uint span = maximumIndex - minimumIndex;
            if (span > ushort.MaxValue)
            {
                error = "Cluster " + clusterIndex + " requires a " + span +
                        "-wide vertex-index range, which does not fit uint16.";
                return false;
            }

            bases[clusterIndex] = minimumIndex;
            maximumClusterIndexSpan = Math.Max(maximumClusterIndexSpan, span);
            for (int sourceIndex = firstIndex; sourceIndex < endIndex; sourceIndex++)
            {
                uint localIndex = sourceIndices[sourceIndex] - minimumIndex;
                int packedWord = sourceIndex >> 1;
                int shift = (sourceIndex & 1) * 16;
                packed[packedWord] |= localIndex << shift;
            }
        }

        // Validate the exact decode contract before any data reaches the GPU.
        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
        {
            int clusterWord = clusterIndex * ClusterWordsPerRecord;
            int firstIndex = (int)clusterWords[clusterWord + 0];
            int indexCount = (int)clusterWords[clusterWord + 1];
            int endIndex = firstIndex + indexCount;
            uint baseVertex = bases[clusterIndex];
            for (int sourceIndex = firstIndex; sourceIndex < endIndex; sourceIndex++)
            {
                uint packedWord = packed[sourceIndex >> 1];
                uint localIndex = (sourceIndex & 1) == 0
                    ? packedWord & 0xffffu
                    : packedWord >> 16;
                if (baseVertex + localIndex != sourceIndices[sourceIndex])
                {
                    error = "Cluster-local uint16 round-trip failed at source index " + sourceIndex + ".";
                    return false;
                }
            }
        }

        packedIndexWords = packed;
        clusterBaseVertices = bases;
        return true;
    }
}
