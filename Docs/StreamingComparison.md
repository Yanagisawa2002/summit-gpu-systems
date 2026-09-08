# Withdrawn streaming comparison: historical measurement notes

The video, original capture downloads and poster were withdrawn on 2026-09-08 at the author's request. They do not establish stable full-frame gains and are no longer portfolio media. Measurement data, code and the historical description below are retained for audit. The evidence archive contains pre-withdrawal documentation; its video download links are no longer available.

## Read the video

- Left: `old-full`, CellSerial queries. Right: `new-full`, BatchedPointScanWave
  queries. Both use the same complete Direct WaveOps index rebuild, Release Player
  binary, 262,144 slots, nine queries, content files and seed 928201.
- Both execute the same 384 logical updates and load/cancel/register/unregister/
  unload timeline. Each was recorded in its own process, sequentially under the
  shared GPU lock; they were subsequently placed side by side. They are not two
  simultaneously benchmarked Players or perfectly frame-locked recordings.
- The nine bars show query hit counts using logarithmic height. They are not FPS,
  GPU utilization or time. Their colors identify queries.
- Both recordings schedule the updates across 45 seconds, with preroll and an
  ending hold. The real GPU commands continue drawing between logical updates.
  Playback speed, encoded frames, and these paced run timings are not performance
  evidence. There is no retiming or image interpolation. The composition uniformly
  scales the recordings and ends at 49.9 seconds; only the final hold is shortened.
- The charts are static historical measurements, **not live video-frame timings**.
  Thin lines retain all 20 raw block trajectories per arm; thick lines are medians
  at each logical frame. Solid blue is baseline, dashed orange is candidate.
  The y-axes use milliseconds on an explicitly labelled logarithmic scale; all
  spikes are retained and inside the axes. No smoothing or outlier removal.

## Measurement scope and results

