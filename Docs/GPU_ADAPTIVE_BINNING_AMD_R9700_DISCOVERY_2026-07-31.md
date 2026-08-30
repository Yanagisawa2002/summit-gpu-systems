# GPU Adaptive Spatial Binning: AMD R9700 Discovery

## Decision

Five forced-backend discovery runs show a clear exploratory,
distribution-dependent crossover in paired average time on the tested AMD
Radeon AI PRO R9700:

- low-bit Radix won all 8/8 adjacent pair averages for the two `C = 16`
  `singlebin` cases;
- Direct count-scan-atomic-scatter won all 8/8 adjacent pair averages for
  `C = 16` uniform, `C = 16` hotset4, and `C = 65,536` uniform;
- the same `N = 1,048,576`, `C = 16` problem changed winner when only the key
  distribution changed;
- Radix required between 2.40x and 2.71x the modeled logical case-resident
  buffer bytes of Direct.

This is enough to justify a **selector candidate and a formal holdout**, but
not an automatic production selector or a universal performance claim. Every
retained root is explicitly exploratory:

- `formalAcceptanceMode=0`;
- `matrixPreset=single`;
- `matrixRole=custom`;
- `aBDataUsable=0`;
- `crossoverClaimUsable=0`.

A later schema-9 formal holdout validated a narrow, fail-closed **exact-cell**
classification replay and is documented in
`GPU_ADAPTIVE_BINNING_AMD_R9700_FORMAL_2026-07-31.md`. That result does not
retroactively make these discovery roots formal, and it does not validate a
broader threshold-based or production selector.

The measured cells support a narrow conclusion: extreme same-bin contention
can make one-pass Radix faster, while Direct is strongly preferred for the
tested less-concentrated or multi-pass workloads.

## Evidence scope

The five audited roots are:

```text
C:\tmp\s3-discovery-c16-singlebin-e9db296
C:\tmp\s3-discovery-c65536-uniform-e9db296
C:\tmp\s3-discovery-c16-uniform-e9db296
C:\tmp\s3-discovery-c16-hotset4-e9db296
C:\tmp\s3-discovery-n262144-c16-singlebin-e9db296
```

Byte-for-byte retained copies now live at:

```text
Reports/GpuAdaptiveBinning/discovery-amd-r9700-e9db296-v1
```

Its 81-entry SHA-256 manifest is `evidence-manifest-sha256.csv`.

### Schema-compatible re-summarization

The retained `runner-config.json` files use runner schema 6. The current HEAD
summarizer accepts schemas 9 and 10 and intentionally fails closed on schema 6; that
rejection is a compatibility guard, not evidence corruption. Do not use the
current HEAD summarizer to regenerate these discovery summaries.

The raw inputs (`raw-frames.csv`, `validation.csv`, `block-summary.csv`, and
`paired-deltas.csv`) and their original final `scenario-summary.csv`,
per-scenario `quality-summary.txt`, root `matrix-summary.csv`, and root
`quality-summary.txt` are already retained under the evidence manifest.

If exact re-summarization is needed, first copy one schema-6 scenario root to
a disposable directory, then use the summarizer from the measured commit:

```powershell
git -C C:\Users\EdwinLiu\Downloads\SUMMIT worktree add --detach `
  C:\tmp\summit-adaptive-e9db296 `
  e9db29669db98f1a8d7ad2dd44b2934a983e2f9d
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File C:\tmp\summit-adaptive-e9db296\Tools\Summarize-GpuAdaptiveBinningBenchmark.ps1 `
  -ReportDirectory C:\tmp\<copied-schema6-scenario-root>
