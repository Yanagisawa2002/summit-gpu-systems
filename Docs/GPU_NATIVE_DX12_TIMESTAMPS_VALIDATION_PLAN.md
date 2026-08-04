# Native DX12 GPU Interval Timestamps: Validation Plan

Date: 2026-07-30
Status: ABI-v2 instrumentation smoke passed; formal A/B pending

## Acceptance objective

Prove that the managed wrapper and native plugin produce generation-safe,
nonblocking, direct-queue timestamp intervals with an exact ABI and explicit
unsupported behavior. A plugin-owned same-queue completion fence must prove
query-readback readiness. Correctness and instrumentation overhead are separate
from any workload-performance comparison.

## Test layers

### 1. Pure managed ABI tests

These tests do not load the native DLL:

- ABI version `2` and required capability mask `31` (`0x1F`);
- distinct begin, end, frequency, and completion event IDs;
- ABI version constant;
- enum underlying types and numeric values;
- sequential layout and explicit packing;
- `Marshal.SizeOf` for every shared structure;
- `Marshal.OffsetOf` for every field;
- fixed-width integer fields for native booleans/status;
- function calling convention and export declarations where reflection exposes
  them;
- x64 pointer-size expectation guarded by the supported-platform contract.

The expected sizes and offsets are duplicated from the reviewed native header,
not calculated from the managed implementation under test.

### 2. Pure managed value and conversion tests

- token equality includes slot, generation, and session identity where present;
- default token is invalid;
- adjacent generations differ;
- exact tick deltas at representative frequencies;
- fractional conversion uses floating-point division;
- large ticks do not overflow before conversion;
- zero tick delta remains a valid zero only when native status is valid;
- pending/unsupported/error statuses never expose a valid duration;
- source-frame and ready-frame latency are preserved.

Suggested conversion vectors:

| Begin | End | Frequency | Expected |
| ---: | ---: | ---: | ---: |
| 100 | 100 | 1,000 | 0 ns |
| 100 | 101 | 1,000 | 1,000,000 ns |
| 1,000 | 1,250 | 10,000,000 | 25,000 ns |
| 9,000,000,000 | 9,016,666,667 | 1,000,000,000 | 16,666,667 ns |

### 3. Managed state-machine tests with a fake native backend

Where the wrapper exposes an internal test seam:

- create supported and unsupported sessions;
- acquire until ring exhaustion;
- reject consume while a sample is reserved;
- make the reserved-to-submitted transition one-way and reject duplicate submission;
- reject cancellation and payload reuse after submission;
- pending poll returns immediately;
- ready poll returns raw ticks/frequency and source/result frames;
- successful ready consume permits slot reuse with a new generation;
- cancel recycles a reserved scope without publishing a result;
- stale token cannot retrieve the reused slot's result;
- double consume and cross-session token misuse are rejected;
- dispose cancels reserved samples but never recycles submitted payloads;
- fake device-lost and callback-error statuses become terminal;
- no per-scope managed collection growth after warmup.

The fake backend must model status transitions, not synthesize performance
numbers for a report.

### 4. Unsupported-platform tests

Feasible Editor tests inject or select:

- platform not Windows;
- pointer size not 64-bit;
- graphics API not D3D12;
- compute support irrelevant but plugin unavailable;
- missing native DLL;
- missing export;
- ABI version mismatch;
- native initialization failure.

Expected behavior is `TryCreate=false` with a nonempty diagnostic and a precise
support/status value. The package must not throw for ordinary unsupported
environments. Programmer misuse may throw where explicitly documented.

### 5. Native Windows/DX12 integration tests

Run only in a built Windows x64 Player using D3D12:

1. Initialize after the D3D12 device is available.
2. Verify ABI v2, capability mask `31`, and a positive direct-queue timestamp
   frequency.
3. Acquire a sample and pre-record one command buffer containing
   begin/work/end+resolve/completion events in that order.
4. Call `MarkSubmitted` immediately before Unity queue submission.
5. Submit that command buffer on the direct graphics queue.
6. Poll the plugin-owned fence without blocking until ready or timeout.
7. Read mapped query data only after the private fence reaches the scope value.
8. Verify native status, generation, sentinels, fence value, and raw fields.
9. Repeat through more scopes than ring capacity while consuming completed
   tokens.
10. Dispose and recreate the session only after all submitted samples complete.

Integration invariants:

- all completed intervals have the expected token generation;
- `endTick >= beginTick` for ordinary non-wrap samples;
- tick delta and reported duration agree within conversion precision;
- ready frame is not earlier than source frame;
- frequency is stable for one session;
- no query resolves before both endpoints were recorded;
- the completion event signals the plugin-owned fence on the same direct queue
  after end-query resolution;
- mapped ticks are never read before that fence value completes;
- generation sentinels are replaced before a result is accepted;
- a completion, signal, or sentinel failure quarantines the slot and fails
  closed;
- no submitted token can be cancelled or have its payload reacquired;
- disposal never recycles a submitted payload referenced by queued callbacks;
- no ring overwrite, stale result, or device removal;
- no blocking wait in the measurement loop.

