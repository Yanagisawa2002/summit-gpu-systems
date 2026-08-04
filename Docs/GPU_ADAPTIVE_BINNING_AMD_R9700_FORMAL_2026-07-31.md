# GPU Adaptive Spatial Binning: AMD R9700 Formal Holdout

## Decision

The schema-9 formal run passed its frozen acceptance contract for a narrow,
five-cell synthetic holdout on one AMD Radeon AI PRO R9700. The evidence
supports two distinct conclusions:

1. Forced-backend A/B selected Radix for two exact single-bin cells and Direct
   for three hotset/uniform cells.
2. An offline replay of the fail-closed exact-cell policy predicted the faster
   forced backend in all 5/5 cells.

This does **not** show that `RecordAdaptive` itself is faster. The run did not
time the adaptive facade or selector overhead. It also does not validate a
broader threshold, a production selector, a city scene, a sensor pipeline, or
another GPU.

The retained evidence is:

```text
Reports/GpuAdaptiveBinning/formal-amd-r9700-contention-53058f0-v1
```

## Hardware and software scope

- Git commit: `53058f00d9f86077debaaf9c059e295219575b5f`
- Unity: `6000.5.2f1`
- Operating system: Windows 11
- Graphics API: Direct3D 12, feature level 12.2
- GPU: AMD Radeon AI PRO R9700
- PCI vendor/device IDs: `0x1002` / `0x7551`
- Driver: `32.0.31035.1003`
- Unity-reported graphics memory: 32,476 MiB
- Native timestamp ABI: 2; capability flags: `0x1F`
- Primitive backend: WaveOps
- Key domain: guaranteed in range
- Within-bin ordering contract: unspecified
- Inner profiler markers: disabled for both measured backends

This is one device and one driver. It is AMD-only evidence, not NVIDIA or
cross-vendor validation.

## Compared algorithms

The two independently forced GPU-resident CSR implementations were:

- **Direct:** clear, trusted count, exclusive scan, prepare, and trusted
  atomic scatter.
- **Radix:** clear, low-bit radix sort, sorted-run range extraction, exclusive
  scan, and terminal-offset generation.

Both consumed the same generated uint key/value input and produced the shared
`binCounts`, `binOffsets`, and `binnedValues` contract. Ordering among values
with the same key is outside that shared contract.

## Formal protocol

The matrix used `formal-amd-r9700-contention-v1`, `matrixRole=holdout`, and
`formalAcceptanceMode=1`. Each cell ran in one Player process with:

```text
control-pre; ABBA; BAAB; ABBA; BAAB; control-post
```

Per cell, the frozen shape was:

- four super rounds;
- eight adjacent A/B pairs, balanced as four AB and four BA;
- 60 case-local warmup frames;
- 900 measured frames per block;
- 15 cooldown frames;
- one dispatch per measured frame;
- 18 blocks and 16,200 complete native timestamp rows;
- Direct and Radix correctness validation before and after measurement.

`A = Direct` and `B = Radix`. Native D3D12 timestamp queries measured the
complete command-buffer region for each forced backend. All five cells had
complete timings, zero acquire/result failures, zero timeouts, and an empty
scope P99 of 0.00008 to 0.00012 ms, below the 0.005 ms gate.

There was zero benchmark-output readback in the timed path. Native timestamp
instrumentation still returned 16 bytes per completed sample, or 259,200
bytes per cell. Correctness readback occurred outside measurement. Therefore
"zero timed benchmark-output readback" is accurate; "no CPU-visible transfer
of any kind" is not.

## Exact five-cell results

The table reports the winning backend's reduction relative to the other
forced backend. Percentages are medians of the eight paired deltas, not ratios
recomputed from independently aggregated time columns. P99 is the P99 of the
measured GPU kernel region, not full-frame P99.

| N | C | Distribution | Exact single-bin key | Winner | GPU average reduction | Absolute average reduction | GPU P99 reduction | Average pairs won | P99 pairs won | Worst-pair P99 reduction |
|---:|---:|---|---:|---|---:|---:|---:|---:|---:|---:|
| 262,144 | 16 | singlebin | 9 | Radix | 30.30% | 0.1213 ms | 19.73% | 8/8 | 8/8 | 9.70% |
| 1,048,576 | 16 | singlebin | 10 | Radix | 47.59% | 0.6092 ms | 31.69% | 8/8 | 8/8 | 16.78% |
| 1,048,576 | 16 | hotset4 | n/a | Direct | 66.98% | 0.4523 ms | 71.99% | 8/8 | 8/8 | 70.80% |
| 1,048,576 | 16 | uniform | n/a | Direct | 83.57% | 0.5515 ms | 87.77% | 8/8 | 8/8 | 86.13% |
| 1,048,576 | 65,536 | uniform | n/a | Direct | 97.68% | 2.5239 ms | 98.04% | 8/8 | 8/8 | 96.85% |

The frozen decisive gate accepted two Radix cells and three Direct cells. All
five also passed the separate selector tail guard: at least 7/8 P99-winning
pairs were required, a worst-pair P99 regression down to -10% was allowed,
and every cell instead won P99 in 8/8 pairs with a positive worst-pair result.
This establishes tail consistency for these kernel regions and exact cells
only. It is not evidence of full-frame or production-scene tail stability.

