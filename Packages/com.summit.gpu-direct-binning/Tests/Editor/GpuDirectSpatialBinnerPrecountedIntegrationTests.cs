using System;
using System.Linq;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDirectBinning.Tests
{
    public sealed class GpuDirectSpatialBinnerPrecountedIntegrationTests
    {
        private const uint Sentinel = 0xdeadbeefu;

        [SetUp]
        public void RequireIndirectComputeDevice()
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.supportsIndirectArgumentsBuffer ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore(
                    "A graphics device with compute and indirect arguments " +
                    "is required for precounted-prefix integration tests.");
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(255)]
        [TestCase(256)]
        [TestCase(257)]
        public void PrecountedPrefixMatchesOracleAndHonorsWordOffsets(
            int elementCount)
        {
            const int binCount = 17;
            int capacity = Math.Max(1, elementCount);
            uint[] logicalKeys = CpuDirectBinningOracle.CreateValidKeys(
                elementCount,
                binCount);
            uint[] logicalValues =
                CpuDirectBinningOracle.CreatePayload(elementCount);
            CpuDirectBinningResult expected = CpuDirectBinningOracle.Build(
                logicalKeys,
                logicalValues,
                binCount);
            uint[] keysData = Pad(logicalKeys, capacity, 0u);
            uint[] valuesData = Pad(logicalValues, capacity, Sentinel);
            uint[] outputData = Enumerable.Repeat(Sentinel, capacity).ToArray();
            uint[] dispatchData = Enumerable.Repeat(Sentinel, 7).ToArray();
            uint[] diagnosticSentinel = { 7u, 32u };

            using (var binner = new GpuDirectSpatialBinner(
                       capacity,
                       binCount))
            using (var keys = CreateStructured(keysData))
            using (var values = CreateStructured(valuesData))
            using (var counts = CreateStructured(expected.Counts))
            using (var offsets = CreateStructured(binCount + 1))
            using (var output = CreateStructured(outputData))
            using (var diagnostics = CreateStructured(diagnosticSentinel))
            using (var gpuCount = CreateStructured(
                       new[]
                       {
                           Sentinel,
                           Sentinel,
                           (uint)elementCount,
                           Sentinel,
                       }))
            using (var validationDispatchArguments =
                       CreateIndirect(dispatchData))
            using (var scatterDispatchArguments =
                       CreateIndirect(dispatchData))
            using (var commands = new CommandBuffer
                   {
                       name = "Test/DirectBinning/PrecountedPrefix",
                   })
            {
                const int countWordOffset = 2;
                const int argumentWordOffset = 2;
                binner.RecordPrecountedPrefixIndirect(
                    commands,
                    keys,
                    values,
                    counts,
                    offsets,
                    output,
                    diagnostics,
                    gpuCount,
                    countWordOffset,
                    validationDispatchArguments,
                    argumentWordOffset * sizeof(uint),
                    scatterDispatchArguments,
                    argumentWordOffset * sizeof(uint),
                    binCount,
                    GpuPrimitiveBackend.Portable);

                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(
                    Read(counts, binCount),
                    Is.EqualTo(expected.Counts),
                    "The producer-owned counts must remain read-only.");
                Assert.That(
                    Read(offsets, binCount + 1),
                    Is.EqualTo(expected.Offsets));
                Assert.That(
                    Read(diagnostics, 2),
                    Is.EqualTo(diagnosticSentinel),
                    "A valid recording must not clear caller diagnostics.");

                uint[] actualValidationDispatch = Read(
                    validationDispatchArguments,
                    7);
                uint[] actualDispatch = Read(scatterDispatchArguments, 7);
                Assert.That(
                    actualValidationDispatch,
                    Is.EqualTo(actualDispatch));
                Assert.That(actualDispatch[0], Is.EqualTo(Sentinel));
                Assert.That(actualDispatch[1], Is.EqualTo(Sentinel));
                Assert.That(
                    actualDispatch[argumentWordOffset],
                    Is.EqualTo((uint)DivideRoundUp(elementCount, 256)));
                Assert.That(
                    actualDispatch[argumentWordOffset + 1],
                    Is.EqualTo(1u));
                Assert.That(
                    actualDispatch[argumentWordOffset + 2],
                    Is.EqualTo(1u));
                Assert.That(actualDispatch[5], Is.EqualTo(Sentinel));
                Assert.That(actualDispatch[6], Is.EqualTo(Sentinel));

                uint[] actualOutput = Read(output, capacity);
                if (elementCount == 0)
                {
                    Assert.That(actualOutput[0], Is.EqualTo(Sentinel));
                }
                else
                {
                    Assert.That(
                        CpuDirectBinningOracle.CanonicalizeBins(
                            actualOutput.Take(elementCount).ToArray(),
                            expected.Offsets,
                            expected.Counts),
                        Is.EqualTo(
                            CpuDirectBinningOracle.CanonicalizeBins(
                                expected.BinnedValues,
                                expected.Offsets,
                                expected.Counts)));
                }
            }
        }

        [Test]
        public void TerminalCountMismatchDisablesScatterAndFlagsProducer()
        {
            const int capacity = 4;
            const int binCount = 2;
            using (var fixture = new PrecountedFixture(
                       capacity,
                       binCount,
                       new[] { 0u, 1u, 0u, 1u },
                       new[] { 10u, 11u, 12u, 13u },
                       new[] { 1u, 2u },
                       4u))
            {
                fixture.RecordAndExecute();

                Assert.That(
                    fixture.ReadValidationDispatch()[0],
                    Is.Zero,
                    "Mismatched counts must disable key validation.");
                Assert.That(
                    fixture.ReadScatterDispatch()[0],
                    Is.Zero,
                    "Mismatched counts must disable indirect scatter.");
                Assert.That(
                    fixture.ReadDiagnostics()[1] &
                    (uint)GpuDirectBinningErrorFlags
                        .PrecountedCountMismatch,
                    Is.Not.Zero);
                Assert.That(
                    fixture.ReadOutput(),
                    Is.EqualTo(Enumerable.Repeat(Sentinel, capacity)));
            }
        }

        [Test]
        public void CountAboveCapacityDisablesScatterAndReportsBothInvariants()
        {
            const int capacity = 4;
            using (var fixture = new PrecountedFixture(
                       capacity,
                       1,
                       new[] { 0u, 0u, 0u, 0u },
                       new[] { 10u, 11u, 12u, 13u },
                       new[] { 5u },
                       5u))
            {
                fixture.RecordAndExecute();

                uint flags = fixture.ReadDiagnostics()[1];
                Assert.That(
                    fixture.ReadValidationDispatch()[0],
                    Is.Zero);
                Assert.That(
                    fixture.ReadScatterDispatch()[0],
                    Is.Zero);
                Assert.That(
                    flags & (uint)GpuDirectBinningErrorFlags
                        .PrecountedElementCountOutOfRange,
                    Is.Not.Zero);
                Assert.That(
                    flags & (uint)GpuDirectBinningErrorFlags
                        .PrecountedCountMismatch,
                    Is.Not.Zero);
                Assert.That(
                    fixture.ReadOutput(),
                    Is.EqualTo(Enumerable.Repeat(Sentinel, capacity)));
            }
        }

        [Test]
        public void PerBinMismatchIsDetectedDuringIndirectScatter()
        {
            const int capacity = 4;
            using (var fixture = new PrecountedFixture(
                       capacity,
                       2,
                       new[] { 0u, 0u, 1u, 1u },
                       new[] { 10u, 11u, 12u, 13u },
                       new[] { 1u, 3u },
                       4u))
            {
                fixture.RecordAndExecute();

                uint flags = fixture.ReadDiagnostics()[1];
                Assert.That(
                    fixture.ReadValidationDispatch()[0],
                    Is.EqualTo(1u));
                Assert.That(
                    fixture.ReadScatterDispatch()[0],
                    Is.EqualTo(1u));
                Assert.That(
                    flags & (uint)GpuDirectBinningErrorFlags
                        .PrecountedCountMismatch,
                    Is.Not.Zero);
                Assert.That(
                    flags & (uint)GpuDirectBinningErrorFlags
                        .ScatterDestinationOutOfRange,
                    Is.Not.Zero);
            }
        }

        [Test]
        public void InvalidPrefixKeyUsesExistingDiagnosticAbi()
        {
            using (var fixture = new PrecountedFixture(
                       2,
                       2,
                       new[] { 0u, 2u },
                       new[] { 10u, 11u },
                       new[] { 1u, 1u },
                       2u))
            {
                fixture.RecordAndExecute();

                uint[] diagnostics = fixture.ReadDiagnostics();
                Assert.That(diagnostics[0], Is.EqualTo(1u));
                Assert.That(
                    diagnostics[1] &
                    (uint)GpuDirectBinningErrorFlags.InvalidKeyEncountered,
                    Is.Not.Zero);
                Assert.That(
                    fixture.ReadValidationDispatch()[0],
                    Is.EqualTo(1u),
                    "Key validation must execute the dense prefix once.");
                Assert.That(
                    fixture.ReadScatterDispatch()[0],
                    Is.Zero,
                    "Invalid keys must disable scatter before output writes.");
                Assert.That(
                    fixture.ReadOutput(),
                    Is.EqualTo(Enumerable.Repeat(Sentinel, 2)));
            }
        }

        [Test]
        public void RecordRejectsInvalidCountAndArgumentOffsetsBeforeMutation()
        {
            using (var fixture = new PrecountedFixture(
                       4,
                       2,
                       new[] { 0u, 1u, 0u, 1u },
                       new[] { 10u, 11u, 12u, 13u },
                       new[] { 2u, 2u },
                       4u,
                       countStorageWords: 2,
                       dispatchStorageWords: 4))
            {
                AssertRejectedBeforeMutation(
                    fixture,
                    -1,
                    0u,
                    0u,
                    typeof(ArgumentOutOfRangeException));
                AssertRejectedBeforeMutation(
                    fixture,
                    2,
                    0u,
                    0u,
                    typeof(ArgumentOutOfRangeException));
                AssertRejectedBeforeMutation(
                    fixture,
                    0,
                    2u,
                    0u,
                    typeof(ArgumentOutOfRangeException));
                AssertRejectedBeforeMutation(
                    fixture,
                    0,
                    2u * sizeof(uint),
                    0u,
                    typeof(ArgumentOutOfRangeException));
                AssertRejectedBeforeMutation(
                    fixture,
                    0,
                    0u,
                    2u,
                    typeof(ArgumentOutOfRangeException));
                AssertRejectedBeforeMutation(
                    fixture,
                    0,
                    0u,
                    2u * sizeof(uint),
                    typeof(ArgumentOutOfRangeException));
            }
        }

        [Test]
        public void RecordRejectsIndirectArgumentsWithoutBothRequiredTargets()
        {
            using (var fixture = new PrecountedFixture(
                       4,
                       2,
                       new[] { 0u, 1u, 0u, 1u },
                       new[] { 10u, 11u, 12u, 13u },
                       new[] { 2u, 2u },
                       4u))
            using (var structuredOnly = CreateStructured(3))
            using (var indirectOnly = new GraphicsBuffer(
                       GraphicsBuffer.Target.IndirectArguments,
                       1,
                       3 * sizeof(uint)))
            {
                Assert.Throws<ArgumentException>(
                    () => fixture.Record(
                        fixture.Commands,
                        structuredOnly,
                        fixture.ScatterDispatchArguments,
                        0,
                        0u,
                        0u));
                Assert.Throws<ArgumentException>(
                    () => fixture.Record(
                        fixture.Commands,
                        fixture.ValidationDispatchArguments,
                        indirectOnly,
                        0,
                        0u,
                        0u));
            }
        }

        [Test]
        public void RecordRejectsWrongStrideUndersizedBuffersAndAliasing()
        {
            using (var fixture = new PrecountedFixture(
                       4,
                       2,
                       new[] { 0u, 1u, 0u, 1u },
                       new[] { 10u, 11u, 12u, 13u },
                       new[] { 2u, 2u },
                       4u))
            using (var wrongStride = new GraphicsBuffer(
                       GraphicsBuffer.Target.Structured,
                       4,
                       2 * sizeof(uint)))
            using (var undersized = CreateStructured(3))
            {
                Assert.Throws<ArgumentException>(
                    () => fixture.RecordWithKeys(wrongStride));
                Assert.Throws<ArgumentException>(
                    () => fixture.RecordWithOutput(undersized));
                Assert.Throws<ArgumentException>(
                    () => fixture.RecordWithOutput(fixture.Keys));
                Assert.Throws<ArgumentException>(
                    () => fixture.RecordWithDiagnostics(
                        fixture.ScatterDispatchArguments));
                Assert.Throws<ArgumentException>(
                    () => fixture.Record(
                        fixture.Commands,
                        fixture.ValidationDispatchArguments,
                        fixture.ValidationDispatchArguments,
                        0,
                        0u,
                        0u));
            }
        }

        [Test]
        public void DisposedBinnerRejectsPrecountedRecordBeforeMutation()
        {
            using (var fixture = new PrecountedFixture(
                       1,
                       1,
                       new[] { 0u },
                       new[] { 1u },
                       new[] { 1u },
                       1u))
            {
                fixture.Binner.Dispose();
                AssertRejectedBeforeMutation(
                    fixture,
                    0,
                    0u,
                    0u,
                    typeof(ObjectDisposedException));
            }
        }

        private static void AssertRejectedBeforeMutation(
            PrecountedFixture fixture,
            int countWordOffset,
            uint validationArgumentByteOffset,
            uint scatterArgumentByteOffset,
            Type exceptionType)
        {
            long sizeBefore = fixture.Commands.sizeInBytes;
            Assert.That(
                () => fixture.Record(
                    fixture.Commands,
                    fixture.ValidationDispatchArguments,
                    fixture.ScatterDispatchArguments,
                    countWordOffset,
                    validationArgumentByteOffset,
                    scatterArgumentByteOffset),
                Throws.TypeOf(exceptionType));
            Assert.That(
                fixture.Commands.sizeInBytes,
                Is.EqualTo(sizeBefore));
        }

        private static uint[] Pad(
            uint[] source,
            int length,
            uint padding)
        {
            var result = Enumerable.Repeat(padding, length).ToArray();
            Array.Copy(source, result, source.Length);
            return result;
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return value == 0 ? 0 : 1 + ((value - 1) / divisor);
        }

        private static GraphicsBuffer CreateStructured(int count)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Math.Max(1, count),
                sizeof(uint));
        }

        private static GraphicsBuffer CreateStructured(uint[] data)
        {
            GraphicsBuffer buffer = CreateStructured(data.Length);
            if (data.Length > 0)
            {
                buffer.SetData(data);
            }
            return buffer;
        }

        private static GraphicsBuffer CreateIndirect(uint[] data)
        {
            var buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured |
                GraphicsBuffer.Target.IndirectArguments,
                Math.Max(3, data.Length),
                sizeof(uint));
            buffer.SetData(data);
            return buffer;
        }

        private static uint[] Read(GraphicsBuffer buffer, int count)
        {
            var result = new uint[count];
            buffer.GetData(result);
            return result;
        }

        private sealed class PrecountedFixture : IDisposable
        {
            internal PrecountedFixture(
                int capacity,
                int binCount,
                uint[] keys,
                uint[] values,
                uint[] counts,
                uint gpuCount,
                int countStorageWords = 1,
                int dispatchStorageWords = 3)
            {
                Capacity = capacity;
                BinCount = binCount;
                Binner = new GpuDirectSpatialBinner(capacity, binCount);
                Keys = CreateStructured(Pad(keys, capacity, 0u));
                Values = CreateStructured(Pad(values, capacity, Sentinel));
                Counts = CreateStructured(counts);
                Offsets = CreateStructured(binCount + 1);
                Output = CreateStructured(
                    Enumerable.Repeat(Sentinel, capacity).ToArray());
                Diagnostics = CreateStructured(2);
                var countData = new uint[countStorageWords];
                countData[0] = gpuCount;
                GpuCount = CreateStructured(countData);
                ValidationDispatchArguments = CreateIndirect(
                    Enumerable.Repeat(
                        Sentinel,
                        dispatchStorageWords).ToArray());
                ScatterDispatchArguments = CreateIndirect(
                    Enumerable.Repeat(
                        Sentinel,
                        dispatchStorageWords).ToArray());
                Commands = new CommandBuffer
                {
                    name = "Test/DirectBinning/PrecountedFixture",
                };
            }

            internal int Capacity { get; }
            internal int BinCount { get; }
            internal GpuDirectSpatialBinner Binner { get; }
            internal GraphicsBuffer Keys { get; }
            internal GraphicsBuffer Values { get; }
            internal GraphicsBuffer Counts { get; }
            internal GraphicsBuffer Offsets { get; }
            internal GraphicsBuffer Output { get; }
            internal GraphicsBuffer Diagnostics { get; }
            internal GraphicsBuffer GpuCount { get; }
            internal GraphicsBuffer ValidationDispatchArguments { get; }
            internal GraphicsBuffer ScatterDispatchArguments { get; }
            internal CommandBuffer Commands { get; }

            internal void RecordAndExecute()
            {
                Record(
                    Commands,
                    ValidationDispatchArguments,
                    ScatterDispatchArguments,
                    0,
                    0u,
                    0u);
                Graphics.ExecuteCommandBuffer(Commands);
            }

            internal void Record(
                CommandBuffer commands,
                GraphicsBuffer validationDispatchArguments,
                GraphicsBuffer scatterDispatchArguments,
                int countWordOffset,
                uint validationArgumentByteOffset,
                uint scatterArgumentByteOffset)
            {
                Binner.RecordPrecountedPrefixIndirect(
                    commands,
                    Keys,
                    Values,
                    Counts,
                    Offsets,
                    Output,
                    Diagnostics,
                    GpuCount,
                    countWordOffset,
                    validationDispatchArguments,
                    validationArgumentByteOffset,
                    scatterDispatchArguments,
                    scatterArgumentByteOffset,
                    BinCount,
                    GpuPrimitiveBackend.Portable);
            }

            internal void RecordWithKeys(GraphicsBuffer keys)
            {
                Binner.RecordPrecountedPrefixIndirect(
                    Commands,
                    keys,
                    Values,
                    Counts,
                    Offsets,
                    Output,
                    Diagnostics,
                    GpuCount,
                    0,
                    ValidationDispatchArguments,
                    0u,
                    ScatterDispatchArguments,
                    0u,
                    BinCount,
                    GpuPrimitiveBackend.Portable);
            }

            internal void RecordWithOutput(GraphicsBuffer output)
            {
                Binner.RecordPrecountedPrefixIndirect(
                    Commands,
                    Keys,
                    Values,
                    Counts,
                    Offsets,
                    output,
                    Diagnostics,
                    GpuCount,
                    0,
                    ValidationDispatchArguments,
                    0u,
                    ScatterDispatchArguments,
                    0u,
                    BinCount,
                    GpuPrimitiveBackend.Portable);
            }

            internal void RecordWithDiagnostics(GraphicsBuffer diagnostics)
            {
                Binner.RecordPrecountedPrefixIndirect(
                    Commands,
                    Keys,
                    Values,
                    Counts,
                    Offsets,
                    Output,
                    diagnostics,
                    GpuCount,
                    0,
                    ValidationDispatchArguments,
                    0u,
                    ScatterDispatchArguments,
                    0u,
                    BinCount,
                    GpuPrimitiveBackend.Portable);
            }

            internal uint[] ReadValidationDispatch()
            {
                return Read(
                    ValidationDispatchArguments,
                    ValidationDispatchArguments.count);
            }

            internal uint[] ReadScatterDispatch()
            {
                return Read(
                    ScatterDispatchArguments,
                    ScatterDispatchArguments.count);
            }

            internal uint[] ReadDiagnostics()
            {
                return Read(Diagnostics, 2);
            }

            internal uint[] ReadOutput()
            {
                return Read(Output, Capacity);
            }

            public void Dispose()
            {
                Commands.Dispose();
                ScatterDispatchArguments.Dispose();
                ValidationDispatchArguments.Dispose();
                GpuCount.Dispose();
                Diagnostics.Dispose();
                Output.Dispose();
                Offsets.Dispose();
                Counts.Dispose();
                Values.Dispose();
                Keys.Dispose();
                Binner.Dispose();
            }
        }
    }
}
