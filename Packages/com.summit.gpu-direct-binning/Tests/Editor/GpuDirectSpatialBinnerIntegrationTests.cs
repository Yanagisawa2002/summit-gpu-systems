using System;
using System.Linq;
using NUnit.Framework;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDirectBinning.Tests
{
    public sealed class GpuDirectSpatialBinnerIntegrationTests
    {
        private const uint Sentinel = 0xdeadbeefu;

        public enum BufferRole
        {
            Keys,
            Values,
            Counts,
            Offsets,
            BinnedValues,
            Diagnostics,
        }

        public enum AliasCase
        {
            CountsWithKeys,
            OffsetsWithValues,
            OutputWithKeys,
            DiagnosticsWithValues,
            OffsetsWithCounts,
            OutputWithCounts,
            DiagnosticsWithCounts,
            OutputWithOffsets,
            DiagnosticsWithOffsets,
            DiagnosticsWithOutput,
        }

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

        [Test]
        public void ZeroElementsClearMetadataAndPreserveOutput()
        {
            const int binCount = 17;
            var metadata = Enumerable.Repeat(Sentinel, binCount).ToArray();
            var offsetMetadata = Enumerable.Repeat(
                Sentinel,
                binCount + 1).ToArray();

            using (var binner = new GpuDirectSpatialBinner(1, binCount))
            using (var keys = CreateBuffer(new[] { 7u }))
            using (var values = CreateBuffer(new[] { 9u }))
            using (var counts = CreateBuffer(metadata))
            using (var offsets = CreateBuffer(offsetMetadata))
            using (var output = CreateBuffer(new[] { Sentinel }))
            using (var diagnostics = CreateBuffer(
                       Enumerable.Repeat(
                           Sentinel,
                           GpuDirectSpatialBinner.DiagnosticWordCount)
                           .ToArray()))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/DirectBinning/ZeroElements",
                   })
            {
                binner.Record(
                    commands,
                    keys,
                    values,
                    counts,
                    offsets,
                    output,
                    diagnostics,
                    0,
                    binCount,
                    GpuPrimitiveBackend.Portable);

                Execute(commands);
                Assert.That(
                    ReadBuffer(counts, binCount),
                    Is.EqualTo(new uint[binCount]));
                Assert.That(
                    ReadBuffer(offsets, binCount + 1),
                    Is.EqualTo(new uint[binCount + 1]));
                Assert.That(ReadBuffer(output, 1)[0], Is.EqualTo(Sentinel));
                Assert.That(
                    ReadBuffer(
                        diagnostics,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(
                        new uint[
                            GpuDirectSpatialBinner.DiagnosticWordCount]));
            }
        }

        [TestCaseSource(
            typeof(CpuDirectBinningOracle),
            nameof(CpuDirectBinningOracle.BoundarySizes))]
        public void PortablePathMatchesCpuOracleAtBoundarySizes(
            int elementCount)
        {
            const int binCount = 17;
            ExecuteAndAssert(
                CpuDirectBinningOracle.CreateValidKeys(
                    elementCount,
                    binCount),
                CpuDirectBinningOracle.CreatePayload(elementCount),
                binCount,
                $"boundary N={elementCount}");
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(17)]
        [TestCase(257)]
        public void PortablePathSupportsNonPowerOfTwoBinCounts(int binCount)
        {
            const int elementCount = 4097;
            ExecuteAndAssert(
                CpuDirectBinningOracle.CreateValidKeys(
                    elementCount,
                    binCount),
                CpuDirectBinningOracle.CreatePayload(elementCount),
                binCount,
                $"non-power-of-two C={binCount}");
        }

        [TestCase(GpuPrimitiveBackend.Portable)]
        [TestCase(GpuPrimitiveBackend.Auto)]
        [TestCase(GpuPrimitiveBackend.WaveOps)]
        public void LargeNonPowerOfTwoCsrMatchesOracleAcrossScanBackends(
            GpuPrimitiveBackend backend)
        {
            if (backend == GpuPrimitiveBackend.WaveOps &&
                !GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Ignore(
                    "The active device/backend does not expose wave operations.");
            }

            const int elementCount = 4097;
            const int binCount = 257;
            ExecuteAndAssert(
                CpuDirectBinningOracle.CreateValidKeys(
                    elementCount,
                    binCount),
                CpuDirectBinningOracle.CreatePayload(elementCount),
                binCount,
                $"backend={backend}, N={elementCount}, C={binCount}",
                backend);
        }

        [TestCase(0, 1, GpuPrimitiveBackend.Portable)]
        [TestCase(0, 1, GpuPrimitiveBackend.WaveOps)]
        [TestCase(1, 1, GpuPrimitiveBackend.WaveOps)]
        [TestCase(257, 4097, GpuPrimitiveBackend.WaveOps)]
        [TestCase(257, 65537, GpuPrimitiveBackend.WaveOps)]
        [TestCase(4097, 257, GpuPrimitiveBackend.WaveOps)]
        public void GuaranteedInRangePathMatchesCpuOracleAtBoundaries(
            int elementCount,
            int binCount,
            GpuPrimitiveBackend backend)
        {
            if (backend == GpuPrimitiveBackend.WaveOps &&
                !GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Ignore(
                    "The active device/backend does not expose wave operations.");
            }

            ExecuteAndAssert(
                CpuDirectBinningOracle.CreateValidKeys(
                    elementCount,
                    binCount),
                CpuDirectBinningOracle.CreatePayload(elementCount),
                binCount,
                $"trusted backend={backend}, N={elementCount}, C={binCount}",
                backend,
                true);
        }

        [Test]
        public void MixedInvalidKeysAreExcludedAndReported()
        {
            const int elementCount = 257;
            const int binCount = 17;
            ExecuteAndAssert(
                CpuDirectBinningOracle.CreateMixedValidityKeys(
                    elementCount,
                    binCount),
                CpuDirectBinningOracle.CreatePayload(elementCount),
                binCount,
                "mixed invalid keys");
        }

        [Test]
        public void AllInvalidKeysProduceEmptyCsrAndInvalidKeyFlag()
        {
            const int elementCount = 65;
            const int binCount = 3;
            ExecuteAndAssert(
                Enumerable.Repeat(
                    (uint)binCount,
                    elementCount).ToArray(),
                CpuDirectBinningOracle.CreatePayload(elementCount),
                binCount,
                "all invalid keys");
        }

        [Test]
        public void DiscardKeyExcludesPayloadWithoutInvalidDiagnostic()
        {
            const uint discardKey = uint.MaxValue;
            uint[] keysData = { 0u, discardKey, 1u, discardKey, 7u };
            uint[] valuesData = { 10u, 11u, 12u, 13u, 14u };
            using (var binner = new GpuDirectSpatialBinner(5, 2))
            using (var keys = CreateBuffer(keysData))
            using (var values = CreateBuffer(valuesData))
            using (var counts = CreateBuffer(2))
            using (var offsets = CreateBuffer(3))
            using (var output = CreateBuffer(5))
            using (var diagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer())
            {
                binner.RecordWithDiscardKey(
                    commands,
                    keys,
                    values,
                    counts,
                    offsets,
                    output,
                    diagnostics,
                    keysData.Length,
                    2,
                    discardKey,
                    GpuPrimitiveBackend.Portable);
                Execute(commands);

                Assert.That(
                    ReadBuffer(counts, 2),
                    Is.EqualTo(new[] { 1u, 1u }));
                Assert.That(
                    ReadBuffer(offsets, 3),
                    Is.EqualTo(new[] { 0u, 1u, 2u }));
                Assert.That(
                    ReadBuffer(output, 2),
                    Is.EqualTo(new[] { 10u, 12u }));
                Assert.That(
                    ReadBuffer(
                        diagnostics,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(new[] { 1u, 1u }));
            }
        }

        [Test]
        public void DiscardKeyCanPreserveCallerOwnedDiagnostics()
        {
            const uint discardKey = uint.MaxValue;
            using (var binner = new GpuDirectSpatialBinner(3, 2))
            using (var keys = CreateBuffer(
                       new[] { 0u, discardKey, 1u }))
            using (var values = CreateBuffer(new[] { 4u, 5u, 6u }))
            using (var counts = CreateBuffer(2))
            using (var offsets = CreateBuffer(3))
            using (var output = CreateBuffer(3))
            using (var diagnostics = CreateBuffer(new[] { 9u, 10u }))
            using (var commands = new CommandBuffer())
            {
                binner.RecordWithDiscardKeyWithoutDiagnosticClear(
                    commands,
                    keys,
                    values,
                    counts,
                    offsets,
                    output,
                    diagnostics,
                    3,
                    2,
                    discardKey,
                    GpuPrimitiveBackend.Portable);
                Execute(commands);

                Assert.That(
                    ReadBuffer(diagnostics, 2),
                    Is.EqualTo(new[] { 9u, 10u }));
            }
        }

        [Test]
        public void DiscardKeyInsideBinRangeIsRejected()
        {
            using (var binner = new GpuDirectSpatialBinner(1, 2))
            using (var keys = CreateBuffer(new[] { 0u }))
            using (var values = CreateBuffer(new[] { 1u }))
            using (var counts = CreateBuffer(2))
            using (var offsets = CreateBuffer(3))
            using (var output = CreateBuffer(1))
            using (var diagnostics = CreateBuffer(2))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => binner.RecordWithDiscardKey(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        1,
                        2,
                        1u));
            }
        }

        [Test]
        public void SingleBinAtomicContentionPreservesEveryPayload()
        {
            const int elementCount = 4097;
            ExecuteAndAssert(
                new uint[elementCount],
                CpuDirectBinningOracle.CreatePayload(elementCount),
                1,
                "single-bin contention");
        }

        [Test]
        public void KeysAndValuesMayUseTheSameReadOnlyInputBuffer()
        {
            const int elementCount = 257;
            const int binCount = 17;
            var keys = CpuDirectBinningOracle.CreateValidKeys(
                elementCount,
                binCount);
            var expected = CpuDirectBinningOracle.Build(
                keys,
                keys,
                binCount);

            using (var binner = new GpuDirectSpatialBinner(
                       elementCount,
                       binCount))
            using (var aliasedInput = CreateBuffer(keys))
            using (var counts = CreateBuffer(binCount))
            using (var offsets = CreateBuffer(binCount + 1))
            using (var output = CreateBuffer(elementCount))
            using (var diagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/DirectBinning/ReadOnlyAlias",
                   })
            {
                binner.Record(
                    commands,
                    aliasedInput,
                    aliasedInput,
                    counts,
                    offsets,
                    output,
                    diagnostics,
                    elementCount,
                    binCount,
                    GpuPrimitiveBackend.Portable);

                Execute(commands);
                var actualCounts = ReadBuffer(counts, binCount);
                var actualOffsets = ReadBuffer(offsets, binCount + 1);
                var actualValues = ReadBuffer(output, elementCount);
                Assert.That(actualCounts, Is.EqualTo(expected.Counts));
                Assert.That(actualOffsets, Is.EqualTo(expected.Offsets));
                Assert.That(
                    CpuDirectBinningOracle.CanonicalizeBins(
                        actualValues,
                        actualOffsets,
                        actualCounts),
                    Is.EqualTo(
                        CpuDirectBinningOracle.CanonicalizeBins(
                            expected.BinnedValues,
                            expected.Offsets,
                            expected.Counts)));
            }
        }

        [Test]
        public void RecordRejectsCountsOutsideFixedCapacities()
        {
            using (var binner = new GpuDirectSpatialBinner(8, 3))
            using (var keys = CreateBuffer(16))
            using (var values = CreateBuffer(16))
            using (var counts = CreateBuffer(16))
            using (var offsets = CreateBuffer(16))
            using (var output = CreateBuffer(16))
            using (var diagnostics = CreateBuffer(16))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        9,
                        3));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        8,
                        4));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        -1,
                        3));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        8,
                        0));
            }
        }

        [TestCase(BufferRole.Keys)]
        [TestCase(BufferRole.Values)]
        [TestCase(BufferRole.Counts)]
        [TestCase(BufferRole.Offsets)]
        [TestCase(BufferRole.BinnedValues)]
        [TestCase(BufferRole.Diagnostics)]
        public void RecordRejectsUndersizedBuffers(BufferRole role)
        {
            AssertBufferContractRejected(role, useWrongStride: false);
        }

        [TestCase(BufferRole.Keys)]
        [TestCase(BufferRole.Values)]
        [TestCase(BufferRole.Counts)]
        [TestCase(BufferRole.Offsets)]
        [TestCase(BufferRole.BinnedValues)]
        [TestCase(BufferRole.Diagnostics)]
        public void RecordRejectsNonUintStrides(BufferRole role)
        {
            AssertBufferContractRejected(role, useWrongStride: true);
        }

        [TestCase(BufferRole.Keys)]
        [TestCase(BufferRole.Values)]
        [TestCase(BufferRole.Counts)]
        [TestCase(BufferRole.Offsets)]
        [TestCase(BufferRole.BinnedValues)]
        [TestCase(BufferRole.Diagnostics)]
        public void RecordRejectsNonStructuredUintBuffers(BufferRole role)
        {
            AssertBufferTargetRejected(role);
        }

        [TestCase(AliasCase.CountsWithKeys)]
        [TestCase(AliasCase.OffsetsWithValues)]
        [TestCase(AliasCase.OutputWithKeys)]
        [TestCase(AliasCase.DiagnosticsWithValues)]
        [TestCase(AliasCase.OffsetsWithCounts)]
        [TestCase(AliasCase.OutputWithCounts)]
        [TestCase(AliasCase.DiagnosticsWithCounts)]
        [TestCase(AliasCase.OutputWithOffsets)]
        [TestCase(AliasCase.DiagnosticsWithOffsets)]
        [TestCase(AliasCase.DiagnosticsWithOutput)]
        public void RecordRejectsWritableBufferAliasing(AliasCase aliasCase)
        {
            const int count = 8;
            const int binCount = 3;
            using (var binner = new GpuDirectSpatialBinner(
                       count,
                       binCount))
            using (var buffer0 = CreateBuffer(16))
            using (var buffer1 = CreateBuffer(16))
            using (var buffer2 = CreateBuffer(16))
            using (var buffer3 = CreateBuffer(16))
            using (var buffer4 = CreateBuffer(16))
            using (var buffer5 = CreateBuffer(16))
            using (var commands = new CommandBuffer())
            {
                GraphicsBuffer keys = buffer0;
                GraphicsBuffer values = buffer1;
                GraphicsBuffer counts = buffer2;
                GraphicsBuffer offsets = buffer3;
                GraphicsBuffer output = buffer4;
                GraphicsBuffer diagnostics = buffer5;

                ApplyAlias(
                    aliasCase,
                    ref keys,
                    ref values,
                    ref counts,
                    ref offsets,
                    ref output,
                    ref diagnostics);

                Assert.Throws<ArgumentException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        count,
                        binCount));
            }
        }

        [Test]
        public void DisposedBinnerRejectsRecord()
        {
            var binner = new GpuDirectSpatialBinner(8, 3);
            binner.Dispose();

            using (var keys = CreateBuffer(8))
            using (var values = CreateBuffer(8))
            using (var counts = CreateBuffer(3))
            using (var offsets = CreateBuffer(4))
            using (var output = CreateBuffer(8))
            using (var diagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ObjectDisposedException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        8,
                        3));
            }
        }

        [Test]
        public void ConstructorRejectsAlreadyDisposedInjectedPrimitives()
        {
            var primitives = new GpuPrimitivesRuntime(3);
            primitives.Dispose();
            GpuDirectSpatialBinner unexpectedBinner = null;

            try
            {
                Assert.That(
                    () => unexpectedBinner =
                        new GpuDirectSpatialBinner(
                            8,
                            3,
                            primitives),
                    Throws.Exception);
            }
            finally
            {
                unexpectedBinner?.Dispose();
                primitives.Dispose();
            }
        }

        [Test]
        public void DisposedInjectedPrimitivesRejectRecordBeforeCommandsChange()
        {
            var primitives = new GpuPrimitivesRuntime(3);
            try
            {
                using (var binner = new GpuDirectSpatialBinner(
                           8,
                           3,
                           primitives))
                using (var keys = CreateBuffer(8))
                using (var values = CreateBuffer(8))
                using (var counts = CreateBuffer(3))
                using (var offsets = CreateBuffer(4))
                using (var output = CreateBuffer(8))
                using (var diagnostics = CreateBuffer(
                           GpuDirectSpatialBinner.DiagnosticWordCount))
                using (var commands = new CommandBuffer
                       {
                           name = "Test/DisposedInjectedPrimitives",
                       })
                {
                    primitives.Dispose();
                    var nameBefore = commands.name;
                    var sizeBefore = commands.sizeInBytes;

                    Assert.That(
                        () => binner.Record(
                            commands,
                            keys,
                            values,
                            counts,
                            offsets,
                            output,
                            diagnostics,
                            8,
                            3),
                        Throws.Exception);
                    Assert.That(commands.name, Is.EqualTo(nameBefore));
                    Assert.That(
                        commands.sizeInBytes,
                        Is.EqualTo(sizeBefore),
                        "Record appended commands before rejecting disposed primitives.");
                }
            }
            finally
            {
                primitives.Dispose();
            }
        }

        [Test]
        public void TrustedNoDiagnosticClearPreservesSentinelAndCsr()
        {
            const int elementCount = 257;
            const int binCount = 17;
            uint[] keys = CpuDirectBinningOracle.CreateValidKeys(
                elementCount,
                binCount);
            uint[] values =
                CpuDirectBinningOracle.CreatePayload(elementCount);
            CpuDirectBinningResult expected =
                CpuDirectBinningOracle.Build(keys, values, binCount);
            uint[] diagnosticSentinel =
            {
                0x13579BDFu,
                0x2468ACE0u,
            };

            using (var binner = new GpuDirectSpatialBinner(
                       elementCount,
                       binCount))
            using (var keyBuffer = CreateBuffer(keys))
            using (var valueBuffer = CreateBuffer(values))
            using (var countBuffer = CreateBuffer(binCount))
            using (var offsetBuffer = CreateBuffer(binCount + 1))
            using (var outputBuffer = CreateBuffer(elementCount))
            using (var diagnosticBuffer = CreateBuffer(diagnosticSentinel))
            {
                using (var commands = new CommandBuffer
                       {
                           name =
                               "Test/DirectBinning/TrustedClearsDiagnostics",
                       })
                {
                    binner.RecordGuaranteedInRange(
                        commands,
                        keyBuffer,
                        valueBuffer,
                        countBuffer,
                        offsetBuffer,
                        outputBuffer,
                        diagnosticBuffer,
                        elementCount,
                        binCount,
                        GpuPrimitiveBackend.Portable);
                    Execute(commands);
                }
                Assert.That(
                    ReadBuffer(
                        diagnosticBuffer,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(new uint[
                        GpuDirectSpatialBinner.DiagnosticWordCount]));

                diagnosticBuffer.SetData(diagnosticSentinel);
                using (var commands = new CommandBuffer
                       {
                           name =
                               "Test/DirectBinning/TrustedPreservesDiagnostics",
                       })
                {
                    binner
                        .RecordGuaranteedInRangeWithoutDiagnosticClear(
                            commands,
                            keyBuffer,
                            valueBuffer,
                            countBuffer,
                            offsetBuffer,
                            outputBuffer,
                            diagnosticBuffer,
                            elementCount,
                            binCount,
                            GpuPrimitiveBackend.Portable);
                    Execute(commands);
                }

                uint[] actualCounts =
                    ReadBuffer(countBuffer, binCount);
                uint[] actualOffsets =
                    ReadBuffer(offsetBuffer, binCount + 1);
                uint[] actualValues =
                    ReadBuffer(outputBuffer, elementCount);
                Assert.That(actualCounts, Is.EqualTo(expected.Counts));
                Assert.That(actualOffsets, Is.EqualTo(expected.Offsets));
                Assert.That(
                    CpuDirectBinningOracle.CanonicalizeBins(
                        actualValues,
                        actualOffsets,
                        actualCounts),
                    Is.EqualTo(
                        CpuDirectBinningOracle.CanonicalizeBins(
                            expected.BinnedValues,
                            expected.Offsets,
                            expected.Counts)));
                Assert.That(
                    ReadBuffer(
                        diagnosticBuffer,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(diagnosticSentinel));
            }
        }

        private static void ExecuteAndAssert(
            uint[] keys,
            uint[] values,
            int binCount,
            string context,
            GpuPrimitiveBackend backend =
                GpuPrimitiveBackend.Portable,
            bool guaranteedInRange = false)
        {
            var elementCount = keys.Length;
            Assert.That(values, Has.Length.EqualTo(elementCount));
            var expected = CpuDirectBinningOracle.Build(
                keys,
                values,
                binCount);
            var outputStorage = Enumerable.Repeat(
                Sentinel,
                Math.Max(1, elementCount)).ToArray();

            using (var binner = new GpuDirectSpatialBinner(
                       Math.Max(1, elementCount),
                       binCount))
            using (var keyBuffer = CreateBuffer(keys))
            using (var valueBuffer = CreateBuffer(values))
            using (var countBuffer = CreateBuffer(binCount))
            using (var offsetBuffer = CreateBuffer(binCount + 1))
            using (var outputBuffer = CreateBuffer(outputStorage))
            using (var diagnosticBuffer = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer
                   {
                       name = $"Test/DirectBinning/{context}",
                   })
            {
                if (guaranteedInRange)
                {
                    binner.RecordGuaranteedInRange(
                        commands,
                        keyBuffer,
                        valueBuffer,
                        countBuffer,
                        offsetBuffer,
                        outputBuffer,
                        diagnosticBuffer,
                        elementCount,
                        binCount,
                        backend);
                }
                else
                {
                    binner.Record(
                        commands,
                        keyBuffer,
                        valueBuffer,
                        countBuffer,
                        offsetBuffer,
                        outputBuffer,
                        diagnosticBuffer,
                        elementCount,
                        binCount,
                        backend);
                }

                Execute(commands);
                var actualCounts = ReadBuffer(countBuffer, binCount);
                var actualOffsets = ReadBuffer(offsetBuffer, binCount + 1);
                var diagnostics = ReadBuffer(
                    diagnosticBuffer,
                    GpuDirectSpatialBinner.DiagnosticWordCount);

                Assert.That(
                    actualCounts,
                    Is.EqualTo(expected.Counts),
                    $"Counts differ for {context}.");
                Assert.That(
                    actualOffsets,
                    Is.EqualTo(expected.Offsets),
                    $"Offsets differ for {context}.");
                Assert.That(
                    diagnostics[
                        GpuDirectSpatialBinner.InvalidKeyCountWord],
                    Is.EqualTo(expected.InvalidKeyCount),
                    $"Invalid-key diagnostic differs for {context}.");
                Assert.That(
                    diagnostics[GpuDirectSpatialBinner.ErrorFlagsWord],
                    Is.EqualTo(expected.ErrorFlags),
                    $"Error flags differ for {context}.");

                var actualValidCount = checked(
                    (int)actualOffsets[binCount]);
                Assert.That(
                    actualValidCount,
                    Is.EqualTo(expected.ValidCount),
                    $"Terminal offset differs for {context}.");
                var actualValues = ReadBuffer(
                        outputBuffer,
                        Math.Max(1, elementCount))
                    .Take(actualValidCount)
                    .ToArray();
                Assert.That(
                    CpuDirectBinningOracle.CanonicalizeBins(
                        actualValues,
                        actualOffsets,
                        actualCounts),
                    Is.EqualTo(
                        CpuDirectBinningOracle.CanonicalizeBins(
                            expected.BinnedValues,
                            expected.Offsets,
                            expected.Counts)),
                    $"Per-bin membership differs for {context}.");

                CpuDirectBinningOracleTests.AssertPayloadKeyAssociation(
                    keys,
                    actualValues,
                    actualOffsets,
                    actualCounts);
                Assert.That(
                    diagnostics[GpuDirectSpatialBinner.ErrorFlagsWord] &
                    (uint)GpuDirectBinningErrorFlags
                        .ScatterDestinationOutOfRange,
                    Is.Zero,
                    $"Scatter bounds flag was raised for {context}.");

                if (elementCount == 0)
                {
                    Assert.That(
                        ReadBuffer(outputBuffer, 1)[0],
                        Is.EqualTo(Sentinel));
                }
            }
        }

        private static void AssertBufferContractRejected(
            BufferRole role,
            bool useWrongStride)
        {
            const int elementCount = 8;
            const int binCount = 3;
            using (var binner = new GpuDirectSpatialBinner(
                       elementCount,
                       binCount))
            using (var keys = CreateContractBuffer(
                       role,
                       BufferRole.Keys,
                       elementCount,
                       useWrongStride))
            using (var values = CreateContractBuffer(
                       role,
                       BufferRole.Values,
                       elementCount,
                       useWrongStride))
            using (var counts = CreateContractBuffer(
                       role,
                       BufferRole.Counts,
                       binCount,
                       useWrongStride))
            using (var offsets = CreateContractBuffer(
                       role,
                       BufferRole.Offsets,
                       binCount + 1,
                       useWrongStride))
            using (var output = CreateContractBuffer(
                       role,
                       BufferRole.BinnedValues,
                       elementCount,
                       useWrongStride))
            using (var diagnostics = CreateContractBuffer(
                       role,
                       BufferRole.Diagnostics,
                       GpuDirectSpatialBinner.DiagnosticWordCount,
                       useWrongStride))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        elementCount,
                        binCount));
            }
        }

        private static void AssertBufferTargetRejected(BufferRole role)
        {
            const int elementCount = 8;
            const int binCount = 3;
            using (var binner = new GpuDirectSpatialBinner(
                       elementCount,
                       binCount))
            using (var keys = CreateTargetContractBuffer(
                       role,
                       BufferRole.Keys,
                       elementCount))
            using (var values = CreateTargetContractBuffer(
                       role,
                       BufferRole.Values,
                       elementCount))
            using (var counts = CreateTargetContractBuffer(
                       role,
                       BufferRole.Counts,
                       binCount))
            using (var offsets = CreateTargetContractBuffer(
                       role,
                       BufferRole.Offsets,
                       binCount + 1))
            using (var output = CreateTargetContractBuffer(
                       role,
                       BufferRole.BinnedValues,
                       elementCount))
            using (var diagnostics = CreateTargetContractBuffer(
                       role,
                       BufferRole.Diagnostics,
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<ArgumentException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        elementCount,
                        binCount));
            }
        }

        private static GraphicsBuffer CreateContractBuffer(
            BufferRole selected,
            BufferRole current,
            int requiredCount,
            bool useWrongStride)
        {
            var count = selected == current && !useWrongStride
                ? Math.Max(1, requiredCount - 1)
                : requiredCount;
            var stride = selected == current && useWrongStride
                ? sizeof(uint) * 2
                : sizeof(uint);
            return CreateBuffer(count, stride);
        }

        private static GraphicsBuffer CreateTargetContractBuffer(
            BufferRole selected,
            BufferRole current,
            int requiredCount)
        {
            var target = selected == current
                ? GraphicsBuffer.Target.Raw
                : GraphicsBuffer.Target.Structured;
            return CreateBuffer(
                requiredCount,
                sizeof(uint),
                target);
        }

        private static void ApplyAlias(
            AliasCase aliasCase,
            ref GraphicsBuffer keys,
            ref GraphicsBuffer values,
            ref GraphicsBuffer counts,
            ref GraphicsBuffer offsets,
            ref GraphicsBuffer output,
            ref GraphicsBuffer diagnostics)
        {
            switch (aliasCase)
            {
                case AliasCase.CountsWithKeys:
                    counts = keys;
                    break;
                case AliasCase.OffsetsWithValues:
                    offsets = values;
                    break;
                case AliasCase.OutputWithKeys:
                    output = keys;
                    break;
                case AliasCase.DiagnosticsWithValues:
                    diagnostics = values;
                    break;
                case AliasCase.OffsetsWithCounts:
                    offsets = counts;
                    break;
                case AliasCase.OutputWithCounts:
                    output = counts;
                    break;
                case AliasCase.DiagnosticsWithCounts:
                    diagnostics = counts;
                    break;
                case AliasCase.OutputWithOffsets:
                    output = offsets;
                    break;
                case AliasCase.DiagnosticsWithOffsets:
                    diagnostics = offsets;
                    break;
                case AliasCase.DiagnosticsWithOutput:
                    diagnostics = output;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(aliasCase),
                        aliasCase,
                        null);
            }
        }

        private static GraphicsBuffer CreateBuffer(uint[] values)
        {
            var buffer = CreateBuffer(Math.Max(1, values.Length));
            if (values.Length > 0)
            {
                buffer.SetData(values);
            }

            return buffer;
        }

        private static GraphicsBuffer CreateBuffer(int count)
        {
            return CreateBuffer(count, sizeof(uint));
        }

        private static GraphicsBuffer CreateBuffer(
            int count,
            int stride)
        {
            return CreateBuffer(
                count,
                stride,
                GraphicsBuffer.Target.Structured);
        }

        private static GraphicsBuffer CreateBuffer(
            int count,
            int stride,
            GraphicsBuffer.Target target)
        {
            return new GraphicsBuffer(
                target,
                Math.Max(1, count),
                stride);
        }

        private static void Execute(CommandBuffer commands)
        {
            Graphics.ExecuteCommandBuffer(commands);
        }

        private static uint[] ReadBuffer(
            GraphicsBuffer buffer,
            int count)
        {
            var values = new uint[count];
            buffer.GetData(values);
            return values;
        }
    }
}
