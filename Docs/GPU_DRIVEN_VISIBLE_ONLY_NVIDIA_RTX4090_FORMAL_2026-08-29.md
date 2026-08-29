# GPU-Driven Visible-Only Scatter — RTX 4090 Formal Result

## Outcome

Commit `36d92e6dee55033ec0ea13caa00edf4db7468736` adds and measures a
general discard-key path for GPU count/scan/scatter. The optimized
`VisibleOnly` mode does not reserve a shared culled bin or scatter rejected
instance/view pairs. The original `CulledTail` mode remains available and is
still the API default.

On an NVIDIA GeForce RTX 4090, with `1,048,576` instances, four views, and
eight draw groups, the retained same-process D3D12 matrix measured these GPU
region results:

| Visible instances | Culled-tail mean | Visible-only mean | Mean reduction | Paired-median reduction | Pair range | Decision |
|---:|---:|---:|---:|---:|---:|:---|
| 5% | 4.2259 ms | 0.2903 ms | 93.13% | 93.13% | 93.12% to 93.14% | material improvement |
| 25% | 4.3669 ms | 1.0419 ms | 76.14% | 76.14% | 76.12% to 76.16% | material improvement |
| 75% | 3.5242 ms | 1.9438 ms | 44.84% | 44.86% | 44.77% to 44.89% | material improvement |
| 100% | 2.4162 ms | 2.4079 ms | 0.34% | 0.05% | -0.08% to 1.34% | parity |

The accepted interpretation is therefore narrow: visible-only scatter removed
a large low-visibility contention/write bottleneck and remained effectively
neutral when nothing could be rejected. The sub-1% all-visible paired result
is noise, not a performance claim.

## General mechanism

The classification shader emits one key/value pair for each instance/view
pair. Under the baseline contract, rejected pairs use a valid extra bin. A low
visibility workload therefore sends most atomic increments and scatter writes
to one culled bin. The new primitive kernels take an explicit discard key:

- a key equal to the discard key contributes no count and no output write;
- other out-of-range keys still increment diagnostics and fail closed;
- visible keys retain the same CSR counts, offsets, membership, and indirect
  arguments;
- the consumer chooses `CulledTail` or `VisibleOnly` explicitly.

This mechanism is independent of traffic, buildings, terrain, weather,
orthophotos, BFP2, GIS, networking, scenes, meshes, and materials. It applies
to any GPU pipeline that can represent rejected work with a reserved key.

The allocated output capacity is deliberately unchanged in this PR. The
result proves reduced atomic/scatter work, not lower reserved VRAM.

## Frozen protocol

- Unity `6000.5.2f1`, Windows 11, Direct3D 12 feature level 12.2.
- NVIDIA GeForce RTX 4090, 24,138 MiB reported graphics memory, driver
  `32.0.15.9186`.
- Four visibility cells: `5%`, `25%`, `75%`, and `100%`.
- Visible membership uses the exact-count,
  `seeded-coprime-permutation-v1` layout. This disperses visible and rejected
  records across the input instead of placing either class in one contiguous
  block.
- Two super-rounds with `ABBA;BAAB` ordering, giving four matched A/B pairs.
- `60` case-local warm-up frames and `900` measured frames per block.
- `3,600` measured samples per variant per cell; `36,000/36,000` total native
  timestamp rows, including empty-command-buffer controls, were ready.
- Native D3D12 timestamp queries with a private completion fence; no timed
  benchmark-output readback.
- CPU-oracle validation before and after measurement for both variants.

A result is called a material improvement only when paired-median reduction is
at least `1%` and every matched pair is positive. This rule classifies the
all-visible cell as parity.

## Correctness and provenance

- Full D3D12 EditMode suite: `544/544` passed, zero skipped.
- Formal matrix: `4/4` scenarios completed.
- Oracle checks: `16/16` passed; invalid-key and diagnostic flags were zero.
- Source hashes stayed stable across the build.
- The 282-file, 155,830,815-byte Player payload stayed stable through the
  matrix.
- Runtime shader SHA-256:
  `C8F365BC2883FD85C6F9DEE94AD738DA7D2DFD061B785E935FCF2E41421B278D`.
- Direct-binning shader SHA-256:
  `E17C6071926F4459AB02FBCF46442CD585FF3AAC93FE427B1FBF8172138BBD2A`.
- Combined runtime API SHA-256:
  `E676DC51F5CEE6B720F904964F8AB987D3497D1B86BFA9B18E161E4660CD8844`.
- Player payload SHA-256:
  `9FEAF16D4670AAFEEA2454786E1BD2A0F0508D209FF8040DA500233A0EC2309B`.

Two complete matrices used the dispersed layout and identical runtime shader
and API hashes. Mean reduction was `93.13%` in both 5% runs, `76.14%` in both
25% runs, and `44.84%–44.90%` in the 75% runs. The 100% paired median stayed
below `0.08%` in both runs and included negative pairs, reinforcing the parity
decision. Earlier contiguous-layout discovery runs are not used for the
retained claim.

Compact retained evidence is under
`Evidence/GpuDrivenInstances/NVIDIA_RTX4090_2026-08-29`. Raw per-frame rows and
generated Player files remain ignored under `Reports/` and `Builds/`.

## Claim boundary

This is an asset-free systems microbenchmark of the full GPU classification,
binning, scan, scatter, and indirect-argument region. It is not an end-to-end
FPS result, a CPU submission result, a visual-render parity result, or an
external-engine macrobenchmark. AMD results for this new workload are
unavailable. The next PR should add the strong CPU/engine-native comparison
rather than widening this claim.

## Reproduction

```powershell
.\Tools\Run-UnityEditModeTests.ps1 `
  -UseGraphics -ForceDirect3D12 `
  -ResultsPath .\TestResults\gpu-driven-full-d3d12.xml

.\Tools\Run-GpuDrivenInstanceBenchmark.ps1 `
  -FormalAcceptanceMode `
  -MatrixPreset visibility-sweep-v1 `
  -InstanceCount 1048576 `
  -ViewCount 4 `
  -SuperRounds 2 `
  -WarmupFrames 60 `
  -SampleFrames 900 `
  -CooldownFrames 15 `
  -DispatchesPerFrame 1 `
  -EditModeResultsPath .\TestResults\gpu-driven-full-d3d12.xml
```

## Resume-safe wording

> Built an asset-independent Unity/D3D12 GPU-driven visibility and indirect
> draw pipeline, then eliminated rejected-pair atomic/scatter work with a
> reusable discard-key primitive; reduced the measured GPU region by
> `44.8%–93.1%` at `5%–75%` visibility for `1.05M` instances across four
> views on an RTX 4090, while an all-visible control remained at parity and
> `36,000/36,000` native timestamp samples passed fail-closed provenance and
> CPU-oracle gates.
