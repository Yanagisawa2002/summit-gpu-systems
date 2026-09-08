# Public Unity GPU integration benchmark

This standalone procedural scene compares two explicit query choices and two index choices in a four-arm experiment. It is a reproducible workload for this repository, not an industry-standard benchmark. Runtime defaults are unchanged. Read the complete [limited benchmark reproduction license](LICENSE.md) before building or using the packages; public visibility does not grant general reuse rights.

The [focused collection diagnosis](RESULTS-focused-costs-2026-09-08.md) documents persistent engine GPU missing values, allocation-counter calibration, the collector/checkpoint repair and its single frozen confirmation. Its separate protocol is `protocol-focused-costs.json`; pass it through `Matrix.ps1 -ProtocolPath` to reproduce that round. Unavailable allocation counters now produce -1 in raw rows and null in analysis; historical uncalibrated zero values do not establish zero allocation.

## Requirements and provenance

Clone `https://github.com/Yanagisawa2002/summit-gpu-systems.git` and check out the complete source commit named in the build attestation accompanying a result. Install Unity **6000.5.2f1**, Windows x64 build support, PowerShell 7, and Python 3.12 or newer. A Windows D3D12 GPU with Shader Model 6 wave operations is required; the reported reference device is the AMD Radeon AI PRO R9700. Unity has its own installation/license terms. No Unity account, company asset, network service, PSO package, Addressables package, or author-specific filesystem path is a project dependency.

The manifest uses four repository-relative packages: GPU primitives, direct binning, sensor pipeline, and native GPU timestamps. All package sources and the timestamp plugin DLL are pinned by the checkout. Bootstrap hashes every dependency file and preserves each complete package license; the build attestation also hashes all Player files, including the native DLL. `Native~` contains the plugin's source for inspection; rebuilding that DLL is optional and is not required to reproduce the pinned binary experiment. Existing third-party notices remain applicable. The project does not import root Assets or Integrations. Generated bundles, textures and particles are created by the checked-in build/fixture code.

## Build and reproduce

Run these commands from this directory, supplying your installed Unity executable. All output directories must be new; failed attempts are retained. Build requires a clean checkout and produces a non-Development Player (`BuildOptions.None`). Use `-AllowDirtySource` only for development diagnosis, never for formal evidence.

```powershell
$unity = 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe'
./Scripts/Bootstrap.ps1
./Scripts/Build.ps1 -Unity $unity -OutputRoot ./Artifacts/release-v1
./Scripts/Matrix.ps1 -BuildRoot ./Artifacts/release-v1 -OutputRoot ./Artifacts/oracles-v1 -Mode oracle
./Scripts/Matrix.ps1 -BuildRoot ./Artifacts/release-v1 -OutputRoot ./Artifacts/formal-v1 -Mode formal -OracleRoot ./Artifacts/oracles-v1
python ./Scripts/test_analysis.py
python ./Scripts/analyze.py ./Artifacts/formal-v1 ./Artifacts/analysis-v1
```

Every build and Player acquires `Local\CodexR9700VNextUnityGpu`; `-SerializedRunner` can point to an existing shared-lock wrapper. Do not acquire an outer copy of the same mutex around these scripts. The scripts only terminate a child they started if its fixed timeout expires. Player windows are visible during measurement. They do not close other applications, reset global caches or modify drivers or power settings.

`Run.ps1` accepts a JSON configuration for a single diagnostic run. `mode=oracle` calculates all nine CPU queries independently every frame and writes `expected.bin`; `mode=validate` compares the complete GPU history with that oracle, and may set `screenshot=true`. Both modes are diagnostic even though the executable is Release. Formal mode never captures screenshots or computes CPU oracle results. It asynchronously reads the full history after each arm; there is no per-frame synchronous GPU readback.

## Fixed work and measurement

The source-controlled [protocol](protocol.json) fixes 262144 sample slots, nine canonical queries, three scenarios, 384 frames/arm (64 warmup + 320 steady), four balanced blocks/process and five independent processes/scenario. It also freezes seeds, order, statistical units, thresholds and stop rules. The 15 CPU-oracle processes use the same independent payload seeds and trajectories before the 15 formal processes. Payload seeds do not change the canonical spatial distribution.

