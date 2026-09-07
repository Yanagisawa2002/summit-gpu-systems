using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Summit.GpuPrimitives;

internal sealed class GpuPrimitiveBenchmarkCase
{
    public string Id;
    public string Operation;
    public string Variant;
    public string Marker;
    public GpuPrimitiveBackend Backend;
    public long LogicalBytesPerDispatch;
}

internal enum GpuPrimitiveValidationPhase
{
    Warmup,
    Final
}

internal sealed class GpuPrimitiveValidationResult
{
    public string Phase;
    public string CaseId;
    public string Operation;
    public string Variant;
    public bool Passed;
    public string Message;
    public long ReadbackBytes;
    public string ResultHash;
}

/// <summary>
/// The only benchmark class coupled to the reusable primitive API. The controller deliberately
/// depends on this adapter instead of knowing about shaders, buffers, or primitive entry points.
/// </summary>
internal sealed class GpuPrimitiveBenchmarkCoreAdapter : IDisposable
{
    // The current wave histogram contract supports up to 16 bins. Keeping the A/B domain at 16
    // compares the portable and wave implementations without silently changing algorithms.
    private const int HistogramBinCount = 16;
    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;

    private readonly int count;
    private readonly int dispatchesPerFrame;
    private readonly GpuPrimitives primitives;
    private readonly Dictionary<string, GpuPrimitives> candidates = new Dictionary<string, GpuPrimitives>();
    private readonly List<string> capabilityRows = new List<string>();
    private readonly int keyBits;
    private readonly uint reductionExpected;
    private readonly GraphicsBuffer input;
    private readonly GraphicsBuffer values;
    private readonly GraphicsBuffer predicates;
    private readonly GraphicsBuffer output;
    private readonly GraphicsBuffer auxiliary;
    private readonly GraphicsBuffer radixValuesOutput;
    private readonly GraphicsBuffer outputCount;
    private readonly GraphicsBuffer histogram;
    private readonly uint[] scanExpected;
    private readonly uint[] histogramExpected;
    private readonly uint[] compactExpected;
    private readonly uint[] radixKeysExpected;
    private readonly uint[] radixValuesExpected;
    private readonly List<GpuPrimitiveBenchmarkCase> cases;

    private PendingValidation pendingValidation;
    private bool disposed;

    public GpuPrimitiveBenchmarkCoreAdapter(
        int elementCount,
        int seed,
        int repetitionsPerFrame,
        string operationFilter,
        string backendFilter,
        string distribution = "uniform",
        int keyBitCount = 32)
    {
        count = elementCount;
        if (keyBitCount < 1 || keyBitCount > 32) throw new ArgumentOutOfRangeException(nameof(keyBitCount));
        keyBits = keyBitCount;
        if (!new[] { "uniform", "duplicates", "single-bin", "ascending", "descending" }.Contains(distribution))
            throw new ArgumentException("Unknown distribution: " + distribution);
        dispatchesPerFrame = repetitionsPerFrame;

        uint[] inputData = new uint[count];
        uint[] valueData = new uint[count];
        uint[] predicateData = new uint[count];
        scanExpected = new uint[count];
        histogramExpected = new uint[HistogramBinCount];

        List<uint> compactValues = new List<uint>(count);
        uint prefix = 0u;
        for (int i = 0; i < count; i++)
        {
            uint index = (uint)i;
            uint hashed = Mix(index ^ (uint)seed);
            uint key = CreateKey(i, count, seed, distribution, keyBits);
            uint value = index ^ unchecked((uint)seed * 2246822519u);
            uint predicate = (hashed & 3u) == 0u ? 0u : 1u;
            uint scanValue = hashed & 7u;

            inputData[i] = scanValue;
            valueData[i] = value;
            predicateData[i] = predicate;
            scanExpected[i] = prefix;
            prefix = unchecked(prefix + scanValue);
            histogramExpected[key & (HistogramBinCount - 1)]++;
            if (predicate != 0u)
            {
                compactValues.Add(value);
            }
        }
        compactExpected = compactValues.ToArray();
        reductionExpected = prefix;

        uint[] radixKeys = new uint[count];
        for (int i = 0; i < count; i++)
        {
            radixKeys[i] = CreateKey(i, count, seed, distribution, keyBits);
        }
        radixKeysExpected = (uint[])radixKeys.Clone();
        radixValuesExpected = (uint[])valueData.Clone();
        int[] stableOrder = Enumerable.Range(0, count).OrderBy(i => radixKeys[i]).ToArray();
        radixKeysExpected = stableOrder.Select(i => radixKeys[i]).ToArray();
        radixValuesExpected = stableOrder.Select(i => valueData[i]).ToArray();

        input = CreateStructuredBuffer(count, "GpuPrimitiveBenchmark.Input");
        values = CreateStructuredBuffer(count, "GpuPrimitiveBenchmark.Values");
        predicates = CreateStructuredBuffer(count, "GpuPrimitiveBenchmark.Predicates");
        output = CreateStructuredBuffer(count, "GpuPrimitiveBenchmark.Output");
        auxiliary = CreateStructuredBuffer(count, "GpuPrimitiveBenchmark.Auxiliary");
        radixValuesOutput = CreateStructuredBuffer(
            count,
            "GpuPrimitiveBenchmark.RadixValuesOutput");
        outputCount = CreateStructuredBuffer(1, "GpuPrimitiveBenchmark.OutputCount");
        histogram = CreateStructuredBuffer(HistogramBinCount, "GpuPrimitiveBenchmark.Histogram");

        // Scan and radix sort require different primary inputs. Radix input is held in auxiliary;
        // the scan input stays in input. Values are shared by both compaction and key/value sort.
        input.SetData(inputData);
        auxiliary.SetData(radixKeys);
        values.SetData(valueData);
        predicates.SetData(predicateData);
        outputCount.SetData(new uint[1]);
        histogram.SetData(new uint[HistogramBinCount]);

        ComputeShader portableShader =
            Resources.Load<ComputeShader>("GpuPrimitives/GpuPrimitivesPortable");
        ComputeShader waveShader =
            Resources.Load<ComputeShader>("GpuPrimitives/GpuPrimitivesWave");
        primitives = new GpuPrimitives(count, portableShader, waveShader);
        primitives.EnsureCapacity(count);

        cases = BuildCases(operationFilter, backendFilter);
        if (cases.Count == 0)
        {
            throw new InvalidOperationException(
                "No GPU primitive cases matched the requested operation/backend filters.");
        }
    }

