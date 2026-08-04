# GPU Performance Engineering Worktree Roadmap

## Purpose

The city project is a realistic workload provider, not the boundary of the
technical work. This roadmap builds scene-agnostic GPU primitives, algorithms,
and runtime systems for real-time simulation and robotics sensor data.

Each stage has its own branch and sibling worktree. A later stage starts only
from the previous stage's validated local commit, never from a dirty working
tree. This preserves physical isolation while keeping the algorithm history
intentionally stacked and reversible.

## Milestones

| Stage | Worktree | Branch | Deliverable |
| --- | --- | --- | --- |
| S0 | `SUMMIT-gpu-spatial-index` | `codex/gpu-resident-spatial-index` | GPU-resident Morton/radix research baseline and reviewed AMD evidence |
| S1 | `SUMMIT-gpu-primitives` | `codex/gpu-primitives-benchmark` | Scene-agnostic scan, histogram, compaction, scatter, radix, validation, and same-process benchmark |
| S1T | `SUMMIT-gpu-dx12-timestamps` | `codex/gpu-native-dx12-timestamps` | Private-fence DX12 timestamps, schema-v5 provenance, and formal AMD primitive evidence |
| S2 | `SUMMIT-gpu-direct-binning` | `codex/gpu-direct-spatial-binning` | Generic uint-key count/scan/scatter into a CSR spatial index |
| S3 | `SUMMIT-gpu-adaptive-spatial-backend` | `codex/gpu-adaptive-spatial-backend` | Evidence-backed direct-versus-radix backend selection |
| S4 | `SUMMIT-gpu-dynamic-sensor` | `codex/gpu-dynamic-sensor-pipeline` | Changing sensor inputs and an end-to-end GPU-resident processing graph |
| S5 | `SUMMIT-gpu-data-layout` | `codex/gpu-quantized-soa-fusion` | Quantized SoA layouts, compression, and producer-consumer fusion |
| S6 | `SUMMIT-gpu-shared-spatial-index` | `codex/gpu-multisensor-shared-index` | One spatial build reused by multiple sensor and annotation consumers |
| S7 | `SUMMIT-gpu-deadline-scheduler` | `codex/gpu-deadline-aware-scheduling` | Deadline-aware compute/copy/graphics scheduling and backpressure |
| S8 | `SUMMIT-gpu-residency-manager` | `codex/gpu-residency-manager` | Budgeted large-map and point-cloud paging, feedback, prefetch, and eviction |
| S9 | `SUMMIT-gpu-cross-vendor-autotune` | `codex/gpu-cross-vendor-autotuning` | Wave/thread-group/layout autotuning with AMD and later NVIDIA validation |

S9 is created only when NVIDIA hardware is available. Offline compilation is
not treated as NVIDIA performance validation.

## Isolation contract

Every stage must:

1. Use a dedicated sibling worktree under `C:\Users\EdwinLiu\Downloads`.
2. Start from an immutable verified milestone commit.
3. Avoid changes to `GISTutorial`, other worktree indexes, or other branches.
4. Keep production scenes and generated data outside Git as verified fixtures.
5. Keep raw multi-megabyte player logs local; commit only reviewed compact
   evidence and manifests.
6. Use English for source comments, documents, and commit messages.
7. Finish with a clean local commit before the next worktree is created.

## Benchmark contract

Performance evidence must include:

- a CPU oracle independent of the GPU implementation;
- exact correctness gates before and after measurement;
- one Player process with counterbalanced variant blocks;
- constrained, position-balanced AB/BA pairs, ABBA/BAAB, or balanced Latin
  square ordering, plus case-local warmup for short kernels where block
  position can dominate;
- fence-drained transitions that are excluded from samples;
- no GPU readback, buffer resize, or managed allocation during measurement;
- raw per-frame samples plus paired summaries;
- GPU average, P95, P99, and control-drift checks;
- logical bytes moved, resident/scratch bytes, dispatch count, and workload
  configuration;
- GPU, driver, graphics API, Unity version, Git commit, and shader/source hash;
- AMD RGP/RGA evidence where applicable, and NVIDIA Nsight evidence only after
  an actual NVIDIA run.

A performance claim is accepted only when correctness remains unchanged and
the paired result is directionally stable. A negative result or a workload
crossover is retained when the experiment is valid.

## External production fixture

The full production scene is intentionally not a 361 MB Git blob. It is copied
from a verified external fixture before a production-scene run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File Tools/Sync-NYCGISGpuBenchmarkFixture.ps1 `
  -FixtureRoot 'C:\path\to\SUMMIT-GPU-Benchmark-Fixtures'
```

Expected scene SHA-256:

```text
B1E650A08F43F8A47C470CB1EB4796FCF6926209BE36DBFED7E5E5FB2CA6FB5E
```

Scene-agnostic primitive benchmarks must not require this fixture.

## Integration policy

This research chain is based on the frozen GPU research lineage and is not
rebased midstream onto the current main branch. After the reusable modules are
validated, a separate integration worktree ports selected package commits onto
the latest production line.
