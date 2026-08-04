# Native DX12 GPU Primitives Experiment Plan

Planned before the formal run: 2026-07-30T09:13:28Z

## Question

On the local AMD Radeon AI PRO R9700, which wave-optimized primitive
implementations beat their portable HLSL fallbacks under the same-process,
same-input benchmark, and which do not?

This experiment validates the instrumentation and compares isolated kernels.
It does not establish a whole-scene FPS result, an NVIDIA result, an
asynchronous-compute result, or a driver-level optimization result.

## Frozen formal matrix

- Unity: 6000.5.2f1
- graphics API: Direct3D 12
- device index: 0
- element count: 1,048,576
- deterministic seed: 20260730
- variants: portable and wave-ops
- operations: exclusive scan, 16-bin histogram, stable compaction,
  append compaction, and stable 32-bit key/value radix sort
- rounds: 3
- measured frames per block: 900
- local warmup frames per block: 60
- cooldown frames per block: 15
- dispatches per measured frame: 1
- one Player process with counterbalanced case order
- raw native timestamp intervals; empty-scope overhead is reported separately
  and is not subtracted

The deterministic input family in this run is only one workload. Later spatial
binning work must add uniform, duplicate-heavy, single-cell, and dynamic input
families before generalizing an algorithm-selection rule.

## Instrumentation acceptance gate

The formal dataset is performance-usable only if all of the following hold:

- the native ABI, lifecycle, fallback, and fail-closed EditMode suite has zero
  failures and zero skips;
- the built Player uses Direct3D 12 and loads ABI v2 with capability flags 31;
- every measured row returns one generation-safe native timestamp result;
- each private completion-fence value proves that the same direct queue
  executed end-query resolution before mapped readback;
- token, user tag, flags, source frame, private fence, device generation, and
  raw tick fields validate;
- timestamp frequency is nonzero and constant for every ready row;
- there are zero acquire failures, result failures, timeouts, pending rows,
  terminal states, or device resets;
- the empty-scope P99 is at most 0.005 ms;
- warmup and final correctness hashes match CPU oracles and match between
  portable and wave variants;
- algorithm measurement readback remains zero bytes;
- the 16-byte timestamp instrumentation readback is accounted separately;
- the Player PID is unique and all requested rounds and case blocks exist.

If any gate fails, the run remains correctness or instrumentation evidence and
must not be used for an A/B performance claim.

## Per-primitive candidate rule

A wave variant is accepted as a meaningful win only when:

- all three paired rounds are present;
- at least two of three paired GPU-average deltas favor wave-ops;
- the median paired GPU-average improvement is at least 3%;
- the median absolute GPU-average reduction is at least 0.005 ms;
- median paired GPU P99 does not regress by more than 5%;
- correctness, resident bytes, and readback behavior remain unchanged.

Results below these thresholds are reported as neutral/inconclusive, even if
their sign is positive. A negative result is retained rather than hidden.
Acceptance is per primitive; the experiment cannot support the statement that
wave HLSL is universally faster.

Whole-frame time and CPU enqueue time are secondary diagnostics. They are not
substitutes for the native GPU interval and are not used to accept an isolated
kernel variant.

## ABI-v2 instrumentation smoke gate

The accepted one-round smoke used:

- AMD Radeon AI PRO R9700, driver `32.0.31035.1003`;
- Unity `6000.5.2f1`, Direct3D 12;
- source commit `5f4bc3bc29dce05df926b7bb3b49642d524a3562`;
- native DLL SHA-256
  `5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA`;
- ABI v2, capability flags `31`;
- one process and one round, 65,536 elements, five warmup frames and 60
  measured frames per case;
- empty control plus portable/wave exclusive scan and portable/wave
  32-bit radix sort.

Observed gates:

- `48/48` EditMode tests passed with zero skips;
- `300/300` native Player intervals were ready;
- `8/8` workload validation rows passed;
- zero native acquire/result failures, timeouts, pending rows, or terminal state;
- algorithm measurement readback was `0` bytes; timestamp instrumentation
  readback was accounted separately at 16 bytes per completed sample.

The smoke proves ABI-v2 ordering, private-fence readiness, and lifecycle
behavior on this AMD direct-graphics-queue path only. One round is not formal
A/B evidence and does not support a kernel speedup. It does not validate async
compute, NVIDIA, or driver-level optimization.

Earlier short runs, including the previous `180/180` result, predate the
private completion fence. Their readiness condition did not causally prove
query resolution was complete, so their timing values are excluded.

## Evidence policy

Raw output stays under `Reports/GpuTimestamps/` on the local machine. The
reviewed result document must retain the run directory, hashes, absolute
milliseconds, paired percentages, negative outcomes, and AMD-only scope. No
resume statement may be written until the formal quality gate is evaluated.
