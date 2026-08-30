# GPU Primitives Benchmark and Validation Design

Date: 2026-07-30

Status: implementation and validation design; no performance result is claimed

## Objective

Establish a reusable and falsifiable baseline for GPU histogram, exclusive
scan, stable compaction, and stable key/value radix sort. The benchmark exists
to answer three separate questions:

1. Is every output exactly correct at dispatch, group, and wave boundaries?
2. When does a wave-optimized implementation outperform a portable fallback?
3. Which primitive or memory transition dominates a larger spatial-binning
   pipeline?

The benchmark is deliberately independent of the NYC scene. Synthetic inputs
are not a substitute for later production validation; they isolate algorithmic
behavior so regressions can be diagnosed.

## Correctness contract

Each implementation is compared with a deterministic CPU oracle before timing.
The required sizes are:

| Boundary | Sizes |
| --- | --- |
| Wave32 neighborhood | 1, 31, 32, 33 |
| Wave64 and group subdivisions | 63, 64, 65, 127, 128, 129 |
| Thread-group neighborhood | 255, 256, 257 |
| Scan/radix block neighborhood | 4095, 4096, 4097 |

Required gates:

| Primitive | Exact gate | Invariant gates |
| --- | --- | --- |
| Histogram | Every bucket equals the CPU oracle | Sum equals logical element count |
| Exclusive scan | Every output equals the CPU oracle | First value is zero; adjacent offsets differ by the preceding input |
| Stable compaction | Count and every emitted payload equal the CPU oracle | Input order is preserved; no rejected or padded lane is emitted |
| Radix sort | Every key and payload equal the CPU stable oracle where the public API exposes both | Keys are nondecreasing; payload is a permutation; key/payload association and equal-key stability hold |

For every supported primitive, wave and fallback variants must also match one
another exactly. A hash alone is insufficient: hashes may be logged for quick
triage, but the warmup validation reads and compares complete logical outputs.

Zero logical elements are a separate API-contract case rather than a timing
boundary: scan/radix outputs remain unchanged, histogram obeys its clear flag,
and compaction publishes a zero output count. Tests provide one-word dummy
buffers because Unity does not create zero-length `GraphicsBuffer` instances.

GPU integration tests must fail on any unexpected readback error, buffer-size
mismatch, shader error, timeout, or unsupported implementation selection. If
the machine cannot run compute, the result must be reported as skipped and must
not be counted as a passed GPU validation.

## Deterministic data families

Every formal sweep should include several distributions because aggregate
contention and cache behavior are data-dependent:

- uniform pseudo-random keys;
- duplicate-heavy keys with a small bucket domain;
- all keys in one bucket;
- monotonic and reverse-monotonic keys;
- alternating predicates;
- sparse predicates (approximately 1% selected);
- dense predicates (approximately 99% selected);
- run-heavy predicates crossing wave and group boundaries.

Seeds, generators, logical sizes, padded sizes, bucket counts, and query
parameters must be written into each result row.

## Same-process A/B methodology

Separate Player processes are vulnerable to scene-load, shader-cache, clock,
thermal, and residency drift. Formal comparisons therefore run both variants
inside one built Player process.

For each repeat:

1. Create deterministic inputs and expected CPU outputs once.
2. Validate fallback and wave outputs in an untimed warmup phase.
3. Warm both variants until pipeline creation and shader compilation no longer
   affect samples.
4. Record prerecorded command buffers for both variants.
5. Execute every selected case once per round. Reverse odd rounds and rotate
   the starting case every two rounds, preserving the exact order in each row.
6. Insert GPU timestamp queries around only the primitive region.
7. Keep all unrelated rendering and sensor work constant.
8. Reject or separately flag epochs with shader compilation, asset loading,
   device loss, correctness failure, or measurement readback.

Use at least three repeats. Pair epochs within the same repeat before computing
relative deltas. Report the distribution of paired deltas; do not compare a
pooled A average against a pooled B average when order or clock state differs.

The benchmark must record both order sequences across repeats. A suitable
complete-case counterbalance for cases `C0...Ck` is:

```text
round 1: C0 -> C1 -> ... -> Ck
round 2: Ck -> ... -> C1 -> C0
round 3: C1 -> C2 -> ... -> Ck -> C0
round 4: C0 -> Ck -> ... -> C2 -> C1
```

Do not perform correctness readback inside timed epochs. A small post-epoch
sentinel readback may be used as an additional guard, but it must be excluded
from GPU timing and reported separately.

## Workload matrix

The first formal matrix should include:

- logical sizes: the sixteen correctness boundaries plus 65,536, 1,048,576, and
  4,194,304 elements;
- histogram domains: 16, 256, 4,096, and 262,144 buckets where memory permits;
- 16-bin histogram distributions: `uniform-16`, `hotset-4`, and `single-bin`;
  non-default distributions are valid only in histogram-only runs so they do
  not silently alter scan, compaction, or radix workloads;
- compaction selectivity: 1%, 25%, 50%, 75%, and 99%;
- radix key entropy: 4, 8, 16, 18, and 32 effective bits;
- implementations: portable fallback and wave-optimized;
- update modes: rebuild every frame and reuse across 2, 4, and 8 consumers.

