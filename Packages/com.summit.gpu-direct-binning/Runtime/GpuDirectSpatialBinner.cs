using System;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDirectBinning
{
    /// <summary>
    /// Records a direct GPU count, exclusive-scan, and atomic-scatter pipeline
    /// that maps uint key/value pairs to compressed-sparse-row bins.
    /// </summary>
    /// <remarks>
    /// This type owns persistent write-head scratch. Record performs no buffer
    /// allocation and no readback. Atomic scatter intentionally leaves
    /// equal-key payload order unspecified.
    /// </remarks>
    public sealed class GpuDirectSpatialBinner : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int DiagnosticWordCount = 2;
        public const int InvalidKeyCountWord = 0;
        public const int ErrorFlagsWord = 1;

        private const string ResourcePath =
            "GpuDirectBinning/GpuDirectBinning";
        private const string BinningSample =
            "Summit.GpuDirectBinning/DirectSpatialBinning";
        private const string TrustedBinningSample =
            "Summit.GpuDirectBinning/DirectSpatialBinning/GuaranteedInRange";
        private const string FilteredBinningSample =
            "Summit.GpuDirectBinning/DirectSpatialBinning/DiscardKey";
        private const string ClearSample =
            "Summit.GpuDirectBinning/Clear";
        private const string CountSample =
            "Summit.GpuDirectBinning/Count";
        private const string TrustedCountSample =
            "Summit.GpuDirectBinning/Count/GuaranteedInRange";
        private const string FilteredCountSample =
            "Summit.GpuDirectBinning/Count/DiscardKey";
        private const string PrepareSample =
            "Summit.GpuDirectBinning/Prepare";
        private const string ScatterSample =
            "Summit.GpuDirectBinning/Scatter";
        private const string TrustedScatterSample =
            "Summit.GpuDirectBinning/Scatter/GuaranteedInRange";
        private const string FilteredScatterSample =
            "Summit.GpuDirectBinning/Scatter/DiscardKey";

        private static readonly int ClearCountId =
            Shader.PropertyToID("_ClearCount");
        private static readonly int ElementCountId =
            Shader.PropertyToID("_ElementCount");
        private static readonly int BinCountId =
            Shader.PropertyToID("_BinCount");
        private static readonly int DiscardKeyId =
            Shader.PropertyToID("_DiscardKey");
        private static readonly int ClearBufferId =
            Shader.PropertyToID("_ClearBuffer");
        private static readonly int KeysId =
            Shader.PropertyToID("_Keys");
        private static readonly int ValuesId =
            Shader.PropertyToID("_Values");
        private static readonly int BinCountsId =
            Shader.PropertyToID("_BinCounts");
        private static readonly int BinOffsetsId =
            Shader.PropertyToID("_BinOffsets");
        private static readonly int BinnedValuesId =
            Shader.PropertyToID("_BinnedValues");
        private static readonly int WriteHeadsId =
            Shader.PropertyToID("_WriteHeads");
        private static readonly int DiagnosticsId =
            Shader.PropertyToID("_Diagnostics");

        private readonly ComputeShader shader;
        private readonly int clearUintKernel;
        private readonly int countBinsKernel;
        private readonly int countBinsGuaranteedInRangeKernel;
        private readonly int countBinsWithDiscardKeyKernel;
        private readonly int prepareOffsetsAndWriteHeadsKernel;
        private readonly int scatterValuesKernel;
        private readonly int scatterValuesGuaranteedInRangeKernel;
        private readonly int scatterValuesWithDiscardKeyKernel;
        private readonly GpuPrimitivesRuntime primitives;
        private readonly bool ownsPrimitives;
        private readonly GraphicsBuffer writeHeads;
        private readonly bool emitProfilerMarkers;
        private bool disposed;

        /// <summary>
        /// Creates a fixed-capacity direct-binning recorder.
        /// </summary>
        public GpuDirectSpatialBinner(
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
            int selectedCountKernel =
                selectedShader.FindKernel("CountBins");
            int selectedTrustedCountKernel = selectedShader.HasKernel(
                "CountBinsGuaranteedInRange")
                    ? selectedShader.FindKernel("CountBinsGuaranteedInRange")
                    : -1;
            int selectedFilteredCountKernel = selectedShader.HasKernel(
                "CountBinsWithDiscardKey")
                    ? selectedShader.FindKernel("CountBinsWithDiscardKey")
                    : -1;
            int selectedPrepareKernel =
                selectedShader.FindKernel("PrepareOffsetsAndWriteHeads");
            int selectedScatterKernel =
                selectedShader.FindKernel("ScatterValues");
            int selectedTrustedScatterKernel = selectedShader.HasKernel(
                "ScatterValuesGuaranteedInRange")
                    ? selectedShader.FindKernel("ScatterValuesGuaranteedInRange")
                    : -1;
            int selectedFilteredScatterKernel = selectedShader.HasKernel(
                "ScatterValuesWithDiscardKey")
                    ? selectedShader.FindKernel("ScatterValuesWithDiscardKey")
                    : -1;

            bool shouldOwnPrimitives = primitives == null;
            GpuPrimitivesRuntime selectedPrimitives = primitives;
            GraphicsBuffer selectedWriteHeads = null;
            try
            {
                if (selectedPrimitives == null)
                {
                    selectedPrimitives = new GpuPrimitivesRuntime(
                        binCapacity,
                        emitProfilerMarkers: emitProfilerMarkers);
                }
                else if (selectedPrimitives.Capacity < binCapacity)
                {
                    throw new ArgumentException(
                        "Injected primitives capacity must cover " +
                        "binCapacity.",
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

                selectedPrimitives.EnsureCapacity(binCapacity);
                selectedWriteHeads = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    binCapacity,
                    sizeof(uint));
                selectedWriteHeads.name =
                    "GPU Direct Binning Write Heads";
            }
            catch
            {
                selectedWriteHeads?.Dispose();
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
            countBinsKernel = selectedCountKernel;
            countBinsGuaranteedInRangeKernel = selectedTrustedCountKernel;
            countBinsWithDiscardKeyKernel = selectedFilteredCountKernel;
            prepareOffsetsAndWriteHeadsKernel = selectedPrepareKernel;
            scatterValuesKernel = selectedScatterKernel;
            scatterValuesGuaranteedInRangeKernel = selectedTrustedScatterKernel;
            scatterValuesWithDiscardKeyKernel =
                selectedFilteredScatterKernel;
            this.primitives = selectedPrimitives;
            ownsPrimitives = shouldOwnPrimitives;
            writeHeads = selectedWriteHeads;
            this.emitProfilerMarkers = emitProfilerMarkers;
        }

        public int ElementCapacity { get; }

        public int BinCapacity { get; }

        public bool OwnsPrimitives => ownsPrimitives;

        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        public bool PrimitiveEmitsProfilerMarkers =>
            primitives.EmitsProfilerMarkers;

        /// <summary>
        /// Logical payload bytes of the write-head buffer owned by this type.
        /// </summary>
        public long InternalScratchBytes =>
            checked((long)BinCapacity * sizeof(uint));

        /// <summary>
        /// Logical payload bytes of the referenced primitives scratch.
        /// </summary>
        public long PrimitiveScratchBytes => primitives.ScratchBytes;

        /// <summary>
        /// Logical scratch used by the complete recording path.
        /// </summary>
        public long ScratchBytes =>
            checked(InternalScratchBytes + PrimitiveScratchBytes);

        /// <summary>
        /// Logical scratch owned by this object. Injected primitive scratch is
        /// excluded.
        /// </summary>
        public long OwnedScratchBytes =>
            checked(
                InternalScratchBytes +
                (ownsPrimitives ? PrimitiveScratchBytes : 0L));

        public long ResidentBytes => ScratchBytes;

        /// <summary>
        /// Records uint key/value to CSR direct binning.
        /// </summary>
        /// <remarks>
        /// Invalid keys are excluded. Ordering within each bin is unspecified.
        /// Keys and values may alias because both are read-only; all writable
        /// buffers must be distinct from each other and both inputs.
        /// </remarks>
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
            GpuPrimitiveBackend scanBackend =
                GpuPrimitiveBackend.Auto)
        {
            RecordInternal(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                scanBackend,
                false,
                true,
                false,
                0u);
        }

        /// <summary>
        /// Records the same CSR contract without per-element key validation.
        /// </summary>
        /// <remarks>
        /// Every key must be strictly less than <paramref name="binCount"/>.
        /// Violating this producer contract permits out-of-bounds GPU access.
        /// Existing <see cref="Record"/> remains the safe untrusted-key path.
        /// </remarks>
        public void RecordGuaranteedInRange(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuPrimitiveBackend scanBackend =
                GpuPrimitiveBackend.Auto)
        {
            RecordInternal(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                scanBackend,
                true,
                true,
                false,
                0u);
        }

        /// <summary>
        /// Records the trusted-key CSR contract without clearing diagnostics.
        /// </summary>
        /// <remarks>
        /// This path is intended for a measured producer that has a separate
        /// validation pass and therefore does not consume this binner's
        /// diagnostics. Every key must be strictly less than
        /// <paramref name="binCount"/>. Existing public recording methods
        /// retain their diagnostic-clear behavior.
        /// </remarks>
        public void RecordGuaranteedInRangeWithoutDiagnosticClear(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuPrimitiveBackend scanBackend =
                GpuPrimitiveBackend.Auto)
        {
            RecordInternal(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                scanBackend,
                true,
                false,
                false,
                0u);
        }

        /// <summary>
        /// Records safe CSR binning while excluding one explicit key.
        /// </summary>
        /// <remarks>
        /// Elements whose key equals <paramref name="discardKey"/> do not
        /// contribute to counts, offsets, diagnostics, or output writes.
        /// Other out-of-range keys retain the normal invalid-key diagnostic.
        /// The discard key must be outside the active bin range.
        /// </remarks>
        public void RecordWithDiscardKey(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            uint discardKey,
            GpuPrimitiveBackend scanBackend =
                GpuPrimitiveBackend.Auto)
        {
            RecordInternal(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                scanBackend,
                false,
                true,
                true,
                discardKey);
        }

        /// <summary>
        /// Records discard-key binning without clearing caller diagnostics.
        /// </summary>
        public void RecordWithDiscardKeyWithoutDiagnosticClear(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            uint discardKey,
            GpuPrimitiveBackend scanBackend =
                GpuPrimitiveBackend.Auto)
        {
            RecordInternal(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                scanBackend,
                false,
                false,
                true,
                discardKey);
        }

        private void RecordInternal(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuPrimitiveBackend scanBackend,
            bool guaranteedInRange,
            bool clearDiagnostics,
            bool hasDiscardKey,
            uint discardKey)
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
                scanBackend);

            if (guaranteedInRange && hasDiscardKey)
            {
                throw new InvalidOperationException(
                    "Guaranteed-in-range and discard-key modes are mutually " +
                    "exclusive.");
            }
            if (hasDiscardKey && discardKey < (uint)binCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(discardKey),
                    "Discard key must be outside the active bin range.");
            }

            if (guaranteedInRange &&
                (countBinsGuaranteedInRangeKernel < 0 ||
                 scatterValuesGuaranteedInRangeKernel < 0))
            {
                throw new InvalidOperationException(
                    "The injected compute shader does not provide the " +
                    "GuaranteedInRange Direct kernels.");
            }
            if (hasDiscardKey &&
                (countBinsWithDiscardKeyKernel < 0 ||
                 scatterValuesWithDiscardKeyKernel < 0))
            {
                throw new InvalidOperationException(
                    "The injected compute shader does not provide the " +
                    "discard-key Direct kernels.");
            }

            int selectedCountKernel = hasDiscardKey
                ? countBinsWithDiscardKeyKernel
                : guaranteedInRange
                    ? countBinsGuaranteedInRangeKernel
                    : countBinsKernel;
            int selectedScatterKernel = hasDiscardKey
                ? scatterValuesWithDiscardKeyKernel
                : guaranteedInRange
                    ? scatterValuesGuaranteedInRangeKernel
                    : scatterValuesKernel;
            string selectedBinningSample = hasDiscardKey
                ? FilteredBinningSample
                : guaranteedInRange
                    ? TrustedBinningSample
                    : BinningSample;
            string selectedCountSample = hasDiscardKey
                ? FilteredCountSample
                : guaranteedInRange
                    ? TrustedCountSample
                    : CountSample;
            string selectedScatterSample = hasDiscardKey
                ? FilteredScatterSample
                : guaranteedInRange
                    ? TrustedScatterSample
                    : ScatterSample;

            BeginSample(commands, selectedBinningSample);

            BeginSample(commands, ClearSample);
            RecordClear(commands, binCounts, binCount);
            if (clearDiagnostics)
            {
                RecordClear(
                    commands,
                    diagnostics,
                    DiagnosticWordCount);
            }
            EndSample(commands, ClearSample);

            BeginSample(commands, selectedCountSample);
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
                if (hasDiscardKey)
                {
                    commands.SetComputeIntParam(
                        shader,
                        DiscardKeyId,
                        unchecked((int)discardKey));
                }
                commands.SetComputeBufferParam(
                    shader,
                    selectedCountKernel,
                    KeysId,
                    keys);
                commands.SetComputeBufferParam(
                    shader,
                    selectedCountKernel,
                    BinCountsId,
                    binCounts);
                if (!guaranteedInRange)
                {
                    commands.SetComputeBufferParam(
                        shader,
                        selectedCountKernel,
                        DiagnosticsId,
                        diagnostics);
                }
                commands.DispatchCompute(
                    shader,
                    selectedCountKernel,
                    DivideRoundUp(elementCount, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, selectedCountSample);

            primitives.RecordExclusiveScan(
                commands,
                binCounts,
                binOffsets,
                binCount,
                scanBackend);

            BeginSample(commands, PrepareSample);
            commands.SetComputeIntParam(shader, BinCountId, binCount);
            commands.SetComputeBufferParam(
                shader,
                prepareOffsetsAndWriteHeadsKernel,
                BinCountsId,
                binCounts);
            commands.SetComputeBufferParam(
                shader,
                prepareOffsetsAndWriteHeadsKernel,
                BinOffsetsId,
                binOffsets);
            commands.SetComputeBufferParam(
                shader,
                prepareOffsetsAndWriteHeadsKernel,
                WriteHeadsId,
                writeHeads);
            commands.DispatchCompute(
                shader,
                prepareOffsetsAndWriteHeadsKernel,
                DivideRoundUp(binCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, PrepareSample);

            BeginSample(commands, selectedScatterSample);
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
                if (hasDiscardKey)
                {
                    commands.SetComputeIntParam(
                        shader,
                        DiscardKeyId,
                        unchecked((int)discardKey));
                }
                commands.SetComputeBufferParam(
                    shader,
                    selectedScatterKernel,
                    KeysId,
                    keys);
                commands.SetComputeBufferParam(
                    shader,
                    selectedScatterKernel,
                    ValuesId,
                    values);
                commands.SetComputeBufferParam(
                    shader,
                    selectedScatterKernel,
                    BinnedValuesId,
                    binnedValues);
                commands.SetComputeBufferParam(
                    shader,
                    selectedScatterKernel,
                    WriteHeadsId,
                    writeHeads);
                if (!guaranteedInRange)
                {
                    commands.SetComputeBufferParam(
                        shader,
                        selectedScatterKernel,
                        DiagnosticsId,
                        diagnostics);
                }
                commands.DispatchCompute(
                    shader,
                    selectedScatterKernel,
                    DivideRoundUp(elementCount, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, selectedScatterSample);

            EndSample(commands, selectedBinningSample);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            writeHeads?.Dispose();
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
            GpuPrimitiveBackend scanBackend)
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
            if (scanBackend != GpuPrimitiveBackend.Auto &&
                scanBackend != GpuPrimitiveBackend.Portable &&
                scanBackend != GpuPrimitiveBackend.WaveOps)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(scanBackend));
            }
            if (primitives.Capacity < binCount)
            {
                throw new InvalidOperationException(
                    "The referenced primitives instance is disposed or no " +
                    "longer covers the requested bin count.");
            }
            primitives.EnsureCapacity(binCount);

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
                DiagnosticWordCount,
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

        private static int DivideRoundUp(
            int value,
            int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuDirectSpatialBinner));
            }
        }
    }
}
