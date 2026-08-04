using System;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Bit-exact CPU counterpart of the deterministic GPU sensor producer.
    /// </summary>
    public static class GpuSensorDeterministicGenerator
    {
        public const int StateCount = 64;
        public const int GridAxis = 64;
        public const int BinCount = GridAxis * GridAxis * GridAxis;
        public const int CoordinateBits = 16;
        public const int GridBits = 6;
        public const int GridShift = CoordinateBits - GridBits;
        public const uint CoordinateMask = (1u << CoordinateBits) - 1u;

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

        public static GpuSensorSample GenerateSample(
            uint index,
            uint seed,
            uint logicalState)
        {
            ValidateLogicalState(logicalState);
            unchecked
            {
                uint source = index ^ seed;
                uint baseX = Mix(source ^ 0xA511E9B3u) & CoordinateMask;
                uint baseY = Mix(source ^ 0x63D83595u) & CoordinateMask;
                uint baseZ = Mix(source ^ 0xC2B2AE35u) & CoordinateMask;
                uint shiftX = ((logicalState * 17u) & 63u) << GridShift;
                uint shiftY = ((logicalState * 29u) & 63u) << GridShift;
                uint shiftZ = ((logicalState * 43u) & 63u) << GridShift;
                uint x = (baseX + shiftX) & CoordinateMask;
                uint y = (baseY + shiftY) & CoordinateMask;
                uint z = (baseZ + shiftZ) & CoordinateMask;
                uint payload = Mix(source ^ 0x9E3779B9u) ^
                    Mix(logicalState ^ 0xD1B54A35u);
                return new GpuSensorSample(x, y, z, payload);
            }
        }

        /// <summary>
        /// Maps a 16-bit position into a 64^3 linear cell. Each shifted axis is
        /// in [0,63], so the result is provably in [0,262144).
        /// </summary>
        public static uint ComputeKey(GpuSensorSample sample)
        {
            return ComputeKey(sample.X, sample.Y, sample.Z);
        }

        public static uint ComputeKey(uint x, uint y, uint z)
        {
            if ((x & ~CoordinateMask) != 0u ||
                (y & ~CoordinateMask) != 0u ||
                (z & ~CoordinateMask) != 0u)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(x),
                    "Coordinates must fit the fixed 16-bit sensor domain.");
            }

            uint cellX = x >> GridShift;
            uint cellY = y >> GridShift;
            uint cellZ = z >> GridShift;
            return cellX | (cellY << GridBits) |
                (cellZ << (GridBits * 2));
        }

        public static void Populate(
            GpuSensorSample[] samples,
            uint[] keys,
            uint seed,
            uint logicalState)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            if (samples.Length != keys.Length)
            {
                throw new ArgumentException(
                    "Samples and keys must have identical lengths.");
            }

            Populate(
                samples,
                keys,
                samples.Length,
                seed,
                logicalState);
        }

        public static void Populate(
            GpuSensorSample[] samples,
            uint[] keys,
            int count,
            uint seed,
            uint logicalState)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            if (count < 0 || count > samples.Length || count > keys.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            ValidateLogicalState(logicalState);

            for (int index = 0; index < count; index++)
            {
                GpuSensorSample sample = GenerateSample(
                    unchecked((uint)index),
                    seed,
                    logicalState);
                samples[index] = sample;
                uint key = ComputeKey(sample);
                if (key >= BinCount)
                {
                    throw new InvalidOperationException(
                        "The deterministic key proof was violated.");
                }
                keys[index] = key;
            }
        }

        public static void ValidateLogicalState(uint logicalState)
        {
            if (logicalState >= StateCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalState),
                    $"Logical state must be in [0,{StateCount - 1}].");
            }
        }
    }
}
