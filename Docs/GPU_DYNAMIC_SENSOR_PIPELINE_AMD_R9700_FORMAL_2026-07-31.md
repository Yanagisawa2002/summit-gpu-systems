# Dynamic GPU-Resident Sensor Pipeline - AMD R9700 Formal Result

- Date: 2026-07-31
- Benchmark source commit:
  `461bcee85ff063b8fab0bf5916eb86f4b652f7ad`
- Branch: `codex/gpu-dynamic-sensor-pipeline`
- Evidence:
  `Reports/GpuSensorPipeline/formal-amd-r9700-461bcee-v1`

## Outcome

The formal holdout accepted the GPU-resident producer in both frozen workload
cells.

- `N=262,144`, `Q=64`: 8/8 paired wins; median paired GPU-average
  improvement `85.99%`; median paired block-P99 improvement `82.85%`.
- `N=1,048,576`, `Q=256`: 8/8 paired wins; median paired GPU-average
  improvement `89.66%`; median paired block-P99 improvement `87.81%`.
- At all 20 checkpoints, every entry in the 64-state CPU/GPU aggregate
  frame-digest rings matched. Final-resident key scans reported zero invalid
  keys.
- The measured workload performed zero readback. Validation and native
  timestamp instrumentation readbacks were outside or separate from the
  measured workload.

This supports a narrow claim: on the tested AMD Radeon AI PRO R9700, moving
deterministic dynamic sample/key production from CPU generation plus logical
`SetBufferData` payloads to a GPU-resident producer substantially reduced the
measured D3D12 main-graphics-command-list interval for this synthetic sensor
workload.

## Compared paths

### A - CPU-produced, uploaded

1. Generate a dynamic 16-byte sample and 4-byte spatial key per element on the
   CPU.
2. Record both logical `SetBufferData` payloads through a persistent four-slot
   staging path.
3. Build the GPU-resident CSR index with direct
   `count -> scan -> scatter`.
4. Execute the payload-consuming range-query and aggregate frame-digest
   reduction.

### B - GPU-produced, resident

1. Generate the same dynamic sample/key sequence directly on the GPU.
2. Keep the produced buffers GPU resident.
3. Build the same CSR index and execute the same range-query and aggregate
   frame-digest reduction.

Both paths used the WaveOps primitive backend, fixed `C=262,144`, identical
queries, the same 64-state sequence, and the same downstream consumer. The
portable HLSL backend remains available in the runtime package, but it was not
the backend measured in this AMD holdout.

## Formal result

The A/B values below are medians of eight block-level values for each path.
Each block P99 is the nearest-rank P99 of its 900 measured frames. The paired
percentage is the median of eight adjacent-pair percentage changes; it is not a
global run P99 and is intentionally computed independently from the displayed
A/B medians.

| Elements / queries | A GPU avg | B GPU avg | Median paired avg improvement | A block P99 | B block P99 | Median paired block-P99 improvement | Paired wins |
|---|---:|---:|---:|---:|---:|---:|---:|
| 262,144 / 64 | 0.2927 ms | 0.0409 ms | 85.99% | 0.4544 ms | 0.0751 ms | 82.85% | 8/8 |
| 1,048,576 / 256 | 0.9669 ms | 0.0988 ms | 89.66% | 1.2583 ms | 0.1539 ms | 87.81% | 8/8 |

The median paired absolute GPU-average reductions were `0.2483 ms` and
`0.8687 ms`, respectively. Position balance also held:

| Elements / queries | AB median improvement | BA median improvement |
|---|---:|---:|
| 262,144 / 64 | 85.89% | 85.99% |
| 1,048,576 / 256 | 89.84% | 89.59% |

The empty-scope control P99 was `0.00008 ms` in both cells, below the frozen
`0.005 ms` sanity limit.

## Data movement and resource accounting

The logical producer footprint is 20 bytes per element: a 16-byte sample plus
a 4-byte key.

| Elements | A logical payload per update | B logical host payload per update | B logical device write per update | A isolated host staging | B isolated host staging |
|---:|---:|---:|---:|---:|---:|
| 262,144 | 5,242,880 B (5 MiB) | 0 | 5,242,880 B | 20,971,520 B (20 MiB) | 0 |
| 1,048,576 | 20,971,520 B (20 MiB) | 0 | 20,971,520 B | 83,886,080 B (80 MiB) | 0 |

The paired harness still allocated A's four-slot staging ring so both variants
could run in one process. The zero value is B's isolated requirement, not the
measured process allocation. These are API/data-contract byte counts. They do
not measure PCIe traffic, transfer duration, or a dedicated copy queue.

The shared harness reported these logical `GraphicsBuffer` allocations:

| Elements / queries | Actual benchmark GPU-resident accounting |
|---|---:|
| 262,144 / 64 | 14,824,520 B (14.138 MiB) |
| 1,048,576 / 256 | 36,850,760 B (35.144 MiB) |

The between-cell capacity increase was 22,026,240 bytes. These values describe
shared harness buffer accounting; they are neither an A-to-B VRAM delta nor
driver-reported physical residency.

