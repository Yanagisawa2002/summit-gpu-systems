using System;
using System.Linq;
using NUnit.Framework;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuAdaptiveBinning.Tests
{
    public sealed class GpuRadixSpatialBinnerIntegrationTests
    {
        private const uint Sentinel = 0xdeadbeefu;

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

        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(257, 17)]
        [TestCase(4097, 257)]
        [TestCase(8193, 4096)]
        [TestCase(257, 4097)]
        [TestCase(257, 65536)]
        [TestCase(257, 65537)]
        public void PortableRadixMatchesStableCpuOracle(
            int elementCount,
            int binCount)
        {
            ExecuteRadixAndAssert(
                elementCount,
                binCount,
                GpuPrimitiveBackend.Portable);
        }

        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(257, 17)]
        [TestCase(4097, 257)]
        [TestCase(257, 4097)]
        [TestCase(257, 65536)]
        [TestCase(257, 65537)]
        public void WaveRadixMatchesStableCpuOracle(
            int elementCount,
            int binCount)
        {
            if (!GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Ignore(
                    "The active device/backend does not expose wave operations.");
            }

            ExecuteRadixAndAssert(
                elementCount,
                binCount,
                GpuPrimitiveBackend.WaveOps);
        }

        [Test]
        public void ZeroElementsClearMetadataAndPreserveOutput()
        {
            const int binCount = 17;
            using (var binner = new GpuRadixSpatialBinner(1, binCount))
            using (var keys = CreateBuffer(new[] { 0u }))
            using (var values = CreateBuffer(new[] { 7u }))
            using (var counts = CreateBuffer(
                       Enumerable.Repeat(Sentinel, binCount).ToArray()))
            using (var offsets = CreateBuffer(
                       Enumerable.Repeat(
                           Sentinel,
                           binCount + 1).ToArray()))
            using (var output = CreateBuffer(new[] { Sentinel }))
            using (var diagnostics = CreateBuffer(
                       Enumerable.Repeat(
                           Sentinel,
                           GpuDirectSpatialBinner.DiagnosticWordCount)
                           .ToArray()))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/AdaptiveBinning/RadixZero",
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
                    GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
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

        [Test]
        public void RadixRejectsUntrustedKeyDomain()
        {
            using (var binner = new GpuRadixSpatialBinner(1, 1))
            using (var keys = CreateBuffer(new[] { 0u }))
            using (var values = CreateBuffer(new[] { 0u }))
            using (var counts = CreateBuffer(1))
            using (var offsets = CreateBuffer(2))
            using (var output = CreateBuffer(1))
            using (var diagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer())
            {
                Assert.Throws<InvalidOperationException>(
                    () => binner.Record(
                        commands,
                        keys,
                        values,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        1,
                        1,
                        GpuAdaptiveBinningKeyDomain.Untrusted,
                        GpuPrimitiveBackend.Portable));
            }
        }

        [Test]
        public void KeysAndValuesMayAliasAsReadOnlyInput()
        {
            const int elementCount = 4097;
            const int binCount = 257;
            uint[] keys =
                GpuAdaptiveBinningTestOracle.CreateValidKeys(
                    elementCount,
                    binCount);
            GpuAdaptiveBinningTestOracle.Result expected =
                GpuAdaptiveBinningTestOracle.Build(
                    keys,
                    keys,
                    binCount);

            using (var binner = new GpuRadixSpatialBinner(
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
                       name = "Test/AdaptiveBinning/ReadOnlyAlias",
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
                    GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
                    GpuPrimitiveBackend.Portable);
                Execute(commands);

                Assert.That(
                    ReadBuffer(counts, binCount),
                    Is.EqualTo(expected.Counts));
                Assert.That(
                    ReadBuffer(offsets, binCount + 1),
                    Is.EqualTo(expected.Offsets));
                Assert.That(
                    ReadBuffer(output, elementCount),
                    Is.EqualTo(expected.Values));
            }
        }

        [TestCase(4097, 257, GpuPrimitiveBackend.Portable)]
        [TestCase(0, 1, GpuPrimitiveBackend.WaveOps)]
        [TestCase(1, 1, GpuPrimitiveBackend.WaveOps)]
        [TestCase(257, 4097, GpuPrimitiveBackend.WaveOps)]
        [TestCase(4097, 257, GpuPrimitiveBackend.WaveOps)]
        public void ForcedTrustedDirectAndRadixProduceEquivalentCsrMembership(
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

            uint[] keys =
                GpuAdaptiveBinningTestOracle.CreateValidKeys(
                    elementCount,
                    binCount);
            uint[] values =
                GpuAdaptiveBinningTestOracle.CreateValues(elementCount);

            using (var binner = new GpuAdaptiveSpatialBinner(
                       Math.Max(1, elementCount),
                       binCount))
            using (var keysBuffer = CreateBuffer(keys))
            using (var valuesBuffer = CreateBuffer(values))
            using (var directCounts = CreateBuffer(binCount))
            using (var directOffsets = CreateBuffer(binCount + 1))
            using (var directValues = CreateBuffer(
                       Math.Max(1, elementCount)))
            using (var directDiagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var radixCounts = CreateBuffer(binCount))
            using (var radixOffsets = CreateBuffer(binCount + 1))
            using (var radixValues = CreateBuffer(
                       Math.Max(1, elementCount)))
            using (var radixDiagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer
                   {
                       name = $"Test/AdaptiveBinning/ForcedEquivalence/{backend}",
                   })
            {
                binner.Record(
                    commands,
                    keysBuffer,
                    valuesBuffer,
                    directCounts,
                    directOffsets,
                    directValues,
                    directDiagnostics,
                    elementCount,
                    binCount,
                    GpuAdaptiveBinningBackend.Direct,
                    GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
                    backend);
                binner.Record(
                    commands,
                    keysBuffer,
                    valuesBuffer,
                    radixCounts,
                    radixOffsets,
                    radixValues,
                    radixDiagnostics,
                    elementCount,
                    binCount,
                    GpuAdaptiveBinningBackend.Radix,
                    GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
                    backend);
                Execute(commands);

                uint[] directCountData =
                    ReadBuffer(directCounts, binCount);
                uint[] directOffsetData =
                    ReadBuffer(directOffsets, binCount + 1);
                uint[] directValueData =
                    ReadBuffer(directValues, elementCount);
                uint[] radixCountData =
                    ReadBuffer(radixCounts, binCount);
                uint[] radixOffsetData =
                    ReadBuffer(radixOffsets, binCount + 1);
                uint[] radixValueData =
                    ReadBuffer(radixValues, elementCount);

                Assert.That(radixCountData, Is.EqualTo(directCountData));
                Assert.That(radixOffsetData, Is.EqualTo(directOffsetData));
                Assert.That(
                    GpuAdaptiveBinningTestOracle.Canonicalize(
                        radixValueData,
                        radixCountData,
                        radixOffsetData),
                    Is.EqualTo(
                        GpuAdaptiveBinningTestOracle.Canonicalize(
                            directValueData,
                            directCountData,
                            directOffsetData)));
                Assert.That(
                    ReadBuffer(
                        directDiagnostics,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(new uint[2]));
                Assert.That(
                    ReadBuffer(
                        radixDiagnostics,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(new uint[2]));
            }
        }

        [TestCase(
            0x1002,
            9u,
            true,
            GpuAdaptiveBinningBackend.Radix)]
        [TestCase(
            0x1002,
            10u,
            true,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            0x1002,
            9u,
            false,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            0x10de,
            9u,
            true,
            GpuAdaptiveBinningBackend.Direct)]
        public void RecordAdaptiveExactCellPreservesCsrContract(
            int runtimeVendorId,
            uint exactSingleBinKey,
            bool hasExactSingleBinKey,
            GpuAdaptiveBinningBackend expectedBackend)
        {
            if (!GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                Assert.Ignore(
                    "The active device/backend does not expose wave operations.");
            }

            const int elementCount = 262144;
            const int binCount = 16;
            uint[] keys = Enumerable.Repeat(
                exactSingleBinKey, elementCount).ToArray();
            uint[] values =
                GpuAdaptiveBinningTestOracle.CreateValues(elementCount);
            GpuAdaptiveBinningTestOracle.Result expected =
                GpuAdaptiveBinningTestOracle.Build(
                    keys,
                    values,
                    binCount);
            var profile = new GpuAdaptiveBinningCalibrationProfile(
                GpuAdaptiveBinningCalibrationProfile.CurrentSchemaVersion,
                "test-amd-r9700-dx12-exact-cells-v2",
                2,
                new GpuAdaptiveBinningDeviceBinding(
                    0x1002,
                    0x7551,
                    GraphicsDeviceType.Direct3D12),
                GpuPrimitiveBackend.WaveOps,
                requiredProfilerMarkersEnabled: false,
                radixCellCount: 2,
                radixCell0: new GpuAdaptiveBinningCalibrationCell(
                    262144,
                    16,
                    GpuAdaptiveBinningWorkloadConcentration
                        .SingleBinGuaranteed,
                    hasExactSingleBinKey: true,
                    exactSingleBinKey: 9u),
                radixCell1: new GpuAdaptiveBinningCalibrationCell(
                    1048576,
                    16,
                    GpuAdaptiveBinningWorkloadConcentration
                        .SingleBinGuaranteed,
                    hasExactSingleBinKey: true,
                    exactSingleBinKey: 10u));
            var hint = new GpuAdaptiveBinningWorkloadHint(
                elementCount,
                binCount,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey,
                exactSingleBinKey);
            var identity = new GpuAdaptiveBinningDeviceIdentity(
                runtimeVendorId,
                0x7551,
                GraphicsDeviceType.Direct3D12);

            using (var binner = new GpuAdaptiveSpatialBinner(
                       elementCount,
                       binCount,
                       emitProfilerMarkers: false))
            using (var keysBuffer = CreateBuffer(keys))
            using (var valuesBuffer = CreateBuffer(values))
            using (var counts = CreateBuffer(binCount))
            using (var offsets = CreateBuffer(binCount + 1))
            using (var output = CreateBuffer(elementCount))
            using (var diagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/AdaptiveBinning/CalibratedSelection",
                   })
            {
                Assert.That(binner.EmitsProfilerMarkers, Is.False);
                GpuAdaptiveBinningBackend selected =
                    binner.RecordAdaptive(
                        commands,
                        keysBuffer,
                        valuesBuffer,
                        counts,
                        offsets,
                        output,
                        diagnostics,
                        elementCount,
                        binCount,
                        GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
                        in profile,
                        in hint,
                        in identity,
                        GpuPrimitiveBackend.WaveOps);
                Execute(commands);

                uint[] countData = ReadBuffer(counts, binCount);
                uint[] offsetData = ReadBuffer(offsets, binCount + 1);
                uint[] valueData = ReadBuffer(output, elementCount);
                Assert.That(selected, Is.EqualTo(GpuAdaptiveBinningBackend.Direct),
                    "Legacy v2 calibration lacks an independent environment identity.");
                Assert.That(countData, Is.EqualTo(expected.Counts));
                Assert.That(offsetData, Is.EqualTo(expected.Offsets));
                Assert.That(
                    GpuAdaptiveBinningTestOracle.Canonicalize(
                        valueData,
                        countData,
                        offsetData),
                    Is.EqualTo(
                        GpuAdaptiveBinningTestOracle.Canonicalize(
                            expected.Values,
                            expected.Counts,
                            expected.Offsets)));
                Assert.That(
                    ReadBuffer(
                        diagnostics,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(new uint[2]));
            }
        }

        [Test]
        public void MatrixTransitionsAndUntrustedFallbackMatchIndependentCsrOracle()
        {
            const int n = 513, c = 17;
            var features = GpuAdaptiveBinningMatrixTests.Features(n); features.binCount = c;
            var document = GpuAdaptiveBinningMatrixTests.Document();
            document.rows = new[] { new GpuAdaptiveBinningMatrixRow { features = features,
                backend = GpuAdaptiveBinningBackend.Radix, primitiveCandidateId = "Portable",
                validationPassed = true, evidenceId = "independent-test-csr", calibrationSamples = 3 } };
            var selector = new GpuAdaptiveBinningStableSelector(new GpuAdaptiveBinningMatrix(document),
                GpuAdaptiveBinningMatrixTests.Device(), GpuAdaptiveBinningMatrixTests.Environment());
            var keyData = Enumerable.Repeat(7u, n).ToArray();
            var valueData = GpuAdaptiveBinningTestOracle.CreateValues(n);
            using (var binner = new GpuAdaptiveSpatialBinner(n, c, false))
            using (var keyBuffer = CreateBuffer(keyData))
            using (var valueBuffer = CreateBuffer(valueData))
            using (var counts = CreateBuffer(c))
            using (var offsets = CreateBuffer(c + 1))
            using (var output = CreateBuffer(n))
            using (var diagnostics = CreateBuffer(2))
            using (var commands = new CommandBuffer())
            {
                for (int frame = 0; frame < 8; frame++)
                {
                    var hint = features;
                    if (frame == 3) hint.workloadId = "unknown-workload";
                    var domain = frame == 7 ? GpuAdaptiveBinningKeyDomain.Untrusted : GpuAdaptiveBinningKeyDomain.GuaranteedInRange;
                    if (frame == 7) keyData[0] = uint.MaxValue;
                    keyBuffer.SetData(keyData); commands.Clear();
                    var decision = binner.RecordAdaptive(commands, keyBuffer, valueBuffer, counts, offsets, output, diagnostics,
                        n, c, domain, selector, in hint, GpuPrimitiveBackend.Portable);
                    Execute(commands);
                    var expected = GpuAdaptiveBinningTestOracle.Build(keyData, valueData, c);
                    var actualCounts = ReadBuffer(counts, c); var actualOffsets = ReadBuffer(offsets, c + 1);
                    Assert.That(actualCounts, Is.EqualTo(expected.Counts), "frame " + frame);
                    Assert.That(actualOffsets, Is.EqualTo(expected.Offsets), "frame " + frame);
                    Assert.That(GpuAdaptiveBinningTestOracle.Canonicalize(ReadBuffer(output, n), actualCounts, actualOffsets),
                        Is.EqualTo(GpuAdaptiveBinningTestOracle.Canonicalize(expected.Values, expected.Counts, expected.Offsets)));
                    Assert.That(decision.Backend, Is.EqualTo(frame == 2 || frame == 6 ? GpuAdaptiveBinningBackend.Radix : GpuAdaptiveBinningBackend.Direct));
                    if (frame == 3) Assert.That(decision.Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.UnknownCell));
                    if (frame == 7)
                    {
                        Assert.That(decision.Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.UntrustedKeys));
                        Assert.That(ReadBuffer(diagnostics, 2), Is.EqualTo(new uint[] { 1, 1 }));
                    }
                }
            }
        }

        private static void ExecuteRadixAndAssert(
            int elementCount,
            int binCount,
            GpuPrimitiveBackend backend)
        {
            uint[] keys =
                GpuAdaptiveBinningTestOracle.CreateValidKeys(
                    elementCount,
                    binCount);
            uint[] values =
                GpuAdaptiveBinningTestOracle.CreateValues(elementCount);
            GpuAdaptiveBinningTestOracle.Result expected =
                GpuAdaptiveBinningTestOracle.Build(
                    keys,
                    values,
                    binCount);

            using (var binner = new GpuRadixSpatialBinner(
                       Math.Max(1, elementCount),
                       binCount))
            using (var keysBuffer = CreateBuffer(keys))
            using (var valuesBuffer = CreateBuffer(values))
            using (var counts = CreateBuffer(binCount))
            using (var offsets = CreateBuffer(binCount + 1))
            using (var output = CreateBuffer(
                       Math.Max(1, elementCount)))
            using (var diagnostics = CreateBuffer(
                       GpuDirectSpatialBinner.DiagnosticWordCount))
            using (var commands = new CommandBuffer
                   {
                       name =
                           $"Test/AdaptiveBinning/Radix/{backend}",
                   })
            {
                binner.Record(
                    commands,
                    keysBuffer,
                    valuesBuffer,
                    counts,
                    offsets,
                    output,
                    diagnostics,
                    elementCount,
                    binCount,
                    GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
                    backend);
                Execute(commands);

                Assert.That(
                    ReadBuffer(counts, binCount),
                    Is.EqualTo(expected.Counts));
                Assert.That(
                    ReadBuffer(offsets, binCount + 1),
                    Is.EqualTo(expected.Offsets));
                Assert.That(
                    ReadBuffer(output, elementCount),
                    Is.EqualTo(expected.Values));
                Assert.That(
                    ReadBuffer(
                        diagnostics,
                        GpuDirectSpatialBinner.DiagnosticWordCount),
                    Is.EqualTo(new uint[2]));
            }
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
            var values = new uint[count];
            if (count > 0)
            {
                buffer.GetData(values);
            }
            return values;
        }

        private static void Execute(CommandBuffer commands)
        {
            Graphics.ExecuteCommandBuffer(commands);
        }
    }
}
