# R9700 vNext implementation integration — 2026-09-07

All six implementation branches are merged and the combined correctness gates
pass. New candidates remain opt-in. No formal performance matrix was run, no
speedup is established, and no new production default is promoted.

The integrated runtime and final query/index measurement harnesses were tested
at `25f230654382b7c1e67c6658e210674fbaf28ac4`. The subsequent final commit adds
documentation and a compact evidence index only. The final integration SHA and
main fast-forward result are recorded in the shared `reports/integration.json`.

## Source and merge provenance

Baseline: `3faad555c6044f966403261d627d8d6ac232d2d9`.
Branch: `codex/r9700-vnext-integration-20260907`.
Integration worktree: `C:/Users/EdwinLiu/Downloads/SUMMIT-gpu-r9700-vnext-integration`.
Main worktree: `C:/Users/EdwinLiu/Downloads/SUMMIT-gpu-systems`.

Every worker's complete report, committed diff, actual validation evidence and
clean worktree were checked before merging. Index was merged first because its
native plugin importer repair is a prerequisite for integrated GPU validation.

| Scope | Verified worker tip | Integration merge |
| --- | --- | --- |
| Index | `cc102dbcc15d3398055dc8c47ef3adc821ab6d34` | `9f7f7c04e5c67638619ded46f0646ab71a37d774` |
| Residency | `a9a5b1730cd45d5feff074da60e294088f7dcb87` | `ececa94c7e6aa2eb255d14b24d7d76af12a0d0dc` |
| Primitives | `250b8da818ca65bab74fb2583508540de776ccb9` | `41899e13ce68a6aefea41ecb8279564f5ad83430` |
| Adaptive | `49cd5ff2d176ec9993283eff7e43e9af8f0fd513` | `749d848dde10c67ae2f2cb23b5aa3a03c730184e` |
| Scheduler | `bf903979d98ba5d3ce6eb62b29d25236fa0f75fe` | `ff1ca22a9bef9c59a34b5a30e8ba2e046bec87fa` |
| Query | `a4c000d7e61cd5dfb16d5bfad39edac0793550d8` | `adf7dc6591a7c6da6a7b53dd6943c44a17e6e9b6` |

Historical source/evidence and executable baseline paths are retained. The native
timestamp DLL was not rebuilt or replaced; its Editor importer metadata was fixed.

## Integrated behavior

- **Primitives:** four executable candidates cover portable 128x4 radix-4 and
  radix-8, automatic-wave 128x4 radix-4 and 256x2 radix-4. IDs, capability gates,
  stable-sort contracts, partial tiles and resource/dispatch accounting are explicit.
- **Adaptive:** exact workload/N/C/concentration/occupancy matrix, trusted key
  domains, stable promotion and immediate safe fallback. Device, independent
  Windows driver, Unity/compiler and shader/build identity must match. Discovery
  and frozen evaluation use separate seeds. Legacy identity-free calls fall back.
- **Query:** CellSerial remains default; PointChunks and PointChunksWave include
  GPU work-list setup, indirect arguments and reduction in their measured scope.
  The external index path is supported by all three backends.
- **Index:** static revision and dynamic inspection paths preserve payload changes,
  stable slots and removals/reactivation. Reserved CSR holes use tombstones;
  churn, fragmentation or capacity conditions select a full rebuild. The independent
  Direct full-rebuild reference and CPU oracle remain available.
- **Residency:** deterministic heap/free-slot policy is optional beside scan LRU.
  Leased plans, in-flight slot protection, bounded uploads, pending demand, aging
  and prefetch have explicit ownership and retry semantics. Dispatch limits are
  checked before command-buffer mutation.
- **Scheduler:** bounded cost estimation, delayed-sample validation, DAG admission,
  backpressure and aging preserve main-queue fallback. Cross-queue dependencies
  require fences; already submitted dispatches cannot be preempted.

Detailed API/lifetime contracts: [primitives](R9700_PRIMITIVE_CANDIDATES.md),
[adaptive](GPU_ADAPTIVE_RUNTIME_VNEXT.md), [query](GPU_SENSOR_QUERY_BACKENDS.md),
[index](GPU_SENSOR_INCREMENTAL_INDEX.md),
[residency](GPU_RESIDENCY_MANAGER_BENCHMARK_PLAN.md),
[scheduler](GPU_DEADLINE_SCHEDULER_RUNTIME.md).