Readback accounting per cell was:

- measured workload: `0 B` per frame and `0 B` total;
- correctness validation: `320 B` outside timing;
- native timestamp instrumentation: `259,200 B` reported separately
  (`16 B x 16,200` rows, including 1,800 control and 14,400 measurement
  rows).

## Secondary CPU-side harness timings

The paired synthetic harness also recorded CPU producer, command-recording,
and submission durations with `Stopwatch`.

| Elements / queries | A CPU producer avg | B CPU producer field | A pipeline-record avg | B pipeline-record avg | A submission avg | B submission avg |
|---|---:|---:|---:|---:|---:|---:|
| 262,144 / 64 | 3.5148 ms | 0 ms | 0.1839 ms | 0.00184 ms | 0.2541 ms | 0.00814 ms |
| 1,048,576 / 256 | 13.3658 ms | 0 ms | 0.8256 ms | 0.00197 ms | 2.4947 ms | 0.00845 ms |

B's producer field is zero by definition because the CPU producer timer is not
invoked for B; it is not an independently timed 100% CPU speedup. Both Player
logs also contain Unity's `Ran out of Graphics Ring Buffer space` warning.
Therefore, pipeline-record and submission figures are secondary diagnostics,
not headline performance claims.

Work-slot fence waits further separate the timestamped interval from
end-to-end throughput. A recorded zero staging-wait frames. B recorded 113
total wait frames in the small cell and 5,298 in the large cell; the maximum
single-block totals were 44 and 1,149. These waits occur outside the reported
native GPU interval, so the result must not be converted into FPS or sensor
throughput.

## Method and evidence gates

- Unity `6000.5.2f1`, Direct3D 12, device index 0.
- AMD Radeon AI PRO R9700, vendor ID `4098`, device ID `30033`, driver
  `32.0.31035.1003`.
- One machine and one Player process per frozen cell. Eight pairs are
  within-process observations, not eight independent process launches.
- Four super-rounds, eight adjacent A/B pairs per cell, four AB and four BA
  orders.
- One control-pre block, 16 measurement blocks, and one control-post block per
  cell.
- 60 case-local warm-up frames, 900 measured frames, and 15 cooldown frames per
  block.
- 16,200 native timestamp samples per cell.
- Native D3D12 timestamp ABI 2 on the main graphics command list, with unique
  token/tag checks, consecutive fence checks, non-overlapping tick ranges,
  fixed device generation, and fixed timestamp frequency.
- Ten out-of-timing validation checkpoints per cell: before, after each of
  eight pairs, and after.
- Every checkpoint compared all 64 CPU/GPU aggregate frame-digest entries and
  validated final-resident keys.
- Fresh, source-bound Unity EditMode result: all 37 S4-specific expanded cases
  passed; the entire project run passed 410/410 with 0 failed, skipped, or
  inconclusive.
- The runner captured a clean source commit at run time, stable source hashes,
  a stable Player payload, and finalized provenance.

## Claim boundaries

The evidence does not establish any of the following:

- Unity scene FPS or city-scene frame time;
- live camera, LiDAR, radar, or point-cloud ingestion;
- end-to-end sensor latency, steady-state throughput, or deadline compliance;
- PCIe bytes or duration, upload-queue duration, or copy-queue duration;
- async-compute overlap;
- NVIDIA behavior or cross-vendor autotuning;
- production integration with a robotics policy, perception model, or data
  writer;
- per-query output identity or complete-buffer bit identity in the formal run.

The timestamped metric is the D3D12 main-graphics-command-list region. The
configuration explicitly records
`uploadQueueCoverageVerified=false`,
`asyncComputeClaim=false`, and `copyQueueClaim=false`.

## Resume-ready wording

> Built and formally profiled a GPU-resident synthetic dynamic-sensor pipeline
> in Unity 6000.5.2f1/D3D12 on an AMD Radeon AI PRO R9700. Replaced per-update
> CPU generation and 5-20 MiB/update of logical `SetBufferData` payloads with a
> GPU producer feeding a count-scan-scatter CSR index and range-query consumer.
> Across 16 within-process counterbalanced A/B pairs with 900 measured frames
> per path, reduced the main-graphics command-list interval by a paired median
> 85.99-89.66% for GPU average and 82.85-87.81% for block P99. All 64 CPU/GPU
> per-state aggregate frame digests matched at every checkpoint, final key
> scans found zero invalids, and measured-workload readback was zero.

Use this wording only with the AMD, synthetic-workload, within-process, and
metric-scope qualifiers above. NVIDIA and production sensor claims remain
future work.

## Retained evidence

The retained directory contains the raw 900-frame CSVs, block and pair
summaries, validation rows, device/config metadata, finalized runner
provenance, Player payload manifest, Unity build/EditMode logs, NUnit XML, and
quality summaries.

`evidence-manifest-sha256.csv` records the length and SHA-256 digest of all 28
retained source evidence files. The manifest excludes itself by design. The
retained files are byte-identical to the external formal output package.
