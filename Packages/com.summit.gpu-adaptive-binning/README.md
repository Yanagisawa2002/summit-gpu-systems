# SUMMIT GPU Adaptive Binning

This package provides a backend-neutral uint key/value to CSR binning
contract and two independently forceable GPU implementations:

- `Direct`: count, exclusive scan, and atomic scatter. The existing safe
  `Record` path accepts untrusted keys, excludes invalid keys, and reports
  diagnostics. `RecordGuaranteedInRange` uses dedicated count/scatter kernels
  without per-element validation when the producer contract is explicit.
- `Radix`: stable low-bit radix sort, sorted-run range extraction, and CSR
  offset generation. Its fast path requires the caller to guarantee that every
  key is less than the active bin count.

`GpuAdaptiveSpatialBinner` keeps the forced API and also exposes an opt-in
`RecordAdaptive` path. This is not a universal automatic default: the caller
must supply a versioned calibration profile, a workload-concentration hint,
and the numeric identity of the device on which that profile is being used.
Unknown, invalid, mismatched, or untrusted inputs fail closed to `Direct`.

## Calibrated selection

The schema-9 formal holdout at commit `53058f0` validated a narrow offline
exact-cell classification replay on one AMD Radeon AI PRO R9700 / DX12
device. The five forced-backend A/B cells were:

- `N=262144`, `C=16`, every key exactly `9`
- `N=1048576`, `C=16`, every key exactly `10`
- `N=1048576`, `C=16`, `hotset4`
- `N=1048576`, `C=16`, uniform
- `N=1048576`, `C=65536`, uniform

Across eight counterbalanced pairs per cell, Radix reduced median GPU-kernel
average time by 30.30% and 47.59% in the two exact single-bin cells. Direct
reduced it by 66.98%, 83.57%, and 97.68% in the three hotset/uniform cells.
The policy prediction matched the faster forced backend in all 5/5 exact
holdout cells. Every winning forced backend also won kernel-region P99 in 8/8
pairs for its cell. Full protocol, absolute deltas, P99 results, and claim
boundaries are in
`Docs/GPU_ADAPTIVE_BINNING_AMD_R9700_FORMAL_2026-07-31.md`.

This is forced-backend A/B plus offline classification replay. It is not
timing evidence for `RecordAdaptive`, selector overhead, a broader threshold,
or a production workload. The exact Radix cells require
`SingleBinGuaranteed`, explicit caller-owned exact-key evidence, WaveOps,
profiler markers disabled, and the exact R9700 / DX12 identity. Selector
schema v2 does not interpolate between cells. A midpoint, cross-key
combination, missing or invalid exact key, another bin count, distribution,
device, API, primitive backend, or marker state selects `Direct`. That
fail-closed fallback is a safety policy, not evidence that Direct is optimal
for every unmeasured input.

The package ships policy mechanics but no built-in universal profile. A
caller-owned profile matching the validated exact-cell policy can be
constructed explicitly:

```csharp
var binner = new GpuAdaptiveSpatialBinner(
    elementCapacity,
    binCapacity,
    emitProfilerMarkers: false);

var cell0 = new GpuAdaptiveBinningCalibrationCell(
    elementCount: 262144,
    binCount: 16,
    concentration:
        GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
    hasExactSingleBinKey: true,
    exactSingleBinKey: 9u);
var cell1 = new GpuAdaptiveBinningCalibrationCell(
    elementCount: 1048576,
    binCount: 16,
    concentration:
        GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
    hasExactSingleBinKey: true,
    exactSingleBinKey: 10u);

var profile = new GpuAdaptiveBinningCalibrationProfile(
    GpuAdaptiveBinningCalibrationProfile.CurrentSchemaVersion,
    "amd-r9700-dx12-53058f0-exact-cells-v2",
    profileRevision: 1,
    deviceBinding: new GpuAdaptiveBinningDeviceBinding(
        vendorId: 0x1002,
        deviceId: 0x7551,
        graphicsApi: GraphicsDeviceType.Direct3D12),
    requiredPrimitiveBackend: GpuPrimitiveBackend.WaveOps,
    requiredProfilerMarkersEnabled: false,
    radixCellCount: 2,
    radixCell0: cell0,
    radixCell1: cell1);

var hint = new GpuAdaptiveBinningWorkloadHint(
    elementCount,
    binCount,
    GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
    hasExactSingleBinKey: true,
    exactSingleBinKey: exactSingleBinKey);
GpuAdaptiveBinningDeviceIdentity device =
    GpuAdaptiveBinningDeviceIdentity.CaptureCurrent();

GpuAdaptiveBinningBackend selected = binner.RecordAdaptive(
    commands,
    keys,
    values,
    binCounts,
    binOffsets,
    binnedValues,
    diagnostics,
    elementCount,
    binCount,
    GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
    in profile,
    in hint,
    in device,
    GpuPrimitiveBackend.WaveOps);
```