```

The placeholder must point to the disposable copy, not the retained evidence.

All five used:

- Git commit:
  `e9db29669db98f1a8d7ad2dd44b2934a983e2f9d`;
- branch: `codex/gpu-adaptive-spatial-backend`;
- Unity: `6000.5.2f1`;
- Windows 11;
- Direct3D 12, feature level 12.2;
- GPU: AMD Radeon AI PRO R9700;
- PCI vendor/device IDs: `0x1002` / `0x7551`;
- Unity-reported graphics memory: 32,476 MiB;
- driver: `32.0.31035.1003`;
- native timestamp ABI 2 and capability flags `0x1F`;
- explicit `wave-ops`, `guaranteed-in-range`, and
  `unspecified-within-bin` contracts.

The retained Player used input-generator contract
`gpu-adaptive-binning-input-v2`. Under that historical contract,
`singlebin` always occupied bin 0. The hashes and conclusions below remain
valid for those measured inputs; they do not establish that performance is
independent of which bin is occupied.

The audited source and binary bindings are identical across all five roots:

| Binding | SHA-256 or value |
|---|---|
| Source snapshot, 72 files | `179C0998EAD205E25A777C9C5CFF605045749257726BAD34961B8A5F174F4337` |
| Player payload, 350 files / 181,029,754 bytes | `999A2088CF004AD8878175B5A0326982AD5068F5280428FCB0E2581E70125000` |
| Player executable, 667,648 bytes | `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE` |
| Direct shader | `4C9F9B4FC57B20D6DCD7832E446224C527AFB918F592547753DA8B5F659ECD75` |
| Radix shader | `B0CA460E52048C1BFE1577A5F63C08162BBA94E06BDEFC35E5F5A678CBA6E6A9` |
| Runtime API | `F9053E5D193359F44815F6555D464A2712D1CC9D275C8F207C9E363F800AAC68` |
| Native timestamp DLL | `5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA` |
| Copied EditMode XML | `F96368FA55920492E29DD9466CD1DF13581EFE45B9380324E3861740C15284B1` |

Every run reported `sourceHashesStableAcrossBuild=true`. All five began from a
clean named HEAD. Four finished clean. The first root recorded two known Unity
import side effects at final capture:

```text
 M Assets/Settings/UniversalRenderPipelineGlobalSettings.asset
 D Packages/com.firstgeargames.fishnet/CodeGenerating/cecil-0.11.4/Mono.Cecil.sln.meta