    public IReadOnlyList<GpuPrimitiveBenchmarkCase> Cases => cases;

    public string ImplementationName => typeof(GpuPrimitives).FullName;

    public int ElementCount => count;

    public int DispatchesPerFrame => dispatchesPerFrame;

    public bool SupportsWaveOperations => GpuPrimitives.SupportsWaveOperations;

    public static int MaxElementCount => GpuPrimitives.MaxElementCount;

    public long ExternalBufferBytes =>
        ((long)count * 6L + HistogramBinCount + 1L) * sizeof(uint);

    public long PrimitiveScratchBytes => primitives.ScratchBytes + candidates.Values.Sum(p => p.ScratchBytes);

    public long ResidentBytes => ExternalBufferBytes + PrimitiveScratchBytes;

    public CommandBuffer CreateMeasurementCommandBuffer(
        GpuPrimitiveBenchmarkCase benchmarkCase)
    {
        CommandBuffer commands = new CommandBuffer
        {
            name = benchmarkCase.Marker
        };
        RecordMeasurement(commands, benchmarkCase);
        return commands;
    }

    public void RecordMeasurement(
        CommandBuffer commands,
        GpuPrimitiveBenchmarkCase benchmarkCase)
    {
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        if (benchmarkCase == null)
        {
            throw new ArgumentNullException(nameof(benchmarkCase));
        }

        commands.BeginSample(benchmarkCase.Marker);
        for (int dispatch = 0; dispatch < dispatchesPerFrame; dispatch++)
        {
            Record(commands, benchmarkCase);
        }
        commands.EndSample(benchmarkCase.Marker);
    }

