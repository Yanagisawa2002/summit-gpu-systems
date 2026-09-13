# Actual Cabana/ArborX comparison — 2026-09-10

The two executable external-library workloads were built and run, and their
complete native results matched actual SUMMIT GPU output. **All five frozen
cross-backend replay cases cost more on the current SUMMIT path than on Kokkos
Serial.** This is a result about the specified staging-to-consumed-CSR task,
including the current adapter's repeated conversion/upload/rebuild costs.
It does not establish a universal CPU/GPU ranking, kernel speed, engine frame
benefit, planner improvement or deployment profile. Defaults remain unchanged.

Overall delivery is **partial**: Cabana LinkedCell CSR and ArborX sphere
comparisons completed; the pinned official Boids application was not run.
The protocol, real sources and full evidence are available below. No historical
NYCGIS, HLSL or focused-index measurement enters these numbers.

## Frozen identity and execution

| Item | Actual identity |
| --- | --- |
| Handoff | `e0db6af185985c5911a4de482e555584387faa01` |
| Measured source/protocol commit | `acc4a669f0403e9ff4d6f5d152db974da050c430` |
| Freeze time | 2026-09-10 06:32:39.742907 UTC, before any formal pair |
| Cabana | `dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802`, BSD-3-Clause |
| ArborX | `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90`, BSD-3-Clause |
| Kokkos | 4.7.02 / `6739bc623081648af9e752b616d9671527922cbf`, Serial only |
| Google Benchmark | 1.9.1 / `c58e6d0710581e3a08d65c349664128a8d9a2461`, timing framework |
| Boost / CMake | 1.87.0 / 3.31.10; official archive SHA verified |
| Native compiler | MSVC 19.51.36248, x64 Release, C++20, NMake |
| GPU host | Unity 6000.5.2f1, Windows Mono, D3D12; build GUID `614f4469570a4f0cbb76c499f5619901` |
| Device / driver | AMD Radeon AI PRO R9700, device `0x7551`, D3D12 level 12.2, driver `32.0.31041.1004` |
| CPU / execution | Ryzen 9 9950X; inherited process affinity `FF`, eight visible logical CPUs; native Kokkos Serial uses one execution thread |
| Process settings | Inherited Normal priority and affinity, unchanged; Player `-batchmode -force-d3d12 -screen-width 64 -screen-height 64`, nonthreaded graphics client reported in logs |

The [protocol](EXTERNAL_ACTUAL_PROTOCOL_2026-09-10.md) fixes four independent
process pairs per workload, ordered CPU/GPU, GPU/CPU, CPU/GPU, GPU/CPU. Each case
has ten warmups and ten measured repetitions per process. All 16 formal processes
completed with exit code 0. No sample, process or outlier was discarded/replaced.
Separate canonical native runs retain upstream iteration rules and narrower
timing boundaries. Later commits add audit tools, reproduction instructions and
this report; they do not change the measured adapters, Player or native binaries.
The final delivery commit is recorded in `resume-outcome.json`.

The source archives are pinned independently of native version strings: Cabana
prints `Not a git repository` and ArborX prints `No hash available` because these
are verified archives, not Git checkouts. `frozen-run.json` records every input,
staged runtime source, native executable, installed Kokkos file and Player file,
plus compiler/editor hashes. It passed post-run SHA revalidation.

## Inputs and complete correctness

Cabana runs its original default N=100/1000, requested widths=3/4, double
positions, native `InitRandom` seed 342343901 and ten iterations. The capture hook
is outside `create_timer`; original neighbor traversal and permutation continue.
All 40 post-build/pre-permutation snapshots are preserved, including the changing
position order. CSR bin counts are respectively 8/1/64/27 for N100-w3/N100-w4/
N1000-w3/N1000-w4. The real native replay rebuilds each snapshot through Cabana.
The paired task ends after complete bin CSR consumption; it does not include
neighbor traversal or particle permutation on either side.

ArborX uses its original `filled_box` generator, `Random_XorShift1024_Pool` seed 0,
generator batch size 8, 50,000 float points, 20,000 spheres, desired neighbors 10,
predicate sorting true and buffer size 0. The actual float radius is
`2.673009157180786`. The exported native no-callback query yields **213,313 IDs**.
Every original point, sphere, ID and offset is frozen. No synthetic generator or
radius/coordinate substitution is used. SUMMIT's conservative index domain is
[-37,37] on all axes; the original floats remain in its exact sphere predicate.