### 6. Ordering discrimination test

An empty interval and at least two deterministic workloads are required. Choose
workloads with clearly separated dispatch counts while holding buffer sizes and
recording structure constant.

The test is a sanity discriminator, not a benchmark claim. Across repeated
samples:

- heavy-work raw median should exceed empty-control raw median;
- heavy-work raw median should exceed light-work raw median;
- invalid or unavailable results fail the gate;
- the distributions and overlap are retained, not reduced to one assertion.

## Boundary and stress matrix

| Dimension | Cases |
| --- | --- |
| Ring capacity | 1, 2, 3, 31, 32, 33, 255, 256, 257 |
| In-flight scopes | 0, capacity, capacity + 1 |
| Token generation | 0/default, first live, reused slot, maximum-near-wrap |
| Result latency | ready immediately in fake, 1, 2, 8 frames, timeout |
| Timestamp delta | 0, 1, ordinary, large 64-bit |
| Frequency | 1, 1,000, 10 MHz, 1 GHz, invalid zero |
| Lifecycle | create/dispose, device reset, plugin reload |

Native stress should issue thousands of scopes over several ring wraps without
increasing resident resources.

## Benchmark quality gates

A performance dataset is usable only when:

- one named Player binary and one PID generated the paired run;
- Windows x64/DX12/direct-queue support is positively established;
- ABI v2, required capability mask `31`, plugin/DLL hash, source hash, GPU,
  driver, and queue frequency are
  retained;
- warmup and final workload correctness both pass;
- every claimed sample has valid raw begin/end ticks;
- no token drops, ring overwrite, timeout, device loss, or stale result occurred
  unless the experiment explicitly studies capacity;
- result-latency and slot-occupancy distributions are present;
- measurement recording made no managed or GPU allocation;
- measurement polling never blocked;
- empty-control overhead is interleaved and reported;
- raw intervals are preserved without silent overhead subtraction;
- counterbalanced A/B order and complete paired repeats are retained.

## Fixed-overhead reporting

Report at minimum:

| Metric | Empty control | Baseline workload | Candidate workload |
| --- | ---: | ---: | ---: |
| Raw P50 | required | required | required |
| Raw P95 | required | required | required |
| Raw P99 | required | required | required |
| Sample count | required | required | required |
| Invalid/drop count | required | required | required |
| Ready latency P99 | required | required | required |

If adjusted estimates are included, place them in separate columns. The report
must name whether the adjustment is paired empty control, control median, or
another model. It must not silently clamp negative results or omit cases where
control overhead is a large fraction of the workload.

## Result classification

| State | Meaning |
| --- | --- |
| Validated instrumentation | ABI, lifecycle, unsupported, and native ordering gates pass |
| Performance usable | Instrumentation passes and all benchmark quality gates pass |
| Correctness only | Managed/native gates pass but timing dataset is absent or incomplete |
| Inconclusive | Valid data exists but effect is near overhead/noise or changes sign |
| Rejected | Incorrect results, invalid timing, drops, blocking, or untracked drift |
| Unsupported | Platform/API/plugin requirement is not met |

## Precise claim checklist

Allowed after the validated AMD instrumentation smoke:

> Implemented and smoke-validated an ABI-v2 native D3D12 timestamp-query bridge
> for Unity direct-graphics-queue command regions, using a plugin-owned
> same-queue completion fence, generation-safe readback, and explicit
> unsupported fallback.

Only add workload comparisons after the frozen formal A/B gate passes. The
one-round smoke is not a performance result.

Never infer from interval timestamps alone:

- shader instruction count;
- occupancy, VGPR, cache, atomic, or bandwidth behavior;
- async-compute execution time;
- whole-frame FPS or P99 improvement;
- driver optimization;
- NVIDIA behavior.

## Current smoke-gate evidence

The accepted smoke used source commit
`5f4bc3bc29dce05df926b7bb3b49642d524a3562` and native DLL SHA-256
`5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA`.
It ran on AMD Radeon AI PRO R9700, driver `32.0.31035.1003`, with Unity
`6000.5.2f1` and Direct3D 12.

The ABI/fail-closed EditMode suite passed `48/48` with zero skips; `300/300`
native intervals were ready; `8/8` workload validation rows passed; and
algorithm measurement readback was `0` bytes. There were no native
acquire/result failures, timeouts, pending rows, or terminal state.

This is direct-graphics-queue evidence only, not async-compute evidence. Earlier
short runs without the private completion fence are invalid for timing claims
and are excluded. See
`Docs/GPU_NATIVE_DX12_TIMESTAMPS_SMOKE_EVIDENCE_2026-07-30.md`.

## Evidence artifacts

```text
Reports/GpuTimestamps/<experiment-id>/
  environment.json
  abi.json
  raw-intervals.csv
  overhead-summary.csv
  paired-deltas.csv
  quality-gates.json
  test-results.xml
  native-player.log
```

Raw intervals are the source of truth. Derived summaries identify their input
hash and analysis version.
