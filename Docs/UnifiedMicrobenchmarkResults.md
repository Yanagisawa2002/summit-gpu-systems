# Frozen SUMMIT microbenchmark results

Source: `46f09b0c337e303378beab705c6df3153b2119f1`. Five independent Release Player processes; 41760 measured observations in 18 fixed cells.

Paired block arithmetic means -> log ratios -> mean within each process; t(4) 95% CI over five process log ratios. Tail ratios use within-block empirical quantiles then the same aggregation. CV gates on within-process block means and across-process means for both arms; raw observation CV also disclosed. Baseline drift is maximum first/last chronological block change within each process and first/last process mean change. Unadjusted per-cell CIs, no familywise guarantee.

30 query or 24 index observations per block; p99 is interpolation near the maximum and has insufficient rare-tail support. No p99 improvement claim.

GPU intervals include every dispatch/reset/indirect argument update in the recorded operation. CPU waits are not GPU time; empty controls are retained without subtraction. Upload, setup, async readback request/wait/copy, CPU record and submission are separate in raw evidence.

## Query candidate versus CellSerial

| Cell | Baseline ms | Candidate ms | Speed ratio [95% CI] | p95 ratio | Max unit CV | Drift | Verdict |
|---|---:|---:|---|---:|---:|---:|---|
| hotspot-n262145 | 51.062736 | 0.036969 | 1441.437 [1377.736, 1508.084] | 1410.161 | 53.4% | 3.5% | inconclusive |
| hotspot-n4097 | 1.350360 | 0.009719 | 142.964 [137.115, 149.062] | 147.206 | 32.2% | 1.2% | inconclusive |
| hotspot-n65541 | 13.772195 | 0.014461 | 982.914 [898.330, 1075.462] | 956.500 | 37.2% | 0.7% | inconclusive |
| single-cell-n262145 | 63.362627 | 0.037799 | 1729.809 [1528.889, 1957.134] | 1695.649 | 53.5% | 2.1% | inconclusive |
| single-cell-n4097 | 1.301684 | 0.009927 | 135.930 [126.591, 145.958] | 136.745 | 36.0% | 2.1% | inconclusive |
| single-cell-n65541 | 16.285794 | 0.016068 | 1059.566 [1010.816, 1110.668] | 1036.657 | 42.8% | 2.4% | inconclusive |
| sparse-n262145 | 1.164881 | 0.026933 | 44.321 [43.970, 44.675] | 49.466 | 28.2% | 9.2% | inconclusive |
| sparse-n4097 | 0.292353 | 0.005650 | 51.865 [49.342, 54.517] | 50.266 | 10.0% | 3.1% | inconclusive |
| sparse-n65541 | 0.526471 | 0.010574 | 49.519 [43.643, 56.187] | 50.910 | 41.1% | 93.8% | inconclusive |
| uniform-n262145 | 2.045496 | 0.031266 | 66.615 [66.250, 66.983] | 66.641 | 23.9% | 0.8% | inconclusive |
| uniform-n4097 | 0.672992 | 0.009022 | 75.515 [73.153, 77.954] | 78.661 | 30.4% | 27.7% | inconclusive |
| uniform-n65541 | 1.253898 | 0.013736 | 93.780 [88.473, 99.407] | 101.857 | 37.9% | 6.5% | inconclusive |

## Index complete operation, including unchanged CellSerial consumer

