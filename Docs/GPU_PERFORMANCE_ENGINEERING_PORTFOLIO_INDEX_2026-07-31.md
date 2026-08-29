# GPU Performance Engineering Portfolio Index

> Historical AMD snapshot dated 2026-07-31. NVIDIA RTX 4090 primitive and
> device-keyed autotuning follow-up completed on 2026-08-29; see
> `GPU_PRIMITIVES_NVIDIA_RTX4090_FORMAL_RESULTS_2026-08-29.md` and
> `GPU_CROSS_VENDOR_AUTOTUNING_NVIDIA_RTX4090_REPORT_2026-08-29.md`.

## Overall status

The planned AMD phase is complete. Every implementation lives in an independent
Git worktree and branch; none was merged into the active production branch by
this work. NVIDIA execution remains the only hardware-dependent follow-up.

All performance claims below are limited to AMD Radeon AI PRO R9700 and the
stated synthetic or city-scene workload. Negative and mixed results are retained
rather than rewritten as wins.

## Reusable GPU/data systems

| Project | Branch | AMD result | Portfolio interpretation |
|---|---|---|---|
| Native DX12 timestamps and GPU primitives | `codex/gpu-native-dx12-timestamps` / `codex/gpu-primitives-benchmark` | Wave exclusive scan +29.7%, radix sort +16.82%, stable compaction +26.57%; 29,700/29,700 timestamp samples valid | Reusable GPU primitives, native timing ABI, correctness/provenance harness |
| Direct count-scan-scatter spatial binning | `codex/gpu-direct-spatial-binning` | Correct, but 0/3 workloads met the frozen performance gate | Valuable negative result: custom direct binning is not automatically faster |
| Adaptive Direct/Radix backend | `codex/gpu-adaptive-spatial-backend` | Forced Radix won two exact single-bin cells by 30.30-47.59% average; Direct won three hotset/uniform cells by 66.98-97.68%; offline exact-cell replay 5/5 | Algorithm selection changes with contention/distribution; runtime facade itself was not timed |
| Dynamic GPU-resident sensor pipeline | `codex/gpu-dynamic-sensor-pipeline` | GPU average +85.99-89.66%, block P99 +82.85-87.81%, 8/8 wins in both workloads | Removes 5-20 MiB/update logical CPU payload and keeps producer/index/consumer GPU resident |
| Packed SoA, quantization, and fusion v2 | `codex/gpu-sensor-data-end-cursor` | 1.05M workload: GPU average +7.16%, P99 +9.74%, 8/8; 262K workload was neutral under the absolute-time gate | Strong data-layout/fusion implementation, but no universal cross-workload performance claim |
| Shared GPU index for four sensors | `codex/gpu-multi-sensor-shared-index` | GPU average +65.40-68.37%, P99 +66.16-67.99%, 8/8 in both workloads | Producer-consumer reuse and elimination of redundant index construction |
| Deadline-aware GPU scheduler | `codex/gpu-deadline-aware-scheduler` | Critical P99 +77.55-88.92%; miss rate 0.83-2.64% to 0-0.014%; async correctly rejected on saturated AMD | Latency/SLO optimization with adaptive queue admission, not a throughput speedup |
| GPU page residency manager | `codex/gpu-residency-manager` | Upload -92.31-92.32%, GPU average +45.63-45.88%, P99 +29.74-35.90%, 8/8 | Virtual page table, LRU physical slots, compact deltas, and compute scatter for maps/point clouds |
| Device-keyed GPU autotuning | `codex/gpu-cross-vendor-autotuning` | Independent evaluation: scan +29.90% avg/+20.49% P99; radix +15.33%/+13.93%; compaction +24.72%/+23.92%; all 4/4 | Vendor-neutral fingerprint/profile/fallback pipeline; performance validated on AMD only |

## City-rendering experiments

| Project | Branch | Result | Decision |
|---|---|---|---|
| Wave64 visible-cluster compaction | `codex/gpu-wave-compaction-rgp` | Returned atomic reservations reduced at least 98.19%; four-camera GPU average +2.28%, P99 +6.69% | Valid HLSL/WaveOps/ISA story; does not remove index bandwidth |
| No-copy Visible Tiles | `codex/gpu-no-copy-visible-tiles` | About 96.4M visible indices replaced by about 1M descriptors; output 385.7 MB to 8.04 MB; single-camera GPU average/P99 +52.5%/+51.9%; four-camera frame average/P99 +55.5%/+16.5% | Strongest city-scene bandwidth/algorithm result |
| Cluster-local uint16 indices | `codex/gpu-cluster-local-index16` | Source-index residency contract -49.74%; one-camera performance neutral/negative, four-camera GPU average +0.38% but frame average -0.14% | Keep as a memory-format/fallback experiment, not a frame-time claim |
| Cinematic NYC stress A/B | `codex/gpu-cinematic-benchmark` | Clean rerun pending after adding exact payload-pass, stable-residency, image-equivalence, and source/binary provenance gates | Visual integration of the accumulated GPU stack; no retained percentage until the corrected A/B completes |

## Accurate claim boundaries

- AMD RDNA 4 is validated; NVIDIA is not.
- Native timestamps measure specified DX12 command-list regions, not universal
  end-to-end application latency.
- Logical byte/write/read accounting is not a measured PCIe, DRAM, cache, or
  physical-VRAM counter unless a report explicitly says otherwise.
- The residency manager uses fixed `GraphicsBuffer` storage, not DX12 reserved
  resources or sparse/tiled-resource binding.
- The deadline scheduler selected same-queue least-slack ordering on AMD; it is
  not an async-compute speedup claim.
- The packed-SoA result is accepted only for the 1.05M workload; the smaller
  workload did not clear the absolute-time gate.
- The adaptive spatial report measures forced backends and offline policy replay,
  not the runtime adaptive facade overhead.
- The earlier cinematic pilot was invalidated after review found duplicate
  payload-camera rendering and unmatched BFP2 residency. It is intentionally
  excluded until a clean gated rerun completes.

## NVIDIA completion protocol

When NVIDIA hardware becomes available, rerun the existing formal presets with
the NVIDIA device index. Do not tune thresholds on the formal holdout. Generate
a separate device-keyed autotune profile, then report AMD and NVIDIA results as
two hardware-specific rows. Until then, use “vendor-neutral infrastructure,
AMD-validated performance,” never “cross-vendor validated.”