    public void BeginValidation(
        GpuPrimitiveBenchmarkCase benchmarkCase,
        GpuPrimitiveValidationPhase phase)
    {
        if (pendingValidation != null)
        {
            throw new InvalidOperationException("A primitive validation readback is already pending.");
        }

        CommandBuffer commands = new CommandBuffer
        {
            name = benchmarkCase.Marker + "/Validation"
        };
        commands.BeginSample(benchmarkCase.Marker + "/Validation");
        Record(commands, benchmarkCase);
        commands.EndSample(benchmarkCase.Marker + "/Validation");
        Graphics.ExecuteCommandBuffer(commands);
        commands.Dispose();

        PendingValidation pending = new PendingValidation
        {
            BenchmarkCase = benchmarkCase,
            Phase = phase,
            Requests = new List<AsyncGPUReadbackRequest>(2)
        };

        switch (benchmarkCase.Operation)
        {
            case "reduce-sum":
                pending.Requests.Add(AsyncGPUReadback.Request(outputCount));
                pending.ReadbackBytes = 4;
                break;
            case "exclusive-scan":
                pending.Requests.Add(AsyncGPUReadback.Request(output));
                pending.ReadbackBytes = (long)count * sizeof(uint);
                break;
            case "histogram-16":
                pending.Requests.Add(AsyncGPUReadback.Request(histogram));
                pending.ReadbackBytes = (long)HistogramBinCount * sizeof(uint);
                break;
            case "stable-compaction":
            case "append-compaction":
                pending.Requests.Add(AsyncGPUReadback.Request(outputCount));
                pending.Requests.Add(AsyncGPUReadback.Request(output));
                pending.ReadbackBytes = ((long)count + 1L) * sizeof(uint);
                break;
            case "radix-sort-32":
                pending.Requests.Add(AsyncGPUReadback.Request(output));
                pending.Requests.Add(AsyncGPUReadback.Request(radixValuesOutput));
                pending.ReadbackBytes = (long)count * sizeof(uint) * 2L;
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported validation operation: " + benchmarkCase.Operation);
        }

        pendingValidation = pending;
    }

    public bool TryCompleteValidation(out GpuPrimitiveValidationResult result)
    {
        result = null;
        if (pendingValidation == null)
        {
            return false;
        }

        for (int i = 0; i < pendingValidation.Requests.Count; i++)
        {
            if (!pendingValidation.Requests[i].done)
            {
                return false;
            }
        }

        PendingValidation completed = pendingValidation;
        pendingValidation = null;
        GpuPrimitiveValidationResult validation = new GpuPrimitiveValidationResult
        {
            Phase = completed.Phase.ToString().ToLowerInvariant(),
            CaseId = completed.BenchmarkCase.Id,
            Operation = completed.BenchmarkCase.Operation,
            Variant = completed.BenchmarkCase.Variant,
            ReadbackBytes = completed.ReadbackBytes
        };

        for (int i = 0; i < completed.Requests.Count; i++)
        {
            if (completed.Requests[i].hasError)
            {
                validation.Passed = false;
                validation.Message = "Async GPU readback failed.";
                validation.ResultHash = "unavailable";
                result = validation;
                return true;
            }
        }

        try
        {
            ValidateData(completed, validation);
        }
        catch (Exception exception)
        {
            validation.Passed = false;
            validation.Message = exception.GetType().Name + ": " + exception.Message;
            validation.ResultHash = "unavailable";
        }

        result = validation;
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        primitives.Dispose();
        foreach (var p in candidates.Values) p.Dispose();
        input.Dispose();
        values.Dispose();
        predicates.Dispose();
        output.Dispose();
        auxiliary.Dispose();
        radixValuesOutput.Dispose();
        outputCount.Dispose();
        histogram.Dispose();
    }

    private List<GpuPrimitiveBenchmarkCase> BuildCases(
        string operationFilter,
        string backendFilter)
    {
        HashSet<string> requestedOperations = ParseFilter(
            operationFilter,
            new[]
            {
                "exclusive-scan",
                "histogram-16",
                "stable-compaction",
                "append-compaction",
                "radix-sort-32"
            });
        HashSet<string> requestedBackends = ParseFilter(
            backendFilter,
            new[] { "portable", "wave-ops" });

        List<GpuPrimitiveBenchmarkCase> result = new List<GpuPrimitiveBenchmarkCase>();
        foreach (string operation in requestedOperations)
        {
            if (operation == "reduce-sum") continue;
            if (requestedBackends.Contains("portable"))
            {
                result.Add(CreateCase(operation, GpuPrimitiveBackend.Portable, "portable"));
            }
            if (requestedBackends.Contains("wave-ops") && GpuPrimitives.SupportsWaveOperations)
            {
                result.Add(CreateCase(operation, GpuPrimitiveBackend.WaveOps, "wave-ops"));
            }
            if (requestedBackends.Contains("auto"))
            {
                result.Add(CreateCase(operation, GpuPrimitiveBackend.Auto, "auto"));
            }
        }
        foreach (var c in GpuPrimitiveCandidates.All)
        {
            if (!requestedBackends.Contains("candidates") && !requestedBackends.Contains(c.Id)) continue;
            bool supported = GpuPrimitiveCandidates.TryProbeWaveSize(c.Id, out int observed, out string reason);
            capabilityRows.Add($"{c.Id},{supported},{observed},{reason}");
            if (!supported)
            {
                if (requestedBackends.Contains(c.Id)) throw new NotSupportedException(c.Id + ": " + reason);
                continue;
            }
            var instance = new GpuPrimitives(count, candidateId: c.Id);
            candidates.Add(c.Id, instance);
            foreach (string op in requestedOperations)
                if (op == "exclusive-scan" || op == "stable-compaction" || op == "radix-sort-32" || op == "reduce-sum")
                    result.Add(CreateCase(op, GpuPrimitiveBackend.Auto, c.Id));
        }
        foreach (string id in requestedBackends)
            if (id != "portable" && id != "wave-ops" && id != "auto" && id != "candidates" && !GpuPrimitiveCandidates.All.Any(c => c.Id == id))
                throw new ArgumentException("Unknown backend/candidate: " + id);
        result.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return result;
    }

