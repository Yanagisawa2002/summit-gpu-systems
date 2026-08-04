# NYC GPU Cinematic Stress Benchmark

## Purpose

This benchmark turns the production NYC scene into a deterministic visual GPU
stress test. It is designed to make the performance difference visible while
producing report-ready evidence after the documented validation matrix.

The hero camera flies a fixed perspective rail through a golden-hour Manhattan
sequence. The default profile renders only this hero view, matching a Port
Royal-style visual benchmark. An explicit four-camera diagnostic profile adds a
hidden full-city orthographic view and two oblique payload views for robotics
multi-sensor experiments.

## Visual workload

The route is identified as manhattan-golden-hour-v1 and contains repeatable
skyline, density, roofline, facade-canyon, orthophoto, and pull-up shots.

The runtime presentation uses:

- a perspective hero camera rendered into a fixed 1920x1080 off-screen target,
  with SMAA High and restrained ACES, bloom, vignette, contrast, and saturation;
- direct 1920x1080 gallery readback from that target, independent of the local
  desktop/display resolution;
- scaled presentation of the hero target in the visible Player window, so the
  same workload can be inspected on the local 1024x768 display;
- deterministic clear weather at 17.1 local hours;
- a selected primary virtual-texture feed for the hero camera;
- optionally, one fixed full-city orthographic and two synchronized oblique
  payload views when `-CameraCount 4` is selected;
- a compact letterboxed HUD with FPS, frame time, GPU time, P99, hitch count,
  a 240-frame graph, route progress, and resident asset counts;
- untimed skyline, facade, texture, and final-result captures.

The clean gallery images are true 1920x1080 captures. The HUD/result-card PNG
remains at the physical Player-window size (1024x768 on the current local
display) and is not presented as a 1080p screenshot.

Motion blur and depth of field are intentionally disabled. They can hide
stutter and would weaken a performance comparison.

## Production asset scale

The generated benchmark scene is built from the same production source scene
and external data root as the full NYC application.

Current source evidence describes:

- 59 building packs;
- approximately 61.8 million vertices;
- approximately 107.6 million indices / 35.9 million triangles;
- approximately 280,560 building clusters;
- 19,278 logical 2048x2048 LOD0 orthophoto pages;
- a 240-page runtime LOD0 cache.

The orthophoto source ground-sampling distance is approximately 15.24 cm,
streamed through 2048x2048 virtual-texture pages. The HUD and report show
loaded pages separately from manifest pages; the benchmark does not claim that
every source page is simultaneously resident in VRAM.

Facade variation is metadata-driven. The source currently has 24 PBR metadata
profiles and nine repeated 512x512 source albedo images, so it should not be
described as millions of unique high-resolution facade scans.

## Camera profiles

The profiles answer different questions and their percentages must not be mixed:

- `HeroVisual` (default, `-CameraCount 1`) renders the fixed 1080p hero rail.
  It is the presentation benchmark for dense buildings, orthophoto streaming,
  and a visually obvious A/B. The optimized city path uses no-copy Tile32.
- `RoboticsMultiView` (`-CameraCount 4`) renders the hero plus three payload
  cameras. It measures multi-view reuse and sensor-style pressure. It is retained
  as a diagnostic profile; a backend that wins in the hero profile is not assumed
  to win when vertex/index consumption is repeated four times.

Both profiles still execute the identical synthetic sensor, residency, and
deadline workloads unless their sizes are explicitly changed. Reports retain
the camera count, cull mode, SRP callback counts, and topology hash.

## A/B definition

A (baseline) uses the standard stress stack:

- Scalar AoS cluster culling;
- full visible-index materialization;
- CPU sensor production and upload;
- per-sensor spatial-index rebuilds;
- visible-page rebuild/upload;
- portable GPU primitives;
- FIFO task scheduling.

B (optimized) uses the accumulated GPU performance stack:

- Wave64 compaction and no-copy Tile32 descriptors in the default hero profile;
- direct source-index fetch in the vertex stage;
- GPU-resident sensor production;
- one shared GPU-resident spatial index;
- persistent residency with delta uploads;
- accepted device-specific primitive tuning;
- least-slack scheduling on the main queue.

