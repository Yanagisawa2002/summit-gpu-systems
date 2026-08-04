using System;
using NUnit.Framework;

namespace Summit.GpuSensorPipeline.Tests
{
    public sealed class GpuSensorIntensityQuantizerTests
    {
        [Test]
        public void EndpointsMapToTheFullUnorm16Range()
        {
            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.That(
                    GpuSensorIntensityQuantizer.Quantize(0u),
                    Is.EqualTo((ushort)0));
                Assert.That(
                    GpuSensorIntensityQuantizer.Quantize(uint.MaxValue),
                    Is.EqualTo(ushort.MaxValue));
                Assert.That(
                    GpuSensorIntensityQuantizer.QuantizeToUInt(
                        uint.MaxValue),
                    Is.EqualTo(GpuSensorIntensityQuantizer.QuantizedMax));
                Assert.That(
                    GpuSensorIntensityQuantizer.Dequantize((ushort)0),
                    Is.EqualTo(0u));
                Assert.That(
                    GpuSensorIntensityQuantizer.Dequantize(
                        ushort.MaxValue),
                    Is.EqualTo(uint.MaxValue));
            });
        }

        [TestCase(1u)]
        [TestCase(32768u)]
        [TestCase(65534u)]
        public void BoundaryNeighborhoodsRoundToTheNearestCode(uint code)
        {
            ulong center =
                code * (ulong)GpuSensorIntensityQuantizer.ReconstructionStep;
            uint lowerOutside = checked(
                (uint)(center -
                    GpuSensorIntensityQuantizer.MaxRawError - 1u));
            uint lowerInside = checked(
                (uint)(center -
                    GpuSensorIntensityQuantizer.MaxRawError));
            uint upperInside = checked(
                (uint)(center +
                    GpuSensorIntensityQuantizer.MaxRawError));
            uint upperOutside = checked(
                (uint)(center +
                    GpuSensorIntensityQuantizer.MaxRawError + 1u));

            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.That(
                    GpuSensorIntensityQuantizer.Quantize(lowerOutside),
                    Is.EqualTo(checked((ushort)(code - 1u))));
                Assert.That(
                    GpuSensorIntensityQuantizer.Quantize(lowerInside),
                    Is.EqualTo(checked((ushort)code)));
                Assert.That(
                    GpuSensorIntensityQuantizer.Quantize(upperInside),
                    Is.EqualTo(checked((ushort)code)));
                Assert.That(
                    GpuSensorIntensityQuantizer.Quantize(upperOutside),
                    Is.EqualTo(checked((ushort)(code + 1u))));
            });
        }

        [Test]
        public void DeterministicCorpusRespectsTheExactRawErrorBound()
        {
            uint observedMaxError = 0u;
            uint[] sentinels =
            {
                0u,
                1u,
                GpuSensorIntensityQuantizer.MaxRawError - 1u,
                GpuSensorIntensityQuantizer.MaxRawError,
                GpuSensorIntensityQuantizer.MaxRawError + 1u,
                GpuSensorIntensityQuantizer.ReconstructionStep,
                uint.MaxValue - 1u,
                uint.MaxValue,
            };
            foreach (uint raw in sentinels)
            {
                AssertReferenceQuantization(raw, ref observedMaxError);
            }

            uint state = 0xC001D00Du;
            for (var index = 0; index < 32768; index++)
            {
                unchecked
                {
                    state = state * 1664525u + 1013904223u;
                }
                AssertReferenceQuantization(state, ref observedMaxError);
            }

            Assert.That(
                observedMaxError,
                Is.EqualTo(GpuSensorIntensityQuantizer.MaxRawError),
                "The corpus must exercise, not merely remain below, the " +
                "documented worst-case reconstruction error.");
        }

        [Test]
        public void InvalidUintDequantizationInputsFailClosed()
        {
            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorIntensityQuantizer.Dequantize(65536u));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorIntensityQuantizer.Dequantize(uint.MaxValue));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    GpuSensorIntensityQuantizer.ValidateQuantized(65536u));
            });
        }

        private static void AssertReferenceQuantization(
            uint raw,
            ref uint observedMaxError)
        {
            uint expected = checked(
                (uint)(((ulong)raw +
                    GpuSensorIntensityQuantizer.MaxRawError) /
                    GpuSensorIntensityQuantizer.ReconstructionStep));
            ushort first = GpuSensorIntensityQuantizer.Quantize(raw);
            ushort second = GpuSensorIntensityQuantizer.Quantize(raw);
            uint error =
                GpuSensorIntensityQuantizer.AbsoluteRawError(raw);

            GpuSensorTestAssert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(checked((ushort)expected)));
                Assert.That(second, Is.EqualTo(first));
                Assert.That(
                    GpuSensorIntensityQuantizer.QuantizeToUInt(raw),
                    Is.EqualTo(expected));
                Assert.That(
                    GpuSensorIntensityQuantizer.Dequantize(first),
                    Is.EqualTo(
                        expected *
                        GpuSensorIntensityQuantizer.ReconstructionStep));
                Assert.That(
                    error,
                    Is.LessThanOrEqualTo(
                        GpuSensorIntensityQuantizer.MaxRawError),
                    $"raw=0x{raw:X8}");
            });

            observedMaxError = Math.Max(observedMaxError, error);
        }
    }
}