## Integration fixes

1. Resolved query/index hooks together, preserving both normal and external
   recording APIs. Chunk capacity uses reserved CSR size, while query payload
   validation uses stable-slot capacity. Preflight rejects oversized buffers and
   unsafe aliases before recording profiler markers or commands.
2. Added four combined index/query tests: three backends across 18 update frames
   and one preflight rejection case. Coverage includes the highest sparse ID,
   tombstones, payload-only updates, static revision, empty inputs, dense cells,
   teleports, removal/reactivation and forced/incremental transitions.
3. Fixed Null Device candidate support checks and constructor validation order
   after the first integrated CPU-only run exposed two failures.
4. Made standalone historical provenance fixtures self-contained; production
   expected hashes remain unchanged. This repairs missing excluded FishNet/URP
   fixtures in the baseline checkout.
5. Committed Unity's verified canonical project settings and ignored generated
   benchmark folder metadata. The first staged smoke stopped on generated source
   dirt; the complete rerun and final telemetry rerun remained clean.
6. Added environment capture and a staged comparison runner. Query/index schema 2
   includes managed allocation evidence; summary gates reject absent/invalid GC
   fields and report CPU/GPU mean and P99. Adaptive summaries also include means.

## Validation performed

All Unity imports, builds, tests and GPU execution held the shared
`Invoke-SerializedValidation.ps1` mutex until child processes exited. These runs
are correctness and instrumentation checks, not formal performance acceptance.

| Gate | Result / scope |
| --- | --- |
| Repository layout | 8 packages; 168 portable C#/shader files |
| Final tracked syntax | 57 PowerShell scripts; 47 JSON/asmdef files before this evidence index |
| Historical CPU provenance | 1,052 assertions across five scripts |
| Scheduler evidence gates | Valid 20-case fixture; 15 negative cases and payload mutation rejected |
| Query evidence gates | Valid fixture; 10 corruption cases rejected |
| Primitive evidence gates | Actual smoke accepted; tick tampering, missing samples, absent GC and dirty comparison rejected |
| Index evidence gates | Actual schema-2 smoke accepted; absent/negative GC, legacy schema, altered ticks and missing pair rejected |
| Adaptive calibration tooling | 8 Python tests passed |
| Integrated Null Device | 332 passed / 600 total; 0 failed; 268 GPU-dependent skips |
| Final complete DX12 suite | 597 passed / 600 total; 0 failed; 3 expected skips |
| Combined index/query contract tests | All 4 passed within the full DX12 suite |
| Residency CPU smoke | Independent delta oracle; atomic retry/bounds/fairness/leases; 180-frame differential trace; 384/4,096/32,768 slots in both orders |
| Scheduler CPU replay | 10 variants; all 2,434 jobs completed; synthetic planning evidence only |

The DX12 skips are the two unsupported explicit wave-width candidates and the
opt-in index comparison. The latter ran separately and passed 1/1 without skips.

The full six-part staged smoke passed at `1d57b8d1abe117e451b7caaab6865eeadf80fe5c`:

| Smoke | Observed completeness |
| --- | --- |
| Primitives | 22 algorithm cases plus empty control; 44 oracle validations; 1,380 valid native samples; 22 summary rows |
| Query | 144 workload + 144 paired-empty measured samples; all three backends, four distributions, six orders; exact oracle outputs |
| Index | Four scenarios; 64 update/query frames, 48 measured; eight empty controls; paired and CPU digests match |
| Adaptive | 216 samples; six controls; 48 CSR validations; no measurement readback; 84,552 persistent GPU scratch bytes |
| Residency | Four blocks; 256 samples; eight validation rows; zero failures; paired scan/heap policies |
| Scheduler | 20 cases (10 validation + 10 measured); 154 jobs per case; 368 native records including 40 controls; source/Player manifests stable |

At final tested code `25f2306`, query and index smokes were rebuilt/rerun with
schema-2 allocation telemetry, then the complete DX12 suite passed again. The
same query/index sample counts and oracle checks passed. Measured recording
allocation was zero in those runs; index submission allocation was also zero.
These are current-thread scoped counters, not total benchmark allocation claims.