Both variants receive the same route timestamps, weather, time of day,
resolution, post-processing, camera count, seed, workload size, and validation
state. Dynamic quality budgets are disabled for the run.

BFP2 culling bypasses weather-scheduler camera discovery and uses an explicit
set: hero only for `HeroVisual`, or the hero-plus-three union for
`RoboticsMultiView`. Adaptive grouping is disabled. The benchmark submits every
resident source pack in both variants, bypassing CPU `DrawDesired` and pack-AABB
admission differences, so every resident pack executes BFP2 GPU cluster
culling. Unity can still apply camera-level culling to submitted procedural-draw
bounds. Every measured logical frame must observe a newer completed primary
output-0 cull pass with exactly the requested camera count and the full resident
pack set. The ordered camera-state and dispatched-pack topology is hashed per
frame; A and B must produce the same 900-frame topology sequence.

The fixed-camera city oracle does not accept repeated reads of an old buffer. A
primary-pass epoch advances only after the complete output-0 pass has submitted.
Each of the three converged oracle samples must come from a newer epoch, report
one actual hero camera, and carry the exact dispatched-pack identity set used by
the snapshot. The final city and composite hashes must still be bit-identical
between A and B.

The cinematic runner preloads and pins all enabled BFP2 packs before sampling.
Before measurement, resident pack count and GPU bytes must remain unchanged for
30 consecutive frames. Readiness additionally requires all discovered packs and
full-residency packs to be resident, with zero loading, pending CPU, or staged
upload work. Both the fixed-camera oracle and every measured frame must dispatch
exactly that resident pack count. The benchmark requires exactly one renderer so
startup, measurement, and report residency scopes are identical. During measurement it rejects any
residency change. The benchmark controller, rather than the weather scheduler,
owns camera cadence. A coroutine queues the frame; the BFP2 renderer submits the
requested-camera cull and procedural draws from its default-order `LateUpdate`,
then the controller's ordered `LateUpdate(30000)` verifies the exact next epoch,
current frame number, requested camera count, and full resident pack set. It then
renders any payload cameras followed by the hero camera explicitly. SRP
`beginCameraRendering` callbacks prove that every requested camera rendered
exactly once against that completed city pass. Every measured frame must carry
this ordering proof. Orthophoto target selection refreshes once per logical
frame; every fixed gallery pose then waits at least
eight wall-clock seconds and 30 idle upload frames before capture.

Formal GPU measurements use the existing ABI-v2 native DX12 timestamp bridge,
not `FrameTimingManager` or a profiler fallback. A pre-recorded BEGIN command
buffer is submitted after auxiliary sensor/residency/deadline work is issued but
before any `LateUpdate`. BFP2 then submits the full-city cluster cull on the
direct graphics queue, the controller explicitly renders the payload cameras
and hero, and a matching END/resolve command buffer is submitted immediately
after the explicit camera group. The default one-camera profile therefore
measures `city cull + 1920x1080 hero render`; the four-camera diagnostic measures
`city cull + four explicit camera renders`.

Eight warmup hero/empty-control pairs validate this split-command integration.
The hero median must exceed empty-control P99. During measurement, one
empty-control token is interleaved every eight frames in both A and B. Control
duration is reported separately and is never subtracted. Every hero/control
token must resolve with a unique token and user tag, internally consistent raw
ticks, a positive fence, one constant frequency/device generation, and exactly
one/zero hero SRP callbacks respectively. The native interval excludes the
sensor/residency/deadline submissions issued before BEGIN and any independent
async-compute or copy-queue work. If BFP2 later moves to async compute, an
explicit fence join must be added before END.

