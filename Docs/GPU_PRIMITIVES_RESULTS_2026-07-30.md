# GPU Primitives Validation Results

Date: 2026-07-30

Worktree: `SUMMIT-gpu-primitives`

Branch: `codex/gpu-primitives-benchmark`

## Outcome

The scene-agnostic package is implemented and correctness-validated on the
local AMD platform. No primitive performance improvement is claimed from this
milestone because both Unity whole-frame timing and marker-level GPU timing
returned no numeric samples in the minimal Player. The harness treated that as
a failed performance quality gate rather than substituting CPU enqueue time.

## Validated implementation

`com.summit.gpu-primitives` provides:

- hierarchical uint exclusive scan;
- portable and wave-optimized histogram;
- stable scan/scatter compaction;
- portable atomic and wave-reserved append compaction;
- eight-pass stable 32-bit key/value radix sort;
- persistent scratch allocation and CommandBuffer-first recording;
- portable fallback and kernel-level wave capability selection.

The Runtime package has no scene, NYCGIS, or MonoBehaviour dependency.

## Exact correctness result

Environment:

- GPU: AMD Radeon AI PRO R9700
- API: Direct3D 12, feature level 12.2
- Unity: 6000.5.2f1

Unity EditMode result:

- 134 passed
- 0 failed
- 0 skipped

Coverage includes CPU exact oracles, 16 wave/thread-group boundary sizes,
zero-count contracts, portable/wave equivalence, stable compaction order,
append membership/count, histogram domains through 4,096 bins, and stable
full-32-bit key/value radix ordering and permutation.

## Benchmark quality result

The single-Player harness validated:

- one process ID across all variants;
- counterbalanced case ordering;
- warmup and final exact readback outside measurement;
- zero readback bytes during measurement;
- preallocated command buffers and sample storage;
- source/result frame IDs for delayed timing;
- device, API, package memory, commit, and shader/source hashes;
- strict refusal to summarize incomplete GPU timing as A/B evidence.

On this Player, `SystemInfo.supportsGpuRecorder`, every GPU-enabled
`CustomSampler`, and every associated `Recorder` reported valid. However,
`gpuSampleBlockCount` remained zero for every measured non-control row.
`FrameTimingManager` was enabled and exposed a 1 GHz GPU timer, but returned no
whole-frame samples in the minimal workload. An amplified 1,048,576-element,
eight-dispatch diagnostic produced the same result while all correctness
checks remained unchanged.

Consequently:

- `performanceMetricsUsable=0`;
- no GPU average, P95, P99, bandwidth, or backend speedup is reported;

## Native timestamp follow-up

This file remains the historical result for the original Unity timing path.
The later isolated native-DX12 timestamp branch produced formal scoped-GPU
evidence. Use
[AMD R9700 formal results](GPU_PRIMITIVES_AMD_R9700_FORMAL_RESULTS_2026-07-30.md)
for the accepted, negative, and cross-run-inconclusive conclusions.