Raw local evidence remains in the integration worktree (ignored by Git):

- `TestResults/vnext-integrated-cpu-r2/results.xml`
- `TestResults/vnext-final-dx12/results.xml`
- `Reports/vnext-integration/residency-cpu/` and `scheduler-cpu/`
- `Reports/vnext-integrated-stages-r2/Smoke/` (complete six-part run)
- `Reports/vnext-metrics-smoke/` (final query/index telemetry, environment captures)

The tracked [evidence index](R9700_VNEXT_INTEGRATION_EVIDENCE_2026-09-07.json)
records raw-file hashes and exact source revisions. Raw files are not copied into
main automatically; the integration worktree retains them.

## Environment and evidence limits

Windows DX12, AMD Radeon AI PRO R9700, vendor `1002`, device `7551`, driver
`32.0.31041.1004` (2026-08-17), Direct3D12 feature level 12.2.
Unity `6000.5.2f1` (`eb73d3b415a1`).

- UnityShaderCompiler SHA256: `2657D6C076F25DA474DA87CD320CC89F3C2C798DF46B5575F486EB20FFF54DA8`
- Native timestamp DLL SHA256: `BE42A0E925F8BFEC28F8096C841940F8B559C11AF99EA83B9BF5FDE83DF5D503`
- Source shader manifest identity: `4CF4750F6008B1158251B663589FD297CB484B41D23FF9ED7EDCE2A5822BD6E1`

Explicit `[WaveSize(32)]` / `[WaveSize(64)]` require SM6.6; the current Unity
importer rejects them under SM6.0. They remain disabled and outside imported
Resources. Automatic-wave probing observed width 64, which is not proof of forced
Wave64 or a full ISA/resource analysis. No forced-width performance claim is made.

The primitive Player's optional Unity whole-frame GPU timing was unavailable;
native scoped DX12 timing was complete (ABI 2, capability flags 31, frequency
100,000,000, no native failures/missing/pending samples). Index comparison is
explicitly Editor-only and unpromoted. Adaptive/query drained samples measure
latency, not saturated throughput. Scheduler async correctness uses synthetic
eligibility evidence; production async still requires real measured evidence.
Its zero-allocation probe covers successful `TryPrepare`, not the entire harness.

Custom primitive IDs are resolvable for future calibration, but the adaptive
facade currently executes the legacy Portable/WaveOps primitives only. No custom
candidate is silently selected. Resident page caching is application-level
GraphicsBuffer management; sparse resources and a dedicated copy queue are not
implemented or claimed.

## Deferred staged comparisons

These commands are prepared and **not executed as formal comparisons** during
integration. Use a fresh output root, a clean committed main checkout and the
shared lock. Run one stage at a time; inspect its gates before interpreting data.
The `Smoke` stage is the short all-six check already validated during integration.

```powershell
$repo = 'C:/Users/EdwinLiu/Downloads/SUMMIT-gpu-systems'
$lock = 'C:/Users/EdwinLiu/Documents/Codex/2026-09-07/w-m/work/r9700-vnext/Invoke-SerializedValidation.ps1'
$out = Join-Path $repo ('Reports/vnext-comparison-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$runner = Join-Path $repo 'Tools/Run-R9700VNextStage.ps1'
& $runner -Stage Primitives -ValidationLockScript $lock -OutputRoot $out
# Run subsequent stages separately after reviewing each result:
& $runner -Stage AdaptiveDiscovery -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage AdaptiveFreeze -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage AdaptiveEvaluation -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage Query -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage Index -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage ResidencyCpu -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage ResidencyGpu -ValidationLockScript $lock -OutputRoot $out
& $runner -Stage Scheduler -ValidationLockScript $lock -OutputRoot $out
```

Adaptive discovery spans N=262,144/1,048,576 and C=16/256/4,096, then freezes a
matrix for held-out seed 9702 (discovery seed 9701). Keep the same built Player
and identities through evaluation. Other stages use their committed bounded
matrices and counterbalanced repeats. Index remains an Editor comparison even in
matrix mode. Missing evidence, dirty source or incompatible identity fails closed.
Review CPU/GPU mean/P99, scoped GC, memory, dispatch/recording overhead, controls
and correctness before choosing any new default.