```

Those paths are outside the 72-file measured source snapshot, and the Player
payload is byte-identical across all five roots. The dirty final capture must
still remain disclosed because this is discovery evidence, not a formal clean
run.

## Protocol

Each scenario ran in one Player process with:

```text
control-pre; ABBA; BAAB; ABBA; BAAB; control-post
```

The exact per-scenario sample shape was:

- four super rounds;
- eight adjacent pairs: four AB and four BA;
- 60 case-local warmup frames;
- 240 measured frames per block;
- 15 cooldown frames;
- one dispatch per measured frame;
- 18 blocks and 4,320 native timestamp rows;
- four correctness validations: Direct and Radix before and after
  measurement.

`A = Direct` and `B = Radix`. The complete command-buffer region was measured
with the native D3D12 timestamp backend.

The formal plan uses 900 measured frames per block and source-bound test
evidence. These 240-frame runs therefore remain discovery runs even though
they use the formal position-balanced schedule and eight-pair shape.

## Exact discovery results

Positive signed deltas mean Radix was faster:

```text
signed average % = (Direct average - Radix average) / Direct average * 100
signed absolute ms = Direct average - Radix average
```

The reported signed values are medians of eight adjacent pair deltas. The
Direct and Radix time columns are independently computed medians of the eight
pair-level block averages; therefore rounded table values should not be used
to recompute the median pair delta.

| N | C | Distribution | Radix passes | Direct GPU avg median (ms) | Radix GPU avg median (ms) | Signed GPU avg median | Signed absolute median (ms) | Signed GPU P99 median | P99 pairs favoring Radix | AB / BA avg medians | Average-pair winner | Discovery classifier |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---|---|---|
| 1,048,576 | 16 | singlebin | 1 | 1.289081000 | 0.671825333 | +47.535092% | +0.610209500 | +45.591980% | 7/8 | +47.360634% / +47.535092% | Radix 8/8 | `radix-accepted` |
| 1,048,576 | 65,536 | uniform | 4 | 0.061345083 | 2.601542417 | -4,158.865084% | -2.540595167 | -3,889.885683% | 0/8 | -4,168.553983% / -4,142.467255% | Direct 8/8 | `direct-accepted` |
| 1,048,576 | 16 | uniform | 1 | 0.113096917 | 0.702934500 | -515.789679% | -0.589941250 | -598.073828% | 0/8 | -521.204744% / -515.789679% | Direct 8/8 | `direct-accepted` |
| 1,048,576 | 16 | hotset4 | 1 | 0.224064500 | 0.699363583 | -213.488113% | -0.478220667 | -261.306284% | 0/8 | -216.550371% / -210.082122% | Direct 8/8 | `direct-accepted` |
| 262,144 | 16 | singlebin | 1 | 0.399465500 | 0.280128250 | +29.716941% | +0.118707167 | +15.294954% | 7/8 | +28.273318% / +30.201771% | Radix 8/8 | `radix-accepted` |

The average result is position-consistent: AB and BA medians have the same
sign in every cell, and the winning backend won all eight average pairs in
every cell.

The two Radix-winning cells do **not** establish stable P99. Each had one
pair-level P99 regression:

- `N = 1,048,576`, `C = 16`, singlebin: worst pair
  `-40.159319%`, with 7/8 P99 pairs favoring Radix;
- `N = 262,144`, `C = 16`, singlebin: worst pair
  `-8.912839%`, with 7/8 P99 pairs favoring Radix.

The P99 medians pass the exploratory frozen gate, but the pair-level
regressions must remain visible. Do not describe the Radix tail as stable.

## Logical memory tradeoff

`caseResidentBytes` is a modeled sum of shared contract buffers plus the
selected backend's scratch buffers. It is not a driver residency counter or a
measurement of physical VRAM allocation.

| N | C | Direct case bytes | Radix case bytes | Radix extra bytes | Direct / Radix (MiB) | Radix / Direct |
|---:|---:|---:|---:|---:|---:|---:|
| 1,048,576 | 16 | 12,583,504 | 34,111,760 | 21,528,256 | 12.000565 / 32.531509 | 2.710832x |
| 1,048,576 | 65,536 | 14,452,752 | 34,635,920 | 20,183,168 | 13.783218 / 33.031387 | 2.396493x |
| 262,144 | 16 | 3,146,320 | 8,528,048 | 5,381,728 | 3.000565 / 8.132980 | 2.710483x |

The three `N = 1,048,576`, `C = 16` distributions have identical logical
memory footprints. Their opposite winners therefore come from execution
behavior, not a different allocation contract.

## Correctness, timestamp, and readback audit

All four validation rows passed in every scenario. Direct and Radix matched
the same canonical CPU-oracle result during warmup and final validation.

| N | C | Distribution | Canonical CSR SHA-256 result |
|---:|---:|---|---|
| 1,048,576 | 16 | singlebin | `summit.gpu-adaptive-binning.canonical-csr.sha256.v1:A5BB81F8850D0793C5975E7C8426B031DDB6BBA36A48E21B27480C930E11823A` |
| 1,048,576 | 65,536 | uniform | `summit.gpu-adaptive-binning.canonical-csr.sha256.v1:A3464ECF8F39214E20626A13362E410B1D466CA1AC468A9B77317A6057F534B3` |
| 1,048,576 | 16 | uniform | `summit.gpu-adaptive-binning.canonical-csr.sha256.v1:8BA98FB4CFE130F99C046DC539FF62ADDF4BD043B57ADE641559D7481D8E1CC3` |
| 1,048,576 | 16 | hotset4 | `summit.gpu-adaptive-binning.canonical-csr.sha256.v1:EF4D64EB7A43F0151115386930AB596849E795A8DBFA24433B341AC719FC3D66` |
| 262,144 | 16 | singlebin | `summit.gpu-adaptive-binning.canonical-csr.sha256.v1:E5FD7A8E0A99DA758529186FD08616A72165C74EAFBA754EFFE46363ACF5B421` |

The hash covers counts, offsets, diagnostics, and canonicalized per-bin
membership. The undefined output tail is excluded, and physical order within
a bin remains outside the shared contract.

Timestamp evidence passed the following checks in every scenario:

- native timestamp availability was `Available`;
- warmup passed;
- all 4,320/4,320 rows had status `ready`;
- `gpuRegionTimingComplete=1`;
- acquire failures, result failures, timeouts, and pending rows were all zero;
- empty-scope P99 was between 0.00008 and 0.00012 ms, below the 0.005 ms gate;
- the native timestamp device, ABI, capability mask, and DLL hash were
  identical across roots.

The precise readback boundary is:

- workload/result readback inside measured frames: **0 bytes**;
- timestamp-instrumentation result bytes: 16 bytes per completed sample,
  69,120 bytes per scenario;
- correctness validation readback occurred outside measurement:
  16,777,776 bytes for each one-million-element `C = 16` scenario,
  18,874,416 bytes for the one-million-element `C = 65,536` scenario, and
  4,194,864 bytes for the 262,144-element scenario.

It is safe to say "zero measurement workload readback." It is not safe to say
"no CPU-visible data transfer of any kind."

## Evidence quality and limitations

Confidence is high that the two implementations produced equivalent results
and that the recorded native GPU regions are internally usable for these five
specific discovery cells. The strongest evidence is the common binary
payload, exact canonical hashes, complete timestamps, balanced pair order,
zero measured workload readback, and unanimous average-pair direction.

The following limitations block formal or general claims:

1. Every root has `formalAcceptanceMode=false` and only 240 measured frames
   per block rather than the frozen 900.
2. Each cell was measured in one Player process. There are no independent
   process repeats or formal holdout seeds in this evidence set.
3. The 373/373 passing EditMode XML was caller-supplied and copied identically
   into every root, but `editModeEvidenceBoundToSource=false`. Formal mode must
   execute and bind its own test result to HEAD and the source snapshot.
4. The five roots are separate one-cell custom matrices. Their own summaries
   correctly report `crossoverClaimUsable=0`; combining them here identifies
   a selector hypothesis, not a frozen-gate crossover acceptance.
5. `singlebin`, `hotset4`, and uniform are synthetic distributions, not a
   captured production sensor or city-scene trace.
6. Discovery used generator v2, whose `singlebin` target was fixed at bin 0.
   Generator v3 instead selects exactly one target with
   `unchecked((uint)seed) % C`. The formal singlebin seeds therefore provide
   unseen nonzero target-bin inputs, not an exact replay of these v2 cells.
7. The two Radix-winning cells each contain one P99-sign regression.
8. The source roots were produced under `C:\tmp`; durable copies are retained
   under `Reports/GpuAdaptiveBinning/discovery-amd-r9700-e9db296-v1`.
   The 81-entry evidence manifest SHA-256 is
   `7FE8EEBC24D9C426BE0B2302C53B35FDA559100A5DEF33604C988F6220A6C067`.
9. This is one AMD GPU and driver. NVIDIA and cross-vendor behavior remain
   unmeasured.

## Selector candidate

The evidence rejects a selector based only on "small bin count" or "high
contention." At the same `N = 1,048,576` and `C = 16`, Radix won for
singlebin, but Direct won for hotset4 and uniform.

A conservative candidate is:

```text
default -> Direct