The current adapter cannot allocate its full N*Q worst-case result capacity for
the default case. The predeclared batch size 128 creates 157 batches (last 32).
Every batch uploads all points and rebuilds the existing index. All queries and
IDs are retained and CSR offsets concatenated in original query order; the
adapter's worst-case ID capacity is 6,400,000 per batch, a capacity calculation,
not an allocation-counter measurement. No index-reuse optimization was added.

Actual GPU correctness passed before formal timing for all 40 Cabana snapshots
and all 20,000 ArborX queries. Each warmup/measured repetition also saved its raw
GPU CSR and verified every offset, ID and multiplicity outside the timer. The
independent offline decoder then checked **441 raw GPU CSR files, 1,629,000 rows
and 17,476,353 ID occurrences**, including validation and warmups. All matched.
Only within-row ID order is ignored, by sorting both complete rows; this is not
a count, checksum-only, sample or CPU-model substitute. Native replays separately
compare their full reconstructed CSR and retain per-repetition checksums/times.

This validates the frozen workloads, not every floating-point boundary case or
other domain, kNN predicate, application snapshot or hardware backend.

## Paired task observations

Primary scope: synchronized **host wall milliseconds**, from neutral in-memory
input staging through complete CSR consumption. Native copies into its view,
builds the index, queries where applicable, materializes output and consumes all
`(row, ID)` pairs. GPU converts/uploads, records/submits, rebuilds/queries, reads
every offset and valid ID back, aggregates batches and consumes all pairs.
File I/O, process/engine startup, original generation and initial persistent
buffer construction are outside this repeated interval. Full equality and raw
file writing are outside it. None of these clocks is a GPU kernel/frame timer.

Means below average the four process means. Ratio R is the geometric mean of the
four paired **native/GPU** mean-time ratios; R<1 means less time on native Serial.
The CI uses the predeclared Student-t interval on log ratios, df=3.

| Frozen case | Native mean ms | SUMMIT mean ms | R | R, 95% CI |
| --- | ---: | ---: | ---: | --- |
| Cabana N100, width 3 | 0.0025475 | 0.4282675 | 0.005938 | [0.004799, 0.007348] |
| Cabana N100, width 4 | 0.0024525 | 0.4547825 | 0.005397 | [0.004971, 0.005859] |
| Cabana N1000, width 3 | 0.0119700 | 0.4974325 | 0.024015 | [0.020161, 0.028606] |
| Cabana N1000, width 4 | 0.0115975 | 0.5434400 | 0.021408 | [0.018861, 0.024298] |
| ArborX default sphere | 43.286865 | 751.607940 | 0.057593 | [0.057136, 0.058054] |

All pairs are retained, including order sensitivity and process dispersion:

| Case | Ratios in pair order 1 / 2 / 3 / 4 | Native process-mean range ms | GPU process-mean range ms |
| --- | --- | --- | --- |
| N100-w3 | .00555044 / .00725368 / .00548430 / .00563089 | .002410–.002880 | .397040–.446730 |
| N100-w4 | .00544357 / .00573947 / .00536327 / .00506240 | .002390–.002500 | .435580–.483960 |
| N1000-w3 | .02243370 / .02735154 / .02521959 / .02149393 | .011150–.013850 | .450840–.535500 |
| N1000-w4 | .02401910 / .02117929 / .02052084 / .02011896 | .011270–.012040 | .469210–.580050 |
| ArborX sphere | .05797547 / .05732026 / .05764741 / .05743008 | 43.240010–43.308160 | 747.008370–754.358170 |

The very small Cabana CPU cases last only a few microseconds. Four process pairs
describe this session and these fixed sizes; the interval is not a population or
application-wide guarantee. `analysis/comparisons.json` also preserves every
process's measured min/max and phase means, without truncating to table precision.

GPU host subintervals expose where the present adaptation spends time:

