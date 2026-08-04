using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuAdaptiveBinning
{
    /// <summary>
    /// Records stable low-bit radix binning followed by sorted-run CSR range
    /// extraction.
    /// </summary>
    /// <remarks>
    /// The fast path assumes every key is less than binCount. This contract
    /// eliminates validation and sentinel passes from the timed path. Record
    /// performs no allocation and no readback.
    /// </remarks>
    public sealed class GpuRadixSpatialBinner : IDisposable
    {
        public const int ThreadGroupSize = 256;

        private const string ResourcePath =
            "GpuAdaptiveBinning/GpuRadixBinning";
        private const string BinningSample =
            "Summit.GpuAdaptiveBinning/RadixSpatialBinning";
        private const string ClearSample =
            "Summit.GpuAdaptiveBinning/Radix/Clear";
        private const string SortSample =
            "Summit.GpuAdaptiveBinning/Radix/Sort";
        private const string RangesSample =
            "Summit.GpuAdaptiveBinning/Radix/Ranges";
        private const string ScanSample =
            "Summit.GpuAdaptiveBinning/Radix/Scan";
        private const string TerminalSample =
            "Summit.GpuAdaptiveBinning/Radix/Terminal";

        private static readonly int ClearCountId =
            Shader.PropertyToID("_ClearCount");
        private static readonly int ElementCountId =
            Shader.PropertyToID("_ElementCount");
        private static readonly int BinCountId =
            Shader.PropertyToID("_BinCount");
        private static readonly int ClearBufferId =
            Shader.PropertyToID("_ClearBuffer");
        private static readonly int SortedKeysId =
            Shader.PropertyToID("_SortedKeys");
        private static readonly int BinCountsId =
            Shader.PropertyToID("_BinCounts");
        private static readonly int BinOffsetsId =
            Shader.PropertyToID("_BinOffsets");

        private readonly ComputeShader shader;
        private readonly int clearUintKernel;
        private readonly int markSortedRangesKernel;
        private readonly int finalizeCountsKernel;
        private readonly int writeTerminalOffsetKernel;
        private readonly GpuPrimitivesRuntime primitives;
        private readonly bool ownsPrimitives;
        private readonly GraphicsBuffer sortedKeys;
        private readonly bool emitProfilerMarkers;
        private bool disposed;

        public GpuRadixSpatialBinner(
            int elementCapacity,
            int binCapacity,
            GpuPrimitivesRuntime primitives = null,
            ComputeShader shader = null,
            bool emitProfilerMarkers = true)
        {
            ValidateCapacity(elementCapacity, nameof(elementCapacity));
            ValidateCapacity(binCapacity, nameof(binCapacity));

            ComputeShader selectedShader = shader != null
                ? shader
                : Resources.Load<ComputeShader>(ResourcePath);
            if (selectedShader == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{ResourcePath}' could not " +
                    "be loaded.");
            }

            int selectedClearKernel =
                selectedShader.FindKernel("ClearUint");
            int selectedMarkKernel =
                selectedShader.FindKernel("MarkSortedRanges");
            int selectedFinalizeKernel =
                selectedShader.FindKernel("FinalizeCounts");
            int selectedTerminalKernel =
                selectedShader.FindKernel("WriteTerminalOffset");

            bool shouldOwnPrimitives = primitives == null;
            GpuPrimitivesRuntime selectedPrimitives = primitives;
            GraphicsBuffer selectedSortedKeys = null;
            int primitiveCapacity = Math.Max(
                elementCapacity,
                binCapacity);
            try
            {
                if (selectedPrimitives == null)
                {
                    selectedPrimitives =
                        new GpuPrimitivesRuntime(
                            primitiveCapacity,
                            emitProfilerMarkers: emitProfilerMarkers);
                }
                else if (selectedPrimitives.Capacity < primitiveCapacity)
                {
                    throw new ArgumentException(
                        "Injected primitives capacity must cover " +
                        "max(elementCapacity, binCapacity).",
                        nameof(primitives));
                }
                else if (selectedPrimitives.EmitsProfilerMarkers !=
                    emitProfilerMarkers)
                {
                    throw new ArgumentException(
                        "Injected primitives profiler-marker setting must " +
                        "match emitProfilerMarkers.",
                        nameof(primitives));
                }

                selectedPrimitives.EnsureCapacity(primitiveCapacity);
                selectedSortedKeys = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    elementCapacity,
                    sizeof(uint));
                selectedSortedKeys.name =
                    "GPU Adaptive Binning Radix Sorted Keys";
            }
            catch
            {
                selectedSortedKeys?.Dispose();
                if (shouldOwnPrimitives)
                {
                    selectedPrimitives?.Dispose();
                }
                throw;
            }

            ElementCapacity = elementCapacity;
            BinCapacity = binCapacity;
            this.shader = selectedShader;
            clearUintKernel = selectedClearKernel;
            markSortedRangesKernel = selectedMarkKernel;
            finalizeCountsKernel = selectedFinalizeKernel;
            writeTerminalOffsetKernel = selectedTerminalKernel;
            this.primitives = selectedPrimitives;
            ownsPrimitives = shouldOwnPrimitives;
            sortedKeys = selectedSortedKeys;
            this.emitProfilerMarkers = emitProfilerMarkers;
        }

        public int ElementCapacity { get; }

        public int BinCapacity { get; }

        public bool OwnsPrimitives => ownsPrimitives;

        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        public bool PrimitiveEmitsProfilerMarkers =>
            primitives.EmitsProfilerMarkers;

        public long InternalScratchBytes =>
            checked((long)ElementCapacity * sizeof(uint));

        public long PrimitiveScratchBytes => primitives.ScratchBytes;

        public long ScratchBytes =>
            checked(InternalScratchBytes + PrimitiveScratchBytes);

        public long OwnedScratchBytes =>
            checked(
                InternalScratchBytes +
                (ownsPrimitives ? PrimitiveScratchBytes : 0L));

        public long ResidentBytes => ScratchBytes;

        /// <summary>
        /// Returns the minimum number of low key bits required to represent
        /// keys in [0, binCount).
        /// </summary>
        public static int GetRequiredKeyBitCount(int binCount)
        {
            if (binCount < 1 ||
                binCount > GpuPrimitivesRuntime.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(nameof(binCount));
            }

            uint highestKey = (uint)(binCount - 1);
            int bitCount = 1;
            while ((highestKey >>= 1) != 0u)
            {
                bitCount++;
            }
            return bitCount;
        }

        public void Record(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuAdaptiveBinningKeyDomain keyDomain,
            GpuPrimitiveBackend primitiveBackend =
                GpuPrimitiveBackend.Auto)
        {
            ValidateRecordArguments(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                keyDomain,
                primitiveBackend);

            int keyBitCount = GetRequiredKeyBitCount(binCount);
            BeginSample(commands, BinningSample);

            BeginSample(commands, ClearSample);
            RecordClear(commands, binCounts, binCount);
            RecordClear(
                commands,
                diagnostics,
                GpuDirectSpatialBinner.DiagnosticWordCount);
            EndSample(commands, ClearSample);

            BeginSample(commands, SortSample);
            primitives.RecordRadixSortKeyBits(
                commands,
                keys,
                values,
                sortedKeys,
                binnedValues,
                elementCount,
                keyBitCount,
                primitiveBackend);
            EndSample(commands, SortSample);

            BeginSample(commands, RangesSample);
            if (elementCount > 0)
            {
                commands.SetComputeIntParam(
                    shader,
                    ElementCountId,
                    elementCount);
                commands.SetComputeIntParam(
                    shader,
                    BinCountId,
                    binCount);
                commands.SetComputeBufferParam(
                    shader,
                    markSortedRangesKernel,
                    SortedKeysId,
                    sortedKeys);
                commands.SetComputeBufferParam(
                    shader,
                    markSortedRangesKernel,
                    BinCountsId,
                    binCounts);
                commands.SetComputeBufferParam(
                    shader,
                    markSortedRangesKernel,
                    BinOffsetsId,
                    binOffsets);
                commands.DispatchCompute(
                    shader,
                    markSortedRangesKernel,
                    DivideRoundUp(elementCount, ThreadGroupSize),
                    1,
                    1);
            }

            commands.SetComputeIntParam(shader, BinCountId, binCount);
            commands.SetComputeBufferParam(
                shader,
                finalizeCountsKernel,
                BinCountsId,
                binCounts);
            commands.SetComputeBufferParam(
                shader,
                finalizeCountsKernel,
                BinOffsetsId,
                binOffsets);
            commands.DispatchCompute(
                shader,
                finalizeCountsKernel,
                DivideRoundUp(binCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, RangesSample);

            BeginSample(commands, ScanSample);
            primitives.RecordExclusiveScan(
                commands,
                binCounts,
                binOffsets,
                binCount,
                primitiveBackend);
            EndSample(commands, ScanSample);

            BeginSample(commands, TerminalSample);
            commands.SetComputeIntParam(shader, BinCountId, binCount);
            commands.SetComputeBufferParam(
                shader,
                writeTerminalOffsetKernel,
                BinCountsId,
                binCounts);
            commands.SetComputeBufferParam(
                shader,
                writeTerminalOffsetKernel,
                BinOffsetsId,
                binOffsets);
            commands.DispatchCompute(
                shader,
                writeTerminalOffsetKernel,
                1,
                1,
                1);
            EndSample(commands, TerminalSample);

            EndSample(commands, BinningSample);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sortedKeys?.Dispose();
            if (ownsPrimitives)
            {
                primitives.Dispose();
            }
        }

        private void ValidateRecordArguments(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuAdaptiveBinningKeyDomain keyDomain,
            GpuPrimitiveBackend primitiveBackend)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (elementCount < 0 ||
                elementCount > ElementCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elementCount),
                    $"Element count must be in [0, {ElementCapacity}].");
            }
            if (binCount < 1 || binCount > BinCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binCount),
                    $"Bin count must be in [1, {BinCapacity}].");
            }
            if (keyDomain !=
                GpuAdaptiveBinningKeyDomain.GuaranteedInRange)
            {
                throw new InvalidOperationException(
                    "The radix fast path requires " +
                    "GpuAdaptiveBinningKeyDomain.GuaranteedInRange.");
            }
            if (primitiveBackend != GpuPrimitiveBackend.Auto &&
                primitiveBackend != GpuPrimitiveBackend.Portable &&
                primitiveBackend != GpuPrimitiveBackend.WaveOps)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(primitiveBackend));
            }
            if (primitives.Capacity <
                Math.Max(ElementCapacity, BinCapacity))
            {
                throw new InvalidOperationException(
                    "The referenced primitives instance is disposed or no " +
                    "longer covers max(elementCapacity, binCapacity).");
            }

            ValidateUintBuffer(keys, elementCount, nameof(keys));
            ValidateUintBuffer(values, elementCount, nameof(values));
            ValidateUintBuffer(binCounts, binCount, nameof(binCounts));
            ValidateUintBuffer(
                binOffsets,
                checked(binCount + 1),
                nameof(binOffsets));
            ValidateUintBuffer(
                binnedValues,
                elementCount,
                nameof(binnedValues));
            ValidateUintBuffer(
                diagnostics,
                GpuDirectSpatialBinner.DiagnosticWordCount,
                nameof(diagnostics));

            RequireDistinct(
                binCounts,
                nameof(binCounts),
                keys,
                nameof(keys));
            RequireDistinct(
                binCounts,
                nameof(binCounts),
                values,
                nameof(values));
            RequireDistinct(
                binOffsets,
                nameof(binOffsets),
                keys,
                nameof(keys));
            RequireDistinct(
                binOffsets,
                nameof(binOffsets),
                values,
                nameof(values));
            RequireDistinct(
                binnedValues,
                nameof(binnedValues),
                keys,
                nameof(keys));
            RequireDistinct(
                binnedValues,
                nameof(binnedValues),
                values,
                nameof(values));
            RequireDistinct(
                diagnostics,
                nameof(diagnostics),
                keys,
                nameof(keys));
            RequireDistinct(
                diagnostics,
                nameof(diagnostics),
                values,
                nameof(values));
            RequireDistinct(
                binCounts,
                nameof(binCounts),
                binOffsets,
                nameof(binOffsets));
            RequireDistinct(
                binCounts,
                nameof(binCounts),
                binnedValues,
                nameof(binnedValues));
            RequireDistinct(
                binCounts,
                nameof(binCounts),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                binOffsets,
                nameof(binOffsets),
                binnedValues,
                nameof(binnedValues));
            RequireDistinct(
                binOffsets,
                nameof(binOffsets),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                binnedValues,
                nameof(binnedValues),
                diagnostics,
                nameof(diagnostics));
        }

        private void RecordClear(
            CommandBuffer commands,
            GraphicsBuffer buffer,
            int count)
        {
            commands.SetComputeIntParam(
                shader,
                ClearCountId,
                count);
            commands.SetComputeBufferParam(
                shader,
                clearUintKernel,
                ClearBufferId,
                buffer);
            commands.DispatchCompute(
                shader,
                clearUintKernel,
                DivideRoundUp(count, ThreadGroupSize),
                1,
                1);
        }

        private void BeginSample(
            CommandBuffer commands,
            string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.BeginSample(sampleName);
            }
        }

        private void EndSample(
            CommandBuffer commands,
            string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.EndSample(sampleName);
            }
        }

        private static void ValidateCapacity(
            int capacity,
            string parameterName)
        {
            if (capacity < 1 ||
                capacity > GpuPrimitivesRuntime.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Capacity must be in [1, " +
                    $"{GpuPrimitivesRuntime.MaxElementCount}].");
            }
        }

        private static void ValidateUintBuffer(
            GraphicsBuffer buffer,
            int requiredCount,
            string parameterName)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != sizeof(uint) ||
                buffer.count < requiredCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a structured uint " +
                    $"GraphicsBuffer with at least {requiredCount} " +
                    "elements.",
                    parameterName);
            }
        }

        private static void RequireDistinct(
            GraphicsBuffer first,
            string firstName,
            GraphicsBuffer second,
            string secondName)
        {
            if (ReferenceEquals(first, second))
            {
                throw new ArgumentException(
                    $"{firstName} must not alias {secondName}.",
                    firstName);
            }
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuRadixSpatialBinner));
            }
        }
    }
}
