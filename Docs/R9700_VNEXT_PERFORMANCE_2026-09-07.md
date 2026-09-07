# R9700 vNext complete performance matrix — 2026-09-07

All nine prescribed stages completed, with full original rounds and sample counts. This does not imply that every candidate improved performance: Index total GPU mean regressed in all 54 scenarios, and no default was promoted. Index remains explicitly Editor-only comparison evidence. No push was performed.

## Accepted evidence

All paths below are repository-relative. Each stage contains before/after environment identities and a complete `stage.json` receipt.

| Stages | Root under Reports | Measured source commit |
|---|---|---|
| AdaptiveDiscovery, AdaptiveFreeze, AdaptiveEvaluation, Query, Scheduler | vnext-performance-20260907-044621-937be3 | 8452d4713b023a015f1adca2b55eae44f9af7cd8 |
| Primitives, ResidencyGpu | vnext-performance-20260907-055300-b8eb56 | 5dbc417ad9a95d24a33dd29dcb555e4cbf2adfd9 |
| ResidencyCpu | vnext-performance-20260907-061652-f7acf9 | c96373120ab9daeeeffd16f7620169f6a1a7595c |
| Index | vnext-performance-20260907-062549-2cb7ce | 090b33c56ec7e7af06674280d7d56e78af7d7d32 |

The three Adaptive stages remain coupled to one frozen matrix, original build and holdout ordering. No failed or incomplete stage samples were pooled into accepted data. All shader and timestamp-plugin identities remained unchanged across these benchmark-harness fixes.

## Findings

- Primitives: large uniform radix sort, Wave t256/e2/r4, approximately 5.163→0.209 ms versus legacy WaveOps (95.9% lower mean). Small workloads and some Portable candidates regress. Reduce uses the new Portable candidate as its internal reference because no legacy Reduce exists.
- Query: PointChunks mean improves in 10/12 cells; PointChunksWave in 9/12. At N=262145, single-cell Wave query mean is 59.755→0.551 ms, while sparse Wave query mean is 0.900→3.479 ms (286.5% worse). Additional dispatch cost remains material.
- Index: no mean total-GPU wins in 54 cells; P99 improves in only one. Mean regressions range from approximately 1.1% to 58.2%. Correctness and matrix completion do not establish an optimization win.
- Adaptive: 24 frozen matrix rows, one Radix row; 8176 Direct and 464 Radix selections in the 8640 Adaptive evaluation samples. Most comparisons execute the same Direct kernel; differences cannot all be attributed to the selector.
- CPU residency: heap LRU mean improves in 12/15 cells. At 32768 slots/overcapacity, mean is 163.550→5.963 ms; at 384 slots/prefetch, 0.0167→0.0240 ms. The accepted four serialized repetitions contain 28800 measured frames with zero counted allocation.
- GPU residency: CPU planning plus staging mean improves in 4/6 cells. Logical uploads are identical between paired LRU policies. Observed GPU differences must not be described as a heap-driven GPU kernel improvement or sparse-resource benefit.
- Scheduler: critical-task P99 changes vary by scenario and round. All offered work completes, but the critical deadline miss rate remains 100% for both variants. The reference is bounded FIFO, not the best of every legacy scheduler.

## Repairs and retained failures

1. Primitive readback validation now copies results into owned arrays in completion callbacks, outside measured scopes. A 120-frame delayed-consumer regression passed before the complete seven-cell rerun. The original lifetime-race explanation is an inference, not a proven driver diagnosis.
2. The Index Editor harness could complete timestamp waits synchronously without yielding Editor updates. It now clears command references and yields after every completed sample. Three earlier runs encountered DX12 device removal with driver-internal reason 0x887a0020. The requested debug layer could not activate because Windows Graphics Tools is absent. The first yielded run exceeded the framework's default 180-second timeout; the dedicated test now allows 30 minutes while each native wait remains bounded by 20 seconds. The full matrix then passed in approximately 575 seconds. The exact internal driver mechanism remains unproven.
3. CPU residency retains its zero-allocation assertion and now records raw frames plus component counters. A serialized prior attempt failed in repetition 3 with 7184 bytes at slots32768/prefetch/heap frame62. Its source remains unexplained. The subsequent four complete serialized repetitions passed. Earlier un-serialized four-pass data is explicitly excluded from acceptance.
4. GPU residency's prerequisite gate now accepts only the three exact known ignored cases while rejecting failures, unknown skips and zero-pass runs. All three formal capacity runs executed the 600-test suite: 597 passed, zero failed, three known skips each.
5. Unity test runners wait for the actual Editor process, avoiding idle lock retention by persistent compiler descendants.

## Independent checks and artifacts

Independent verification checked 434700 Primitive rows, 51840 Query rows including controls, 31104 Index rows including warmup, 25920 Adaptive evaluation samples, 28800 CPU residency frames, 86400 GPU residency samples and 38920 measured Scheduler jobs. Index prerequisite tests passed 83/83 and its full comparison passed 1/1. Primitive raw tick arithmetic and all 93312 Index timestamp intervals matched reported times. A manifest records SHA-256 hashes for 507 raw evidence files.

Mean/P95/P99 use raw measured samples and nearest-rank percentiles. Same-round comparisons subtract marginal statistics; a P99 difference is not the P99 of sample differences. Scheduler's displayed cross-round tail summary is the average of four round P99 values, not a pooled P99. Allocation scopes and configured buffer memory are explicit; configured buffers are not process peak-memory measurements.

The complete Chinese HTML report, English audit, CSVs, SQL cross-check, raw hash manifest and QA receipt are in:

`C:/Users/EdwinLiu/Documents/Codex/2026-09-07/r9700-vnext-integration/outputs`

Primary files: `performance-report.zh.html`, `performance-audit.en.md`, `performance-evidence.json`, `paired-comparisons.csv`, `all-metrics.csv`, `resources-and-gc.csv`, `performance-raw-hashes.json`, `report-qa.json`.

The shared report reader had an 8-pixel scrollbar-related top-bar overflow on Windows. Only the report's embedded CSS copy was corrected from viewport width to containing-block width; the shared plugin and verification gates were unchanged. Final canonical payload, desktop/narrow layout (1440/390), source interaction, chart extraction and no-network/error checks passed.

The machine is a shared Windows desktop. A shared mutex serializes controlled builds/Unity/GPU work; GPU exclusivity is not claimed. Auxiliary GPU counters have invalid samples and gaps. No runtime default changes or main fast-forward are included in this performance follow-up.
