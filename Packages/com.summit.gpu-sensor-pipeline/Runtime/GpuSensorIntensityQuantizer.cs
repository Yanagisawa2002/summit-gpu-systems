using System;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Deterministic UNORM32-to-UNORM16 intensity quantization shared by the
    /// expanded reference and packed SoA sensor paths.
    /// </summary>
    public static class GpuSensorIntensityQuantizer
    {
        public const uint QuantizedMax = ushort.MaxValue;
        public const uint ReconstructionStep = 65537u;
        public const uint MaxRawError = 32768u;

        /// <summary>
        /// Rounds an unsigned 32-bit intensity to the nearest UNORM16 code.
        /// There are no integer half-way ties because the reconstruction step
        /// is odd.
        /// </summary>
        public static ushort Quantize(uint rawIntensity)
        {
            ulong rounded = (ulong)rawIntensity + MaxRawError;
            return checked((ushort)(rounded / ReconstructionStep));
        }

        public static uint QuantizeToUInt(uint rawIntensity)
        {
            return Quantize(rawIntensity);
        }

        /// <summary>
        /// Reconstructs the UNORM16 code in the original uint domain.
        /// </summary>
        public static uint Dequantize(ushort quantizedIntensity)
        {
            return (uint)quantizedIntensity * ReconstructionStep;
        }

        public static uint Dequantize(uint quantizedIntensity)
        {
            ValidateQuantized(quantizedIntensity);
            return quantizedIntensity * ReconstructionStep;
        }

        public static uint AbsoluteRawError(uint rawIntensity)
        {
            uint reconstructed = Dequantize(Quantize(rawIntensity));
            return rawIntensity >= reconstructed
                ? rawIntensity - reconstructed
                : reconstructed - rawIntensity;
        }

        public static void ValidateQuantized(uint quantizedIntensity)
        {
            if (quantizedIntensity > QuantizedMax)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quantizedIntensity),
                    $"Quantized intensity must be in [0,{QuantizedMax}].");
            }
        }
    }
}