Arms are `old-full` (CellSerial query/full Direct WaveOps CSR rebuild), `new-full` (BatchedPointScanWave/full), `old-incremental` (CellSerial/GPU-driven incremental index), and `new-incremental`. The new query scans authoritative CSR members in parallel and batches queries; it does not spatially prune grid ranges. Full index update records 11 dispatches: snapshot keys, two clears, count, five hierarchical scan/add passes for 64^3 bins, offsets/write heads, scatter. Improved index records 13 dispatch commands, including indirect zero-dimension maintenance branches. These are command counts, not counts of nonempty GPU work. Query timing includes the required frame-digest reduction, giving two old-query or three new-query dispatches; each frame adds one history dispatch and two draw calls (262144 particle vertices plus 54 overlay triangle vertices).

The shared `r9700-query-v1-seed51ed270b` fixture provides positions and queries. `r9700-index-update-v1` supplies dynamic motion. Sparse is static; hotspot changes 1% of slots with 1% of that set crossing; streaming changes 5% with 20% crossing. Changed frames upload the full common sample/active buffers. CPU generation, upload and real content registration are included in frame and recording measurements. Active count is maintained from actual registration/unregistration deltas and checked against the independent full-range CPU query in oracle mode.

Query digests change particle colors and the heights of nine visible query bars entirely on the GPU. Streaming loads two actual LZ4 AssetBundles from disk, each containing 16384 serialized samples and a 256x256 RGBA palette used by the draw shader. Frame 16 starts and logically cancels bundle 0 (the underlying asynchronous request finishes and is discarded/unloaded); frame 80 requests it again, frame 128 registers and first uses it. Bundle 1 is requested at 192 and registered/used at 240; bundle 0 unregisters at 224 and unloads at 256; bundle 1 unregisters at 320 and unloads at 352. Registration deadlines are fixed. Missing a deadline is a retained failure, not an excuse to slow the run or retry it. Completion times are observed at polling frames; cache state is natural, not guaranteed cold.

Native `sceneGpu` covers explicit scene clear, full index update, query and frame digest, history, particles and overlay on the main D3D12 queue. It excludes other Unity GPU work. Full engine GPU and main/render CPU values are separately obtained from FrameTimingManager. Windows QPC source timestamps are mapped to the first subsequent Update QPC and its Unity frame; asynchronous observation frames never determine attribution. Missing, zero or ambiguous samples are retained as unavailable, and per-block engine metrics need at least 95% coverage. Unity Mono Stopwatch can use a relative epoch, so it is not substituted for QPC.

Engine cadence is a coroutine frame interval, not OS display cadence. OS presentation is unavailable. First screen is the engine's first rendered EOF since Unity startup, not process launch to physical display. All process Update intervals (including setup, report and drain), all 384 arm frames, memory/GC and content events remain in raw JSON. The steady comparison cannot establish rare p99 improvements: only about three tail samples fall above p99 per block. Analysis reports paired process-level ratios and 95% t intervals, CV, baseline drift, p95 ratios and every failed gate. No metric is promoted into a universal winner or a new runtime default.

## Evidence files

The subsequent [native probe diagnosis](RESULTS-causal-costs-2026-09-08.md) uses the separately frozen [causal protocol](protocol-causal-costs.json). `Scripts/Causal.ps1` runs nine diagnostic processes with `nativeProbeMode=none/whole/three`; `three` remains the default and the only formal mode. Unrequested scopes retain `NotRequested`/-1 values. `Scripts/audit_causal.py` checks equal work, full history, event counts and descriptive timing distributions. The optional `Run.ps1 -PresentMon` attaches to the owned PID without elevation; actual capture failures and unavailable presentation data remain explicit.

`build-attestation.json` binds clean source, Unity hash, process arguments and complete binary hashes. `release-build.json` adds Unity build GUID, bundle byte counts, SHA256 and CRC. `dependency-inventory.json` records package provenance. Each run retains its exact config, OS/GPU/CPU/driver and owned PID receipt, Player log, complete timing/results JSON and all binary histories. Oracle SHA256 binds every arm to its expected trajectory. Analysis writes JSON, per-block CSV and every over-budget cadence frame. These files, including failed diagnostics, must accompany any result claim.
