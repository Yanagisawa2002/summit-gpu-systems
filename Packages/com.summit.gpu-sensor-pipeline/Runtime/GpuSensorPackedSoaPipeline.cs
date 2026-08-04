using System;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// GPU-resident sensor path with pair-packed 16-bit SoA attributes and a
    /// producer-fused Direct CSR count stage.
    /// </summary>
    /// <remarks>
    /// X, Y, Z, and quantized intensity are four independent uint streams.
    /// Each uint contains two 16-bit elements. Spatial keys and identity
    /// values are derived at use sites and are never materialized. Scatter
    /// atomically advances exclusive bin starts in place, leaving cumulative
    /// bin ends for consumers without a separate write-head buffer.
    /// </remarks>
    public sealed class GpuSensorPackedSoaPipeline : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int FixedBinCount =
            GpuSensorDeterministicGenerator.BinCount;
        public const int FrameDigestCount =
            GpuSensorDeterministicGenerator.StateCount;
        public const int DigestStride = 16;
        public const int UintStride = sizeof(uint);
        public const int PackedStreamCount = 4;
        public const int ValuesPerPackedWord = 2;
        public const int ValidationWordCount = 8;
        public const int PackedSampleMismatchCountWord = 0;
        public const int PackedSampleMismatchHashWord = 1;
        public const int CsrMismatchCountWord = 2;
        public const int CsrMismatchHashWord = 3;
        public const int CsrElementCountWord = 4;
        public const int CsrIdXorWord = 5;
        public const int CsrIdSumWord = 6;
        public const int CsrIdMixedSumWord = 7;
        public const int LogicalPositionReadBytesPerCandidate =
            3 * UintStride;
        public const int LogicalIntensityReadBytesPerAcceptedCandidate =
            UintStride;

        private const string ResourcePath =
            "GpuSensorPipeline/GpuSensorPackedSoaPipeline";
        private const string PathSample =
            "Summit.GpuSensorPipeline/PackedSoA";
        private const string ClearSample =
            "Summit.GpuSensorPipeline/PackedSoA/Clear";
        private const string ProducerCountSample =
            "Summit.GpuSensorPipeline/PackedSoA/ProduceAndCount";
        private const string ScatterSample =
            "Summit.GpuSensorPipeline/PackedSoA/Scatter";
        private const string QuerySample =
            "Summit.GpuSensorPipeline/PackedSoA/RangeQuery";
        private const string FrameDigestSample =
            "Summit.GpuSensorPipeline/PackedSoA/FrameDigest";
        private const string ValidationSample =
            "Summit.GpuSensorPipeline/PackedSoA/Validate";

        private static readonly int ClearCountId =
            Shader.PropertyToID("_ClearCount");
        private static readonly int ElementCountId =
            Shader.PropertyToID("_ElementCount");
        private static readonly int BinCountId =
            Shader.PropertyToID("_BinCount");
        private static readonly int QueryCountId =
            Shader.PropertyToID("_QueryCount");
        private static readonly int SeedId =
            Shader.PropertyToID("_Seed");
        private static readonly int LogicalStateId =
            Shader.PropertyToID("_LogicalState");
        private static readonly int ClearBufferId =
            Shader.PropertyToID("_ClearBuffer");
        private static readonly int PackedXId =
            Shader.PropertyToID("_PackedX");
        private static readonly int PackedYId =
            Shader.PropertyToID("_PackedY");
        private static readonly int PackedZId =
            Shader.PropertyToID("_PackedZ");
        private static readonly int PackedIntensityId =
            Shader.PropertyToID("_PackedIntensity");
        private static readonly int PackedXRwId =
            Shader.PropertyToID("_PackedXRW");
        private static readonly int PackedYRwId =
            Shader.PropertyToID("_PackedYRW");
        private static readonly int PackedZRwId =
            Shader.PropertyToID("_PackedZRW");
        private static readonly int PackedIntensityRwId =
            Shader.PropertyToID("_PackedIntensityRW");
        private static readonly int BinCountsId =
            Shader.PropertyToID("_BinCounts");
        private static readonly int BinOffsetsId =
            Shader.PropertyToID("_BinOffsets");
        private static readonly int BinOffsetsRwId =
            Shader.PropertyToID("_BinOffsetsRW");
        private static readonly int BinnedIdsId =
            Shader.PropertyToID("_BinnedIds");
        private static readonly int BinnedIdsRwId =
            Shader.PropertyToID("_BinnedIdsRW");
        private static readonly int QueriesId =
            Shader.PropertyToID("_Queries");
        private static readonly int QueryDigestsId =
            Shader.PropertyToID("_QueryDigests");
        private static readonly int FrameDigestId =
            Shader.PropertyToID("_FrameDigest");
        private static readonly int ValidationDiagnosticsId =
            Shader.PropertyToID("_ValidationDiagnostics");

        private readonly ComputeShader shader;
        private readonly int clearUintKernel;
        private readonly int produceAndCountKernel;
        private readonly int scatterKernel;
        private readonly int rangeQueryKernel;
        private readonly int reduceFrameDigestKernel;
        private readonly int validatePackedSamplesKernel;
        private readonly int validateCsrBinsKernel;
        private readonly GpuPrimitivesRuntime primitives;
        private readonly bool emitProfilerMarkers;
        private int configuredQueryCount;
        private bool queriesInitialized;
        private bool disposed;

        public GpuSensorPackedSoaPipeline(
            int elementCapacity,
            int queryCapacity,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.WaveOps,
            bool emitProfilerMarkers = true,
            ComputeShader shader = null)
        {
            if (elementCapacity < 1 ||
                elementCapacity > GpuPrimitivesRuntime.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(nameof(elementCapacity));
            }
            if (queryCapacity < 1 || queryCapacity > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(queryCapacity));
            }
            ValidateBackend(backend);
            if (backend == GpuPrimitiveBackend.WaveOps &&
                !GpuPrimitivesRuntime.SupportsWaveOperations)
            {
                throw new InvalidOperationException(
                    "The explicit WaveOps backend is not supported by the " +
                    "active graphics device. Select Portable explicitly.");
            }

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
            int selectedProduceKernel =
                selectedShader.FindKernel("ProducePackedSoaAndCount");
            int selectedScatterKernel =
                selectedShader.FindKernel("ScatterPackedIds");
            int selectedRangeQueryKernel =
                selectedShader.FindKernel("RangeQueryPackedSoaDigest");
            int selectedReduceKernel =
                selectedShader.FindKernel("ReduceFrameDigest");
            int selectedValidatePackedKernel =
                selectedShader.FindKernel("ValidatePackedSamples");
            int selectedValidateCsrKernel =
                selectedShader.FindKernel("ValidateCsrBins");

            int packedWordCapacity = PackedWordCount(elementCapacity);
            GpuPrimitivesRuntime selectedPrimitives = null;
            GraphicsBuffer selectedPackedX = null;
            GraphicsBuffer selectedPackedY = null;
            GraphicsBuffer selectedPackedZ = null;
            GraphicsBuffer selectedPackedIntensity = null;
            GraphicsBuffer selectedBinCounts = null;
            GraphicsBuffer selectedBinOffsets = null;
            GraphicsBuffer selectedBinnedIds = null;
            GraphicsBuffer selectedQueries = null;
            GraphicsBuffer selectedQueryDigests = null;
            GraphicsBuffer selectedFrameDigest = null;
            GraphicsBuffer selectedValidation = null;

            try
            {
                selectedPackedX = CreateBuffer(
                    packedWordCapacity,
                    UintStride,
                    "GPU Sensor Packed SoA X");
                selectedPackedY = CreateBuffer(
                    packedWordCapacity,
                    UintStride,
                    "GPU Sensor Packed SoA Y");
                selectedPackedZ = CreateBuffer(
                    packedWordCapacity,
                    UintStride,
                    "GPU Sensor Packed SoA Z");
                selectedPackedIntensity = CreateBuffer(
                    packedWordCapacity,
                    UintStride,
                    "GPU Sensor Packed SoA Intensity");
                selectedBinCounts = CreateBuffer(
                    FixedBinCount,
                    UintStride,
                    "GPU Sensor Packed SoA Bin Counts");
                selectedBinOffsets = CreateBuffer(
                    FixedBinCount + 1,
                    UintStride,
                    "GPU Sensor Packed SoA Bin Offsets");
                selectedBinnedIds = CreateBuffer(
                    elementCapacity,
                    UintStride,
                    "GPU Sensor Packed SoA Binned IDs");
                selectedQueries = CreateBuffer(
                    queryCapacity,
                    DigestStride,
                    "GPU Sensor Packed SoA Queries");
                selectedQueryDigests = CreateBuffer(
                    queryCapacity,
                    DigestStride,
                    "GPU Sensor Packed SoA Query Digests");
                selectedFrameDigest = CreateBuffer(
                    FrameDigestCount,
                    DigestStride,
                    "GPU Sensor Packed SoA Per-State Digests",
                    GraphicsBuffer.Target.CopySource);
                selectedValidation = CreateBuffer(
                    ValidationWordCount,
                    UintStride,
                    "GPU Sensor Packed SoA Validation Diagnostics");
                selectedPrimitives = new GpuPrimitivesRuntime(
                    FixedBinCount,
                    emitProfilerMarkers: emitProfilerMarkers);
            }
            catch
            {
                selectedPrimitives?.Dispose();
                DisposeBuffer(selectedPackedX);
                DisposeBuffer(selectedPackedY);
                DisposeBuffer(selectedPackedZ);
                DisposeBuffer(selectedPackedIntensity);
                DisposeBuffer(selectedBinCounts);
                DisposeBuffer(selectedBinOffsets);
                DisposeBuffer(selectedBinnedIds);
                DisposeBuffer(selectedQueries);
                DisposeBuffer(selectedQueryDigests);
                DisposeBuffer(selectedFrameDigest);
                DisposeBuffer(selectedValidation);
                throw;
            }

            ElementCapacity = elementCapacity;
            QueryCapacity = queryCapacity;
            PackedWordCapacity = packedWordCapacity;
            Backend = backend;
            this.emitProfilerMarkers = emitProfilerMarkers;
            this.shader = selectedShader;
            clearUintKernel = selectedClearKernel;
            produceAndCountKernel = selectedProduceKernel;
            scatterKernel = selectedScatterKernel;
            rangeQueryKernel = selectedRangeQueryKernel;
            reduceFrameDigestKernel = selectedReduceKernel;
            validatePackedSamplesKernel = selectedValidatePackedKernel;
            validateCsrBinsKernel = selectedValidateCsrKernel;
            primitives = selectedPrimitives;
            PackedX = selectedPackedX;
            PackedY = selectedPackedY;
            PackedZ = selectedPackedZ;
            PackedIntensity = selectedPackedIntensity;
            BinCounts = selectedBinCounts;
            BinOffsets = selectedBinOffsets;
            BinnedIds = selectedBinnedIds;
            Queries = selectedQueries;
            QueryDigests = selectedQueryDigests;
            FrameDigest = selectedFrameDigest;
            ValidationDiagnostics = selectedValidation;
        }

        public int ElementCapacity { get; }

        public int QueryCapacity { get; }

        public int PackedWordCapacity { get; }

        public int BinCount => FixedBinCount;

        public int ConfiguredQueryCount => configuredQueryCount;

        public GpuPrimitiveBackend Backend { get; }

        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        public GraphicsBuffer PackedX { get; }

        public GraphicsBuffer PackedY { get; }

        public GraphicsBuffer PackedZ { get; }

        public GraphicsBuffer PackedIntensity { get; }

        public GraphicsBuffer BinCounts { get; }

        /// <summary>
        /// Contains cumulative bin ends after scatter; index BinCount is unused.
        /// </summary>
        public GraphicsBuffer BinOffsets { get; }

        public GraphicsBuffer BinnedIds { get; }

        public GraphicsBuffer Queries { get; }

        public GraphicsBuffer QueryDigests { get; }

        public GraphicsBuffer FrameDigest { get; }

        public GraphicsBuffer ValidationDiagnostics { get; }

        public long PackedAttributeBytes =>
            checked(
                (long)PackedWordCapacity *
                UintStride *
                PackedStreamCount);

        public long PrimitiveScratchBytes => primitives.ScratchBytes;

        public long OwnedBufferBytes =>
            checked(
                PackedAttributeBytes +
                (long)ElementCapacity * UintStride +
                (long)FixedBinCount * UintStride +
                (long)(FixedBinCount + 1) * UintStride +
                (long)QueryCapacity * DigestStride * 2L +
                (long)FrameDigestCount * DigestStride +
                (long)ValidationWordCount * UintStride);

        public long ResidentBytes =>
            checked(OwnedBufferBytes + PrimitiveScratchBytes);

        public long PackedDynamicBytes =>
            checked(
                PackedAttributeBytes +
                (long)ElementCapacity * UintStride);

        public long ReferenceExpandedDynamicBytes =>
            checked((long)ElementCapacity * 28L);

        public long DynamicBytesSavedAgainstReference =>
            checked(ReferenceExpandedDynamicBytes - PackedDynamicBytes);

        public long GpuProducerLogicalWriteBytes(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked(
                (long)PackedWordCount(elementCount) *
                UintStride *
                PackedStreamCount);
        }

        public long ReferenceExpandedProducerLogicalWriteBytes(
            int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked(
                (long)elementCount *
                (GpuSensorPipeline.SampleStride + UintStride));
        }

        public long MaterializedKeyBytesAvoided(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked((long)elementCount * UintStride);
        }

        public long StableIdBytesAvoided(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked((long)elementCount * UintStride);
        }

        public long PipelineElementMaterializedWriteBytes(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked(
                GpuProducerLogicalWriteBytes(elementCount) +
                (long)elementCount * UintStride);
        }

        public long ReferencePipelineElementMaterializedWriteBytes(
            int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked(
                ReferenceExpandedProducerLogicalWriteBytes(elementCount) +
                (long)elementCount * UintStride);
        }

        /// <summary>
        /// Typed-buffer bytes addressed by pairwise XYZ loads during packed
        /// scatter. This is not a hardware DRAM-traffic measurement.
        /// </summary>
        public long SpatialBuildAddressedReadBytes(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked(
                (long)PackedWordCount(elementCount) *
                3L *
                UintStride);
        }

        /// <summary>
        /// Typed-buffer bytes addressed by baseline key count plus key/ID
        /// scatter. This is not a hardware DRAM-traffic measurement.
        /// </summary>
        public long ReferenceSpatialBuildAddressedReadBytes(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked((long)elementCount * 3L * UintStride);
        }

        public long FusedCountAtomicOperations(int elementCount)
        {
            ValidateElementCount(elementCount);
            return elementCount;
        }

        public long ScatterReservationAtomicOperations(int elementCount)
        {
            ValidateElementCount(elementCount);
            return elementCount;
        }

        public static long LogicalRangeQueryReadBytes(
            long candidateVisits,
            long acceptedVisits)
        {
            if (candidateVisits < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(candidateVisits));
            }
            if (acceptedVisits < 0 || acceptedVisits > candidateVisits)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(acceptedVisits));
            }

            return checked(
                candidateVisits *
                    LogicalPositionReadBytesPerCandidate +
                acceptedVisits *
                    LogicalIntensityReadBytesPerAcceptedCandidate);
        }

        public static long ReferenceExpandedRangeQueryReadBytes(
            long candidateVisits)
        {
            if (candidateVisits < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(candidateVisits));
            }
            return checked(
                candidateVisits * GpuSensorPipeline.SampleStride);
        }

        public void SetQueries(GpuSensorRangeQuery[] queries)
        {
            ThrowIfDisposed();
            if (queries == null)
            {
                throw new ArgumentNullException(nameof(queries));
            }
            if (queries.Length < 1 || queries.Length > QueryCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(queries));
            }
            for (int index = 0; index < queries.Length; index++)
            {
                GpuSensorRangeQuery query = queries[index];
                if (query.CenterX >
                        GpuSensorDeterministicGenerator.CoordinateMask ||
                    query.CenterY >
                        GpuSensorDeterministicGenerator.CoordinateMask ||
                    query.CenterZ >
                        GpuSensorDeterministicGenerator.CoordinateMask ||
                    query.Radius >
                        GpuSensorDeterministicGenerator.CoordinateMask)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(queries),
                        "Query centers and radii must fit the fixed " +
                        "16-bit sensor domain.");
                }
            }

            Queries.SetData(queries, 0, 0, queries.Length);
            configuredQueryCount = queries.Length;
            queriesInitialized = true;
        }

        public void RecordGpuProduced(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount,
            int queryCount)
        {
            ValidateCommonRecord(
                commands,
                elementCount,
                queryCount,
                logicalState);

            BeginSample(commands, PathSample);
            BeginSample(commands, ClearSample);
            RecordClear(commands, BinCounts, FixedBinCount);
            EndSample(commands, ClearSample);

            BeginSample(commands, ProducerCountSample);
            SetCommonProducerParameters(
                commands,
                seed,
                logicalState,
                elementCount);
            commands.SetComputeBufferParam(
                shader,
                produceAndCountKernel,
                PackedXRwId,
                PackedX);
            commands.SetComputeBufferParam(
                shader,
                produceAndCountKernel,
                PackedYRwId,
                PackedY);
            commands.SetComputeBufferParam(
                shader,
                produceAndCountKernel,
                PackedZRwId,
                PackedZ);
            commands.SetComputeBufferParam(
                shader,
                produceAndCountKernel,
                PackedIntensityRwId,
                PackedIntensity);
            commands.SetComputeBufferParam(
                shader,
                produceAndCountKernel,
                BinCountsId,
                BinCounts);
            commands.DispatchCompute(
                shader,
                produceAndCountKernel,
                DivideRoundUp(
                    PackedWordCount(elementCount),
                    ThreadGroupSize),
                1,
                1);
            EndSample(commands, ProducerCountSample);

            primitives.RecordExclusiveScan(
                commands,
                BinCounts,
                BinOffsets,
                FixedBinCount,
                Backend);

            BeginSample(commands, ScatterSample);
            commands.SetComputeIntParam(
                shader,
                ElementCountId,
                elementCount);
            commands.SetComputeIntParam(
                shader,
                BinCountId,
                FixedBinCount);
            BindPackedCoordinateReadBuffers(commands, scatterKernel);
            commands.SetComputeBufferParam(
                shader,
                scatterKernel,
                BinOffsetsRwId,
                BinOffsets);
            commands.SetComputeBufferParam(
                shader,
                scatterKernel,
                BinnedIdsRwId,
                BinnedIds);
            commands.DispatchCompute(
                shader,
                scatterKernel,
                DivideRoundUp(
                    PackedWordCount(elementCount),
                    ThreadGroupSize),
                1,
                1);
            EndSample(commands, ScatterSample);

            RecordConsumer(
                commands,
                elementCount,
                queryCount,
                logicalState);
            EndSample(commands, PathSample);
        }

        /// <summary>
        /// Records deterministic packed-sample and CSR membership validation.
        /// This pass is not part of a performance measurement window.
        /// </summary>
        public void RecordValidate(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount)
        {
            ThrowIfDisposed();
            ValidateCommands(commands);
            ValidateElementCount(elementCount);
            GpuSensorDeterministicGenerator.ValidateLogicalState(logicalState);

            BeginSample(commands, ValidationSample);
            RecordClear(
                commands,
                ValidationDiagnostics,
                ValidationWordCount);

            SetCommonProducerParameters(
                commands,
                seed,
                logicalState,
                elementCount);
            BindPackedReadBuffers(commands, validatePackedSamplesKernel);
            commands.SetComputeBufferParam(
                shader,
                validatePackedSamplesKernel,
                ValidationDiagnosticsId,
                ValidationDiagnostics);
            commands.DispatchCompute(
                shader,
                validatePackedSamplesKernel,
                DivideRoundUp(elementCount, ThreadGroupSize),
                1,
                1);

            commands.SetComputeIntParam(
                shader,
                ElementCountId,
                elementCount);
            commands.SetComputeIntParam(
                shader,
                BinCountId,
                FixedBinCount);
            BindPackedReadBuffers(commands, validateCsrBinsKernel);
            commands.SetComputeBufferParam(
                shader,
                validateCsrBinsKernel,
                BinCountsId,
                BinCounts);
            commands.SetComputeBufferParam(
                shader,
                validateCsrBinsKernel,
                BinOffsetsId,
                BinOffsets);
            commands.SetComputeBufferParam(
                shader,
                validateCsrBinsKernel,
                BinnedIdsId,
                BinnedIds);
            commands.SetComputeBufferParam(
                shader,
                validateCsrBinsKernel,
                ValidationDiagnosticsId,
                ValidationDiagnostics);
            commands.DispatchCompute(
                shader,
                validateCsrBinsKernel,
                DivideRoundUp(FixedBinCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, ValidationSample);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            primitives.Dispose();
            PackedX.Dispose();
            PackedY.Dispose();
            PackedZ.Dispose();
            PackedIntensity.Dispose();
            BinCounts.Dispose();
            BinOffsets.Dispose();
            BinnedIds.Dispose();
            Queries.Dispose();
            QueryDigests.Dispose();
            FrameDigest.Dispose();
            ValidationDiagnostics.Dispose();
        }

        public static int PackedWordCount(int elementCount)
        {
            if (elementCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            }
            return checked((int)(((long)elementCount + 1L) / 2L));
        }

        private void RecordConsumer(
            CommandBuffer commands,
            int elementCount,
            int queryCount,
            uint logicalState)
        {
            BeginSample(commands, QuerySample);
            commands.SetComputeIntParam(
                shader,
                ElementCountId,
                elementCount);
            commands.SetComputeIntParam(
                shader,
                QueryCountId,
                queryCount);
            BindPackedReadBuffers(commands, rangeQueryKernel);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                BinOffsetsId,
                BinOffsets);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                BinnedIdsId,
                BinnedIds);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                QueriesId,
                Queries);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                QueryDigestsId,
                QueryDigests);
            commands.DispatchCompute(
                shader,
                rangeQueryKernel,
                queryCount,
                1,
                1);
            EndSample(commands, QuerySample);

            BeginSample(commands, FrameDigestSample);
            commands.SetComputeIntParam(
                shader,
                QueryCountId,
                queryCount);
            commands.SetComputeIntParam(
                shader,
                LogicalStateId,
                unchecked((int)logicalState));
            commands.SetComputeBufferParam(
                shader,
                reduceFrameDigestKernel,
                QueryDigestsId,
                QueryDigests);
            commands.SetComputeBufferParam(
                shader,
                reduceFrameDigestKernel,
                FrameDigestId,
                FrameDigest);
            commands.DispatchCompute(
                shader,
                reduceFrameDigestKernel,
                1,
                1,
                1);
            EndSample(commands, FrameDigestSample);
        }

        private void SetCommonProducerParameters(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount)
        {
            commands.SetComputeIntParam(
                shader,
                ElementCountId,
                elementCount);
            commands.SetComputeIntParam(
                shader,
                BinCountId,
                FixedBinCount);
            commands.SetComputeIntParam(
                shader,
                SeedId,
                unchecked((int)seed));
            commands.SetComputeIntParam(
                shader,
                LogicalStateId,
                unchecked((int)logicalState));
        }

        private void BindPackedReadBuffers(
            CommandBuffer commands,
            int kernel)
        {
            BindPackedCoordinateReadBuffers(commands, kernel);
            commands.SetComputeBufferParam(
                shader,
                kernel,
                PackedIntensityId,
                PackedIntensity);
        }

        private void BindPackedCoordinateReadBuffers(
            CommandBuffer commands,
            int kernel)
        {
            commands.SetComputeBufferParam(
                shader,
                kernel,
                PackedXId,
                PackedX);
            commands.SetComputeBufferParam(
                shader,
                kernel,
                PackedYId,
                PackedY);
            commands.SetComputeBufferParam(
                shader,
                kernel,
                PackedZId,
                PackedZ);
        }

        private void RecordClear(
            CommandBuffer commands,
            GraphicsBuffer buffer,
            int count)
        {
            commands.SetComputeIntParam(shader, ClearCountId, count);
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

        private void ValidateCommonRecord(
            CommandBuffer commands,
            int elementCount,
            int queryCount,
            uint logicalState)
        {
            ThrowIfDisposed();
            ValidateCommands(commands);
            ValidateElementCount(elementCount);
            GpuSensorDeterministicGenerator.ValidateLogicalState(logicalState);
            if (!queriesInitialized)
            {
                throw new InvalidOperationException(
                    "SetQueries must run before recording the pipeline.");
            }
            if (queryCount < 1 || queryCount > configuredQueryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(queryCount));
            }
        }

        private void ValidateElementCount(int elementCount)
        {
            if (elementCount < 1 || elementCount > ElementCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            }
        }

        private static void ValidateCommands(CommandBuffer commands)
        {
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
        }

        private static void ValidateBackend(GpuPrimitiveBackend backend)
        {
            if (backend != GpuPrimitiveBackend.Portable &&
                backend != GpuPrimitiveBackend.WaveOps)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(backend),
                    "Select Portable or WaveOps explicitly; Auto is rejected.");
            }
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

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuSensorPackedSoaPipeline));
            }
        }

        private static GraphicsBuffer CreateBuffer(
            int count,
            int stride,
            string name,
            GraphicsBuffer.Target additionalTargets =
                (GraphicsBuffer.Target)0)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | additionalTargets,
                count,
                stride)
            {
                name = name
            };
        }

        private static void DisposeBuffer(GraphicsBuffer buffer)
        {
            buffer?.Dispose();
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }
    }
}