The chart data come from the existing
[focused-costs confirmation](https://github.com/Yanagisawa2002/summit-gpu-systems/releases/tag/r9700-focused-costs-2026-09-08),
collected September 7 UTC / September 8 Singapore time. This delivery does not
repeat the formal matrix. All five streaming processes are included, with four
balanced paired blocks in each process. Each arm has 384 frames; the predeclared
first 64 are warmup, leaving 320 steady frames per block.

The five payload seeds are 928201, 928203, 928207, 928211 and 928213. Positions and
motion are the same; within each process all arms share exactly the same input.
The new recordings use the first process's seed and original oracle.

| Scope | Baseline mean ms | Candidate mean ms | Paired baseline/candidate ratio [95% CI] | Frozen decision |
|---|---:|---:|---:|---|
| Instrumented scene GPU | 1.7194 | 0.1644 | 10.471 [9.855, 11.126] | Passes narrow metric gates |
| Engine logical-frame cadence | 1.8774 | 0.5788 | 3.268 [2.875, 3.714] | Inconclusive |
| Nine-query GPU interval | 1.5863 | 0.0235 | 67.746 [62.049, 73.966] | Inconclusive |
| Index GPU interval | 0.0236 | 0.0270 | 0.876 [0.810, 0.947] | Inconclusive |
| CPU command recording | 0.2215 | 0.2294 | 0.966 [0.923, 1.012] | Inconclusive |

Means are arithmetic means over the equally sized blocks. Ratios are geometric
means of paired block ratios within each process, then across five processes;
the CI is Student t on five process log ratios (df=4). Ratios above one favor the
candidate. The 6,400 steady frames per arm are **not** 6,400 independent repeats.
CI comparisons are descriptive and unadjusted for multiplicity. The JSON also
contains block-averaged p95 values, paired p95 ratios, all CVs, drift and gates.

- **Scene GPU** brackets clear, index construction, nine queries and frame
  digests, history storage, particles and query bars on the main D3D12 queue.
  It is an instrumented command interval, not complete engine GPU time. Three
  native scopes introduce completion/flush/synchronization effects; a narrower
  result cannot be reinterpreted as an uninstrumented kernel speedup.
- **Engine cadence** is the coroutine logical-frame interval, including its
  end-of-frame and next-Update waits. It excludes separate setup, checkpoints,
  final verification and teardown. It is not OS display cadence. The candidate
  CV is 10.580% (limit 5%); maximum baseline drift is 18.114% (limit 15%).
  Its point estimate and CI therefore do not establish stable full-frame gains.
- **Query GPU**, **index GPU** and **CPU recording** are retained above, including
  index and CPU point estimates below one. Their failed gates are not hidden.
  Do not sum or subtract nested probe intervals to infer uninstrumented costs.
- Positive complete-engine GPU coverage in candidate blocks is 68.44–92.50%,
  below the required 95%. That comparison remains unavailable; zero values are
  not replaced with native intervals. No OS presentation trace was acquired.

## Correctness, provenance and limits

All 40 selected historical arms (15,360 frames) and both new recordings (768
frames) match their CPU oracle histories byte for byte. Each frame stores nine
query digest records plus one metadata record. Each record has four uint32s.
This checks hit counts and payload digest aggregates, not collision-free exact
per-point query membership. Work counters and actual content lifecycle events
were also matched; native timestamp ticks were independently recalculated.

- Measurement source: `4a63663c2ea0d089e8ca88c229f93b333d0df23e`.
- Recording source: `276c4ee9efa62113036922444c6687a79032b81e`; one clean Release binary for both
  recordings, Unity 6000.5.2f1, D3D12, Radeon AI PRO R9700.
- Package Runtime sources, fixture, content lifecycle and resource shaders match
  across those commits. The recording harness adds pacing and disables probes;
  it is deliberately not the historical measurement harness.
- Composition: H.264, 1920×1080, 49.9 seconds, nominal 30 fps, 1,497 encoded frames,
  no audio. Full decoding and start/middle/end visual checks passed. Raw captures
  retain variable capture timing and dropped capture frames; encoding rate is
  not a measured engine frame rate.
- Video SHA256: `d936437727aeee038ac41efd168c5086531f58f2e9ca913de898a568a48fa714`.

## Reproduce the composition or record again

Download `comparison-evidence.zip` and extract it. It contains a portable `scene`
subset with all five full process reports, selected histories, original oracles,
protocol and published analysis; `capture` contains both recording reports,
histories, logs and build receipts. Existing absolute paths are provenance only.

The original video downloads have been withdrawn. To reproduce the historical composition, make fresh recordings with the commands below and place them as
`capture/baseline-v1/summit-streaming-point-cloud.mp4` and
`capture/candidate-v1/summit-streaming-point-cloud.mp4`. In a Python virtual
environment, install `render-requirements.txt` from the evidence `composition`
folder. From this repository's `PublicBenchmarks/UnityGpuIntegration` directory:

```powershell
python ./Scripts/compose_comparison.py --evidence-root '<extracted>/scene' --capture-root '<extracted>/capture' --output '<fresh>/composition' --ffmpeg '<ffmpeg executable>' --ffprobe '<ffprobe executable>' --render-video
```

Windows Microsoft YaHei fonts are used by default; `--font` and `--bold-font`
accept local Chinese-capable font files. Fonts are not redistributed. Versions
are recorded for reproducibility, not a claim of byte-identical encoding on
different FFmpeg or font versions.

To make fresh real recordings, build the current clean project with `Build.ps1`,
then run the two commands sequentially (each recorder owns the shared GPU lock):

```powershell
./Scripts/Record-Showcase.ps1 -BuildRoot '<build>' -OutputRoot '<fresh>/baseline-v1' -Oracle '<extracted>/scene/oracles-v1/0-streaming-switch/expected.bin' -Seed 928201 -Arm old-full -Ffmpeg '<ffmpeg executable>'
./Scripts/Record-Showcase.ps1 -BuildRoot '<build>' -OutputRoot '<fresh>/candidate-v1' -Oracle '<extracted>/scene/oracles-v1/0-streaming-switch/expected.bin' -Seed 928201 -Arm new-full -Ffmpeg '<ffmpeg executable>'
```

For new formal measurements, use the original frozen protocol and the
[focused-costs release report](https://github.com/Yanagisawa2002/summit-gpu-systems/releases/tag/r9700-focused-costs-2026-09-08).
Do not substitute the showcase pacing for the formal runner.

The repository's limited benchmark reproduction license and upstream licenses
remain in effect. No plugin default, company project, driver setting, global
cache or persistent system permission was changed for this delivery.
