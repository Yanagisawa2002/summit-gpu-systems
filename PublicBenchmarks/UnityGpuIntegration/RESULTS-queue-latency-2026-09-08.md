# GPU-verified task delivery experiment — 2026-09-08

Follow-up: the [competitive baseline and transport study](RESULTS-query-boundary-2026-09-08.md)
adds a reasonable parallel scan. It has not shown extra user-facing benefit from
batch over that stronger reference. The video below compares the older CellSerial
baseline and should be interpreted with that limitation.

Real completion-driven progress, outstanding work and per-job result latency are
implemented and validated. **The frozen performance confirmation is inconclusive**:
both capture modes fail baseline CV and drift gates. Some processes also lose
focus. The English diagnostic video below shows the observed task delivery,
without claiming a confirmed stable speedup or smoother displayed frames.
The previously withdrawn streaming videos remain withdrawn.

## Minimal English video

The [50-second English edit](https://github.com/Yanagisawa2002/summit-gpu-systems/releases/tag/r9700-task-delivery-video-2026-09-08)
is published at the owner's request. It uses the same two DXGI reliability-check
recordings described below. It removes the long explanations, retains captured
live metrics and task tiles, and keeps a short English scope statement. Only
spatial cropping and constant start offsets are applied; no speed change,
interpolation, replacement load or new performance sample is introduced.
The source marker alignment is checked before cropping. The original evidence-only
release and its publication-state receipts describe the earlier delivery and remain
unchanged. The new release carries its own composition receipt and decode check.
Reproduce this edit with `Scripts/compose_queue_video_english.py` using the same
capture root and `analysis-final/analysis.json`.

## What now drives the visible progress

`QueueLatencyPlayer` is a separate opt-in entry point selected by `-queue-config`.
It uses the existing hotspot-dynamic fixture: 262144 points, nine queries,
384 jobs, full index rebuild in both arms. Only CellSerial versus
BatchedPointScanWave changes. There is one in-flight job in both arms.

All 384 task descriptors exist in advance. Descriptor i becomes due at i/60
seconds on a monotonic clock. This is a fixed synthetic arrival schedule, not
measured network traffic or actual sensor I/O. Tasks are not dropped or merged
when processing falls behind. The arrival schedule does not advance completion.

Each job updates its input, records a complete index rebuild and nine queries,
then copies ten query/metadata digest records to history. A nonblocking async
readback requests exactly that job's 160-byte range. Only a successful returned
readback whose ten records match the original CPU oracle increments completion.
No CPU-submission count, elapsed-time fraction or historical sample drives it.

The displayed outstanding count is due jobs minus verified jobs. Waiting and
in-flight states are distinguished. Result latency is scheduled arrival to
verified host-visible result; it includes queueing, CPU uploads/recording, GPU
execution, asynchronous readback and polling/validation. Readback observation
is an upper bound on GPU work completion, not an exact hardware timestamp.
No per-job blocking wait or native timestamp-plugin event is used. A pending
request is waited only during resource cleanup on a failed/aborted exit.

The point-cloud view and HUD remain live. Their draw count depends on the free
render loop and is not identical across arms. Equal work refers to the 384 task
input/output sequence, not identical total visualization draws. This mode measures
task delivery in that application context, not isolated shader throughput or
displayed frame rate. Eight identical warmup jobs precede the measured stream.
Setup, initial upload and final serialization are outside the completion interval.

## Correctness and confirmation

- Two engineering preflights passed 768 jobs. A deliberately corrupted oracle
  for job 1 caused rejection with completion still at 1; the failed job never
  appeared as verified. Original and failed records are retained.
- The frozen matrix ran once: five paired replicates, two arms, recorder on/off,
  twenty separate Release Player processes. All 7680 jobs matched their original
  GPU query/metadata histories byte for byte. Ordered submission, nonnegative
  latency, one-in-flight limits and every UI completion observation were audited.
- The existing eight analysis tests passed. Newly added analysis independently
  reconstructs each result timestamp from its recorded monotonic ticks.
- Digest agreement verifies hit counts and payload aggregates, not collision-free
  exact per-point membership. The test does not silently claim a stronger oracle.

| Mode | Baseline mean finish s | Candidate mean finish s | Paired finish ratio [95% CI] | Baseline CV | Baseline drift | Decision |
|---|---:|---:|---:|---:|---:|---|
| Recorder off | 34.272 | 6.388 | 5.327 [4.518, 6.282] | 13.451% | 20.150% | Inconclusive |
| Recorder on | 31.606 | 6.388 | 4.931 [4.383, 5.547] | 8.909% | 17.827% | Inconclusive |

The independent unit is one paired replicate. The interval uses Student t on
five log finish ratios (df4); tasks are not independent statistical repeats.
CV must be <=5%, drift <=15%, CI lower bound >1 and paired p95 result-latency
ratio >=1.01. All candidates finish earlier in the observed pairs, but the full
predeclared gates fail. Neither favorable CIs nor the visible first pair override
those failures. No load/rate search, repeated formal cell or gate relaxation follows.

Aggregate recorder-on/off finish ratios are 0.9256 for baseline and 1.0001 for
candidate, inside the predeclared 10% tolerance. Baseline per-replicate ratios span
0.6873–1.1165, so the aggregate is not proof of negligible recorder interference.
The one-in-flight 60 jobs/s producer imposes a minimum final release time of
6.38333 s: the candidate's approximately 6.39 s finish is arrival-limited and
cannot be interpreted as maximum GPU throughput.

Four processes contain unfocused workload observations: `0-on-old-full`,
`3-off-old-full`, `4-on-new-full`, `4-on-old-full`. Focus is an additional context
problem, not a proven sole cause of variability. For example, fully focused
`2-off-old-full` finishes in 29.969 s while fully focused `4-off-old-full` takes
38.770 s. Normalizing focus alone is not yet a demonstrated repair.

## Recording reliability and original time

The original fixed first recorded pair was rejected by video QA: GDI desktop
capture contained missing/dim marker frames and visually blank frames. Failed
footage was not repaired by deleting, interpolating or replacing frames.

After the frozen matrix ended, a separate DXGI Desktop Duplication check recorded
one candidate and one baseline on the identical frozen Player. These two processes
are solely capture-reliability checks; they are not replacement formal cells and
are not pooled into the five-pair statistics. Both passed all 384 oracle checks.
Their completion times were 34.166 s and 6.386 s respectively.

Both DXGI videos retained their white task-start marker on every subsequent
encoded frame. The local 50-second diagnostic composition aligns first visible
markers and subtracts only a constant timestamp offset. Source intervals retain
their original lengths. Marker capture uncertainty is 50.0 ms baseline and
16.667 ms candidate; this is not exact OS-presentation alignment. The compositor
holds each source frame until the next captured timestamp without interpolation.
Its 60 fps encoding is not measured engine/display FPS. Full decode and multiple
start/middle/end visual inspections passed. The local video visibly labels the
failed performance gates and is not a public optimization proof.

## Sources and reproduction

Measured Player source: `9e9afd51a6b5892cbf85d135446e4c56a248bba5`.
Protocol: `protocol-queue-latency.json`, SHA256
`1c20dc3fabb27f0f8ed5811e837a7426d4d89abbbec2128b62909ea8a91a7dd1`. The later analysis and optional DXGI wrapper do not
change the measured runtime. Build receipts, the exact frozen runner, input
oracles, every process report/history and all failures accompany the evidence.

Use the existing `Build.ps1` to build a clean Release Player. Extract the evidence
oracles and use fresh output folders. These commands demonstrate the runtime;
the protocol defines the complete twenty-process order and seeds for confirmation.

```powershell
./Scripts/Run-QueueLatency.ps1 -BuildRoot '<build>' -OutputRoot '<fresh>/baseline' -Oracle '<evidence>/oracles-v1/0-hotspot-dynamic/expected.bin' -Seed 928201 -Arm old-full
./Scripts/Run-QueueLatency.ps1 -BuildRoot '<build>' -OutputRoot '<fresh>/candidate' -Oracle '<evidence>/oracles-v1/0-hotspot-dynamic/expected.bin' -Seed 928201 -Arm new-full
python ./Scripts/analyze_queue_latency.py '<evidence>/formal-v1' '<evidence>/oracles-v1' '<fresh>/analysis'
```

`-Capture -Ffmpeg '<ffmpeg.exe>'` uses the original GDI backend.
`-Capture -CaptureBackend ddagrab -Ffmpeg '<ffmpeg.exe>'` uses DXGI output 0;
the owned window must lie on that output. Capture checks here used the primary
output and exact 1280x720 owned client rectangle. The wrapper starts/ends only
its own processes under the shared GPU mutex. No Computer Use tool, administrator
elevation, global cache reset, driver/power change or company repository was used.

The composition helper refuses a normal performance video when publication gates
fail. `--diagnostic` creates an explicitly labelled local audit artifact; the
separate `--capture-check-root` option cannot silently substitute a formal pair.
No new recording assets are attached to the public evidence release. License and
third-party notices remain unchanged.