| Case | CPU encoding/validation + GPU upload ms | Record/submit/full readback/sync ms | Host consume ms |
| --- | ---: | ---: | ---: |
| N100-w3 | .0349050 | .3930725 | .0002900 |
| N100-w4 | .0311025 | .4234650 | .0002150 |
| N1000-w3 | .1131075 | .3830625 | .0012625 |
| N1000-w4 | .1327225 | .4096375 | .0010800 |
| ArborX sphere | 617.0894800 | 133.9093025 | .2696300 |

For ArborX another .3395275 ms is batch aggregation and clock overhead in the
primary interval. Native phase means are .0439975 ms input copy, 42.8347775 ms
index+query, .4080900 ms output materialization/temporary destruction/consumption.
GPU upload includes the adapter's input validation and conversion on every batch;
the 617 ms is not a PCIe-only or transfer-bandwidth result. GPU rebuild, exact
filter, scan/scatter and synchronization are included in the combined host
submit/readback interval and are **not individually measured GPU phases**.

ArborX's one native input-generation/export preparation records 1.2809 ms for
the original generators only (not file export). It is diagnostic, not repeated
generation performance or an end-to-end ratio. Cabana generation, initial
allocations and application/render/presentation consumption have no separate
measurement here. CSR checksum consumption is the explicitly bounded consumer.

## Canonical native outputs, kept separate

The unchanged Cabana `LinkedCellPerformance` executed all four original cases,
ten iterations each, including native neighbor iteration and permutation. Its
timer utility outputs microseconds and publishes min/max/mean, not individual
iteration times. The full emitted output is retained; no per-iteration samples
are invented for that canonical driver. These are its means:

| Case | Build us | Unsorted-neighbor us | Permute us | Sorted-neighbor us |
| --- | ---: | ---: | ---: | ---: |
| N100-w3 | 3.09 | 50.37 | 2.41 | 48.67 |
| N100-w4 | 4.28 | 50.52 | 2.41 | 53.94 |
| N1000-w3 | 10.13 | 875.14 | 4.11 | 892.58 |
| N1000-w4 | 10.01 | 1697.93 | 3.91 | 1631.11 |

The unchanged ArborX driver executed the registered
`BM_radius_search<ArborX::BVH<Serial>>/50000/20000/10/1/0/0/0/manual_time` case.
Google Benchmark's default minimum-time/iteration selection used **17 iterations**;
its manual query time is **39,634.364706 us (39.634365 ms)**. The native interval
surrounds the no-callback full-CSR query and fences; initial tree construction,
generators and the replay checksum consumer are outside it. It is not pooled with
the 43.286865 ms native complete replay, nor compared directly to a GPU kernel.
The original registration listing, stdout, stderr and JSON are all retained.

## Repairs, checks and unexecuted scope

- Added thin native capture/replay entry points, a minimal real GPU Player,
  fixed input manifests and guarded serial process/stage receipts. Existing
  Cabana/ArborX generators and native algorithms were used directly.
- The first Unity Player build returned success but logged FXC's
  `internal error: flattened side effect` for `CountMatches`. Preserved the failed
  shader bytes, complete log and entire original Player. Added only `#pragma use_dxc`
  to the owned sphere shader, preserving its precise predicate and traversal.
  Incremental `player-r1` build had no shader/C# errors, then passed the real GPU
  full-output gate. No failing/incomplete result was timed as a formal sample.
- Preserved the initial PowerShell parse failure and CMake `\U` path-escape
  configure failure/log. Path normalization repaired the Windows supplementary
  build. Existing upstream Boost helper C4244 warnings remain in full compile
  logs; the unselected BoostRTree registration is compiled but not benchmarked.
- Built real Kokkos Serial, canonical Cabana/ArborX, native replays and Unity
  Player; validated native and real GPU outputs; completed all 16 formal processes
  and both canonical runs; independently audited full GPU bytes and frozen hashes.
  No existing source/HLSL lock changed, and no default candidate/profile changed.
- Boids requires pinned Editor **6000.2.10f1**, which is not installed. This
  worktree's retained official reference directory has only eight preparation
  files (38,505 bytes), not the official scene/subscenes and complete assets.
  The installed Editors are 6000.1.0f1, 6000.4.10f1, 6000.5.2f1 and 6000.5.3f1.
  A new Editor/full application restore was not performed to fill an application
  checkbox. Its import/build/storage peak and application comparison remain
  unestablished; the small GPU host is not substituted for it.
