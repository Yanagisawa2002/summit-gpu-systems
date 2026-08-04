using System;
using System.Linq;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives.Tests
{
    public sealed class GpuPrimitivesIntegrationTests
    {
        [SetUp]
        public void RequireComputeDevice()
        {
            if (!SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("A graphics-capable compute device is required for GPU integration tests.");
            }
        }

        [Test]
        public void ZeroCountOperationsHonorDocumentedNoOpAndClearContracts()
        {
            const uint sentinel = 0xdeadbeefu;
            const int binCount = 16;
            var histogramSentinels = Enumerable.Repeat(sentinel, binCount).ToArray();

            using (var primitives = new GpuPrimitives(1))
            using (var dummyInput = CreateBuffer(new[] { 7u }))
            using (var dummyPredicates = CreateBuffer(new[] { 1u }))
            using (var scanOutput = CreateBuffer(new[] { sentinel }))
            using (var histogramCleared = CreateBuffer(histogramSentinels))
            using (var histogramPreserved = CreateBuffer(histogramSentinels))
            using (var compactOutput = CreateBuffer(new[] { sentinel }))
            using (var stableCount = CreateBuffer(new[] { sentinel }))
            using (var appendCount = CreateBuffer(new[] { sentinel }))
            using (var radixKeysOut = CreateBuffer(new[] { sentinel }))
            using (var radixValuesOut = CreateBuffer(new[] { sentinel }))
            using (var commandBuffer = new CommandBuffer { name = "Test/ZeroCountContracts" })
            {
                primitives.RecordExclusiveScan(
                    commandBuffer,
                    dummyInput,
                    scanOutput,
                    0,
                    GpuPrimitiveBackend.Portable);
                primitives.RecordHistogram(
                    commandBuffer,
                    dummyInput,
                    histogramCleared,
                    0,
                    binCount,
                    backend: GpuPrimitiveBackend.Portable,
                    clearOutput: true);
                primitives.RecordHistogram(
                    commandBuffer,
                    dummyInput,
                    histogramPreserved,
                    0,
                    binCount,
                    backend: GpuPrimitiveBackend.Portable,
                    clearOutput: false);
                primitives.RecordStableCompaction(
                    commandBuffer,
                    dummyInput,
                    dummyPredicates,
                    compactOutput,
                    stableCount,
                    0,
                    GpuPrimitiveBackend.Portable);
                primitives.RecordAppendCompaction(
                    commandBuffer,
                    dummyInput,
                    dummyPredicates,
                    compactOutput,
                    appendCount,
                    0,
                    GpuPrimitiveBackend.Portable);
                primitives.RecordRadixSort32(
                    commandBuffer,
                    dummyInput,
                    dummyInput,
                    radixKeysOut,
                    radixValuesOut,
                    0,
                    GpuPrimitiveBackend.Portable);

                Execute(commandBuffer);

                Assert.That(ReadBuffer(scanOutput, 1)[0], Is.EqualTo(sentinel));
                Assert.That(
                    ReadBuffer(histogramCleared, binCount),
                    Is.EqualTo(new uint[binCount]));
                Assert.That(
                    ReadBuffer(histogramPreserved, binCount),
                    Is.EqualTo(histogramSentinels));
                Assert.That(ReadBuffer(stableCount, 1)[0], Is.Zero);
                Assert.That(ReadBuffer(appendCount, 1)[0], Is.Zero);
                Assert.That(ReadBuffer(compactOutput, 1)[0], Is.EqualTo(sentinel));
                Assert.That(ReadBuffer(radixKeysOut, 1)[0], Is.EqualTo(sentinel));
                Assert.That(ReadBuffer(radixValuesOut, 1)[0], Is.EqualTo(sentinel));
            }
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void PortableExclusiveScanMatchesCpuOracle(int elementCount)
        {
            var input = CreateScanInput(elementCount);
            var expected = CpuPrimitiveOracle.ExclusiveScan(input);

            using (var primitives = new GpuPrimitives(elementCount))
            using (var inputBuffer = CreateBuffer(input))
            using (var outputBuffer = CreateBuffer(elementCount))
            using (var commandBuffer = new CommandBuffer { name = "Test/PortableExclusiveScan" })
            {
                primitives.RecordExclusiveScan(
                    commandBuffer,
                    inputBuffer,
                    outputBuffer,
                    elementCount,
                    GpuPrimitiveBackend.Portable);

                Execute(commandBuffer);
                AssertBufferEquals(outputBuffer, expected, "portable exclusive scan", elementCount);
            }
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void PortableHistogramMatchesCpuOracleAndPreservesTotal(int elementCount)
        {
            const int binCount = 256;
            var keys = CpuPrimitiveOracle.CreateKeys(elementCount, binCount);
            var expected = CpuPrimitiveOracle.Histogram(keys, binCount);

            using (var primitives = new GpuPrimitives(elementCount))
            using (var keyBuffer = CreateBuffer(keys))
            using (var histogramBuffer = CreateBuffer(binCount))
            using (var commandBuffer = new CommandBuffer { name = "Test/PortableHistogram" })
            {
                primitives.RecordHistogram(
                    commandBuffer,
                    keyBuffer,
                    histogramBuffer,
                    elementCount,
                    binCount,
                    backend: GpuPrimitiveBackend.Portable);

                Execute(commandBuffer);
                var actual = ReadBuffer(histogramBuffer, binCount);
                Assert.That(actual, Is.EqualTo(expected), $"Portable histogram differs for N={elementCount}.");
                Assert.That(actual.Sum(value => (long)value), Is.EqualTo(elementCount));
            }
        }

        [TestCase(1)]
        [TestCase(16)]
        [TestCase(256)]
        [TestCase(4096)]
        public void PortableHistogramMatchesCpuOracleAcrossDocumentedDomains(int binCount)
        {
            const int elementCount = 4097;
            var keys = CpuPrimitiveOracle.CreateKeys(elementCount, (uint)binCount);
            var expected = CpuPrimitiveOracle.Histogram(keys, binCount);

            using (var primitives = new GpuPrimitives(elementCount))
            using (var keyBuffer = CreateBuffer(keys))
            using (var histogramBuffer = CreateBuffer(binCount))
            using (var commandBuffer = new CommandBuffer { name = "Test/PortableHistogramDomains" })
            {
                primitives.RecordHistogram(
                    commandBuffer,
                    keyBuffer,
                    histogramBuffer,
                    elementCount,
                    binCount,
                    backend: GpuPrimitiveBackend.Portable);

                Execute(commandBuffer);
                var actual = ReadBuffer(histogramBuffer, binCount);
                Assert.That(
                    actual,
                    Is.EqualTo(expected),
                    $"Portable histogram differs for {binCount} bins.");
                Assert.That(actual.Sum(value => (long)value), Is.EqualTo(elementCount));
            }
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void PortableStableCompactionMatchesCpuOracleAndPreservesOrder(int elementCount)
        {
            var values = CpuPrimitiveOracle.CreatePayload(elementCount);
            var keep = CpuPrimitiveOracle.CreateKeepMask(elementCount);
            var predicates = keep.Select(value => value ? 1u : 0u).ToArray();
            var expected = CpuPrimitiveOracle.StableCompact(values, keep);

            using (var primitives = new GpuPrimitives(elementCount))
            using (var valueBuffer = CreateBuffer(values))
            using (var predicateBuffer = CreateBuffer(predicates))
            using (var outputBuffer = CreateBuffer(elementCount))
            using (var outputCountBuffer = CreateBuffer(1))
            using (var commandBuffer = new CommandBuffer { name = "Test/PortableStableCompaction" })
            {
                primitives.RecordStableCompaction(
                    commandBuffer,
                    valueBuffer,
                    predicateBuffer,
                    outputBuffer,
                    outputCountBuffer,
                    elementCount,
                    GpuPrimitiveBackend.Portable);

                Execute(commandBuffer);
                var actualCount = ReadBuffer(outputCountBuffer, 1)[0];
                Assert.That(actualCount, Is.EqualTo((uint)expected.Length));

                var actual = ReadBuffer(outputBuffer, elementCount)
                    .Take((int)actualCount)
                    .ToArray();
                Assert.That(actual, Is.EqualTo(expected), $"Portable stable compaction differs for N={elementCount}.");
            }
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void PortableAppendCompactionMatchesCpuOracleMembership(int elementCount)
        {
            var values = CpuPrimitiveOracle.CreatePayload(elementCount);
            var keep = CpuPrimitiveOracle.CreateKeepMask(elementCount);
            var predicates = keep.Select(value => value ? 1u : 0u).ToArray();
            var expected = CpuPrimitiveOracle.StableCompact(values, keep);
            var actual = ExecuteAppendCompaction(
                values,
                predicates,
                GpuPrimitiveBackend.Portable);

            Assert.That(actual.Count, Is.EqualTo((uint)expected.Length));
            Assert.That(
                actual.Values.OrderBy(value => value),
                Is.EqualTo(expected.OrderBy(value => value)),
                $"Portable append-compaction membership differs for N={elementCount}.");
        }

        [TestCaseSource(typeof(CpuPrimitiveOracle), nameof(CpuPrimitiveOracle.BoundarySizes))]
        public void PortableRadixSortMatchesStableCpuOracle(int elementCount)
        {
            var keys = CpuPrimitiveOracle.CreateRadixKeys(elementCount);
            var values = CpuPrimitiveOracle.CreatePayload(elementCount);
            var expected = CpuPrimitiveOracle.StableRadixSort(keys, values);

            using (var primitives = new GpuPrimitives(elementCount))
            using (var keysIn = CreateBuffer(keys))
            using (var valuesIn = CreateBuffer(values))
            using (var keysOut = CreateBuffer(elementCount))
            using (var valuesOut = CreateBuffer(elementCount))
            using (var commandBuffer = new CommandBuffer { name = "Test/PortableRadixSort32" })
            {
                primitives.RecordRadixSort32(
                    commandBuffer,
                    keysIn,
                    valuesIn,
                    keysOut,
                    valuesOut,
                    elementCount,
                    GpuPrimitiveBackend.Portable);

                Execute(commandBuffer);
                var actualKeys = ReadBuffer(keysOut, elementCount);
                var actualValues = ReadBuffer(valuesOut, elementCount);

                Assert.That(actualKeys, Is.EqualTo(expected.Keys), $"Portable radix keys differ for N={elementCount}.");
                Assert.That(actualValues, Is.EqualTo(expected.Payload), $"Portable radix payload differs for N={elementCount}.");
                AssertRadixInvariants(keys, actualKeys, actualValues, elementCount);
            }
        }

        [Test]
        public void WaveAndPortableBackendsAreExactlyEquivalentAtAllBoundaries()
        {
            if (!GpuPrimitives.SupportsWaveOperations)
            {
                Assert.Ignore("The active device/backend does not expose wave operations.");
            }

            foreach (var elementCount in CpuPrimitiveOracle.BoundarySizes)
            {
                AssertScanBackendsEquivalent(elementCount);
                AssertHistogramBackendsEquivalent(elementCount);
                AssertStableCompactionBackendsEquivalent(elementCount);
                AssertAppendCompactionBackendsEquivalent(elementCount);
                AssertRadixBackendsEquivalent(elementCount);
            }
        }

        private static void AssertScanBackendsEquivalent(int elementCount)
        {
            var input = CreateScanInput(elementCount);
            var portable = ExecuteScan(input, GpuPrimitiveBackend.Portable);
            var wave = ExecuteScan(input, GpuPrimitiveBackend.WaveOps);

            Assert.That(wave, Is.EqualTo(portable), $"Wave scan differs from portable scan for N={elementCount}.");
            Assert.That(wave, Is.EqualTo(CpuPrimitiveOracle.ExclusiveScan(input)));
        }

        private static uint[] ExecuteScan(uint[] input, GpuPrimitiveBackend backend)
        {
            using (var primitives = new GpuPrimitives(input.Length))
            using (var inputBuffer = CreateBuffer(input))
            using (var outputBuffer = CreateBuffer(input.Length))
            using (var commandBuffer = new CommandBuffer { name = $"Test/ExclusiveScan/{backend}" })
            {
                primitives.RecordExclusiveScan(
                    commandBuffer,
                    inputBuffer,
                    outputBuffer,
                    input.Length,
                    backend);

                Execute(commandBuffer);
                return ReadBuffer(outputBuffer, input.Length);
            }
        }

        private static void AssertHistogramBackendsEquivalent(int elementCount)
        {
            const int waveCompatibleBinCount = 16;
            var keys = CpuPrimitiveOracle.CreateKeys(elementCount, waveCompatibleBinCount);
            var portable = ExecuteHistogram(keys, waveCompatibleBinCount, GpuPrimitiveBackend.Portable);
            var wave = ExecuteHistogram(keys, waveCompatibleBinCount, GpuPrimitiveBackend.WaveOps);

            Assert.That(wave, Is.EqualTo(portable), $"Wave histogram differs from portable histogram for N={elementCount}.");
            Assert.That(wave, Is.EqualTo(CpuPrimitiveOracle.Histogram(keys, waveCompatibleBinCount)));
            Assert.That(wave.Sum(value => (long)value), Is.EqualTo(elementCount));
        }

        private static uint[] ExecuteHistogram(
            uint[] keys,
            int binCount,
            GpuPrimitiveBackend backend)
        {
            using (var primitives = new GpuPrimitives(keys.Length))
            using (var keyBuffer = CreateBuffer(keys))
            using (var histogramBuffer = CreateBuffer(binCount))
            using (var commandBuffer = new CommandBuffer { name = $"Test/Histogram/{backend}" })
            {
                primitives.RecordHistogram(
                    commandBuffer,
                    keyBuffer,
                    histogramBuffer,
                    keys.Length,
                    binCount,
                    backend: backend);

                Execute(commandBuffer);
                return ReadBuffer(histogramBuffer, binCount);
            }
        }

        private static void AssertStableCompactionBackendsEquivalent(int elementCount)
        {
            var values = CpuPrimitiveOracle.CreatePayload(elementCount);
            var keep = CpuPrimitiveOracle.CreateKeepMask(elementCount);
            var predicates = keep.Select(value => value ? 1u : 0u).ToArray();
            var expected = CpuPrimitiveOracle.StableCompact(values, keep);
            var portable = ExecuteStableCompaction(values, predicates, GpuPrimitiveBackend.Portable);
            var wave = ExecuteStableCompaction(values, predicates, GpuPrimitiveBackend.WaveOps);

            Assert.That(wave.Count, Is.EqualTo(portable.Count));
            Assert.That(wave.Values, Is.EqualTo(portable.Values), $"Wave compaction differs for N={elementCount}.");
            Assert.That(wave.Count, Is.EqualTo((uint)expected.Length));
            Assert.That(wave.Values, Is.EqualTo(expected));
        }

        private static (uint Count, uint[] Values) ExecuteStableCompaction(
            uint[] values,
            uint[] predicates,
            GpuPrimitiveBackend backend)
        {
            using (var primitives = new GpuPrimitives(values.Length))
            using (var valueBuffer = CreateBuffer(values))
            using (var predicateBuffer = CreateBuffer(predicates))
            using (var outputBuffer = CreateBuffer(values.Length))
            using (var outputCountBuffer = CreateBuffer(1))
            using (var commandBuffer = new CommandBuffer { name = $"Test/StableCompaction/{backend}" })
            {
                primitives.RecordStableCompaction(
                    commandBuffer,
                    valueBuffer,
                    predicateBuffer,
                    outputBuffer,
                    outputCountBuffer,
                    values.Length,
                    backend);

                Execute(commandBuffer);
                var count = ReadBuffer(outputCountBuffer, 1)[0];
                var output = ReadBuffer(outputBuffer, values.Length)
                    .Take((int)count)
                    .ToArray();
                return (count, output);
            }
        }

        private static void AssertAppendCompactionBackendsEquivalent(int elementCount)
        {
            var values = CpuPrimitiveOracle.CreatePayload(elementCount);
            var keep = CpuPrimitiveOracle.CreateKeepMask(elementCount);
            var predicates = keep.Select(value => value ? 1u : 0u).ToArray();
            var expected = CpuPrimitiveOracle.StableCompact(values, keep)
                .OrderBy(value => value)
                .ToArray();
            var portable = ExecuteAppendCompaction(
                values,
                predicates,
                GpuPrimitiveBackend.Portable);
            var wave = ExecuteAppendCompaction(
                values,
                predicates,
                GpuPrimitiveBackend.WaveOps);

            Assert.That(wave.Count, Is.EqualTo(portable.Count));
            Assert.That(wave.Count, Is.EqualTo((uint)expected.Length));
            Assert.That(
                portable.Values.OrderBy(value => value),
                Is.EqualTo(expected),
                $"Portable append-compaction membership differs for N={elementCount}.");
            Assert.That(
                wave.Values.OrderBy(value => value),
                Is.EqualTo(expected),
                $"Wave append-compaction membership differs for N={elementCount}.");
        }

        private static (uint Count, uint[] Values) ExecuteAppendCompaction(
            uint[] values,
            uint[] predicates,
            GpuPrimitiveBackend backend)
        {
            using (var primitives = new GpuPrimitives(values.Length))
            using (var valueBuffer = CreateBuffer(values))
            using (var predicateBuffer = CreateBuffer(predicates))
            using (var outputBuffer = CreateBuffer(values.Length))
            using (var outputCountBuffer = CreateBuffer(1))
            using (var commandBuffer = new CommandBuffer { name = $"Test/AppendCompaction/{backend}" })
            {
                primitives.RecordAppendCompaction(
                    commandBuffer,
                    valueBuffer,
                    predicateBuffer,
                    outputBuffer,
                    outputCountBuffer,
                    values.Length,
                    backend);

                Execute(commandBuffer);
                var count = ReadBuffer(outputCountBuffer, 1)[0];
                var output = ReadBuffer(outputBuffer, values.Length)
                    .Take((int)count)
                    .ToArray();
                return (count, output);
            }
        }

        private static void AssertRadixBackendsEquivalent(int elementCount)
        {
            var keys = CpuPrimitiveOracle.CreateRadixKeys(elementCount);
            var values = CpuPrimitiveOracle.CreatePayload(elementCount);
            var expected = CpuPrimitiveOracle.StableRadixSort(keys, values);
            var portable = ExecuteRadix(keys, values, GpuPrimitiveBackend.Portable);
            var wave = ExecuteRadix(keys, values, GpuPrimitiveBackend.WaveOps);

            Assert.That(wave.Keys, Is.EqualTo(portable.Keys), $"Wave radix keys differ for N={elementCount}.");
            Assert.That(wave.Values, Is.EqualTo(portable.Values), $"Wave radix payload differs for N={elementCount}.");
            Assert.That(wave.Keys, Is.EqualTo(expected.Keys));
            Assert.That(wave.Values, Is.EqualTo(expected.Payload));
            AssertRadixInvariants(keys, wave.Keys, wave.Values, elementCount);
        }

        private static (uint[] Keys, uint[] Values) ExecuteRadix(
            uint[] keys,
            uint[] values,
            GpuPrimitiveBackend backend)
        {
            using (var primitives = new GpuPrimitives(keys.Length))
            using (var keysIn = CreateBuffer(keys))
            using (var valuesIn = CreateBuffer(values))
            using (var keysOut = CreateBuffer(keys.Length))
            using (var valuesOut = CreateBuffer(values.Length))
            using (var commandBuffer = new CommandBuffer { name = $"Test/RadixSort32/{backend}" })
            {
                primitives.RecordRadixSort32(
                    commandBuffer,
                    keysIn,
                    valuesIn,
                    keysOut,
                    valuesOut,
                    keys.Length,
                    backend);

                Execute(commandBuffer);
                return (
                    ReadBuffer(keysOut, keys.Length),
                    ReadBuffer(valuesOut, values.Length));
            }
        }

        private static uint[] CreateScanInput(int elementCount)
        {
            var input = new uint[elementCount];
            for (var index = 0; index < elementCount; index++)
            {
                input[index] = (uint)((index * 17 + 3) % 11);
            }

            return input;
        }

        private static GraphicsBuffer CreateBuffer(uint[] values)
        {
            var buffer = CreateBuffer(values.Length);
            buffer.SetData(values);
            return buffer;
        }

        private static GraphicsBuffer CreateBuffer(int count)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Math.Max(1, count),
                sizeof(uint));
        }

        private static void Execute(CommandBuffer commandBuffer)
        {
            Graphics.ExecuteCommandBuffer(commandBuffer);
        }

        private static uint[] ReadBuffer(GraphicsBuffer buffer, int count)
        {
            var output = new uint[count];
            buffer.GetData(output);
            return output;
        }

        private static void AssertBufferEquals(
            GraphicsBuffer buffer,
            uint[] expected,
            string operation,
            int elementCount)
        {
            Assert.That(
                ReadBuffer(buffer, expected.Length),
                Is.EqualTo(expected),
                $"{operation} differs from the CPU oracle for N={elementCount}.");
        }

        private static void AssertRadixInvariants(
            uint[] inputKeys,
            uint[] outputKeys,
            uint[] outputPayload,
            int elementCount)
        {
            Assert.That(outputKeys, Has.Length.EqualTo(elementCount));
            Assert.That(outputPayload, Has.Length.EqualTo(elementCount));
            Assert.That(
                outputPayload.OrderBy(value => value),
                Is.EqualTo(CpuPrimitiveOracle.CreatePayload(elementCount)),
                $"Radix payload is not a permutation for N={elementCount}.");

            for (var index = 0; index < elementCount; index++)
            {
                Assert.That(outputPayload[index], Is.LessThan((uint)elementCount));
                Assert.That(outputKeys[index], Is.EqualTo(inputKeys[outputPayload[index]]));
                if (index == 0)
                {
                    continue;
                }

                Assert.That(outputKeys[index], Is.GreaterThanOrEqualTo(outputKeys[index - 1]));
                if (outputKeys[index] == outputKeys[index - 1])
                {
                    Assert.That(
                        outputPayload[index],
                        Is.GreaterThan(outputPayload[index - 1]),
                        $"Radix sort is not stable at output {index} for N={elementCount}.");
                }
            }
        }
    }
}
