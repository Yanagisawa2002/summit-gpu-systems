# AMD R9700 Native DX12 GPU Primitives — Formal Results

Date: 2026-07-30

## Outcome

The final end-to-end formal run on the local AMD Radeon AI PRO R9700
supports three scoped-GPU conclusions for this fixed workload:

- wave-ops exclusive scan passed the frozen acceptance gate;
- wave-ops stable 32-bit key/value radix sort passed the gate;
- wave-ops stable compaction passed the gate.

A separate pre-fix formal measurement run used identical portable/wave shader
sources, runtime implementation, Player executable, hardware, driver, Unity
version, workload, and matrix. Its GPU
samples and provenance were valid, but the on-commit schema-v5 summarizer
stopped on two fail-closed PowerShell validation bugs. The fixed summarizer
subsequently revalidated the preserved artifacts. That run is used only as a
secondary repeatability check, not as the primary end-to-end artifact.

Across the two separate measurement runs, scan, radix sort, and stable
compaction retained the same mean direction in all six paired rounds. The
wave-ops 16-bin histogram was slower in all six rounds. Append compaction
changed sign between full runs and therefore remains inconclusive.

These conclusions apply only to the native scoped GPU regions measured here.
They are not whole-frame, FPS, NVIDIA, async-compute, or driver-level results.

## Formal matrix

Both runs used the frozen experiment matrix:

- GPU: AMD Radeon AI PRO R9700, 32,476 MiB reported graphics memory
- driver: `32.0.31035.1003`
- Unity: `6000.5.2f1`
- graphics API: Direct3D 12
- native timestamp ABI: v2
- capability flags: `31`
- queue: direct graphics queue
- elements: 1,048,576
- deterministic seed: `20260730`
- variants: portable HLSL and wave-ops HLSL
- operations: append compaction, exclusive scan, 16-bin histogram, stable
  32-bit key/value radix sort, and stable compaction
- rounds: 3
- measured frames per case and round: 900
- local warmup frames: 60
- cooldown frames: 15
- dispatches per measured frame: 1
- one counterbalanced Player process per run
- raw timestamp intervals; empty-scope overhead reported separately and not
  subtracted

The predeclared per-primitive gate required three paired rounds, at least two
favorable GPU-average pairs, at least 3% median GPU-average improvement, at
least 0.005 ms median absolute reduction, no worse than 5% median GPU-P99
regression, and unchanged correctness, readback, and resident-memory behavior.

## Final formal v2 result

Report: `Reports/GpuTimestamps/formal-amd-r9700-abi2-v2`

| Operation | Median GPU-average improvement | Median absolute reduction | Median GPU-P99 improvement | Favorable mean pairs | v2 result |
| --- | ---: | ---: | ---: | ---: | --- |
| Append compaction | +31.095% | +0.011830 ms | +56.621% | 2/3 | Accepted in v2 |
| Exclusive scan | +29.715% | +0.013305 ms | +61.205% | 3/3 | Accepted |
| Histogram-16 | -132.685% | -0.053537 ms | -49.422% | 0/3 | Negative |
| Radix sort 32 | +16.820% | +1.062677 ms | +12.096% | 3/3 | Accepted |
| Stable compaction | +26.570% | +0.017473 ms | +25.847% | 3/3 | Accepted |

Positive percentages mean lower native scoped GPU time for wave-ops. Positive
absolute reductions are `portable - wave-ops`. Values are paired medians across
the three counterbalanced rounds.

Every operation preserved correctness, zero algorithm readback during
measurement, and the shared resident allocation of 42,500,296 bytes.

## Cross-run repeatability

