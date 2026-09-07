# Focused index cost diagnostic

Baseline: public main `20191e5050805adb6b44f2803eaa966bd1571d8e`. Only two existing
N262144 scene trajectories: hotspot-dynamic (1% changed, 1% of changed crossing,
CellSerial query) and streaming-switch (5% changed, 20% crossing,
BatchedPointScanWave query). Exactly 384 frames, 64 warmup, seed 927101 from
formal replicate 0. Input update and content code are copied byte for byte from
the baseline into a generated standalone project, without changing the public
scene or runtime packages. The exact previously built content-0/content-1
AssetBundle bytes are reused, with hashes; cancellation, loading, registration,
unregistration and unload events stay at the existing frames. No synthetic
load delay or replacement input is used.

This is a bounded **diagnostic, not formal performance confirmation**. Before
GPU execution, commit these sources. Four new Release Player processes in fixed
order: hotspot phases-off, streaming phases-on, hotspot phases-on, streaming
phases-off. No extra cases, tuning, adaptive sample count or automatic retry.
One input sequence per process; no new five-process performance claim.

Each diagnostic frame records both complete full-direct-waveops and GpuDriven
incremental indices, then queries compact and reserved CSR using the **same
incremental Samples buffer**. Index order alternates by frame parity; query
order alternates every two frames, covering four combinations. Both query
digests must equal the pre-existing independent oracle for every frame.
Six additional GPU validation dispatches prove each CSR has exactly the active
ID set (no duplicate/missing IDs), proper cell membership, and the exact input
snapshot. A seventh history dispatch stores both queries, full index state and
both CSR counters. These are additional diagnostic work, separately timed.
Original two draw calls still execute and streaming content is really loaded.
Rendering is explicitly submitted by Camera.Render into a 1280x720 ARGB32/depth24
RenderTexture, with CPU render submission reported separately. This guarantees
the hidden diagnostic Player executes camera commands; it intentionally does
not measure window presentation or the original auto-render cadence.

The outer native interval includes the complete paired diagnostic graph,
validation/history and both draws. It is neither the original scene interval,
full-engine GPU time nor presentation time. Full reconstruction/compaction is
never free: the full-index interval remains explicitly charged and reported.

Phases-off executes production index classes. Phases-on uses automatically
source-derived adapters: only class names and timestamp observer hooks differ,
and original compute shaders/buffer layout/dispatch order stay unchanged.
Incremental hooks bracket all 13 recorded dispatch calls. Full hooks bracket
snapshot keys, two clears, count, five-dispatch hierarchical scan, prepare and
scatter. Nested intervals perturb command processing; retain phase-off/on whole
intervals and never subtract an empty marker estimate. Zero-X indirect work is
reported as command/barrier/marker overhead, not omitted or called useful kernel
work. GPU state identifies which fallback/maintenance branches really execute.

Dedicated validation shaders count all live and Invalid slots in each CSR.
These are real shader-produced counts of the representation, **not hardware
performance counters or actual query-specific slots visited**. Compact and
reserved representations also differ in ordering, layout, capacity and holes;
without another independent intervention their cost difference cannot be
assigned solely to holes. No further intervention is pre-authorized here.

CPU trace generation, real content operations, input upload call, full/index
command recording, both query recordings, validation recording and native
result polling are separate. Every GPU result is obtained asynchronously;
readback of owned history happens after the complete trace. No per-frame CPU
GPU-sync decision, dirty-list shortcut, default change or API removal.
Clocks/cache states are not collected; queue/order associations cannot identify
a clock cause. Source estimates of memory traffic are labeled estimates.

All Unity/build/GPU execution holds Local\CodexR9700VNextUnityGpu exactly once
via the provided shared lock script. Fail on any device/native error, unequal
work, oracle/membership failure, missing bundle, registration deadline,
60-second drain timeout or 30-minute owned process timeout. Retain failures;
never terminate user processes, clear global caches or alter drivers/power.
A concrete cost bug may motivate a separately frozen single-candidate formal
confirmation, otherwise deliver a scoped recommendation without tuning for gain.

```powershell
python -B Tools/IndexCostDiagnostics/generate.py --project Tools/IndexCostDiagnostics/Project --content <existing-release-StreamingAssets/IntegrationContent>
Tools/IndexCostDiagnostics/Run.ps1 -ProjectPath <generated-project> -OutputDirectory <fresh-output> -OracleRoot <prior-oracles-v1> -LockScript <shared-lock-script>
```

The generator records every original source and generated file hash. The runner
records source, binaries, device/driver, commands and process epochs. Generated
projects are ignored locally; all generator and diagnostic source is committed.
Existing benchmark reproduction license and public-scene ownership are retained.

The allocation counter is qualified with a retained 1MiB allocation before any
measurement. If the current-thread counter fails that probe, row allocation
bytes are -1 (unavailable), never evidence of zero allocation. Timestamp failures
include concrete status, pending/consumed counts and native terminal state;
direct device-removal reason is unavailable through this public session API.

