# GPU Direct Spatial Binning Benchmark Plan

## Scope

This benchmark is an independent S2 experiment for a reusable GPU-resident
`count -> exclusive scan -> scatter` primitive. It does not modify the city
scene, the existing GPU primitive benchmark, or sensor application code.

The benchmark compares:

- **A — `reference-compose-portable-v1`**: the generic portable histogram and
  exclusive-scan primitives, followed by benchmark-owned prepare/scatter
  kernels.
- **B — `direct-count-scan-scatter-portable-v1`**: the production
  `Summit.GpuDirectBinning.GpuDirectSpatialBinner.Record` API.

Both cases share the same input/output buffers, one `GpuPrimitives` instance,
portable scan backend, 256-thread prepare/scatter kernels, workload, and
logical output contract. S2 deliberately excludes radix sorting, adaptive
selection, and wave-aggregated bin atomics.

This is a steady-state, GPU-resident primitive benchmark with fixed inputs.
It does not measure dynamic sensor ingestion, end-to-end frame time, FPS, or
whole-application performance.

The reference histogram masks key bits. Formal performance inputs therefore
contain only valid keys and power-of-two bin counts. Production invalid-key,
zero-element, capacity, and diagnostic behavior is validated by the package
contract/integration tests rather than misrepresented as reference-path
behavior.

## Output contract

For `N` input `uint` keys/values and `C` bins:

- `binCounts[C]`
- `binOffsets[C + 1]`
- `binOffsets[C] == validElementCount`
- `binnedValues[N]`
- `diagnostics[0] == invalidKeyCount`
- `diagnostics[1] == flags`

Invalid keys are excluded by the production path. Per-bin scatter order is
unspecified, so correctness must be checked by canonical per-bin membership,
not by raw output order.

## Position-balanced execution

Each workload runs in one Player process with local warmup before every block:

```text
control/pre
super-round 1: A B B A
super-round 2: B A A B
control/post
```

Adjacent pairs are `AB`, `BA`, `BA`, `AB`. A and B occupy each of sequence
positions 1–4 exactly once. Each block has:

1. 15 cooldown frames;
2. 60 case-local warmup frames;
3. 900 measured frames in formal mode;
4. complete native timestamp drain;
5. a Unity graphics completion fence.

No correctness readback occurs inside a measurement block.

The benchmark command-line initializer lowers the Unity logger threshold to
`Warning` before scene load. This suppresses unrelated informational logs from
production `RuntimeInitializeOnLoad` code without hiding warnings or errors.
Each scenario records `informationalPlayerLogsSuppressed=true`, and the
summarizer rejects formal evidence without that field.

## Timestamp contract

The primary metric is a Direct3D 12 timestamp interval around the complete
pre-recorded workload command buffer. The harness uses the native ABI 2
private-completion-fence path:

1. acquire a timestamp slot;
2. call `MarkSubmitted` exactly once;
3. execute `begin -> workload -> end/resolve -> private-fence callback`;
4. non-blockingly poll the result;
5. record 16 bytes of timestamp instrumentation per completed sample.

Formal evidence requires ABI 2, capability flags `0x1F`, unique tokens/tags,
consecutive private fence values, non-overlapping timestamp intervals,
strictly increasing source frames, measurement frequency equal to warmup,
stable device generation, independently recomputed tick-to-nanosecond
conversion, zero measurement readback, and an empty-scope P99 no greater than
0.005 ms.

This is a direct-graphics-queue benchmark. It makes no async-compute claim.

## Correctness oracle

The CPU oracle constructs counts, exclusive offsets, and per-bin payload
membership. GPU validation asynchronously reads counts, offsets, values, and
diagnostics before and after measurement. Each bin slice is sorted before
comparison because atomic scatter order is intentionally unspecified.

The canonical FNV-1a hash covers:

- counts;
- offsets;
- sorted values within every bin;
- diagnostics.

Both A and B must match the same oracle hash during warmup and final
validation.

## Frozen AMD matrix

Formal preset: `amd-r9700-v1`.

| Scenario | Elements | Bins | Distribution | Seed |
|---|---:|---:|---|---:|
| `uniform-c4096` | 1,048,576 | 4,096 | uniform | 20260730 |
| `hotset16-c4096` | 1,048,576 | 4,096 | 87.5% directed to 16 hot bins | 20260731 |
| `uniform-c65536` | 1,048,576 | 65,536 | uniform / sparse occupancy | 20260732 |

The matrix covers normal occupancy, high atomic contention, and a larger
scan domain. The Player is built once and hashed before/after all three child
runs. Each child remains a same-process A/B.

## Memory accounting

`logicalProblemBytesPerDispatch` is a logical problem footprint, not measured
DRAM traffic:

```text
12 * N + 8 * C + 12
```

The report separately records:

- shared input/output bytes;
- primitive scratch;
- direct/reference internal scratch;
- isolated per-case resident bytes;
- actual benchmark buffer resident bytes, including both inactive variant
  scratch allocations.

Shared primitive scratch is counted once in actual residency.

These are logical `GraphicsBuffer` payload bytes, not driver-measured VRAM
residency or observed DRAM traffic.

## Evidence and improvement gates

Evidence validity is separate from speedup classification. Valid negative and
neutral data is retained.

A scenario is performance-accepted only when:

- exactly four complete adjacent pairs exist;
- direct wins GPU average in at least three pairs;
- both the AB-order and BA-order median GPU-average improvements are
  nonnegative;
- median GPU-average improvement is at least 5%;
- median absolute GPU-average reduction is at least 0.005 ms;
- median GPU-P99 improvement is no worse than -2%;
- direct isolated resident bytes do not exceed reference;
- all correctness, timestamp, readback, and provenance gates pass.

Classification:

- `accepted`;
- `negative` when median GPU-average improvement is below zero;
- `neutral-or-inconclusive` otherwise.

`crossWorkloadImprovementClaimUsable=1` requires all three frozen scenarios to
be accepted. Failure of the improvement gate never deletes or invalidates
otherwise sound A/B data.

These frozen gates are predefined engineering heuristics. Meeting them means
the observed data met the frozen gate; it is not a claim of statistical
significance or proof of a specific atomic, scan, or bandwidth bottleneck.

## Commands

Exploratory smoke:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\Run-GpuDirectBinningBenchmark.ps1 `
  -UnityPath 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe' `
  -OutputDirectory 'Reports\GpuDirectBinning\smoke-uniform' `
  -ScenarioId 'smoke-uniform' `
  -ElementCount 65536 `
  -BinCount 4096 `
  -Distribution uniform `
  -SuperRounds 2 `
  -WarmupFrames 5 `
  -SampleFrames 60 `
  -CooldownFrames 2
```

Formal AMD matrix:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\Run-GpuDirectBinningBenchmark.ps1 `
  -UnityPath 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe' `
  -OutputDirectory 'Reports\GpuDirectBinning\formal-amd-r9700' `
  -MatrixPreset 'amd-r9700-v1' `
  -SuperRounds 2 `
  -WarmupFrames 60 `
  -SampleFrames 900 `
  -CooldownFrames 15 `
  -FormalAcceptanceMode `
  -EditModeResultsPath 'Reports\GpuDirectBinning\editmode\results.xml'
```

The formal runner rejects `SkipBuild`, `SkipSummary`,
`AllowMissingGpuTiming`, dirty/detached source, unstable Player payloads, and
incomplete EditMode evidence.