The three synthetic sensor, residency, and deadline workloads use a fixed
logical-frame issue cadence. The cinematic runner defaults to one heavy issue
every 12 logical frames; for the default measured range (logical frames 120
through 1019), that is exactly 75 submissions per subsystem and per process,
with 11 measured tail frames after the final issue. Warmup frames 0 through 119
must produce exactly ten submissions per subsystem. The controller and
summarizer reject the run if any subsystem submits a different count, drops an
issue because prior GPU work is still in flight, fails to complete inside the
sample window, covers fewer than all 64 deterministic states, or differs
between A and B. Issue-frame and logical-state sequence hashes are retained.
This keeps equal work without serializing the GPU behind a CPU wait.

This cadence represents sparse heavy batches, not sustained per-frame robotics
throughput. Sustained throughput and backpressure remain a separate benchmark.

## Run an automated A/B

From the isolated worktree:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Run-GpuCinematicBenchmarkAB.ps1
The isolated worktree used for this implementation is:

    C:\Users\EdwinLiu\Downloads\SUMMIT\.codex-worktrees\gpu-cinematic-benchmark


The default exploratory run executes A once and B once at 1920x1080 with one
hero camera, a deterministic route warmup derived at 60 logical frames per
configured warmup second, 120 workload-warmup frames, a fixed 12-frame
heavy-workload cadence, and 900 measured frames. Route pose advances by logical
frame, not wall-clock time. Use three
counterbalanced rounds before making a formal resume claim:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Run-GpuCinematicBenchmarkAB.ps1 -Rounds 3

Run the separate four-camera diagnostic explicitly with:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Run-GpuCinematicBenchmarkAB.ps1 -CameraCount 4

Use -SkipBuild only when the Player was built from the current commit.

## Watch the benchmark

Launch either variant in a visible Player:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Run-GpuCinematicBenchmarkInteractive.ps1 -Variant baseline

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Run-GpuCinematicBenchmarkInteractive.ps1 -Variant optimized -SkipBuild

## Evidence

Each automated variant writes:

- one JSON report;
- one raw per-frame CSV;
- one raw per-token native timestamp CSV;
- one Player log;
- a final result-card PNG;
- skyline, facade, and texture PNGs at identical route timestamps.

The output directory also receives cinematic-runs.csv, visual-equivalence.csv,
paired-improvements.csv, and SUMMARY.md. Sensor, residency, and deadline
workload hashes, the city
cull-set hash, and the composite output hash must match across A and B.
The untimed city oracle bypasses normal camera discovery, forces both variants
to the same hero camera, and requires three consecutive identical full cull
snapshots within a maximum 30-sample convergence window before comparing
hashes.
Acceptance additionally requires every fixed 1920x1080 gallery frame to pass
PSNR >= 40 dB and a <= 1% fraction of pixels whose maximum channel difference
is greater than 5/255.

Acceptance also requires exact payload-pass counts; exact warmup and measured
sensor, residency, and deadline submissions with zero drops; full 64-state
coverage and matching sequence hashes; completion inside the measured window;
900/900 frame and native direct-queue scope samples; the exact scheduled
empty-control count; zero native acquire/result/timeout failures; zero final
pending, active, reserved, and submitted tokens; stable and matching measured
BFP2 residency; zero final loading; a clean source commit; and identical Player
and native timestamp DLL SHA256 values across all processes. The runner and
standalone summarizer independently re-read the per-token CSV, reconcile GPU
average/P99, and reject duplicate identities, stale rows, mismatched scope,
frequency, device generation, or binary provenance.

One A/B pair is a visual and engineering smoke result. Three counterbalanced
rounds, clean thermals, valid native GPU timestamps, matching functional hashes,
equivalent fixed-frame images, identical resident workload state, and clean
provenance are the minimum recommended evidence for a published performance or
resume claim. Improvements are computed within each round before taking the
median; variant-level medians are not divided to manufacture a paired result.

## Current evidence status

Three earlier pilots are rejected evidence. The first had duplicate payload
camera rendering and unmatched BFP2 residency. The second allowed the in-flight
guard to drop more synthetic work in B than A. The third passed equal-work and
image gates but exposed nondeterministic weather-registry camera state in the
untimed city oracle; its percentages are also rejected. A clean rerun must pass
the fixed-cadence, zero-drop, fixed-camera city-oracle gate before any A/B
percentage is retained.