Radix eligible only when:
  radixPassCount == 1
  and N >= 262,144
  and an already-available producer or temporal signal predicts
      a dominant-bin fraction near the singlebin regime
```

The dominant-bin threshold is unknown. The current data only brackets it
between hotset4-like concentration and singlebin. Computing a fresh histogram
or reading it back merely to select a backend could cost more than the
observed gain, so the selector should consume producer-known metadata,
GPU-resident prior-frame state, or another signal whose cost is included in
the A/B.

The likely mechanism is that extreme same-address count/scatter atomics make
Direct expensive, while Radix pays a comparatively fixed one-pass sort cost.
The uniform `C = 65,536` result also shows how four Radix passes can dominate
when Direct atomics are dispersed. This is an algorithmic interpretation of
the observed shape, not an RGP-verified atomic, cache, or bandwidth diagnosis.

## Required broader-policy and production follow-up

The later formal report validates only an offline exact-cell classification
replay. Before claiming a broader threshold-based or production adaptive
policy:

1. Run a calibration sweep at fixed `N` and `C` with controlled dominant-bin
   fractions, for example 25%, 50%, 75%, 90%, 95%, 99%, and 100%.
2. Include unseen holdout cells across at least `N = 262,144` and
   `N = 1,048,576`, plus a second small-bin domain such as `C = 256`.
3. Use formal mode, 900 measured frames per block, source-bound EditMode
   evidence, a clean final tree, and retained artifact manifests.
4. Measure `RecordAdaptive`, selector overhead, and the logical-memory
   guardrail as part of the selected path.
5. Require the broader selector to beat or match the forced-best backend on
   holdout; use hysteresis if a temporal signal can cross the threshold frame
   to frame.
6. Repeat on NVIDIA before making any cross-vendor or autotuning claim.
7. Use RGP/RGA to test the atomic-contention hypothesis; do not infer hardware
   counters from elapsed time alone.

## Safe resume wording

> Implemented and correctness-validated forced Direct
> count-scan-atomic-scatter and low-bit Radix GPU-resident spatial-binning
> backends with canonical CSR hashing, native DX12 timestamp queries, and a
> position-balanced zero-workload-readback A/B harness. On an AMD Radeon AI
> PRO R9700 discovery matrix, Radix reduced paired median GPU average time by
> 29.72% to 47.54% in two extreme single-bin contention cases, while Direct
> won all pairs for uniform and four-hot-bin distributions. Used the measured
> crossover and 2.40x to 2.71x logical-memory tradeoff to define a conservative
> candidate for holdout testing rather than claim a universal winner.

When space is limited:

> Built and profiled GPU-resident Direct and low-bit Radix spatial-binning
> backends on AMD/DX12; correctness hashes and balanced A/B runs exposed a
> distribution-dependent crossover, motivating a guarded adaptive selector
> with explicit latency, P99, and logical-memory gates.

Do not claim:

- a production adaptive selector already chooses correctly;
- a universal 29.72% to 47.54% optimization;
- stable P99 for the Radix-winning cells;
- FPS, frame-time, city-scene, sensor-pipeline, or visually observable gains;
- measured DRAM bandwidth, cache hit rate, atomic count, occupancy, or
  driver-reported residency;
- an RGP/RGA-proven bottleneck;
- driver-level optimization;
- async-compute overlap;
- NVIDIA or cross-vendor validation;
- statistical significance.
