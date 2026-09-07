# SUMMIT GPU Adaptive Binning

The package provides uint key/value to CSR binning with independently forceable
Direct count/scan/scatter and Radix low-bit sort/range-extraction implementations.
`Record` remains unchanged. No unmeasured implementation is a new default.

## Version 3 runtime calibration

`GpuAdaptiveBinningMatrixDocument` stores an explicit R9700 / DX12 matrix with up to
65,536 exact cells. Each cell includes workload identity, element count, bin count,
concentration, occupied-bin count, maximum bin occupancy, and the exact key for a
single-bin input. Cells also bind the actual primitive candidate ID and validation
provenance. No interpolation or concentration-only threshold is inferred.

Only frozen schema-v3 documents with a nonempty calibration run, positive revision,
valid exact device/environment identity and unique cells are usable. Each selected
row needs correctness validation, evidence ID and at least three calibration
samples. The comparison freezer imposes stronger sample gates. Unknown, unvalidated,
invalid, incompatible or untrusted inputs immediately choose Direct.

`GpuAdaptiveBinningMatrix` copies serialized evidence into an immutable runtime
snapshot. `GpuAdaptiveBinningStableSelector` owns state for **one ordered input
stream**, and is not thread-safe. Default Radix promotion requires three consecutive
observations of the same cell. An already active Radix may remain active across
other validated Radix cells; Direct transitions and all fallback conditions are
immediate. `Reset()` starts a new stream. Count observations once per input update,
not once per duplicate command recording. The policy requires no GPU readback.

```csharp
// Independently capture these values from the running artifact/OS, never the profile.
var device = GpuDeviceFingerprint.Capture(osReportedDriverVersion);
var environment = GpuCalibrationEnvironment.Capture(
    runningCompilerIdentity, runningShaderDigest, runningBuildDigest);
var matrix = new GpuAdaptiveBinningMatrix(loadedFrozenDocument);
var selector = new GpuAdaptiveBinningStableSelector(matrix, device, environment);

var decision = binner.RecordAdaptive(commands, keys, values, counts, offsets,
    binnedValues, diagnostics, elementCount, binCount,
    GpuAdaptiveBinningKeyDomain.GuaranteedInRange, selector, in features,
    GpuPrimitiveBackend.WaveOps);
// decision.Backend, Reason, Switched, SelectorCpuTicks, SwitchStateCpuTicks
```

The caller must bind features to the **same keys and active dimensions** used by
the recorded operation. `TryGather` scans CPU-owned keys into caller-owned histogram
scratch, without allocations. It rejects out-of-range keys. Its CPU time and any
uploads belong in an end-to-end cost estimate. GPU-only producers must supply
current validated producer evidence or separately account for feature dispatches,
readback and latency; stale features or an untrusted producer must use Untrusted.

Device matching includes vendor/device IDs, API, GPU name, shader level, graphics
version **and independently supplied driver version**. Unity's graphics-version
string does not reliably include the driver. The environment binds Unity version,
compiler/toolchain and flags, shader digest and build digest. Missing identity is
incompatible. Recreate selectors after device recreation, driver changes or code/
shader reload; cached identities describe one immutable running session.

Candidate IDs are extensible strings. The current facade instantiates legacy
primitive implementations and therefore binds `Portable` / `WaveOps` only. It does
not execute a new candidate simply because a matrix names it. Future wrapper
construction must bind the instantiated candidate ID and its capability check;
unknown IDs currently fail closed. `Auto` cannot match a measured explicit default.

## Migration and evidence

The previous two-cell schema-v2 structs and pure selector remain available for
historical **classification replay**. The old `RecordAdaptive(... profile, hint,
identity ...)` overload remains source-compatible but always uses Direct: that
schema cannot establish driver/compiler/shader/build compatibility. Recalibrate
into v3; changing a schema number or copying `sourceCommit` cannot migrate evidence.

Historical forced A/B results are retained in
`Docs/GPU_ADAPTIVE_BINNING_AMD_R9700_FORMAL_2026-07-31.md`. They do not establish
v3 runtime speedups. See `Docs/GPU_ADAPTIVE_RUNTIME_VNEXT.md` for the new actual
RecordAdaptive benchmark, freeze/evaluation protocol, limits and executable commands.

## Output and memory contract

- `binCounts[C]`, `binOffsets[C+1]` including the terminal valid count.
- `binnedValues[N]`; ordering within a bin is unspecified by the shared contract.
- `diagnostics[2]`; Untrusted uses Direct, excludes invalid keys and reports them.

All recording/selection paths are allocation-free after initialization. The new
selector records CPU stopwatch ticks for observability. Both backends remain
resident, so switching allocates no GPU scratch. `UnionScratchBytes` is the sum of
logical buffer payloads, not driver allocation size or measured VRAM residency.
Detailed profiler markers default on; the benchmark uses identical native outer
scopes with detailed markers disabled.