Large cases may be split into stable suites to keep runtime bounded. Any split
must retain the same binary and shader hashes for comparisons presented as one
experiment.

## Required timing and resource fields

Each raw sample or epoch must identify:

- experiment ID, repeat, epoch, order position, implementation, primitive;
- logical count, padded count, distribution, seed, bucket count/selectivity;
- GPU elapsed time from timestamp queries;
- CPU submission time and whole-frame time as secondary guardrails;
- target update interval, completed updates, and deadline misses;
- measurement readback bytes;
- resident input/output/scratch bytes and peak temporary bytes;
- dispatch count and thread-group dimensions;
- GPU name, vendor/device ID, driver, graphics API and feature level;
- Unity version, branch, commit, Player hash, compute-shader hash;
- wave-lane count and selected capability path;
- correctness gate status and validation timestamp.

Vendor-profiler captures should add VGPR use, occupancy, cache hit rates,
bandwidth, atomics, wave utilization, barriers, and queue bubbles when
available. Those counters are contextual evidence, not substitutes for elapsed
GPU time.

## Statistics and decision rules

Primary metrics:

- paired median GPU-time change;
- GPU P95 and P99 per implementation;
- percentage of paired epochs favoring the candidate.

Secondary metrics:

- whole-frame P99;
- deadline-miss change;
- temporary-memory change;
- effective input and output bandwidth.

Always show absolute milliseconds next to percentages. Preserve all raw
samples. A candidate is not accepted solely because its average improves.
Before calling a variant a production win:

- all correctness gates must pass;
- at least three paired repeats must complete;
- the direction must be consistent in at least two thirds of paired epochs;
- P99 must not regress beyond the experiment's predefined tolerance;
- there must be no new readback, error, or deadline-miss regression;
- effect size must exceed measured same-process noise.

The tolerance and minimum useful effect must be chosen before inspecting the
candidate result. If confidence intervals cross zero or the result changes sign
across repeats, report it as inconclusive.

## Failure and limitation reporting

The report must preserve negative and inconclusive results. In particular:

- no claim may be generalized from one input distribution;
- no NVIDIA or cross-vendor claim may be made from AMD-only evidence;
- no live-sensor claim may be made from deterministic static buffers;
- no frame-rate claim may be inferred solely from isolated-kernel time;
- no driver-level claim may be made without a driver/API capture;
- no wave-size assumption may be embedded in a portable contract;
- no performance claim may be published before the formal dataset exists.

An implementation can pass correctness and still be rejected for speed,
memory, stability, portability, or crossover behavior.

## Implemented evidence artifacts

The one-PID runner and summarizer produce:

```text
Reports/GpuPrimitives/<experiment-id>/
  runner-config.json
  unity-build.log
  player.log
  config.json
  device.json
  raw-frames.csv
  block-summary.csv
  validation.csv
  run-summary.txt
  paired-deltas.csv
  operation-summary.csv
  quality-summary.txt
```

`runner-config.json`, `config.json`, and `device.json` own experiment,
environment, and process identity. `raw-frames.csv` is the source of truth for
analysis. The summarizer rejects multiple Player PIDs, nonzero measurement
readback, incomplete GPU samples, failed fences, failed/missing warmup or final
validation, backend hash disagreement, and missing round/case rows.

Example:

```powershell
pwsh -File Tools/Run-GpuPrimitiveBenchmark.ps1 `
  -OutputDirectory Reports/GpuPrimitives/formal-amd-r9700 `
  -Rounds 3 `
  -WarmupFrames 60 `
  -SampleFrames 900 `
  -ElementCount 1048576 `
  -Operations '*' `
  -Backends 'portable,wave-ops'
```

## Current validation evidence

On 2026-07-30, Unity 6000.5.2f1 on an AMD Radeon AI PRO R9700 with
Direct3D 12 feature level 12.2 completed the EditMode correctness suite:

- 134 total tests;
- 134 passed;
- zero failed, inconclusive, or skipped;
- exact portable output against CPU oracles;
- exact wave/fallback semantic equivalence;
- all sixteen boundary sizes and the zero-count contract;
- zero C# and compute-shader compile errors.

Evidence is retained in
`Reports/GpuPrimitives/editmode-tests-final.xml` and its adjacent log. This is a
correctness result, not a performance result.

The first same-process smoke artifact was correctly rejected because its GPU
timing samples were unavailable. It predates the final wave capability probe
and is not A/B evidence. A formal run must first demonstrate a complete,
reliable GPU timing source.

## Acceptance state for this worktree

This worktree is ready for performance interpretation only after:

- Unity compiles Runtime, benchmark, and Test assemblies without errors;
- all CPU-oracle tests pass at all sixteen boundary sizes;
- all GPU exact-comparison tests pass on the named backend;
- wave/fallback equivalence is demonstrated;
- the same-process harness emits raw timestamp data and complete provenance;
- a fresh validator reproduces summary metrics from raw rows.

Until then, the only valid statement is that the package and experiment harness
have been implemented for validation.
