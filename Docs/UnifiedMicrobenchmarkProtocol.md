# Frozen query and incremental-index confirmation protocol

This declaration is committed before formal sampling. Frozen runtime candidate:
`fdcb4d91c8031a409693a98083e9e4d22e1f602b`; baseline:
`4f6e8f7beeae583ec79bbd3f738c09decbbc8427`. Shared public query fixture/oracle:
`03d52c27c200d322833e697912c497dacf33aac2`. The source commit containing this
protocol, runner and analyzer is the formal harness identity. Each run also
records source and Player file SHA256 manifests. Documentation-only publication
and license commits on main do not replace the frozen implementation baseline.

## Fixed matrix and work

- Query: 4097, 65541, 262145 elements x sparse, uniform, hotspot, single-cell.
  Exactly the existing nine `r9700-query-v1-seed51ed270b` queries, order and input
  construction. Arms: CellSerial, PointChunks, PointChunksWave,
  BatchedPointScanWave. A single unchanged compact CSR and output buffer is
  shared within each cell. The measured query scope excludes index and frame
  digest; every query dispatch, clear, work-list/argument creation is included.
- Index: N262144; rates-n262144-c0-x0, rates-n262144-c1-x0,
  rates-n262144-c1-x1, rates-n262144-c100-x100, static90, lifecycle.
  The original rate set includes 1. The original four index-comparison queries
  and exact `GpuSensorIndexUpdateTrace`/lifecycle Advance sequence are retained.
  Index queries are intentionally not replaced with the nine-query query-only
  workload: the two microbenchmark families have separate established scopes.
  Arms: full-direct-waveops, incremental-original, incremental-gpu-driven.
  Each block/arm starts with fresh arrays and index resources. Consumer is the
  same CellSerial query plus frame digest in all index arms. Initial construction
  is warmup frame 0; frames 1 onward use the existing deterministic trace.
- No matrix expansion, parameter search, automatic backend selection, native
  timestamp DLL rebuild, or default promotion.

## Processes, orders and stopping

One dedicated non-development Windows x64 Release Player, DX12, Mono backend
from the checked-in project configuration. Development diagnostics are kept in
separate directories and cannot enter formal analysis. Full DX12 EditMode
regression and a separate Release validation run precede formal measurements.
Release validation covers every measured trace frame and arm once. Formal
runs also compare every asynchronously captured query digest to independently
precomputed CPU-oracle output for that exact frame.

Exactly five independent formal Player processes, processIndex 0..4. Each
query cell has four blocks. Their arm orders are [0,1,3,2], [1,2,0,3],
[2,3,1,0], [3,0,2,1]; every arm occupies every position once. Each index cell
has six blocks with all six permutations of three arms. The sequence of blocks
is Fisher-Yates shuffled by a checked-in xorshift32 implementation. Seed is
0x713ba121 + processIndex*997 + caseId for query, and 0x61db2391 +
processIndex*997 + caseId for index. caseId starts at 0, follows listed query
distribution order then ascending N, and continues through the listed six index
cases. Actual schedules are retained in every process JSON.

Each query block/arm: 8 warmup + 30 measured operations (120 measured per arm
per process). Each index block/arm: 8 warmup + 24 measured operations (144
measured per arm per process). Warmup rows remain in raw files with measured=false.
One raw empty timestamp scope per block, never subtracted. Every sample returns
control to Player and waits asynchronously for its output before reuse. This
is a serialized latency experiment, not a throughput or presented-frame test.

Stop immediately on correctness mismatch, device/native timing failure, missing
row, mutation of source/Player, or a 60-second sample wait timeout. Each owned
build/Player has a fixed 30-minute process timeout. Retain all failed data.
No automatic retries. A gate failure or inconclusive result does not authorize
resampling, changing sample counts, rerunning selected cells, or tuning until
winning. A pre-formal development failure may be fixed and revalidated with
its failed logs retained; any implementation change after declaration requires
a new declaration before formal sampling.

