using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives
{
    /// <summary>
    /// Owns persistent scratch storage and records scene-agnostic uint compute
    /// primitives into a caller-owned <see cref="CommandBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Record methods do not allocate managed or GPU memory and never perform a
    /// readback. Call <see cref="EnsureCapacity"/> before a measurement window
    /// when a larger input is required.
    /// </remarks>
    public sealed class GpuPrimitives : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int RadixBitsPerPass = 4;
        public const int RadixBinCount = 1 << RadixBitsPerPass;
        public const int RadixPassCount = 32 / RadixBitsPerPass;
        public const int MaxDispatchGroups = 65535;
        public const int MaxElementCount =
            ThreadGroupSize * MaxDispatchGroups;

        private const string PortableResourcePath =
            "GpuPrimitives/GpuPrimitivesPortable";
        private const string WaveResourcePath =
            "GpuPrimitives/GpuPrimitivesWave";

        private static readonly int CountId = Shader.PropertyToID("_Count");
        private static readonly int ClearCountId =
            Shader.PropertyToID("_ClearCount");
        private static readonly int BinCountId =
            Shader.PropertyToID("_BinCount");
        private static readonly int KeyShiftId =
            Shader.PropertyToID("_KeyShift");
        private static readonly int RadixGroupCountId =
            Shader.PropertyToID("_RadixGroupCount");
        private static readonly int RadixShiftId =
            Shader.PropertyToID("_RadixShift");

        private static readonly int ValuesId = Shader.PropertyToID("_Values");
        private static readonly int PredicatesId =
            Shader.PropertyToID("_Predicates");
        private static readonly int NormalizedPredicatesId =
            Shader.PropertyToID("_NormalizedPredicates");
        private static readonly int ScannedPredicatesId =
            Shader.PropertyToID("_ScannedPredicates");
        private static readonly int OutputId = Shader.PropertyToID("_Output");
        private static readonly int OutputCountId =
            Shader.PropertyToID("_OutputCount");
        private static readonly int ClearBufferId =
            Shader.PropertyToID("_ClearBuffer");
        private static readonly int KeysId = Shader.PropertyToID("_Keys");
        private static readonly int HistogramId =
            Shader.PropertyToID("_Histogram");

        private static readonly int ScanInputId =
            Shader.PropertyToID("_ScanInput");
        private static readonly int ScanOutputId =
            Shader.PropertyToID("_ScanOutput");
        private static readonly int BlockSumsId =
            Shader.PropertyToID("_BlockSums");
        private static readonly int BlockOffsetsId =
            Shader.PropertyToID("_BlockOffsets");

        private static readonly int RadixKeysInId =
            Shader.PropertyToID("_RadixKeysIn");
        private static readonly int RadixValuesInId =
            Shader.PropertyToID("_RadixValuesIn");
        private static readonly int RadixKeysOutId =
            Shader.PropertyToID("_RadixKeysOut");
        private static readonly int RadixValuesOutId =
            Shader.PropertyToID("_RadixValuesOut");
        private static readonly int RadixGroupHistogramsId =
            Shader.PropertyToID("_RadixGroupHistograms");
        private static readonly int RadixGroupOffsetsId =
            Shader.PropertyToID("_RadixGroupOffsets");

        private readonly ComputeShader portableShader;
        private readonly ComputeShader waveShader;

        private readonly int clearUintKernel;
        private readonly int normalizePredicatesKernel;
        private readonly int portableScanKernel;
        private readonly int addBlockOffsetsKernel;
        private readonly int histogramAtomicKernel;
        private readonly int stableCompactScatterKernel;
        private readonly int appendCompactAtomicKernel;
        private readonly int radixHistogramPortableKernel;
        private readonly int radixScanOffsetsKernel;
        private readonly int radixScatterPortableKernel;

        private readonly int waveScanKernel;
        private readonly int histogramWave16Kernel;
        private readonly int appendCompactWaveKernel;
        private readonly int radixHistogramWaveKernel;
        private readonly int radixScatterWaveKernel;

        private GraphicsBuffer[] scanBlockSums;
        private GraphicsBuffer[] scanBlockOffsets;
        private GraphicsBuffer normalizedPredicates;
        private GraphicsBuffer scannedPredicates;
        private GraphicsBuffer radixKeysScratch;
        private GraphicsBuffer radixValuesScratch;
        private GraphicsBuffer radixGroupHistograms;
        private GraphicsBuffer radixGroupOffsets;
        private readonly bool emitProfilerMarkers;
        private readonly GpuPrimitiveCandidateRunner candidateRunner;
        /// <summary>Opt-in identity used by Auto for scan, stable-compaction scan,
        /// and radix. Null preserves legacy backend resolution. Explicit Portable
        /// and WaveOps always retain the legacy implementation.</summary>
        public string CandidateId => candidateRunner?.Candidate.Id;
        public int ObservedCandidateWaveSize => candidateRunner?.ObservedWaveSize ?? 0;
        public long CandidateScratchBytes => candidateRunner?.ScratchBytes ?? 0;
        private bool disposed;
        private static int waveSupportCache = -1;

        /// <summary>
        /// Creates persistent scratch storage for up to <paramref name="capacity"/>
        /// uint elements.
        /// </summary>
        public GpuPrimitives(
            int capacity,
            ComputeShader portableShader = null,
            ComputeShader waveShader = null,
            bool emitProfilerMarkers = true,
            string candidateId = null)
        {
            if (capacity < 1 || capacity > MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    $"Capacity must be in [1, {MaxElementCount}].");
            }

            // Reject unsupported opt-in identities before touching legacy shader
            // kernels or allocating resources, including on Unity's Null Device.
            if (candidateId != null)
            {
                GpuPrimitiveCandidates.Get(candidateId);
                if (!GpuPrimitiveCandidates.IsSupported(candidateId, out string reason))
                    throw new NotSupportedException(candidateId + ": " + reason);
            }

            this.portableShader = portableShader != null
                ? portableShader
                : Resources.Load<ComputeShader>(PortableResourcePath);
            this.waveShader = waveShader != null
                ? waveShader
                : Resources.Load<ComputeShader>(WaveResourcePath);
            this.emitProfilerMarkers = emitProfilerMarkers;

            if (this.portableShader == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{PortableResourcePath}' " +
                    "could not be loaded.");
            }

            clearUintKernel = this.portableShader.FindKernel("ClearUint");
            normalizePredicatesKernel =
                this.portableShader.FindKernel("NormalizePredicates");
            portableScanKernel =
                this.portableShader.FindKernel("ScanBlocksPortable");
            addBlockOffsetsKernel =
                this.portableShader.FindKernel("AddBlockOffsets");
            histogramAtomicKernel =
                this.portableShader.FindKernel("HistogramAtomic");
            stableCompactScatterKernel =
                this.portableShader.FindKernel("StableCompactScatter");
            appendCompactAtomicKernel =
                this.portableShader.FindKernel("AppendCompactAtomic");
            radixHistogramPortableKernel =
                this.portableShader.FindKernel("RadixHistogramPortable");
            radixScanOffsetsKernel =
                this.portableShader.FindKernel("RadixScanOffsets");
            radixScatterPortableKernel =
                this.portableShader.FindKernel("RadixScatterPortable");

            if (this.waveShader != null)
            {
                waveScanKernel =
                    this.waveShader.FindKernel("ScanBlocksWave");
                histogramWave16Kernel =
                    this.waveShader.FindKernel("HistogramWave16");
                appendCompactWaveKernel =
                    this.waveShader.FindKernel("AppendCompactWave");
                radixHistogramWaveKernel =
                    this.waveShader.FindKernel("RadixHistogramWave");
                radixScatterWaveKernel =
                    this.waveShader.FindKernel("RadixScatterWave");
            }
            else
            {
                waveScanKernel = -1;
                histogramWave16Kernel = -1;
                appendCompactWaveKernel = -1;
                radixHistogramWaveKernel = -1;
                radixScatterWaveKernel = -1;
            }

            if (candidateId != null) candidateRunner = new GpuPrimitiveCandidateRunner(candidateId, capacity);
            try { AllocateScratch(capacity); }
            catch { candidateRunner?.Dispose(); ReleaseScratch(); throw; }
        }

        public int Capacity { get; private set; }

        private long legacyScratchBytes;

        /// <summary>
        /// Exact logical payload bytes requested for persistent scratch
        /// GraphicsBuffers. Driver allocation alignment, caller-owned buffers,
        /// and shader assets are not included.
        /// </summary>
        public long ScratchBytes => legacyScratchBytes + CandidateScratchBytes;

        /// <summary>
        /// Logical payload bytes of persistent GraphicsBuffers owned by this
        /// instance. All owned buffers are reusable scratch, so this equals
        /// ScratchBytes.
        /// </summary>
        public long ResidentBytes => ScratchBytes;

        /// <summary>
        /// Whether Record methods emit their detailed nested profiler scopes.
        /// </summary>
        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        /// <summary>
        /// Conservative runtime capability check used by the Auto selector.
        /// </summary>
        public static bool SupportsWaveOperations
        {
            get
            {
                if (waveSupportCache >= 0)
                {
                    return waveSupportCache != 0;
                }

                GraphicsDeviceType type = SystemInfo.graphicsDeviceType;
                if (!SystemInfo.supportsComputeShaders ||
                    (type != GraphicsDeviceType.Direct3D12 &&
                     type != GraphicsDeviceType.Vulkan))
                {
                    waveSupportCache = 0;
                    return false;
                }

                try
                {
                    // Unity 6000 can report graphicsShaderLevel=50 on DX12
                    // while a target 6.0 DXC kernel is fully supported. Ask
                    // the imported kernel instead of inferring from that value.
                    ComputeShader shader =
                        Resources.Load<ComputeShader>(WaveResourcePath);
                    if (shader == null)
                    {
                        waveSupportCache = 0;
                        return false;
                    }

                    int kernel = shader.FindKernel("ScanBlocksWave");
                    waveSupportCache = shader.IsSupported(kernel) ? 1 : 0;
                    return waveSupportCache != 0;
                }
                catch (Exception)
                {
                    waveSupportCache = 0;
                    return false;
                }
            }
        }

        /// <summary>
        /// Grows persistent storage. Never call this inside a measurement window.
        /// </summary>
        public void EnsureCapacity(int capacity)
        {
            ThrowIfDisposed();
            if (capacity <= Capacity)
            {
                return;
            }
            if (capacity > MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    $"Capacity cannot exceed {MaxElementCount}.");
            }

            candidateRunner?.Allocate(capacity);
            ReleaseScratch();
            AllocateScratch(capacity);
        }

        public void RecordExclusiveScan(
            CommandBuffer commands,
            GraphicsBuffer input,
            GraphicsBuffer output,
            int count,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto)
        {
            ValidateCommandsAndCount(commands, count);
            ValidateUintBuffer(input, count, nameof(input));
            ValidateUintBuffer(output, count, nameof(output));
            if (input == output)
            {
                throw new ArgumentException(
                    "Exclusive scan input and output must be distinct.");
            }

            BeginSample(commands, "Summit.GpuPrimitives/ExclusiveScan");
            if (count > 0)
            {
                RecordExclusiveScanInternal(
                    commands,
                    input,
                    output,
                    count,
                    candidateRunner != null ? backend : ResolveBackend(backend));
            }
            EndSample(commands, "Summit.GpuPrimitives/ExclusiveScan");
        }

        /// <summary>Opt-in candidate uint sum (modulo 2^32). Empty input writes zero.
        /// Requires a candidate-configured instance; output must be distinct from input.</summary>
        public void RecordReduceSum(CommandBuffer commands, GraphicsBuffer input, GraphicsBuffer output, int count)
        {
            ValidateCommandsAndCount(commands, count);
            ValidateUintBuffer(input, count, nameof(input));
            ValidateUintBuffer(output, 1, nameof(output));
            if (input == output) throw new ArgumentException("Reduction input/output must be distinct.");
            if (candidateRunner == null) throw new InvalidOperationException("Reduction requires an explicit candidateId.");
            BeginSample(commands, "Summit.GpuPrimitives/ReduceSum");
            candidateRunner.Reduce(commands, input, output, count);
            EndSample(commands, "Summit.GpuPrimitives/ReduceSum");
        }

        public void RecordHistogram(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer histogram,
            int count,
            int binCount,
            int keyShift = 0,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto,
            bool clearOutput = true)
        {
            ValidateCommandsAndCount(commands, count);
            if (binCount < 1 || binCount > MaxElementCount ||
                (binCount & (binCount - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binCount),
                    $"Bin count must be a power of two in [1, {MaxElementCount}].");
            }
            if (keyShift < 0 || keyShift > 31)
            {
                throw new ArgumentOutOfRangeException(nameof(keyShift));
            }
            ValidateUintBuffer(keys, count, nameof(keys));
            ValidateUintBuffer(histogram, binCount, nameof(histogram));

            // The wave histogram intentionally specializes for small radix
            // domains. Auto falls back instead of rejecting a large domain.
            GpuPrimitiveBackend resolved =
                backend == GpuPrimitiveBackend.Auto && binCount > 16
                    ? GpuPrimitiveBackend.Portable
                    : ResolveBackend(backend);
            if (resolved == GpuPrimitiveBackend.WaveOps && binCount > 16)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binCount),
                    "The WaveOps histogram supports at most 16 bins.");
            }

            BeginSample(commands, "Summit.GpuPrimitives/Histogram");
            if (clearOutput)
            {
                RecordClear(commands, histogram, binCount);
            }
            if (count > 0)
            {
                ComputeShader shader = resolved ==
                    GpuPrimitiveBackend.WaveOps
                    ? RequireWaveShader()
                    : portableShader;
                int kernel = resolved == GpuPrimitiveBackend.WaveOps
                    ? histogramWave16Kernel
                    : histogramAtomicKernel;
                commands.SetComputeIntParam(shader, CountId, count);
                commands.SetComputeIntParam(shader, BinCountId, binCount);
                commands.SetComputeIntParam(shader, KeyShiftId, keyShift);
                commands.SetComputeBufferParam(
                    shader,
                    kernel,
                    KeysId,
                    keys);
                commands.SetComputeBufferParam(
                    shader,
                    kernel,
                    HistogramId,
                    histogram);
                commands.DispatchCompute(
                    shader,
                    kernel,
                    DivideRoundUp(count, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, "Summit.GpuPrimitives/Histogram");
        }

        public void RecordStableCompaction(
            CommandBuffer commands,
            GraphicsBuffer values,
            GraphicsBuffer predicates,
            GraphicsBuffer output,
            GraphicsBuffer outputCount,
            int count,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto)
        {
            ValidateCompactionArguments(
                commands,
                values,
                predicates,
                output,
                outputCount,
                count);
            BeginSample(commands, "Summit.GpuPrimitives/StableCompaction");
            RecordClear(commands, outputCount, 1);
            if (count > 0)
            {
                commands.SetComputeIntParam(portableShader, CountId, count);
                commands.SetComputeBufferParam(
                    portableShader,
                    normalizePredicatesKernel,
                    PredicatesId,
                    predicates);
                commands.SetComputeBufferParam(
                    portableShader,
                    normalizePredicatesKernel,
                    NormalizedPredicatesId,
                    normalizedPredicates);
                commands.DispatchCompute(
                    portableShader,
                    normalizePredicatesKernel,
                    DivideRoundUp(count, ThreadGroupSize),
                    1,
                    1);

                RecordExclusiveScanInternal(
                    commands,
                    normalizedPredicates,
                    scannedPredicates,
                    count,
                    candidateRunner != null ? backend : ResolveBackend(backend));

                commands.SetComputeIntParam(portableShader, CountId, count);
                commands.SetComputeBufferParam(
                    portableShader,
                    stableCompactScatterKernel,
                    ValuesId,
                    values);
                commands.SetComputeBufferParam(
                    portableShader,
                    stableCompactScatterKernel,
                    PredicatesId,
                    normalizedPredicates);
                commands.SetComputeBufferParam(
                    portableShader,
                    stableCompactScatterKernel,
                    ScannedPredicatesId,
                    scannedPredicates);
                commands.SetComputeBufferParam(
                    portableShader,
                    stableCompactScatterKernel,
                    OutputId,
                    output);
                commands.SetComputeBufferParam(
                    portableShader,
                    stableCompactScatterKernel,
                    OutputCountId,
                    outputCount);
                commands.DispatchCompute(
                    portableShader,
                    stableCompactScatterKernel,
                    DivideRoundUp(count, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, "Summit.GpuPrimitives/StableCompaction");
        }

        public void RecordAppendCompaction(
            CommandBuffer commands,
            GraphicsBuffer values,
            GraphicsBuffer predicates,
            GraphicsBuffer output,
            GraphicsBuffer outputCount,
            int count,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto)
        {
            ValidateCompactionArguments(
                commands,
                values,
                predicates,
                output,
                outputCount,
                count);
            GpuPrimitiveBackend resolved = ResolveBackend(backend);
            BeginSample(commands, "Summit.GpuPrimitives/AppendCompaction");
            RecordClear(commands, outputCount, 1);
            if (count > 0)
            {
                ComputeShader shader = resolved ==
                    GpuPrimitiveBackend.WaveOps
                    ? RequireWaveShader()
                    : portableShader;
                int kernel = resolved == GpuPrimitiveBackend.WaveOps
                    ? appendCompactWaveKernel
                    : appendCompactAtomicKernel;
                commands.SetComputeIntParam(shader, CountId, count);
                commands.SetComputeBufferParam(
                    shader,
                    kernel,
                    ValuesId,
                    values);
                commands.SetComputeBufferParam(
                    shader,
                    kernel,
                    PredicatesId,
                    predicates);
                commands.SetComputeBufferParam(
                    shader,
                    kernel,
                    OutputId,
                    output);
                commands.SetComputeBufferParam(
                    shader,
                    kernel,
                    OutputCountId,
                    outputCount);
                commands.DispatchCompute(
                    shader,
                    kernel,
                    DivideRoundUp(count, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, "Summit.GpuPrimitives/AppendCompaction");
        }

        /// <summary>
        /// Sorts unsigned 32-bit keys stably. The final result always lands in
        /// keysOut and valuesOut after eight 4-bit passes.
        /// </summary>
        public void RecordRadixSort32(
            CommandBuffer commands,
            GraphicsBuffer keysIn,
            GraphicsBuffer valuesIn,
            GraphicsBuffer keysOut,
            GraphicsBuffer valuesOut,
            int count,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto)
        {
            RecordRadixSortKeyBitsInternal(
                commands,
                keysIn,
                valuesIn,
                keysOut,
                valuesOut,
                count,
                32,
                backend,
                "Summit.GpuPrimitives/RadixSort32");
        }

        /// <summary>
        /// Stably sorts unsigned keys whose significant bits are restricted to
        /// the low <paramref name="keyBitCount"/> bits. The final result always
        /// lands in keysOut and valuesOut after the minimum number of 4-bit
        /// passes.
        /// </summary>
        /// <remarks>
        /// Every key must be less than 2^keyBitCount (or keyBitCount must be
        /// 32). This method does not validate key contents because Record
        /// performs no readback. Callers handling untrusted keys must sanitize
        /// them or use the full 32-bit path.
        /// </remarks>
        public void RecordRadixSortKeyBits(
            CommandBuffer commands,
            GraphicsBuffer keysIn,
            GraphicsBuffer valuesIn,
            GraphicsBuffer keysOut,
            GraphicsBuffer valuesOut,
            int count,
            int keyBitCount,
            GpuPrimitiveBackend backend = GpuPrimitiveBackend.Auto)
        {
            RecordRadixSortKeyBitsInternal(
                commands,
                keysIn,
                valuesIn,
                keysOut,
                valuesOut,
                count,
                keyBitCount,
                backend,
                "Summit.GpuPrimitives/RadixSortKeyBits");
        }

        private void RecordRadixSortKeyBitsInternal(
            CommandBuffer commands,
            GraphicsBuffer keysIn,
            GraphicsBuffer valuesIn,
            GraphicsBuffer keysOut,
            GraphicsBuffer valuesOut,
            int count,
            int keyBitCount,
            GpuPrimitiveBackend backend,
            string sampleName)
        {
            ValidateCommandsAndCount(commands, count);
            if (keyBitCount < 1 || keyBitCount > 32)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyBitCount),
                    "Key bit count must be in [1, 32].");
            }
            ValidateUintBuffer(keysIn, count, nameof(keysIn));
            ValidateUintBuffer(valuesIn, count, nameof(valuesIn));
            ValidateUintBuffer(keysOut, count, nameof(keysOut));
            ValidateUintBuffer(valuesOut, count, nameof(valuesOut));
            if (count > 0 &&
                (keysIn == keysOut ||
                 keysIn == valuesOut ||
                 valuesIn == keysOut ||
                 valuesIn == valuesOut ||
                 keysOut == valuesOut))
            {
                throw new ArgumentException(
                    "Non-empty radix sort requires both writable outputs to " +
                    "be distinct from each other and from the read-only " +
                    "inputs. The two read-only inputs may alias.");
            }

            if (candidateRunner != null && backend == GpuPrimitiveBackend.Auto)
            {
                BeginSample(commands, sampleName);
                candidateRunner.Sort(commands, keysIn, valuesIn, keysOut, valuesOut, count, keyBitCount);
                EndSample(commands, sampleName);
                return;
            }
            GpuPrimitiveBackend resolved = ResolveBackend(backend);
            int passCount = DivideRoundUp(
                keyBitCount,
                RadixBitsPerPass);
            int finalParity = (passCount - 1) & 1;
            BeginSample(commands, sampleName);
            if (count > 0)
            {
                int groupCount = DivideRoundUp(count, ThreadGroupSize);
                GraphicsBuffer sourceKeys = keysIn;
                GraphicsBuffer sourceValues = valuesIn;

                for (int pass = 0; pass < passCount; pass++)
                {
                    bool writeFinalBuffer =
                        (pass & 1) == finalParity;
                    GraphicsBuffer destinationKeys = writeFinalBuffer
                        ? keysOut
                        : radixKeysScratch;
                    GraphicsBuffer destinationValues = writeFinalBuffer
                        ? valuesOut
                        : radixValuesScratch;
                    RecordRadixPass(
                        commands,
                        sourceKeys,
                        sourceValues,
                        destinationKeys,
                        destinationValues,
                        count,
                        groupCount,
                        pass * RadixBitsPerPass,
                        resolved);
                    sourceKeys = destinationKeys;
                    sourceValues = destinationValues;
                }
            }
            EndSample(commands, sampleName);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            candidateRunner?.Dispose();
            ReleaseScratch();
        }

        private void RecordExclusiveScanInternal(
            CommandBuffer commands,
            GraphicsBuffer input,
            GraphicsBuffer output,
            int count,
            GpuPrimitiveBackend backend)
        {
            if (candidateRunner != null && backend == GpuPrimitiveBackend.Auto)
            {
                candidateRunner.Scan(commands, input, output, count);
                return;
            }
            backend = ResolveBackend(backend);
            ComputeShader scanShader = backend ==
                GpuPrimitiveBackend.WaveOps
                ? RequireWaveShader()
                : portableShader;
            int scanKernel = backend == GpuPrimitiveBackend.WaveOps
                ? waveScanKernel
                : portableScanKernel;
            GraphicsBuffer levelInput = input;
            GraphicsBuffer levelOutput = output;
            int levelCount = count;
            int lastLevel = 0;

            while (true)
            {
                int groupCount =
                    DivideRoundUp(levelCount, ThreadGroupSize);
                commands.SetComputeIntParam(
                    scanShader,
                    CountId,
                    levelCount);
                commands.SetComputeBufferParam(
                    scanShader,
                    scanKernel,
                    ScanInputId,
                    levelInput);
                commands.SetComputeBufferParam(
                    scanShader,
                    scanKernel,
                    ScanOutputId,
                    levelOutput);
                commands.SetComputeBufferParam(
                    scanShader,
                    scanKernel,
                    BlockSumsId,
                    scanBlockSums[lastLevel]);
                commands.DispatchCompute(
                    scanShader,
                    scanKernel,
                    groupCount,
                    1,
                    1);

                if (groupCount <= 1)
                {
                    break;
                }
                levelInput = scanBlockSums[lastLevel];
                levelOutput = scanBlockOffsets[lastLevel];
                levelCount = groupCount;
                lastLevel++;
            }

            for (int level = lastLevel - 1; level >= 1; level--)
            {
                RecordAddBlockOffsets(
                    commands,
                    scanBlockOffsets[level - 1],
                    scanBlockOffsets[level],
                    ReduceCount(count, level));
            }
            if (lastLevel > 0)
            {
                RecordAddBlockOffsets(
                    commands,
                    output,
                    scanBlockOffsets[0],
                    count);
            }
        }

        private void RecordAddBlockOffsets(
            CommandBuffer commands,
            GraphicsBuffer target,
            GraphicsBuffer blockOffsets,
            int count)
        {
            commands.SetComputeIntParam(portableShader, CountId, count);
            commands.SetComputeBufferParam(
                portableShader,
                addBlockOffsetsKernel,
                ScanOutputId,
                target);
            commands.SetComputeBufferParam(
                portableShader,
                addBlockOffsetsKernel,
                BlockOffsetsId,
                blockOffsets);
            commands.DispatchCompute(
                portableShader,
                addBlockOffsetsKernel,
                DivideRoundUp(count, ThreadGroupSize),
                1,
                1);
        }

        private void RecordRadixPass(
            CommandBuffer commands,
            GraphicsBuffer sourceKeys,
            GraphicsBuffer sourceValues,
            GraphicsBuffer destinationKeys,
            GraphicsBuffer destinationValues,
            int count,
            int groupCount,
            int shift,
            GpuPrimitiveBackend backend)
        {
            ComputeShader parallelShader = backend ==
                GpuPrimitiveBackend.WaveOps
                ? RequireWaveShader()
                : portableShader;
            int histogramKernel = backend ==
                GpuPrimitiveBackend.WaveOps
                ? radixHistogramWaveKernel
                : radixHistogramPortableKernel;
            int scatterKernel = backend ==
                GpuPrimitiveBackend.WaveOps
                ? radixScatterWaveKernel
                : radixScatterPortableKernel;

            commands.SetComputeIntParam(parallelShader, CountId, count);
            commands.SetComputeIntParam(
                parallelShader,
                RadixShiftId,
                shift);
            commands.SetComputeBufferParam(
                parallelShader,
                histogramKernel,
                RadixKeysInId,
                sourceKeys);
            commands.SetComputeBufferParam(
                parallelShader,
                histogramKernel,
                RadixGroupHistogramsId,
                radixGroupHistograms);
            commands.DispatchCompute(
                parallelShader,
                histogramKernel,
                groupCount,
                1,
                1);

            commands.SetComputeIntParam(
                portableShader,
                RadixGroupCountId,
                groupCount);
            commands.SetComputeBufferParam(
                portableShader,
                radixScanOffsetsKernel,
                RadixGroupHistogramsId,
                radixGroupHistograms);
            commands.SetComputeBufferParam(
                portableShader,
                radixScanOffsetsKernel,
                RadixGroupOffsetsId,
                radixGroupOffsets);
            commands.DispatchCompute(
                portableShader,
                radixScanOffsetsKernel,
                1,
                1,
                1);

            commands.SetComputeIntParam(parallelShader, CountId, count);
            commands.SetComputeIntParam(
                parallelShader,
                RadixShiftId,
                shift);
            commands.SetComputeBufferParam(
                parallelShader,
                scatterKernel,
                RadixKeysInId,
                sourceKeys);
            commands.SetComputeBufferParam(
                parallelShader,
                scatterKernel,
                RadixValuesInId,
                sourceValues);
            commands.SetComputeBufferParam(
                parallelShader,
                scatterKernel,
                RadixKeysOutId,
                destinationKeys);
            commands.SetComputeBufferParam(
                parallelShader,
                scatterKernel,
                RadixValuesOutId,
                destinationValues);
            commands.SetComputeBufferParam(
                parallelShader,
                scatterKernel,
                RadixGroupOffsetsId,
                radixGroupOffsets);
            commands.DispatchCompute(
                parallelShader,
                scatterKernel,
                groupCount,
                1,
                1);
        }

        private void RecordClear(
            CommandBuffer commands,
            GraphicsBuffer buffer,
            int count)
        {
            commands.SetComputeIntParam(
                portableShader,
                ClearCountId,
                count);
            commands.SetComputeBufferParam(
                portableShader,
                clearUintKernel,
                ClearBufferId,
                buffer);
            commands.DispatchCompute(
                portableShader,
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

        private void ValidateCompactionArguments(
            CommandBuffer commands,
            GraphicsBuffer values,
            GraphicsBuffer predicates,
            GraphicsBuffer output,
            GraphicsBuffer outputCount,
            int count)
        {
            ValidateCommandsAndCount(commands, count);
            ValidateUintBuffer(values, count, nameof(values));
            ValidateUintBuffer(predicates, count, nameof(predicates));
            ValidateUintBuffer(output, count, nameof(output));
            ValidateUintBuffer(outputCount, 1, nameof(outputCount));
        }

        private void ValidateCommandsAndCount(
            CommandBuffer commands,
            int count)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (count < 0 || count > Capacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    $"Count must be in [0, {Capacity}].");
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
            if (buffer.stride != sizeof(uint) ||
                buffer.count < requiredCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a uint GraphicsBuffer with " +
                    $"at least {requiredCount} elements.",
                    parameterName);
            }
        }

        private GpuPrimitiveBackend ResolveBackend(
            GpuPrimitiveBackend backend)
        {
            if (backend == GpuPrimitiveBackend.Auto)
            {
                return SupportsWaveOperations && waveShader != null
                    ? GpuPrimitiveBackend.WaveOps
                    : GpuPrimitiveBackend.Portable;
            }
            if (backend != GpuPrimitiveBackend.Portable &&
                backend != GpuPrimitiveBackend.WaveOps)
            {
                throw new ArgumentOutOfRangeException(nameof(backend));
            }
            if (backend == GpuPrimitiveBackend.WaveOps)
            {
                RequireWaveShader();
            }
            return backend;
        }

        private ComputeShader RequireWaveShader()
        {
            if (waveShader == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{WaveResourcePath}' could " +
                    "not be loaded. Use the Portable backend.");
            }
            return waveShader;
        }

        private void AllocateScratch(int capacity)
        {
            Capacity = capacity;
            long scratchWords = 0L;
            int levelCapacity = capacity;
            int levelCount = 0;
            do
            {
                levelCapacity =
                    DivideRoundUp(levelCapacity, ThreadGroupSize);
                levelCount++;
            }
            while (levelCapacity > 1);

            scanBlockSums = new GraphicsBuffer[levelCount];
            scanBlockOffsets = new GraphicsBuffer[levelCount];
            levelCapacity = capacity;
            for (int level = 0; level < levelCount; level++)
            {
                int groupCapacity =
                    DivideRoundUp(levelCapacity, ThreadGroupSize);
                scanBlockSums[level] = CreateUintBuffer(
                    groupCapacity,
                    $"GPU Primitives Scan Sums L{level}");
                scratchWords += groupCapacity;
                if (groupCapacity > 1)
                {
                    scanBlockOffsets[level] = CreateUintBuffer(
                        groupCapacity,
                        $"GPU Primitives Scan Offsets L{level}");
                    scratchWords += groupCapacity;
                }
                levelCapacity = groupCapacity;
            }

            normalizedPredicates = CreateUintBuffer(
                capacity,
                "GPU Primitives Normalized Predicates");
            scannedPredicates = CreateUintBuffer(
                capacity,
                "GPU Primitives Scanned Predicates");
            radixKeysScratch = CreateUintBuffer(
                capacity,
                "GPU Primitives Radix Keys Scratch");
            radixValuesScratch = CreateUintBuffer(
                capacity,
                "GPU Primitives Radix Values Scratch");
            int radixGroupCapacity =
                DivideRoundUp(capacity, ThreadGroupSize);
            scratchWords += 4L * capacity;
            scratchWords +=
                2L * radixGroupCapacity * RadixBinCount;
            radixGroupHistograms = CreateUintBuffer(
                radixGroupCapacity * RadixBinCount,
                "GPU Primitives Radix Group Histograms");
            radixGroupOffsets = CreateUintBuffer(
                radixGroupCapacity * RadixBinCount,
                "GPU Primitives Radix Group Offsets");
            legacyScratchBytes = checked(scratchWords * sizeof(uint));
        }

        private void ReleaseScratch()
        {
            DisposeBuffers(scanBlockSums);
            DisposeBuffers(scanBlockOffsets);
            scanBlockSums = null;
            scanBlockOffsets = null;
            DisposeBuffer(ref normalizedPredicates);
            DisposeBuffer(ref scannedPredicates);
            DisposeBuffer(ref radixKeysScratch);
            DisposeBuffer(ref radixValuesScratch);
            DisposeBuffer(ref radixGroupHistograms);
            DisposeBuffer(ref radixGroupOffsets);
            Capacity = 0;
            legacyScratchBytes = 0L;
        }

        private static GraphicsBuffer CreateUintBuffer(
            int count,
            string name)
        {
            GraphicsBuffer buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Math.Max(1, count),
                sizeof(uint));
            buffer.name = name;
            return buffer;
        }

        private static void DisposeBuffers(GraphicsBuffer[] buffers)
        {
            if (buffers == null)
            {
                return;
            }
            for (int index = 0; index < buffers.Length; index++)
            {
                buffers[index]?.Dispose();
                buffers[index] = null;
            }
        }

        private static void DisposeBuffer(ref GraphicsBuffer buffer)
        {
            buffer?.Dispose();
            buffer = null;
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private static int ReduceCount(int count, int levels)
        {
            for (int level = 0; level < levels; level++)
            {
                count = DivideRoundUp(count, ThreadGroupSize);
            }
            return count;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GpuPrimitives));
            }
        }
    }
}