| Cell / baseline | Baseline ms | Candidate ms | Speed ratio [95% CI] | p95 ratio | Max unit CV | Drift | Verdict |
|---|---:|---:|---|---:|---:|---:|---|
| lifecycle / full-direct-waveops | 1.537776 | 1.623638 | 0.949 [0.926, 0.972] | 0.918 | 11.1% | 21.8% | inconclusive |
| lifecycle / incremental-original | 1.620386 | 1.623638 | 0.999 [0.991, 1.007] | 0.982 | 11.1% | 1.8% | inconclusive |
| rates-n262144-c0-x0 / full-direct-waveops | 1.472921 | 1.544393 | 0.953 [0.924, 0.984] | 0.967 | 13.0% | 33.0% | inconclusive |
| rates-n262144-c0-x0 / incremental-original | 1.547605 | 1.544393 | 1.002 [0.943, 1.064] | 1.011 | 13.0% | 33.8% | inconclusive |
| rates-n262144-c1-x0 / full-direct-waveops | 1.525458 | 1.559578 | 0.981 [0.949, 1.013] | 0.984 | 13.3% | 8.5% | inconclusive |
| rates-n262144-c1-x0 / incremental-original | 1.579827 | 1.559578 | 1.014 [0.964, 1.066] | 1.001 | 13.3% | 7.1% | inconclusive |
| rates-n262144-c1-x1 / full-direct-waveops | 1.537502 | 1.610671 | 0.954 [0.917, 0.993] | 0.985 | 10.1% | 7.8% | inconclusive |
| rates-n262144-c1-x1 / incremental-original | 1.608903 | 1.610671 | 0.998 [0.979, 1.017] | 1.025 | 10.2% | 6.1% | inconclusive |
| rates-n262144-c100-x100 / full-direct-waveops | 2.229686 | 2.574176 | 0.865 [0.845, 0.886] | 0.871 | 12.3% | 1.4% | inconclusive |
| rates-n262144-c100-x100 / incremental-original | 2.596450 | 2.574176 | 1.008 [0.995, 1.022] | 1.025 | 7.4% | 14.8% | inconclusive |
| static90 / full-direct-waveops | 1.545406 | 1.602646 | 0.965 [0.936, 0.995] | 0.910 | 10.5% | 15.0% | inconclusive |
| static90 / incremental-original | 1.577101 | 1.602646 | 0.984 [0.954, 1.014] | 0.954 | 10.9% | 24.6% | inconclusive |

All reference-arm, maintenance-only, consumer-only and CPU results, gate failures and raw variability are in comparisons.csv and summary.json. No default backend is changed.

BatchedPointScanWave scans authoritative CSR membership and reuses sample/hash work across the query batch. It does not cull cells, so these fixed fixtures do not establish universal local-query or sparse-workload superiority. It records two dispatches and uses no scratch.

GpuDriven still records 13 index dispatch commands. GPU-generated zero-X indirect dispatches avoid unused shader work; State[15] reports nonempty dispatches (3 idle / 6 changed / 11 rebuild). Detection still launches all capacity groups. State[14] records actually inspected input slots, including the static snapshot contract. Changed-member maintenance inspects 2*changed slots instead of 2*capacity when no rebuild occurs. Extra resident storage is 4*capacity+120 bytes. Complete-operation results include the original CellSerial consumer and frame digest.

Formal samples are used once. Failed stability gates remain inconclusive; no resampling, cache clearing, driver changes or default promotion.

## Review and evidence status

Correctness: DX12 EditMode regression-03 passed 619 tests with zero failures and
three existing skips. A separate Release validation process matched the full
18-cell fixed matrix in 2,418 raw records. All five formal processes exited 0,
with 54,180 raw rows: 41,760 measured, 12,000 warmup, 420 empty controls. The
analyzer checked completeness, balanced positions, frame identity, source and
hardware identity, native timestamp conversion and per-operation digests.
Independent QA recomputed the sparse-N262145 paired confidence interval and
verified dispatch/memory accounting and native-token uniqueness. Four synthetic
statistics tests verify that duplicating correlated frames cannot narrow the
process-level interval, and that drift/missing processes cannot pass gates.

Hardware: AMD Radeon AI PRO R9700, driver 32.0.31041.1004, Unity 6000.5.2f1,
Direct3D12, native timestamp ABI 2. Formal Player PIDs were 30740, 9484, 31860,
25216 and 14176, running from 2026-09-07 15:46:10 through 15:50:46 UTC under
the shared mutex. Source manifest SHA256:
`04B2EA09C349F5B9C9A2E5ABBC4960989D752A3B2FE2B97670F115320700DA30`.