## Cost boundaries and telemetry

GPU index total surrounds full maintenance and CellSerial consumer/frame digest;
additional nested native intervals expose maintenance and consumer. All reset,
Begin/Detect/Decide, fallback and indirect argument generation remain inside.
Native marker overhead is disclosed by empty controls, not removed. CPU record
(including markers), index/query API record time, submit and current-thread
managed allocation are recorded. Trace generation, input upload CPU call,
resource initialization, independent CPU oracle, async request, async wait wall
time, readback copy and native-result wait wall time are separate fields.
There is no per-frame synchronous GPU GetData or CPU dirty-list shortcut.
AsyncGPUReadback.GetData is used only after done and copied into owned arrays.

Query candidate scans authoritative CSR members, sharing sample/hash work
across the batch, without cell-range pruning. It has two dispatches, no scratch.
This is not evidence of universal sparse/local-query superiority. The original
query arms remain intact.

Index GpuDriven still records 13 index dispatch commands (15 including the
unchanged query and digest). Nonempty index dispatches are 3 idle / 6 changed /
11 rebuild, reported separately. Detection dispatch spans all N capacity slots
in every case including static90. State[14] reports actual inspected snapshot
slots. Non-fallback remove/insert logically inspect 2*N slots in Original and
2*changed slots in GpuDriven. Fallback scans remain fully included. Candidate
storage adds 4*N+120 bytes. Full-direct-waveops records 11 index dispatches at
this fixed bin count (snapshot keys, two clears, count, five-level scan dispatches,
prepare and scatter), plus two consumer dispatches. Input and consumer storage
are common and reported separately.

## Statistics and fixed gates

For every cell, baseline/candidate and metric, compute arithmetic mean within
each paired block. Compute log(baseline/candidate) and average block log ratios
within each process. Report exp(mean(process log ratios)) and a two-sided
Student t(4) 95% CI from the five process aggregates. Correlated frames are
never CI replicates. Tail ratios use empirical linear-interpolated p95/p99
within each block, followed by the same paired process aggregation. Publish
all block observations, means, medians, tails, CPU and GPU metrics.

Confirmation gates: mean speed-ratio CI lower bound >1; p95 speed ratio >=1.01;
CV <=5% for both arms' within-process block means and across-process means;
maximum absolute baseline first/last-block drift within each process and
first/last-process mean drift <=15%. Raw observation CV is additionally
reported. No stability gate is relaxed for a large point speedup. Stable upper
mean CI <1 is a stable regression; other failures are inconclusive. These are
unadjusted per-cell CIs, not simultaneous familywise guarantees. With only
30 query /24 index observations per block, p99 is near-maximum interpolation;
rare-tail support is insufficient and no P99 gain will be claimed.

All Unity/build/GPU/formal CPU execution holds the existing shared
`Local\CodexR9700VNextUnityGpu` mutex through the supplied lock script. No user
process termination, global cache deletion, power/driver changes or Computer
Use. Runner logs device/driver, CPU, OS, Unity, complete command lines, owned
PIDs, source and all Player hashes. Repository license/publication changes
remain parent-owned; this work only prepares local reviewable commits.

## Reproduction

```powershell
./Tools/Run-UnifiedMicrobenchmark.ps1 -Phase Validation -OutputDirectory <fresh-validation-dir> -LockScript <Invoke-SerializedValidation.ps1>
./Tools/Run-UnifiedMicrobenchmark.ps1 -Phase Formal -OutputDirectory <fresh-formal-dir> -LockScript <Invoke-SerializedValidation.ps1> -SkipBuild -PlayerPath <validation-dir>/Player/SummitUnified.exe
python ./Tools/Summarize_UnifiedMicrobenchmark.py --input <formal-dir> --output <fresh-summary-dir>
```

Validation/Formal require clean committed sources. Formal reuse checks every
Player file hash and the source manifest; any mutation fails closed. The five
processes are one fixed invocation, with no selectively repeated cells.