## Offline exact-cell classification replay

The replayed policy was:

```text
Radix only for an exact calibrated
  (N, C, distribution, exact-single-bin-key) tuple;
Direct otherwise.
```

The two exact Radix tuples were:

```text
N=262144,  C=16, distribution=singlebin, exact key=9
N=1048576, C=16, distribution=singlebin, exact key=10
```

The policy prediction matched the faster forced backend in all 5/5 formal
cells. At fixed `N=1,048,576`, `C=16`, and seed `20261002`, both frozen
dominant-set-cardinality brackets changed in the required direction:

- cardinality 1 to 4: Radix to Direct;
- cardinality 1 to 16: Radix to Direct.

This is `classification-replay-not-recordadaptive-timing`. The five Player
runs forced Direct and Radix; they did not invoke and time `RecordAdaptive`.
The evidence records:

```text
selectorPolicyClaimUsable=1
selectorPolicyTailClaimUsable=1
recordAdaptiveTimingMeasured=0
selectorRuntimeOverheadMeasured=0
selectorBroaderThresholdValidated=0
selectorProductionReady=0
```

The exact-cell selector therefore has a validated classification result in
this five-cell holdout. Its Direct fallback outside those cells is a
fail-closed safety policy, not proof that Direct is optimal for all unmeasured
inputs.

## Correctness

All warmup and final validation rows passed for both forced backends in all
five cells. Counts, offsets, canonical per-bin membership, and diagnostics
matched the CPU oracle. Direct and Radix produced the same canonical CSR hash
within each cell, all keys were valid, and diagnostic flags remained zero.

The source-bound EditMode run also passed 424/424 tests. These checks support
functional equivalence under the shared contract; they do not broaden the
performance domain.

## Modeled logical memory, not measured bandwidth

`caseResidentBytes` is a model built from shared logical buffers and backend
scratch payloads. It is not a driver residency counter, physical VRAM
allocation, or DRAM-bandwidth measurement.

| N | C | Direct modeled bytes | Radix modeled bytes | Radix / Direct |
|---:|---:|---:|---:|---:|
| 262,144 | 16 | 3,146,320 | 8,528,048 | 2.710x |
| 1,048,576 | 16 | 12,583,504 | 34,111,760 | 2.711x |
| 1,048,576 | 65,536 | 14,452,752 | 34,635,920 | 2.396x |

No bandwidth counter, cache hit rate, occupancy, ISA statistic, atomic count,
or driver-reported residency was captured. Do not convert this logical byte
accounting into a measured bandwidth or physical-memory claim.

## Claim boundaries

It is safe to claim:

- two correctness-equivalent GPU-resident spatial-binning algorithms;
- native DX12 GPU-kernel-region timing with balanced forced-backend A/B;
- the exact per-cell average and P99 results above on the tested R9700;
- two accepted Radix cells, three accepted Direct cells, and two observed
  Radix-to-Direct winner changes at fixed `N` and `C`;
- an offline exact-cell classification replay that matched 5/5 forced winners;
- tail-guard success for the five measured kernel cells;
- zero benchmark-output readback in the timed path, while disclosing timestamp
  instrumentation readback;
- modeled logical buffer payloads with the stated memory boundary.

Do not claim:

- that `RecordAdaptive` delivered any of the measured percentages;
- measured selector or adaptive-dispatch overhead;
- a general concentration threshold, interpolation, or universal winner;
- production readiness or stable full-frame P99;
- FPS, frame-time, city-scene, sensor-pipeline, or visible-quality gains;
- measured DRAM bandwidth, cache behavior, occupancy, ISA quality, atomic
  traffic, or physical VRAM residency;
- async-compute overlap or driver-level optimization;
- NVIDIA or cross-vendor validation;
- statistical significance beyond the frozen paired acceptance gates.

## Safe resume wording

> Engineered and validated two DX12 GPU-resident spatial-binning backends --
> trusted count/exclusive-scan/scatter and low-bit radix sort/range extraction
> -- on an AMD Radeon AI PRO R9700. In a five-cell synthetic holdout with
> 262K to 1.05M elements and 16 to 65,536 bins, counterbalanced forced-backend
> A/B showed Radix reducing GPU-kernel average/P99 by 30.30% to 47.59% / 19.73%
> to 31.69% for two exact single-bin cells, while Direct reduced them by 66.98%
> to 97.68% / 71.99% to 98.04% for three hotset/uniform cells; every result
> matched a CPU oracle with zero benchmark-output readback in the timed path.

> Calibrated a fail-closed, hardware-bound exact-cell selector whose offline
> classification replay matched the faster forced backend in 5/5 holdout
> cells and captured two Radix-to-Direct winner changes at fixed
> `N=1,048,576`, `C=16` as dominant-set cardinality increased from 1 to 4/16.
> The predicted forced winner won average and kernel-region P99 in all 8/8
> paired blocks for every cell.

Keep "forced-backend A/B" and "offline classification replay" in the wording.
Do not rewrite either bullet as an adaptive-runtime, scene, FPS, or
cross-vendor result.