| Operation | Pre-fix run mean / absolute / P99 | Final v2 mean / absolute / P99 | Reviewed conclusion |
| --- | --- | --- | --- |
| Append compaction | -3.257% / -0.001278 ms / +47.632% | +31.095% / +0.011830 ms / +56.621% | Inconclusive: the run-level direction changed; only 3/6 mean pairs favored wave-ops |
| Exclusive scan | +30.486% / +0.013300 ms / +12.504% | +29.715% / +0.013305 ms / +61.205% | Repeatable mean win in 6/6 pairs; P99 direction repeated but magnitude varied |
| Histogram-16 | -181.490% / -0.059291 ms / -49.291% | -132.685% / -0.053537 ms / -49.422% | Consistently negative; 0/6 mean pairs favored wave-ops |
| Radix sort 32 | +16.706% / +1.051695 ms / +13.822% | +16.820% / +1.062677 ms / +12.096% | Cleanest result: mean and P99 direction repeated in 6/6 pairs |
| Stable compaction | +25.986% / +0.016999 ms / +19.746% | +26.570% / +0.017473 ms / +25.847% | Repeatable mean and P99 direction in 6/6 pairs |

The v2 append result satisfies the predeclared single-run gate and remains
recorded as such. The reviewed cross-run conclusion is deliberately stricter:
append compaction is not a validated default and must not enter the AMD
autotuning profile from this evidence.

The v2 append block medians exposed a position-correlated/warm-state confound.
In round 1, portable ran first (37.32 us P50) and wave second (18.40 us); in
round 2, wave ran first (37.48 us) and portable second (18.80 us); in round 3,
portable ran first (36.04 us) and wave second (18.68 us). The second block was
faster in all three rounds. The odd `AB/BA/AB` schedule confounded variant with
position: wave occupied the faster second position in both favorable pairs.
The arithmetic gate passed, but the intrinsic backend effect was not isolated.
Future short-kernel protocols must use constrained, position-balanced ordering
such as ABBA/BAAB or a balanced Latin square, optionally randomized within
those constraints, plus case-local warmup. The frozen historical result is not
relabeled; the cross-run decision prevents it from entering Auto.

The exclusive-scan P99 percentage varied substantially while its median mean
improvement and absolute reduction were nearly identical. The repeatable claim
is therefore the approximately 30% scoped-kernel mean benefit, not one precise
scan P99 magnitude.

## Measurement and integrity evidence

For final formal v2:

- source commit: `6f85f2004913dd572108da1ce25a3e05c80f3b45`
- 29,700/29,700 rows returned `ready` native timestamp results
- 33 blocks each contained exactly 900 measurements
- 29,700 unique tokens and tags
- private completion fences were unique and sequential from 2 through 29,701
  after warmup fence 1
- zero fence gaps, backward values, timestamp interval overlaps, or elapsed
  conversion mismatches
- timestamp frequency was 100,000,000 ticks/s on every row
- device generation remained 1
- all 27,000 non-control scopes had positive elapsed ticks
- 20/20 warmup/final validation rows passed
- algorithm measurement readback was 0 bytes
- validation-only readback was 83,886,368 bytes outside measured samples
- timestamp instrumentation readback was 475,200 bytes, exactly 16 bytes per
  ready sample
- the 2,700 empty scopes averaged 0.000007 ms and had a P99 of 0.000080 ms,
  below the 0.005 ms gate
- empty-scope overhead was not subtracted
- all 29,700 whole-frame GPU diagnostics were unavailable and excluded from
  acceptance decisions
- the Player payload stayed hash-stable throughout the run
- the repository was clean at start and finish
- the two exact known Unity build drifts were identified and restored, with no
  unrecognized drift
- the final EditMode suite passed 48/48 with no failure, skip, or inconclusive
  result

The reported control P99 drift of 100% is a near-zero denominator artifact:
the three control P99 values were 80, 40, and 40 ns, an absolute range of only
40 ns, and the aggregate 80 ns P99 was far below the 5 us gate.

The secondary run also produced 29,700 complete native scoped-GPU samples,
20/20 passing validation rows, zero measurement readback, sequential private
fences, and a performance-usable quality classification after revalidation.

## Provenance

### Final formal v2

- report: `Reports/GpuTimestamps/formal-amd-r9700-abi2-v2`
- branch: `codex/gpu-native-dx12-timestamps`
- run window: `2026-07-30T12:19:57.8229243Z` to
  `2026-07-30T12:21:08.4895331Z`