The compiler reported four warnings: three pre-existing obsolete Unity lookup
APIs and one harmless benchmark field hiding Component.tag. Build succeeded
with zero errors. No performance conclusion is based on warning counts.

One pre-formal validation attempt stopped before build because local Python
bytecode made the tree dirty. Commit 46f09b0 excludes that cache; no algorithm,
fixture or measurement count changed. The next full validation passed. A
separate HLSL development task reported a device-removal event at 15:39 UTC.
Our regression and Release validation ran in new processes afterwards, at
15:43 and 15:45. No earlier diagnostic timings enter these formal statistics.
See failure-and-device-epoch.json for exact times and attribution.

## Reference arms and fixed-cost findings

The preserved reference arms matter: PointChunks on sparse N262145 is a stable
regression versus CellSerial (1.164881 -> 2.556132 ms; ratio 0.455508,
95% CI [0.452625, 0.458409]). PointChunksWave is also a stable regression in
that cell (3.643913 ms; ratio 0.319529 [0.317405, 0.321666]). On uniform N262145,
PointChunks passes this run's per-cell gate at 1.412507 [1.395839, 1.429375],
while PointChunksWave is a stable regression at 0.885579 [0.882475, 0.888694].
These four results concern preserved references, not the new candidates.
Of the 252 metric comparisons, one reference result passes confirmation,
three reference results are stable regressions, and 248 are inconclusive.

For the new index, CPU recording mean speed ratios versus Original range from
2.29x to 2.58x, but all six fail the fixed CV gate. Complete-operation ratios
versus Original range from 0.984x to 1.014x and do not confirm an improvement.
Maintenance-only ratios range from 0.950x to 1.108x and also remain inconclusive.
The unchanged consumer dominates complete-operation time in these fixtures.
This does not establish a faster incremental algorithm than full-direct-waveops.

At c1/x1, GpuDriven's remove/insert phase inspects 52 logical slots per operation
instead of Original's 524,288. Nevertheless, the full 262,144-slot detection
pass and all 13 recorded index dispatch commands remain. Static90 dispatches
all capacity groups but inspects 26,215 input slots under the static-revision
contract. c0/x0 and c1/x0 have three nonempty dispatches, c1/x1 six, and c100/x100
eleven. Lifecycle and static90 have 570 non-fallback /150 fallback measured
operations each across five processes. The extra storage at N262144 is
1,048,696 bytes; candidate index storage totals 15,732,924 bytes.

Post-hoc raw inspection shows position-associated variation in the batched
query: sparse N262145 averages 0.038105 ms in position 0, versus
0.023055--0.023514 ms in positions 1--3. Balanced orders preserve this variation
instead of hiding it. We did not measure clocks or attribute a cache/clock
cause. The CV gate catches it even when paired process CIs are narrow. No
additional samples were collected to investigate or remove this effect.

## Reproduction and integration

Use the frozen protocol in Docs/UnifiedMicrobenchmarkProtocol.md and
Tools/Run-UnifiedMicrobenchmark.ps1. Run Validation, then Formal once with the
same hashed Player, then Tools/Summarize_UnifiedMicrobenchmark.py. The supplied
lock script must acquire Local\CodexR9700VNextUnityGpu. Review source commits
03d52c2 (shared fixture/oracle), fdcb4d9 (runtime and regressions), 041fe0e
(harness/protocol) and 46f09b0 (local bytecode exclusion). Original shader,
PointChunks runtime and full-direct-waveops reference are unchanged from the
frozen baseline. The report-only follow-up commit does not change measured code.

No push, main update or license change was performed by this task. The parent
must preserve main's 6d521ba52c1ddcbf3298109261c204b8464db00a publication/license
commit when integrating. The new runtime modes remain explicit opt-ins.

The delivery includes complete formal JSON/log/config/source/binary manifests,
validation and development logs, regression XML, scalar CSVs, all gate failures,
QA and an evidence SHA256 manifest. The original built Player remains at the
path recorded in provenance; it is identified by its full file hash manifest
and is not duplicated into the report bundle. No claims about presented-frame
performance or the independent scene experiment are made here.
