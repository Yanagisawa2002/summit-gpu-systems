using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using GpuPrimitivesRuntime = Summit.GpuPrimitives.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    /// <summary>
    /// Owns fixed-capacity GPU sensor resources and records deterministic
    /// producer, trusted Direct CSR, and payload-consuming query work.
    /// </summary>
    public sealed class GpuSensorPipeline : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int FixedBinCount =
            GpuSensorDeterministicGenerator.BinCount;
        public const int FrameDigestCount =
            GpuSensorDeterministicGenerator.StateCount;
        public const int DigestStride = 16;
        public const int SampleStride = 16;
        public const int UintStride = sizeof(uint);
        public const int KeyValidationWordCount = 2;
        public const int InvalidKeyCountWord = 0;
        public const int InvalidKeyHashWord = 1;

        private const string ResourcePath =
            "GpuSensorPipeline/GpuSensorPipeline";
        private const string CpuPathSample =
            "Summit.GpuSensorPipeline/CpuProduced";
        private const string CpuUploadSample =
            "Summit.GpuSensorPipeline/CpuUpload";
        private const string GpuPathSample =
            "Summit.GpuSensorPipeline/GpuProduced";
        private const string GpuQuantizedViewPathSample =
            "Summit.GpuSensorPipeline/GpuProduced/QuantizedView";
        private const string GpuProducerSample =
            "Summit.GpuSensorPipeline/GpuProducer";
        private const string MultiSensorIndependentSample =
            "Summit.GpuSensorPipeline/MultiSensor/RebuiltPerSensor";
        private const string MultiSensorSharedSample =
            "Summit.GpuSensorPipeline/MultiSensor/SharedIndex";
        private const string IndexBuildSample =
            "Summit.GpuSensorPipeline/SpatialIndexBuild";
        private const string QuerySample =
            "Summit.GpuSensorPipeline/RangeQuery";
        private const string FrameDigestSample =
            "Summit.GpuSensorPipeline/FrameDigest";
        private const string ValidationSample =
            "Summit.GpuSensorPipeline/ValidateKeys";
        private const string ComparisonSample =
            "Summit.GpuSensorPipeline/CompareDigests";

        private static readonly int ElementCountId =
            Shader.PropertyToID("_ElementCount");
        private static readonly int BinCountId =
            Shader.PropertyToID("_BinCount");
        private static readonly int QueryCountId =
            Shader.PropertyToID("_QueryCount");
        private static readonly int QueryStartId =
            Shader.PropertyToID("_QueryStart");
        private static readonly int CompareCountId =
            Shader.PropertyToID("_CompareCount");
        private static readonly int SeedId =
            Shader.PropertyToID("_Seed");
        private static readonly int LogicalStateId =
            Shader.PropertyToID("_LogicalState");
        private static readonly int QuantizeIntensityId =
            Shader.PropertyToID("_QuantizeIntensity");
        private static readonly int SamplesId =
            Shader.PropertyToID("_Samples");
        private static readonly int SamplesRwId =
            Shader.PropertyToID("_SamplesRW");
        private static readonly int KeysId =
            Shader.PropertyToID("_Keys");
        private static readonly int KeysRwId =
            Shader.PropertyToID("_KeysRW");
        private static readonly int BinOffsetsId =
            Shader.PropertyToID("_BinOffsets");
        private static readonly int BinnedIdsId =
            Shader.PropertyToID("_BinnedIds");
        private static readonly int QueriesId =
            Shader.PropertyToID("_Queries");
        private static readonly int QueryDigestsId =
            Shader.PropertyToID("_QueryDigests");
        private static readonly int FrameDigestId =
            Shader.PropertyToID("_FrameDigest");
        private static readonly int ValidationDiagnosticsId =
            Shader.PropertyToID("_ValidationDiagnostics");
        private static readonly int ExpectedDigestsId =
            Shader.PropertyToID("_ExpectedDigests");
        private static readonly int ActualDigestsId =
            Shader.PropertyToID("_ActualDigests");
        private static readonly int ComparisonDigestId =
            Shader.PropertyToID("_ComparisonDigest");

        private readonly ComputeShader shader;
        private readonly int produceKernel;
        private readonly int rangeQueryKernel;
        private readonly int reduceFrameDigestKernel;
        private readonly int clearValidationKernel;
        private readonly int validateKeysKernel;
        private readonly int clearComparisonKernel;
        private readonly int compareDigestsKernel;
        private readonly GpuPrimitivesRuntime primitives;
        private readonly GpuDirectSpatialBinner binner;
        private readonly bool emitProfilerMarkers;
        private bool stableIdsInitialized;
        private bool queriesInitialized;
        private int configuredQueryCount;
        private bool disposed;

        public GpuSensorPipeline(
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

            int selectedProduceKernel =
                selectedShader.FindKernel("ProduceDeterministicSamplesAndKeys");
            int selectedRangeQueryKernel =
                selectedShader.FindKernel("RangeQueryDigest");
            int selectedReduceKernel =
                selectedShader.FindKernel("ReduceFrameDigest");
            int selectedClearValidationKernel =
                selectedShader.FindKernel("ClearValidationDiagnostics");
            int selectedValidateKeysKernel =
                selectedShader.FindKernel("ValidateKeys");
            int selectedClearComparisonKernel =
                selectedShader.FindKernel("ClearComparisonDigest");
            int selectedCompareKernel =
                selectedShader.FindKernel("CompareDigests");

            GpuPrimitivesRuntime selectedPrimitives = null;
            GpuDirectSpatialBinner selectedBinner = null;
            GraphicsBuffer selectedSamples = null;
            GraphicsBuffer selectedKeys = null;
            GraphicsBuffer selectedStableIds = null;
            GraphicsBuffer selectedBinCounts = null;
            GraphicsBuffer selectedBinOffsets = null;
            GraphicsBuffer selectedBinnedIds = null;
            GraphicsBuffer selectedDiagnostics = null;
            GraphicsBuffer selectedQueries = null;
            GraphicsBuffer selectedQueryDigests = null;
            GraphicsBuffer selectedFrameDigest = null;
            GraphicsBuffer selectedKeyValidation = null;
            GraphicsBuffer selectedComparison = null;

            try
            {
                selectedSamples = CreateBuffer(
                    elementCapacity,
                    SampleStride,
                    "GPU Sensor Pipeline Samples");
                selectedKeys = CreateBuffer(
                    elementCapacity,
                    UintStride,
                    "GPU Sensor Pipeline Keys");
                selectedStableIds = CreateBuffer(
                    elementCapacity,
                    UintStride,
                    "GPU Sensor Pipeline Stable IDs");
                selectedBinCounts = CreateBuffer(
                    FixedBinCount,
                    UintStride,
                    "GPU Sensor Pipeline Bin Counts");
                selectedBinOffsets = CreateBuffer(
                    FixedBinCount + 1,
                    UintStride,
                    "GPU Sensor Pipeline Bin Offsets");
                selectedBinnedIds = CreateBuffer(
                    elementCapacity,
                    UintStride,
                    "GPU Sensor Pipeline Binned IDs");
                selectedDiagnostics = CreateBuffer(
                    GpuDirectSpatialBinner.DiagnosticWordCount,
                    UintStride,
                    "GPU Sensor Pipeline Direct Diagnostics");
                selectedQueries = CreateBuffer(
                    queryCapacity,
                    DigestStride,
                    "GPU Sensor Pipeline Queries");
                selectedQueryDigests = CreateBuffer(
                    queryCapacity,
                    DigestStride,
                    "GPU Sensor Pipeline Query Digests");
                selectedFrameDigest = CreateBuffer(
                    FrameDigestCount,
                    DigestStride,
                    "GPU Sensor Pipeline Per-State Digests",
                    GraphicsBuffer.Target.CopySource);
                selectedKeyValidation = CreateBuffer(
                    KeyValidationWordCount,
                    UintStride,
                    "GPU Sensor Pipeline Key Validation");
                selectedComparison = CreateBuffer(
                    1,
                    DigestStride,
                    "GPU Sensor Pipeline Comparison Digest");

                selectedPrimitives = new GpuPrimitivesRuntime(
                    FixedBinCount,
                    emitProfilerMarkers: emitProfilerMarkers);
                selectedBinner = new GpuDirectSpatialBinner(
                    elementCapacity,
                    FixedBinCount,
                    selectedPrimitives,
                    emitProfilerMarkers: emitProfilerMarkers);
            }
            catch
            {
                selectedBinner?.Dispose();
                selectedPrimitives?.Dispose();
                DisposeBuffer(selectedSamples);
                DisposeBuffer(selectedKeys);
                DisposeBuffer(selectedStableIds);
                DisposeBuffer(selectedBinCounts);
                DisposeBuffer(selectedBinOffsets);
                DisposeBuffer(selectedBinnedIds);
                DisposeBuffer(selectedDiagnostics);
                DisposeBuffer(selectedQueries);
                DisposeBuffer(selectedQueryDigests);
                DisposeBuffer(selectedFrameDigest);
                DisposeBuffer(selectedKeyValidation);
                DisposeBuffer(selectedComparison);
                throw;
            }

            ElementCapacity = elementCapacity;
            QueryCapacity = queryCapacity;
            Backend = backend;
            this.emitProfilerMarkers = emitProfilerMarkers;
            this.shader = selectedShader;
            produceKernel = selectedProduceKernel;
            rangeQueryKernel = selectedRangeQueryKernel;
            reduceFrameDigestKernel = selectedReduceKernel;
            clearValidationKernel = selectedClearValidationKernel;
            validateKeysKernel = selectedValidateKeysKernel;
            clearComparisonKernel = selectedClearComparisonKernel;
            compareDigestsKernel = selectedCompareKernel;
            primitives = selectedPrimitives;
            binner = selectedBinner;
            Samples = selectedSamples;
            Keys = selectedKeys;
            StableIds = selectedStableIds;
            BinCounts = selectedBinCounts;
            BinOffsets = selectedBinOffsets;
            BinnedIds = selectedBinnedIds;
            Diagnostics = selectedDiagnostics;
            Queries = selectedQueries;
            QueryDigests = selectedQueryDigests;
            FrameDigest = selectedFrameDigest;
            KeyValidationDiagnostics = selectedKeyValidation;
            ComparisonDigest = selectedComparison;
        }

        public int ElementCapacity { get; }

        public int QueryCapacity { get; }

        public int BinCount => FixedBinCount;

        public int ConfiguredQueryCount => configuredQueryCount;

        public GpuPrimitiveBackend Backend { get; }

        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        public GraphicsBuffer Samples { get; }

        public GraphicsBuffer Keys { get; }

        public GraphicsBuffer StableIds { get; }

        public GraphicsBuffer BinCounts { get; }

        public GraphicsBuffer BinOffsets { get; }

        public GraphicsBuffer BinnedIds { get; }

        public GraphicsBuffer Diagnostics { get; }

        public GraphicsBuffer Queries { get; }

        public GraphicsBuffer QueryDigests { get; }

        public GraphicsBuffer FrameDigest { get; }

        public GraphicsBuffer KeyValidationDiagnostics { get; }

        public GraphicsBuffer ComparisonDigest { get; }

        public long PrimitiveScratchBytes => primitives.ScratchBytes;

        public long BinnerInternalScratchBytes => binner.InternalScratchBytes;

        public long BinnerScratchBytes => binner.ScratchBytes;

        public long OwnedBufferBytes =>
            checked(
                (long)ElementCapacity * SampleStride +
                (long)ElementCapacity * UintStride * 3L +
                (long)FixedBinCount * UintStride +
                (long)(FixedBinCount + 1) * UintStride +
                GpuDirectSpatialBinner.DiagnosticWordCount * UintStride +
                (long)QueryCapacity * DigestStride * 2L +
                (long)FrameDigestCount * DigestStride +
                KeyValidationWordCount * UintStride +
                DigestStride);

        public long ResidentBytes =>
            checked(OwnedBufferBytes + BinnerScratchBytes);

        /// <summary>
        /// Logical bytes of keys, identity values, CSR outputs, diagnostics,
        /// and count/scan/scatter scratch required by one materialized index.
        /// Sample payload and query buffers are deliberately excluded.
        /// </summary>
        public long SpatialIndexResidentBytes =>
            checked(
                (long)ElementCapacity * UintStride * 3L +
                (long)FixedBinCount * UintStride +
                (long)(FixedBinCount + 1) * UintStride +
                GpuDirectSpatialBinner.DiagnosticWordCount * UintStride +
                BinnerScratchBytes);

        public long IndependentSensorSpatialIndexBytes(int sensorCount)
        {
            ValidateSensorCount(sensorCount);
            return checked(SpatialIndexResidentBytes * sensorCount);
        }

        public long CpuUploadLogicalBytes(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked((long)elementCount * (SampleStride + UintStride));
        }

        public long GpuProducerLogicalWriteBytes(int elementCount)
        {
            ValidateElementCount(elementCount);
            return checked((long)elementCount * (SampleStride + UintStride));
        }

        public void SetStableIds(uint[] stableIds)
        {
            ThrowIfDisposed();
            if (stableIds == null)
            {
                throw new ArgumentNullException(nameof(stableIds));
            }
            if (stableIds.Length != ElementCapacity)
            {
                throw new ArgumentException(
                    "Stable IDs must exactly match element capacity.",
                    nameof(stableIds));
            }
            for (int index = 0; index < stableIds.Length; index++)
            {
                if (stableIds[index] != unchecked((uint)index))
                {
                    throw new ArgumentException(
                        "Stable IDs must be the identity mapping so query " +
                        "payload fetches remain in range.",
                        nameof(stableIds));
                }
            }

            StableIds.SetData(stableIds);
            stableIdsInitialized = true;
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

        /// <summary>
        /// Records CPU array uploads and then the common trusted CSR/query path.
        /// The arrays must remain immutable until GPU completion.
        /// </summary>
        public void RecordCpuProduced(
            CommandBuffer commands,
            GpuSensorSample[] samples,
            uint[] keys,
            int elementCount,
            int queryCount,
            uint logicalState)
        {
            ValidateCommonRecord(
                commands,
                elementCount,
                queryCount,
                logicalState);
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            if (samples.Length < elementCount || keys.Length < elementCount)
            {
                throw new ArgumentException(
                    "CPU producer arrays must cover elementCount.");
            }

            BeginSample(commands, CpuPathSample);
            BeginSample(commands, CpuUploadSample);
            commands.SetBufferData(
                Samples,
                samples,
                0,
                0,
                elementCount);
            commands.SetBufferData(
                Keys,
                keys,
                0,
                0,
                elementCount);
            EndSample(commands, CpuUploadSample);
            RecordConsumer(commands, elementCount, queryCount, logicalState, false);
            EndSample(commands, CpuPathSample);
        }

        /// <summary>
        /// Exact showcase baseline for several sensor consumers: materialize
        /// the dynamic samples and keys on the CPU, upload them once, then
        /// rebuild the same spatial index independently for every sensor.
        /// Query segmentation and the final digest are identical to the
        /// GPU-produced shared-index path.
        /// </summary>
        public void RecordCpuProducedRebuiltPerSensor(
            CommandBuffer commands,
            GpuSensorSample[] samples,
            uint[] keys,
            int elementCount,
            int sensorCount,
            int queriesPerSensor,
            uint logicalState)
        {
            int totalQueryCount = ValidateMultiSensorRecord(
                commands,
                logicalState,
                elementCount,
                sensorCount,
                queriesPerSensor);
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            if (samples.Length < elementCount || keys.Length < elementCount)
            {
                throw new ArgumentException(
                    "CPU producer arrays must cover elementCount.");
            }

            BeginSample(commands, CpuPathSample);
            BeginSample(commands, CpuUploadSample);
            commands.SetBufferData(Samples, samples, 0, 0, elementCount);
            commands.SetBufferData(Keys, keys, 0, 0, elementCount);
            EndSample(commands, CpuUploadSample);

            BeginSample(commands, MultiSensorIndependentSample);
            for (int sensorIndex = 0; sensorIndex < sensorCount; sensorIndex++)
            {
                RecordIndexBuild(commands, elementCount, false);
                RecordRangeQuerySegment(
                    commands,
                    elementCount,
                    sensorIndex * queriesPerSensor,
                    queriesPerSensor,
                    false);
            }
            RecordFrameDigest(commands, totalQueryCount, logicalState);
            EndSample(commands, MultiSensorIndependentSample);
            EndSample(commands, CpuPathSample);
        }

        public void RecordGpuProduced(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount,
            int queryCount)
        {
            RecordGpuProducedInternal(
                commands,
                seed,
                logicalState,
                elementCount,
                queryCount,
                false);
        }

        /// <summary>
        /// Control path for multiple consumers that independently rebuild the
        /// same dynamic spatial index before executing each sensor's query
        /// batch. Query work and final reduction match the shared-index path.
        /// </summary>
        public void RecordGpuProducedRebuiltPerSensor(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount,
            int sensorCount,
            int queriesPerSensor)
        {
            int totalQueryCount = ValidateMultiSensorRecord(
                commands,
                logicalState,
                elementCount,
                sensorCount,
                queriesPerSensor);

            BeginSample(commands, MultiSensorIndependentSample);
            for (int sensorIndex = 0; sensorIndex < sensorCount; sensorIndex++)
            {
                RecordGpuProducer(
                    commands,
                    seed,
                    logicalState,
                    elementCount);
                RecordIndexBuild(commands, elementCount, false);
                RecordRangeQuerySegment(
                    commands,
                    elementCount,
                    sensorIndex * queriesPerSensor,
                    queriesPerSensor,
                    false);
            }
            RecordFrameDigest(commands, totalQueryCount, logicalState);
            EndSample(commands, MultiSensorIndependentSample);
        }

        /// <summary>
        /// Produces and bins a dynamic point set once, then lets every sensor
        /// query batch consume the same GPU-resident CSR index.
        /// </summary>
        public void RecordGpuProducedSharedSensorIndex(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount,
            int sensorCount,
            int queriesPerSensor)
        {
            int totalQueryCount = ValidateMultiSensorRecord(
                commands,
                logicalState,
                elementCount,
                sensorCount,
                queriesPerSensor);

            BeginSample(commands, MultiSensorSharedSample);
            RecordGpuProducer(commands, seed, logicalState, elementCount);
            RecordIndexBuild(commands, elementCount, false);
            for (int sensorIndex = 0; sensorIndex < sensorCount; sensorIndex++)
            {
                RecordRangeQuerySegment(
                    commands,
                    elementCount,
                    sensorIndex * queriesPerSensor,
                    queriesPerSensor,
                    false);
            }
            RecordFrameDigest(commands, totalQueryCount, logicalState);
            EndSample(commands, MultiSensorSharedSample);
        }

        public long RebuiltPerSensorProducerLogicalWriteBytes(
            int elementCount,
            int sensorCount)
        {
            ValidateSensorCount(sensorCount);
            return checked(
                GpuProducerLogicalWriteBytes(elementCount) * sensorCount);
        }

        /// <summary>
        /// Records the AoS32 baseline while exposing a 16-bit quantized
        /// intensity view to the consumer. The producer and key materialization
        /// remain unchanged so this is a fair baseline for packed/fused data
        /// layouts with identical downstream semantics.
        /// </summary>
        public void RecordGpuProducedQuantizedView(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount,
            int queryCount)
        {
            RecordGpuProducedInternal(
                commands,
                seed,
                logicalState,
                elementCount,
                queryCount,
                true);
        }

        private void RecordGpuProducedInternal(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount,
            int queryCount,
            bool quantizeIntensity)
        {
            ValidateCommonRecord(
                commands,
                elementCount,
                queryCount,
                logicalState);

            string pathSample = quantizeIntensity
                ? GpuQuantizedViewPathSample
                : GpuPathSample;
            BeginSample(commands, pathSample);
            RecordGpuProducer(
                commands,
                seed,
                logicalState,
                elementCount);
            RecordConsumer(
                commands,
                elementCount,
                queryCount,
                logicalState,
                quantizeIntensity);
            EndSample(commands, pathSample);
        }

        /// <summary>
        /// Validation-only key check. Do not place it in a performance window.
        /// </summary>
        public void RecordValidateKeys(
            CommandBuffer commands,
            int elementCount)
        {
            ThrowIfDisposed();
            ValidateCommands(commands);
            ValidateElementCount(elementCount);

            BeginSample(commands, ValidationSample);
            commands.SetComputeBufferParam(
                shader,
                clearValidationKernel,
                ValidationDiagnosticsId,
                KeyValidationDiagnostics);
            commands.DispatchCompute(
                shader,
                clearValidationKernel,
                1,
                1,
                1);
            commands.SetComputeIntParam(shader, ElementCountId, elementCount);
            commands.SetComputeIntParam(shader, BinCountId, FixedBinCount);
            commands.SetComputeBufferParam(
                shader,
                validateKeysKernel,
                KeysId,
                Keys);
            commands.SetComputeBufferParam(
                shader,
                validateKeysKernel,
                ValidationDiagnosticsId,
                KeyValidationDiagnostics);
            commands.DispatchCompute(
                shader,
                validateKeysKernel,
                DivideRoundUp(elementCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, ValidationSample);
        }

        public void RecordClearComparison(CommandBuffer commands)
        {
            ThrowIfDisposed();
            ValidateCommands(commands);
            commands.SetComputeBufferParam(
                shader,
                clearComparisonKernel,
                ComparisonDigestId,
                ComparisonDigest);
            commands.DispatchCompute(
                shader,
                clearComparisonKernel,
                1,
                1,
                1);
        }

        /// <summary>
        /// Accumulates mismatch count and order-independent delta hashes into
        /// ComparisonDigest. Call RecordClearComparison first.
        /// </summary>
        public void RecordCompareDigests(
            CommandBuffer commands,
            GraphicsBuffer expected,
            GraphicsBuffer actual,
            int count)
        {
            ThrowIfDisposed();
            ValidateCommands(commands);
            int comparisonCapacity = Math.Max(
                QueryCapacity,
                FrameDigestCount);
            if (count < 1 || count > comparisonCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            ValidateDigestBuffer(expected, count, nameof(expected));
            ValidateDigestBuffer(actual, count, nameof(actual));
            if (ReferenceEquals(expected, ComparisonDigest) ||
                ReferenceEquals(actual, ComparisonDigest))
            {
                throw new ArgumentException(
                    "Comparison inputs must not alias the comparison output.");
            }

            BeginSample(commands, ComparisonSample);
            commands.SetComputeIntParam(shader, CompareCountId, count);
            commands.SetComputeBufferParam(
                shader,
                compareDigestsKernel,
                ExpectedDigestsId,
                expected);
            commands.SetComputeBufferParam(
                shader,
                compareDigestsKernel,
                ActualDigestsId,
                actual);
            commands.SetComputeBufferParam(
                shader,
                compareDigestsKernel,
                ComparisonDigestId,
                ComparisonDigest);
            commands.DispatchCompute(
                shader,
                compareDigestsKernel,
                DivideRoundUp(count, ThreadGroupSize),
                1,
                1);
            EndSample(commands, ComparisonSample);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            binner.Dispose();
            primitives.Dispose();
            Samples.Dispose();
            Keys.Dispose();
            StableIds.Dispose();
            BinCounts.Dispose();
            BinOffsets.Dispose();
            BinnedIds.Dispose();
            Diagnostics.Dispose();
            Queries.Dispose();
            QueryDigests.Dispose();
            FrameDigest.Dispose();
            KeyValidationDiagnostics.Dispose();
            ComparisonDigest.Dispose();
        }

        private void RecordConsumer(
            CommandBuffer commands,
            int elementCount,
            int queryCount,
            uint logicalState,
            bool quantizeIntensity)
        {
            RecordIndexBuild(commands, elementCount, quantizeIntensity);
            RecordRangeQuerySegment(
                commands,
                elementCount,
                0,
                queryCount,
                quantizeIntensity);
            RecordFrameDigest(commands, queryCount, logicalState);
        }

        private void RecordGpuProducer(
            CommandBuffer commands,
            uint seed,
            uint logicalState,
            int elementCount)
        {
            BeginSample(commands, GpuProducerSample);
            commands.SetComputeIntParam(shader, ElementCountId, elementCount);
            commands.SetComputeIntParam(shader, SeedId, unchecked((int)seed));
            commands.SetComputeIntParam(
                shader,
                LogicalStateId,
                unchecked((int)logicalState));
            commands.SetComputeBufferParam(
                shader,
                produceKernel,
                SamplesRwId,
                Samples);
            commands.SetComputeBufferParam(
                shader,
                produceKernel,
                KeysRwId,
                Keys);
            commands.DispatchCompute(
                shader,
                produceKernel,
                DivideRoundUp(elementCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, GpuProducerSample);
        }

        private void RecordIndexBuild(
            CommandBuffer commands,
            int elementCount,
            bool skipDiagnosticClear)
        {
            BeginSample(commands, IndexBuildSample);
            if (skipDiagnosticClear)
            {
                binner.RecordGuaranteedInRangeWithoutDiagnosticClear(
                    commands,
                    Keys,
                    StableIds,
                    BinCounts,
                    BinOffsets,
                    BinnedIds,
                    Diagnostics,
                    elementCount,
                    FixedBinCount,
                    Backend);
            }
            else
            {
                binner.RecordGuaranteedInRange(
                    commands,
                    Keys,
                    StableIds,
                    BinCounts,
                    BinOffsets,
                    BinnedIds,
                    Diagnostics,
                    elementCount,
                    FixedBinCount,
                    Backend);
            }
            EndSample(commands, IndexBuildSample);
        }

        /// <summary>
        /// Queries externally maintained CSR without rebuilding it. Stable IDs
        /// index samples; stableIdCapacity is the payload slot bound, NOT the
        /// live count. Monotonic offsets must terminate within binnedIds.count.
        /// IDs outside [0, stableIdCapacity) are holes and are skipped. Buffers
        /// must remain alive and unmodified until ordered GPU consumers finish.
        /// </summary>
        public void RecordExternalIndexQueries(
            CommandBuffer commands, GraphicsBuffer samples,
            GraphicsBuffer binOffsets, GraphicsBuffer binnedIds,
            int stableIdCapacity, int queryCount, uint logicalState)
        {
            ThrowIfDisposed();
            ValidateCommands(commands);
            ValidateElementCount(stableIdCapacity);
            GpuSensorDeterministicGenerator.ValidateLogicalState(logicalState);
            if (!queriesInitialized || queryCount < 1 || queryCount > configuredQueryCount)
                throw new ArgumentOutOfRangeException(nameof(queryCount));
            ValidateDigestBuffer(samples, stableIdCapacity, nameof(samples));
            ValidateExternalUintBuffer(binOffsets, FixedBinCount + 1, nameof(binOffsets));
            ValidateExternalUintBuffer(binnedIds, 1, nameof(binnedIds));
            if (samples == QueryDigests || samples == FrameDigest ||
                binOffsets == QueryDigests || binOffsets == FrameDigest ||
                binnedIds == QueryDigests || binnedIds == FrameDigest)
                throw new ArgumentException("Index inputs must not alias query outputs.");
            RecordRangeQuerySegment(commands, stableIdCapacity, 0, queryCount,
                false, samples, binOffsets, binnedIds);
            RecordFrameDigest(commands, queryCount, logicalState);
        }

        private static void ValidateExternalUintBuffer(GraphicsBuffer buffer, int count, string name)
        {
            if (buffer == null) throw new ArgumentNullException(name);
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != 4 || buffer.count < count)
                throw new ArgumentException("External CSR buffer has wrong shape.", name);
        }

        private void RecordRangeQuerySegment(
            CommandBuffer commands,
            int elementCount,
            int queryStart,
            int queryCount,
            bool quantizeIntensity,
            GraphicsBuffer externalSamples = null,
            GraphicsBuffer externalOffsets = null,
            GraphicsBuffer externalIds = null)
        {
            BeginSample(commands, QuerySample);
            commands.SetComputeIntParam(shader, ElementCountId, elementCount);
            commands.SetComputeIntParam(
                shader,
                QuantizeIntensityId,
                quantizeIntensity ? 1 : 0);
            commands.SetComputeIntParam(shader, QueryStartId, queryStart);
            commands.SetComputeIntParam(shader, QueryCountId, queryCount);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                SamplesId,
                externalSamples ?? Samples);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                BinOffsetsId,
                externalOffsets ?? BinOffsets);
            commands.SetComputeBufferParam(
                shader,
                rangeQueryKernel,
                BinnedIdsId,
                externalIds ?? BinnedIds);
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
        }

        private void RecordFrameDigest(
            CommandBuffer commands,
            int queryCount,
            uint logicalState)
        {
            BeginSample(commands, FrameDigestSample);
            commands.SetComputeIntParam(shader, QueryCountId, queryCount);
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

        private int ValidateMultiSensorRecord(
            CommandBuffer commands,
            uint logicalState,
            int elementCount,
            int sensorCount,
            int queriesPerSensor)
        {
            ValidateSensorCount(sensorCount);
            if (queriesPerSensor < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(queriesPerSensor));
            }
            int totalQueryCount = checked(sensorCount * queriesPerSensor);
            ValidateCommonRecord(
                commands,
                elementCount,
                totalQueryCount,
                logicalState);
            return totalQueryCount;
        }

        private static void ValidateSensorCount(int sensorCount)
        {
            if (sensorCount < 2 || sensorCount > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sensorCount),
                    "Multi-sensor sharing requires 2..64 consumers.");
            }
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
            if (!stableIdsInitialized)
            {
                throw new InvalidOperationException(
                    "SetStableIds must run before recording the pipeline.");
            }
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

        private static void ValidateDigestBuffer(
            GraphicsBuffer buffer,
            int requiredCount,
            string parameterName)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != DigestStride ||
                buffer.count < requiredCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a structured 16-byte buffer " +
                    $"with at least {requiredCount} elements.",
                    parameterName);
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

        private void BeginSample(CommandBuffer commands, string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.BeginSample(sampleName);
            }
        }

        private void EndSample(CommandBuffer commands, string sampleName)
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
                throw new ObjectDisposedException(nameof(GpuSensorPipeline));
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
