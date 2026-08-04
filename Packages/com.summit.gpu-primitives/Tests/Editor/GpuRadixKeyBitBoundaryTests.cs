using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives.Tests
{
    public sealed class GpuRadixKeyBitBoundaryTests
    {
        [SetUp]
        public void RequireComputeDevice()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore(
                    "A graphics-capable compute device is required for GPU integration tests.");
            }
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(12)]
        [TestCase(13)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(31)]
        [TestCase(32)]
        public void PortablePartialRadixHandlesPassParity(int keyBitCount)
        {
            ExecuteAndAssert(4097, keyBitCount, GpuPrimitiveBackend.Portable);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(12)]
        [TestCase(13)]
        [TestCase(17)]
        [TestCase(32)]
        public void WavePartialRadixMatchesStableCpuOrder(int keyBitCount)
        {
            if (!GpuPrimitives.SupportsWaveOperations)
            {
                Assert.Ignore(
                    "The active device/backend does not expose wave operations.");
            }
            ExecuteAndAssert(4097, keyBitCount, GpuPrimitiveBackend.WaveOps);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(255)]
        [TestCase(256)]
        [TestCase(257)]
        [TestCase(4097)]
        public void PartialRadixHandlesElementBoundaries(int elementCount)
        {
            ExecuteAndAssert(
                elementCount,
                13,
                GpuPrimitiveBackend.Portable);
        }

        [TestCase(0)]
        [TestCase(33)]
        public void PartialRadixRejectsInvalidKeyBitCounts(int keyBitCount)
        {
            using (var primitives = new GpuPrimitives(1))
            using (var keysIn = CreateBuffer(new[] { 0u }))
            using (var valuesIn = CreateBuffer(new[] { 0u }))
            using (var keysOut = CreateBuffer(1))
            using (var valuesOut = CreateBuffer(1))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => primitives.RecordRadixSortKeyBits(
                        commands,
                        keysIn,
                        valuesIn,
                        keysOut,
                        valuesOut,
                        1,
                        keyBitCount,
                        GpuPrimitiveBackend.Portable));
            }
        }

        private static void ExecuteAndAssert(
            int elementCount,
            int keyBitCount,
            GpuPrimitiveBackend backend)
        {
            uint[] keys = CreateKeys(elementCount, keyBitCount);
            uint[] values = Enumerable.Range(0, elementCount)
                .Select(index => (uint)index)
                .ToArray();
            int[] order = Enumerable.Range(0, elementCount)
                .OrderBy(index => keys[index])
                .ThenBy(index => index)
                .ToArray();
            uint[] expectedKeys = order
                .Select(index => keys[index])
                .ToArray();
            uint[] expectedValues = order
                .Select(index => values[index])
                .ToArray();

            using (var primitives = new GpuPrimitives(
                       Math.Max(1, elementCount)))
            using (var keysIn = CreateBuffer(keys))
            using (var valuesIn = CreateBuffer(values))
            using (var keysOut = CreateBuffer(
                       Math.Max(1, elementCount)))
            using (var valuesOut = CreateBuffer(
                       Math.Max(1, elementCount)))
            using (var commands = new CommandBuffer
                   {
                       name =
                           $"Test/RadixKeyBits/{backend}/{keyBitCount}",
                   })
            {
                primitives.RecordRadixSortKeyBits(
                    commands,
                    keysIn,
                    valuesIn,
                    keysOut,
                    valuesOut,
                    elementCount,
                    keyBitCount,
                    backend);
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(
                    ReadBuffer(keysOut, elementCount),
                    Is.EqualTo(expectedKeys));
                Assert.That(
                    ReadBuffer(valuesOut, elementCount),
                    Is.EqualTo(expectedValues));
            }
        }

        private static uint[] CreateKeys(
            int elementCount,
            int keyBitCount)
        {
            uint mask = keyBitCount == 32
                ? uint.MaxValue
                : (1u << keyBitCount) - 1u;
            var keys = new uint[elementCount];
            uint state = 0x12345678u;
            for (int index = 0; index < elementCount; index++)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                keys[index] = state & mask;
            }
            return keys;
        }

        private static GraphicsBuffer CreateBuffer(int count)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Math.Max(1, count),
                sizeof(uint));
        }

        private static GraphicsBuffer CreateBuffer(uint[] values)
        {
            GraphicsBuffer buffer = CreateBuffer(values.Length);
            if (values.Length > 0)
            {
                buffer.SetData(values);
            }
            return buffer;
        }

        private static uint[] ReadBuffer(
            GraphicsBuffer buffer,
            int count)
        {
            var result = new uint[count];
            if (count > 0)
            {
                buffer.GetData(result);
            }
            return result;
        }
    }
}
