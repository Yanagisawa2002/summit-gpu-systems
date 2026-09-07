using System;
using Summit.GpuSensorPipeline;

namespace Summit.GpuSensorPipeline
{
    public static class GpuSensorBenchmarkOracle
    {
        public static GpuSensorQueryDigest[] QueryAll(
            GpuSensorSample[] samples,
            int elementCount,
            GpuSensorRangeQuery[] queries, uint[] activeSlots = null)
        {
            var output = new GpuSensorQueryDigest[queries.Length];
            for (var index = 0; index < queries.Length; index++)
            {
                output[index] = Query(samples, elementCount, queries[index], activeSlots);
            }

            return output;
        }

        public static GpuSensorQueryDigest Query(
            GpuSensorSample[] samples,
            int elementCount,
            GpuSensorRangeQuery query, uint[] activeSlots = null)
        {
            uint radius = Math.Min(
                query.Radius,
                GpuSensorDeterministicGenerator.CoordinateMask);
            uint minX = query.CenterX > radius ? query.CenterX - radius : 0u;
            uint minY = query.CenterY > radius ? query.CenterY - radius : 0u;
            uint minZ = query.CenterZ > radius ? query.CenterZ - radius : 0u;
            uint maxX = Math.Min(
                GpuSensorDeterministicGenerator.CoordinateMask,
                query.CenterX + radius);
            uint maxY = Math.Min(
                GpuSensorDeterministicGenerator.CoordinateMask,
                query.CenterY + radius);
            uint maxZ = Math.Min(
                GpuSensorDeterministicGenerator.CoordinateMask,
                query.CenterZ + radius);

            uint count = 0u;
            uint xorHash = 0u;
            uint sumHash0 = 0u;
            uint sumHash1 = 0u;
            unchecked
            {
                for (var index = 0; index < elementCount; index++)
                {
                    if (activeSlots != null && activeSlots[index] != 1u) continue;
                    GpuSensorSample sample = samples[index];
                    if (sample.X < minX || sample.X > maxX ||
                        sample.Y < minY || sample.Y > maxY ||
                        sample.Z < minZ || sample.Z > maxZ)
                    {
                        continue;
                    }

                    uint id = (uint)index;
                    uint hash = HashSample(id, sample);
                    count++;
                    xorHash ^= hash;
                    sumHash0 += hash;
                    sumHash1 += Mix(hash ^ id ^ 0x27D4EB2Fu);
                }
            }

            return new GpuSensorQueryDigest(
                count,
                xorHash,
                sumHash0,
                sumHash1);
        }

        public static GpuSensorQueryDigest Frame(
            GpuSensorQueryDigest[] queryDigests,
            uint logicalState)
        {
            uint count = 0u;
            uint xorHash = 0u;
            uint sumHash0 = 0u;
            uint sumHash1 = 0u;
            unchecked
            {
                for (uint index = 0u; index < queryDigests.Length; index++)
                {
                    GpuSensorQueryDigest digest = queryDigests[index];
                    uint tag = Mix(
                        index ^ digest.Count ^ digest.XorHash ^
                        digest.SumHash0 ^ digest.SumHash1 ^ 0x165667B1u);
                    count += digest.Count;
                    xorHash ^= tag;
                    sumHash0 += digest.SumHash0 + tag;
                    sumHash1 += digest.SumHash1 + Mix(tag ^ 0x9E3779B9u);
                }
            }

            return new GpuSensorQueryDigest(
                count,
                xorHash ^ Mix(logicalState ^ 0x94D049BBu),
                sumHash0,
                sumHash1);
        }

        public static GpuSensorQueryDigest Comparison(
            GpuSensorQueryDigest[] expected,
            GpuSensorQueryDigest[] actual)
        {
            if (expected.Length != actual.Length)
            {
                throw new ArgumentException("Comparison arrays must match.");
            }

            uint count = 0u;
            uint xorHash = 0u;
            uint sumHash0 = 0u;
            uint sumHash1 = 0u;
            unchecked
            {
                for (uint index = 0u; index < expected.Length; index++)
                {
                    GpuSensorQueryDigest left = expected[index];
                    GpuSensorQueryDigest right = actual[index];
                    if (left == right)
                    {
                        continue;
                    }

                    count++;
                    xorHash ^= Mix(
                        index ^ left.Count ^ right.Count ^ 0x243F6A88u);
                    sumHash0 += Mix(
                        left.XorHash ^ right.XorHash ^ 0x85A308D3u);
                    sumHash1 += Mix(
                        left.SumHash0 ^ right.SumHash0 ^
                        left.SumHash1 ^ right.SumHash1 ^ 0x13198A2Eu);
                }
            }

            return new GpuSensorQueryDigest(
                count,
                xorHash,
                sumHash0,
                sumHash1);
        }

        public static uint InvalidKeyHash(uint index, uint key)
        {
            return Mix(index ^ key ^ 0xD6E8FEB9u);
        }

        public static uint HashSample(uint id, GpuSensorSample sample)
        {
            uint hash = Mix(id ^ 0x85EBCA6Bu);
            hash = Mix(hash ^ sample.X);
            hash = Mix(hash ^ sample.Y);
            hash = Mix(hash ^ sample.Z);
            return Mix(hash ^ sample.Payload);
        }

        public static uint Mix(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                value ^= value >> 16;
                return value;
            }
        }
    }
}