    private GpuPrimitiveBenchmarkCase CreateCase(
        string operation,
        GpuPrimitiveBackend backend,
        string variant)
    {
        long logicalBytes;
        switch (operation)
        {
            case "reduce-sum":
                logicalBytes = (long)count * 4 + 4;
                break;
            case "exclusive-scan":
                logicalBytes = (long)count * sizeof(uint) * 2L;
                break;
            case "histogram-16":
                logicalBytes = (long)count * sizeof(uint) +
                    (long)HistogramBinCount * sizeof(uint);
                break;
            case "stable-compaction":
            case "append-compaction":
                logicalBytes = (long)count * sizeof(uint) * 3L + sizeof(uint);
                break;
            case "radix-sort-32":
                logicalBytes = (long)count * sizeof(uint) * 4L *
                    (candidates.ContainsKey(variant) ? GpuPrimitiveCandidates.Get(variant).RadixPasses(keyBits) : (keyBits + 3) / 4);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation,
                    "Unknown GPU primitive operation.");
        }

        string markerOperation = operation.Replace("-", string.Empty);
        return new GpuPrimitiveBenchmarkCase
        {
            Id = operation + "/" + variant,
            Operation = operation,
            Variant = variant,
            Backend = backend,
            Marker = "GPU.Primitives/" + markerOperation + "/" + variant,
            LogicalBytesPerDispatch = logicalBytes
        };
    }

    private void Record(CommandBuffer commands, GpuPrimitiveBenchmarkCase benchmarkCase)
    {
        GpuPrimitives primitives = candidates.TryGetValue(benchmarkCase.Variant, out var selected) ? selected : this.primitives;
        switch (benchmarkCase.Operation)
        {
            case "reduce-sum":
                primitives.RecordReduceSum(commands, input, outputCount, count);
                break;
            case "exclusive-scan":
                primitives.RecordExclusiveScan(
                    commands,
                    input,
                    output,
                    count,
                    benchmarkCase.Backend);
                break;
            case "histogram-16":
                primitives.RecordHistogram(
                    commands,
                    auxiliary,
                    histogram,
                    count,
                    HistogramBinCount,
                    0,
                    benchmarkCase.Backend,
                    true);
                break;
            case "stable-compaction":
                primitives.RecordStableCompaction(
                    commands,
                    values,
                    predicates,
                    output,
                    outputCount,
                    count,
                    benchmarkCase.Backend);
                break;
            case "append-compaction":
                primitives.RecordAppendCompaction(
                    commands,
                    values,
                    predicates,
                    output,
                    outputCount,
                    count,
                    benchmarkCase.Backend);
                break;
            case "radix-sort-32":
                primitives.RecordRadixSortKeyBits(
                    commands,
                    auxiliary,
                    values,
                    output,
                    radixValuesOutput,
                    count,
                    keyBits,
                    benchmarkCase.Backend);
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported primitive operation: " + benchmarkCase.Operation);
        }
    }

    private void ValidateData(
        PendingValidation completed,
        GpuPrimitiveValidationResult validation)
    {
        switch (completed.BenchmarkCase.Operation)
        {
            case "reduce-sum":
            {
                var actual = completed.Requests[0].GetData<uint>();
                validation.Passed = actual[0] == reductionExpected;
                validation.Message = validation.Passed ? "Sum matches independent CPU oracle." : "Reduction mismatch.";
                validation.ResultHash = actual[0].ToString("X8");
                break;
            }
            case "exclusive-scan":
            {
                NativeArray<uint> actual = completed.Requests[0].GetData<uint>();
                int mismatch = FindMismatch(actual, scanExpected, scanExpected.Length);
                validation.Passed = mismatch < 0;
                validation.Message = mismatch < 0
                    ? "Exclusive scan matches the CPU oracle."
                    : $"Exclusive scan mismatch at {mismatch}: GPU={actual[mismatch]}, CPU={scanExpected[mismatch]}.";
                validation.ResultHash = Hash(actual, actual.Length).ToString("X8");
                break;
            }
            case "histogram-16":
            {
                NativeArray<uint> actual = completed.Requests[0].GetData<uint>();
                int mismatch = FindMismatch(actual, histogramExpected, histogramExpected.Length);
                validation.Passed = mismatch < 0;
                validation.Message = mismatch < 0
                    ? "Histogram matches the CPU oracle."
                    : $"Histogram mismatch at bin {mismatch}: GPU={actual[mismatch]}, CPU={histogramExpected[mismatch]}.";
                validation.ResultHash = Hash(actual, actual.Length).ToString("X8");
                break;
            }
            case "stable-compaction":
            {
                NativeArray<uint> countData = completed.Requests[0].GetData<uint>();
                int actualCount = checked((int)countData[0]);
                NativeArray<uint> actual = completed.Requests[1].GetData<uint>();
                int mismatch = actualCount == compactExpected.Length
                    ? FindMismatch(actual, compactExpected, actualCount)
                    : -2;
                validation.Passed = mismatch < 0 && actualCount == compactExpected.Length;
                validation.Message = validation.Passed
                    ? "Stable compaction count and order match the CPU oracle."
                    : mismatch == -2
                        ? $"Stable compaction count mismatch: GPU={actualCount}, CPU={compactExpected.Length}."
                        : $"Stable compaction mismatch at {mismatch}: GPU={actual[mismatch]}, CPU={compactExpected[mismatch]}.";
                validation.ResultHash = Hash(actual, Math.Min(actualCount, actual.Length)).ToString("X8");
                break;
            }
            case "append-compaction":
            {
                NativeArray<uint> countData = completed.Requests[0].GetData<uint>();
                int actualCount = checked((int)countData[0]);
                NativeArray<uint> actualData = completed.Requests[1].GetData<uint>();
                uint[] actual = new uint[Math.Min(actualCount, actualData.Length)];
                for (int i = 0; i < actual.Length; i++)
                {
                    actual[i] = actualData[i];
                }
                Array.Sort(actual);
                uint[] expected = (uint[])compactExpected.Clone();
                Array.Sort(expected);
                int mismatch = actualCount == expected.Length
                    ? FindMismatch(actual, expected, expected.Length)
                    : -2;
                validation.Passed = mismatch < 0 && actualCount == expected.Length;
                validation.Message = validation.Passed
                    ? "Append compaction membership matches the CPU oracle."
                    : mismatch == -2
                        ? $"Append compaction count mismatch: GPU={actualCount}, CPU={expected.Length}."
                        : $"Append compaction membership mismatch at sorted slot {mismatch}.";
                validation.ResultHash = Hash(actual, actual.Length).ToString("X8");
                break;
            }
            case "radix-sort-32":
            {
                NativeArray<uint> actualKeys = completed.Requests[0].GetData<uint>();
                NativeArray<uint> actualValues = completed.Requests[1].GetData<uint>();
                int keyMismatch = FindMismatch(actualKeys, radixKeysExpected, count);
                int valueMismatch = FindMismatch(actualValues, radixValuesExpected, count);
                validation.Passed = keyMismatch < 0 && valueMismatch < 0;
                validation.Message = validation.Passed
                    ? "Radix-sort keys and associated values match the CPU oracle."
                    : $"Radix-sort mismatch: keySlot={keyMismatch}, valueSlot={valueMismatch}.";
                validation.ResultHash =
                    (Hash(actualKeys, count) ^ RotateLeft(Hash(actualValues, count), 13)).ToString("X8");
                break;
            }
            default:
                throw new InvalidOperationException(
                    "Unsupported validation operation: " + completed.BenchmarkCase.Operation);
        }
    }

    private static GraphicsBuffer CreateStructuredBuffer(int bufferCount, string name)
    {
        return new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCount, sizeof(uint))
        {
            name = name
        };
    }

    private static uint CreateKey(int i, int n, int seed, string distribution, int bits)
    {
        uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        uint key = distribution == "duplicates" ? Mix((uint)i ^ (uint)seed) % 37u :
            distribution == "single-bin" ? 7u : distribution == "ascending" ? (uint)i :
            distribution == "descending" ? (uint)(n - i) : unchecked((uint)i * 2654435761u + (uint)seed);
        return key & mask;
    }
    public void WriteCandidateMetadata(string directory)
    {
        File.WriteAllLines(Path.Combine(directory, "candidate-capabilities.csv"),
            new[] { "candidateId,supported,observedProbeWaveSize,reason" }.Concat(capabilityRows));
        var rows = new List<string> { "caseId,candidateId,threads,elementsPerThread,tileSize,radixBits,keyBits,dispatchCount,instanceScratchBytes,candidateScratchBytes,observedProbeWaveSize" };
        foreach (var c in cases)
        {
            bool candidate = candidates.TryGetValue(c.Variant, out var instance);
            var descriptor = candidate ? GpuPrimitiveCandidates.Get(c.Variant) : null;
            int tile = descriptor?.TileSize ?? 256, levels = 0, n = count;
            do { levels++; n = (n + tile - 1) / tile; } while (n > 1);
            int scanDispatches = count == 0 ? 0 : 2 * levels - 1;
            int dispatches = c.Operation == "radix-sort-32" ? (descriptor?.RadixDispatches(count, keyBits) ?? (count == 0 ? 0 : 3 * ((keyBits + 3) / 4))) :
                c.Operation == "stable-compaction" ? 1 + (count == 0 ? 0 : 2 + scanDispatches) :
                c.Operation == "exclusive-scan" ? scanDispatches : c.Operation == "reduce-sum" ? descriptor.ReductionDispatches(count) : 1 + (count == 0 ? 0 : 1);
            rows.Add($"{c.Id},{c.Variant},{descriptor?.Threads ?? 256},{descriptor?.ElementsPerThread ?? 1},{tile},{descriptor?.RadixBits ?? 4},{keyBits},{dispatches},{(instance ?? primitives).ScratchBytes},{instance?.CandidateScratchBytes ?? 0},{instance?.ObservedCandidateWaveSize ?? 0}");
        }
        File.WriteAllLines(Path.Combine(directory, "candidate-resources.csv"), rows);
    }

    private static HashSet<string> ParseFilter(string filter, IEnumerable<string> defaults)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filter) || filter == "*")
        {
            foreach (string item in defaults)
            {
                result.Add(item);
            }
            return result;
        }

        string[] parts = filter.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string value = parts[i].Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(value))
            {
                result.Add(value);
            }
        }
        return result;
    }

    private static int FindMismatch(NativeArray<uint> actual, uint[] expected, int length)
    {
        if (actual.Length < length || expected.Length < length)
        {
            return -2;
        }
        for (int i = 0; i < length; i++)
        {
            if (actual[i] != expected[i])
            {
                return i;
            }
        }
        return -1;
    }

    private static int FindMismatch(uint[] actual, uint[] expected, int length)
    {
        if (actual.Length < length || expected.Length < length)
        {
            return -2;
        }
        for (int i = 0; i < length; i++)
        {
            if (actual[i] != expected[i])
            {
                return i;
            }
        }
        return -1;
    }

    private static uint Hash(NativeArray<uint> data, int length)
    {
        uint hash = FnvOffset;
        for (int i = 0; i < length; i++)
        {
            hash = unchecked((hash ^ data[i]) * FnvPrime);
        }
        return hash;
    }

    private static uint Hash(uint[] data, int length)
    {
        uint hash = FnvOffset;
        for (int i = 0; i < length; i++)
        {
            hash = unchecked((hash ^ data[i]) * FnvPrime);
        }
        return hash;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7feb352du);
        value ^= value >> 15;
        value = unchecked(value * 0x846ca68bu);
        value ^= value >> 16;
        return value;
    }

    private static uint RotateLeft(uint value, int bits)
    {
        return (value << bits) | (value >> (32 - bits));
    }

    private sealed class PendingValidation
    {
        public GpuPrimitiveBenchmarkCase BenchmarkCase;
        public GpuPrimitiveValidationPhase Phase;
        public List<AsyncGPUReadbackRequest> Requests;
        public long ReadbackBytes;
    }
}