`SingleBinGuaranteed` is an upstream contract, not an estimate. The caller
must also assert the exact single-bin key, and that key must be less than the
active bin count. `Hotset` does not satisfy the contract, regardless of how
concentrated the distribution appears. The selector performs no GPU readback
and does not infer either the distribution or the key.

In schema v2, vendor ID, device ID, and graphics API are all mandatory and
must match exactly. There are no wildcard bindings: zero device ID or
`GraphicsDeviceType.Null` makes the profile invalid, and every invalid or
mismatched profile selects `Direct`. Schema v2 also requires explicit
`GpuPrimitiveBackend.WaveOps` with profiler markers disabled. `Auto`,
`Portable`, an invalid backend enum, or a markers-enabled binner selects
`Direct`; the default `RecordAdaptive` backend is therefore fail-closed.

The runtime does not inspect the graphics driver version, Unity version, or
shader hashes. After a driver, Unity, or shader change, rerun the forced A/B,
rotate the profile ID or revision, and revalidate each exact cell before using
it again. Repeat the calibration on NVIDIA before making a cross-vendor claim.

Cache `CaptureCurrent()` outside the frame loop. Selection is a pure,
allocation-free classification method and `RecordAdaptive` returns the chosen
backend for telemetry and auditing; that return value is not performance
evidence. Existing `Record(..., backend, ...)` calls remain backward compatible
and are still the authoritative forced A/B interface.

## Output contract

- `binCounts[C]`
- `binOffsets[C + 1]`, including the terminal valid-element count
- `binnedValues[N]`
- `diagnostics[2]`

Ordering inside a bin is not part of the shared contract. The radix backend is
stable as an implementation property; the direct backend intentionally leaves
equal-key ordering unspecified.

S3 forced-backend A/B uses the shared `GuaranteedInRange` domain: every key is
strictly less than the active bin count and diagnostics must remain zero.
`Untrusted` is supported only by the direct implementation and is not part of
the cross-backend performance claim.

Detailed nested profiler markers default to enabled for RGP/Profiler analysis.
The formal microbenchmark disables them for both backends and retains identical
outer A/B markers inside the native timestamp scope.

## Scratch ownership

One `GpuAdaptiveSpatialBinner` owns one `GpuPrimitives` scratch arena shared by
its Direct and Radix backends. `DirectScratchBytes` and `RadixScratchBytes`
describe each isolated execution path and therefore both include that shared
arena. `UnionScratchBytes` counts the shared arena once plus the Direct
write-head and Radix sorted-key buffers; it is the logical persistent payload
of the complete facade.

Because both backends reuse the same primitive scratch, executions recorded
through one facade must not overlap. Use separate facade instances when two
command streams may execute concurrently. Forced A/B remains sequential and
uses the same facade, output buffers, and CSR oracle for both backends.

All `Record` methods are allocation-free and readback-free. Scratch byte
properties report logical buffer payload only, not driver allocation size or
measured VRAM residency.
