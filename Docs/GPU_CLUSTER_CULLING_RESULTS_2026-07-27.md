# GPU cluster-culling benchmark results — 2026-07-27

## Decision

The projected-pixel culling path is promising on the AMD Radeon AI PRO R9700, but it is not ready
to become a production default. The 1 px threshold improved average GPU and frame time in all
three paired runs. GPU P99 regressed in two of three pairs, visual review is still required, and
no connected NVIDIA adapter was available on the benchmark workstation.

Keep the implementation opt-in until the same Player passes the NVIDIA matrix and the P99 and
visual-quality gates.

## Reproducible baseline

- Unity: 6000.5.2f1, Windows Development Player, Direct3D 12, 1280 x 720.
- Feature base: `e524470d37a83c17fc977aa54a1c58dc38c8ff8f`.
- Source legacy production scene SHA256:
  `B25020CCBD1CEC1C17A70A4220FC46307CB01EE51643B9AD70748D1928942398`.
- Prepared benchmark scene SHA256:
  `B06BFFC6DD133A68C3FD279F57D840FB8E893E5A99EC0C5C38C0CB7D9E3B2DDD`.
- Player executable SHA256:
  `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE`.
- Player `Assembly-CSharp.dll` SHA256:
  `C884CC9A8B9A513C304C8A108496A7C2F8C31EF4ABB69F11D9679008B271792C`.
- Player `level0` SHA256:
  `5C2F6FF1B1FAD8FDB39B6D1CBFBF10BF38FD6BD39B0A62371D2216C8A0F557D0`.
- Production data root supplied through `NYCGIS_DATA_ROOT`.
- Workload: one camera, HeavyStorm, 30 second warm-up, 600 sampled frames.
- Pair order: control then 1 px, repeated three times. One incomplete control launch was discarded
  before sampling and rerun.
- All accepted runs: 227 total BFP2 packs, 73 resident packs, 1,932,028,912 resident GPU bytes,
  and 600/600 GPU timing samples from `FrameTimingManager`.

The source scene came from the read-only migration source. The supported Water v1 repair and BFP2
renderer configurator were then applied to the copied benchmark scene. The heavy scene and
generated GIS assets remain outside Git.

## AMD Radeon AI PRO R9700

GPU/driver facts recorded by Unity:

- AMD Radeon AI PRO R9700, 32,476 MiB graphics memory.
- Direct3D 12 feature level 12.2.
- Driver 32.0.31021.5001.

| Round | GPU avg off / 1 px | GPU P95 off / 1 px | GPU P99 off / 1 px | Frame avg off / 1 px | FPS off / 1 px |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 31.302 / 29.956 ms | 33.632 / 33.034 ms | 33.831 / 33.252 ms | 38.426 / 37.292 ms | 26.02 / 26.82 |
| 2 | 32.182 / 29.902 ms | 38.212 / 35.503 ms | 44.963 / 49.503 ms | 39.343 / 36.927 ms | 25.42 / 27.08 |
| 3 | 31.330 / 28.522 ms | 33.745 / 33.085 ms | 40.893 / 43.210 ms | 38.151 / 35.372 ms | 26.21 / 28.27 |

Paired deltas, where positive means improvement:

| Metric | Mean delta | Median delta | Range |
| --- | ---: | ---: | ---: |
| GPU average | +6.78% | +7.08% | +4.30% to +8.96% |
| GPU P95 | +3.61% | +1.96% | +1.78% to +7.09% |
| GPU P99 | -4.68% | -5.67% | -10.10% to +1.71% |
| Frame average | +5.46% | +6.14% | +2.95% to +7.28% |
| Frame P95 | +3.15% | +1.85% | +1.55% to +6.06% |
| Frame P99 | +2.34% | +5.53% | -8.08% to +9.57% |
| FPS | +5.82% | +6.53% | +3.07% to +7.86% |
| Peak used memory | -0.34% | -0.12% | -0.90% to -0.01% |

Interpretation:

- The optimization changes cluster visibility work, not residency. Identical pack counts and GPU
  bytes support a valid paired comparison.
- The repeatable average improvement clears the 3% usefulness threshold on this AMD GPU.
- Tail behavior does not clear the acceptance gate. GPU P99 was worse in rounds 2 and 3.
- Unity's draw-call and render-thread counters were unavailable in this standalone configuration.
  The result therefore does not claim a draw-call reduction.

## Additional confirmation pair

A fourth pair was run after the initial report at the user's request, with the same Player,
hardware, data root, warm-up, sample count, pack residency, and GPU timing source.

| Metric | Control | 1 px | Improvement |
| --- | ---: | ---: | ---: |
| GPU average | 31.246 ms | 29.911 ms | +4.27% |
| GPU P95 | 33.702 ms | 32.972 ms | +2.17% |
| GPU P99 | 42.893 ms | 42.428 ms | +1.08% |
| Frame average | 38.074 ms | 37.154 ms | +2.42% |
| Frame P99 | 54.738 ms | 51.896 ms | +5.19% |
| FPS | 26.27 | 26.92 | +2.47% |

This confirmation pair improved GPU P99, but it does not erase the earlier P99 regressions. The
production-default and NVIDIA gates remain unchanged.

## NVIDIA status

The workstation exposed the R9700 and an AMD integrated GPU only. Windows PnP did not enumerate a
connected NVIDIA adapter, so a same-Player NVIDIA result could not be produced honestly.

Run the identical matrix on the NVIDIA host:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-NYCGISGpuClusterCullAB.ps1 `
  -DataRoot C:\path\to\NYCGISData `
  -DeviceIndex 0 `
  -CameraCount 1 `
  -WarmupSeconds 30 `
  -SampleFrames 600 `
  -Repeats 3
```

The runner uses Unity's `-force-device-index`, records the selected GPU in every report, requires
valid GPU timing samples, and rejects zero-pack runs. Do not substitute the repository's older RTX
5060 Ti Multi-RT fixture result; it is a different workload.