- Native GPU backends, same-device comparisons, ArborX kNN/callback cases,
  comparative SUMMIT neighbor traversal/permutation, GPU timestamps/counters,
  engine frames/presentation, and incremental planner benefit were not measured.

Every build, download/extraction, Unity import and native/GPU execution used
`Local\CodexR9700VNextUnityGpu` serially. Stage receipts record no conflicting
editor/build/runtime at admission. Initial free space was 50.4169 GiB; the lowest
recorded stage-boundary free space was 48.7082 GiB, and the smallest predeclared
peak-reserve estimate was 41.0182 GiB, above the required 20 GiB. The six downloaded
archives total 207,020,197 bytes and expand to 936,763,840 bytes, separately from
build caches/Players. No old cache/project/evidence was deleted, no user editor
was stopped and no driver, power, affinity, priority or system cache was changed.
Final free space and process/mutex release are rechecked in the outcome receipt.
The pre-delivery probe confirmed all 25 explicitly recorded processes exited,
no remaining editor/build/runtime workers, and an available mutex that the probe
released. It recorded 48.714108 GiB free; `resources-released.json` preserves the
probe time, process results and 10 MHz host Stopwatch frequency.

## Evidence and reproducibility

All raw evidence is under `Artifacts/actual-20260910/`, separate from prior runs.
The tracked [reproduction guide and real sources](../Tools/ExternalSources/ActualNative/README.md)
document native builds/capture, corrected Player staging, all fixed process pairs
and the offline decoder. Large archives, binaries and caches are retained locally
and excluded from Git. `evidence-inventory.json` lists every raw log, snapshot,
CSR, result, process/stage receipt and analysis with SHA256; final resource/outcome
receipts remain separate to avoid circular receipt hashes.

| Evidence path, relative to dated root | SHA256 |
| --- | --- |
| `evidence-inventory.json` (709 evidence files) | `5ccd0b067fc9bba78dd2484330c2aeda6b1b0df72063e79e4533c701215e1c91` |
| `frozen-run.json` | `ed808e63c36b49dab5548314b4bb6d2d809edcd25e808988b8992af8760349a9` |
| `input-manifest.json` | `9d35f71c55f924d84b031316932ff025bc13ada43e8d450640ab92b957575737` |
| `inputs/arborx-default.bin` | `750d9a9fa8331b005fe5709e8f25395d8b4ce856d0b174d470639a78fa9fad59` |
| `player-r1-staging-receipt.json` | `b1ab97c1b331332e4bcf81e515bee4380ce9df22ca3fc1cca13612504911c429` |
| `dependency-source-receipt.json` | `2379e55058ac8fc0dfa22ad9394ad423fda45f0dcf793d24197a597121559a41` |
| `boost-source-receipt.json` | `69e6fc1389bb85bb9bb9b0b6cd200f159ef0f584a2f0dfd99c16d1ef2546f084` |
| `analysis/comparisons.json` | `d4e1b5016125db10aa86f5906df1d8374dc0a9515de74f59f395ea6d41f69a4f` |
| `analysis/full-csr-audit.json` | `9ec3aa36d5f9b916a35e17b1631bed623856b3cb9200ac319af4306ba947fbdb` |
| `canonical-cabana/native-timers.txt` | `4f8321a495be4ce2a9c6378b1df08592f13667e826c021a1673c821441c300b9` |
| `canonical-arborx/native.json` | `72978582835ea14fc4b130f32124f700a66045fa7c0248e1f26f36f4f30e0b5c` |
| `native-generated/CabanaCapture.patch` | `d096466050e4abad8a4dab3186b12e9631a95a825850ee335a4d1ba447a6beb6` |

Detailed performance: `runs/{cabana,arborx}-p{01..04}-{native,gpu}/`, including
every native CSV, GPU `result.json`, actual `.csr` and process log. Failures:
`stages/01-parse-failure.json`, `05-build-arborx.log`,
`05-configure-failure.yaml`, `09-unity-build-editor.log`,
`09-failed-SummitExternalSphere.compute`; repaired build:
`stages/10-unity-build-editor.log`. Final handoff:
`resume-outcome.json` with source/delivery commits, scopes, correctness,
performance paths, free space, release status and remaining blockers.