- EditMode result SHA-256:
  `A9F796B898E4EBE668C834B9EA54B9189DBF28532E346010EB689C99667FE648`
- Player executable SHA-256:
  `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE`
- Player payload SHA-256:
  `73A41A2368D8BA6FA53963973E20408D0B2453A355D569A7C029F5C7AC35526F`
- payload: 342 files, 180,744,677 bytes
- source snapshot SHA-256:
  `9EE082A0305B512923FC76A46F07A0AFAD8BD4A6E97AA9D1A150CDFE59FDD3F3`
- benchmark harness SHA-256:
  `F8CF795893F1A142133488AF40F970A33F8B2B9E1301111FAC633C4C02AB04C3`
- portable shader SHA-256:
  `73818789D5DF49C0259A239FDB4C23D95AB9B647FA71A7206EDAC4BAB4EB3AFB`
- wave shader SHA-256:
  `24871258157FFF87241D7BDD0D0CC67EF9B078A27BC08CB7571DAAE2AD042065`
- runtime API SHA-256:
  `4C9DE3383F5875CBAF6E0918A50C4D60885B564333526E42B11CD00A690D1A72`
- native timestamp DLL SHA-256:
  `5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA`

Final report artifact hashes:

- `quality-summary.txt`:
  `76F0C0DCF92927CAA5065616E0DC140811134B0C7BCF3AB2553EA461A55B03BA`
- `operation-summary.csv`:
  `BE76F12E4F4F38189738DF9E687440A6E2CB7B3E5079D7FECB1FADC56E7311A9`
- `raw-frames.csv`:
  `193C5455DA08F744927A50FBE0001157E3077395D87A2DD78580CA80AB6EDB17`
- `runner-config.json`:
  `5E368F567472463AAB29448098661534F0876F5F43D81AAD9A4688A3DA819C91`
- `player-payload-manifest.json`:
  `D35A580F961D3283612E21185BC67D0655BEFA42A58AD1C555DB536077C14B97`

### Secondary pre-fix measurement run

- report: `Reports/GpuTimestamps/formal-amd-r9700-abi2-v1`
- source commit: `2100991c5b612a818a19844cf20489a3bc3ee5f1`
- Player executable SHA-256:
  `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE`
- Player payload SHA-256:
  `54E06932CC7CEC15B9B85439516703F7834BD3BB42B18057AA0E9EDF2DDEFF2A`
- payload: 342 files, 180,744,676 bytes

The two runs remain separate builds. The portable and wave shader source,
primitive runtime API, native timestamp DLL, Unity version, GPU, driver,
workload matrix, and Player executable hashes were identical.

## Claim boundary

Supported:

> On an AMD Radeon AI PRO R9700 under Unity 6000.5.2f1 and Direct3D 12,
> private-fence native timestamp queries measured repeatable scoped-GPU mean
> wins for wave-ops exclusive scan, stable radix sort, and stable compaction
> against portable HLSL fallbacks, with identical correctness and zero
> algorithm readback during measurement.

Not supported:

- a universal wave-ops speedup
- a stable append-compaction win or an append rule in AMD Auto selection
- a wave-histogram win
- whole-frame GPU time, frame time, FPS, or visually perceptible improvement
- asynchronous-compute performance
- NVIDIA or other cross-vendor behavior
- a driver-level optimization claim
- extrapolation beyond this element count, seed, input distribution, and
  operation mix

## Use as the next benchmark foundation

The reusable outcome is the measurement and decision framework:

- portable and architecture-aware implementations share one deterministic
  input and correctness oracle
- direct-queue native timestamp scopes measure scoped GPU work without per-frame
  algorithm readback
- correctness, residency, provenance, and timing integrity are gated together
- negative and unstable variants remain visible
- separate runs expose results, such as append compaction, that one formal run
  could overstate

This foundation is the base for direct spatial binning, count-scan-scatter,
adaptive spatial backends, sensor-data layout and fusion, shared multi-sensor
indices, scheduling experiments, and residency management. Each subsystem
still requires its own correctness oracle, frozen matrix, and formal A/B.
