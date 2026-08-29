# Engine-native GPU-driven instance macrobenchmark

This benchmark compares a strong CPU/Burst reference renderer with the
package's GPU-visible-only pipeline in an asset-free Unity Player. It is a
portable macrobenchmark: it uses generated instance state, generated cameras,
Unity engine rendering APIs, and no SUMMIT assets, formats, or scene logic.

## Compared systems

The two variants consume the same deterministic instances, view frusta, draw
groups, mesh, shader, render target, and visibility schedule.

- `cpu-burst-engine-native` runs a Burst/Jobs frustum-and-group pass, performs
  deterministic per-chunk histogram/prefix/scatter, and records
  `CommandBuffer.DrawMeshInstanced` batches of at most 1,023 matrices.
- `gpu-visible-only-engine-indirect` records the package's visible-only GPU
  cull/group pipeline and `CommandBuffer.DrawMeshInstancedIndirect` draws.

Both paths render one viewport per view into the same offscreen target. A
normal visible, windowed Development Player supplies the engine presentation
loop needed for valid frame-timing evidence.

## Measurement contract

Each scenario runs in one process with:

```text
control-pre; ABBA; BAAB; control-post
```

Every measurement block has case-local warm-up. The runner rejects mixed
process IDs, reordered blocks, missing samples, non-D3D12 devices, incomplete
native timestamps, incomplete CPU frame tails, timed managed allocations,
failed completion fences, dirty source state, changed source hashes, or a
changed Player payload.

The primary metrics are:

- `totalCpuSubmissionMs`: `Stopwatch` time for CPU cull/pack, Unity command
  recording, and `Graphics.ExecuteCommandBuffer` enqueue. It is CPU-side API
  time, not GPU completion time. The separate internal render-thread/driver
  submit duration is not exposed by Unity and is reported as `unavailable`,
  not zero.
- `cpuFrameMs` and `cpuMainThreadFrameMs`: Unity `FrameTimingManager` values,
  aligned to the source row using Unity's fixed four-frame result latency.
  Both must be positive for every formal row.
- `nativeGpuRegionMs`: native D3D12 begin/end timestamp queries around the
  exact submitted work command buffer. Every formal row must reach `ready`.

`cpuRenderThreadFrameMs`, full-frame `gpuFrameMs`, and the engine
`firstSubmitTimestamp`-to-`cpuTimePresentCalled` window are optional. Unity can
publish zero or omit these values on light frames. The benchmark records a
validity bit and sample count for each optional metric and writes unavailable
values as `NaN`/`unavailable`, never as zero. The formal GPU P99 guardrail uses
the complete native GPU region rather than a partial full-frame sample set.

Timed frames perform no CPU/GPU validation readback. Correctness readbacks run
only before and after measurement and require identical CPU/GPU result hashes
and image hashes. A transient `AsyncGPUReadback` error may be retried at most
twice outside the measured region; the final row records its attempt count and
failed-request history, and repeated failure still fails closed. The run also
reports:

- render API calls and logical draw commands;
- explicit buffer upload bytes;
- engine instance-matrix payload bytes for the CPU renderer;
- estimated persistent CPU and GPU payload bytes;
- measurement, validation, and timestamp-instrumentation readback bytes;
- per-row managed allocation bytes and per-block completion fences.

## Frozen matrices

Smoke:

```text
10,000 instances, 1 view, 1 draw group, 25% seeded visibility
```

Formal primary matrix:

| Instances | Views | Draw groups | Visibility |
|---:|---:|---:|---:|
| 10,000 | 1 | 1 | 25% |
| 100,000 | 1 | 1 | 25% |
| 10,000 | 4 | 8 | 25% |
| 100,000 | 4 | 8 | 25% |

The 4-view/8-group cells exercise nonzero bin offsets, indirect argument
offsets, and start-instance values. The formal schedule uses 60 warm-up, 900
sample, and 15 cooldown frames per block.

The frozen decision requires at least three of four cells to improve CPU
submission P95 by both `20%` and `0.20 ms`. Every cell must keep native GPU
region P99 regression at or below `5%`, preserve correctness, and pass all
evidence gates.

## Commands

Run focused EditMode tests first:

```powershell
& .\Tools\Run-UnityEditModeTests.ps1 `
  -UseGraphics -ForceDirect3D12 `
  -ResultsPath .\TestResults\gpu-driven-macro-editmode.xml
```

Run the smoke protocol from a clean worktree:

```powershell
& .\Tools\Run-GpuDrivenInstanceMacrobenchmark.ps1 `
  -MatrixPreset smoke-10k-v1-g1 `
  -SuperRounds 2 -WarmupFrames 30 -SampleFrames 60 -CooldownFrames 5
```

Run the frozen formal protocol:

```powershell
& .\Tools\Run-GpuDrivenInstanceMacrobenchmark.ps1 `
  -FormalAcceptanceMode `
  -MatrixPreset formal-primary-v1 `
  -SuperRounds 2 -WarmupFrames 60 -SampleFrames 900 -CooldownFrames 15 `
  -EditModeResultsPath .\TestResults\gpu-driven-macro-editmode.xml
```

The formal runner forbids `-SkipBuild` and rejects any contract drift. Outputs
include raw frames, block summaries, validation receipts, matrix summaries,
Player/source manifests, and a human-readable decision report.

## Evidence boundary

Passing this benchmark supports a procedural Unity engine-native claim on the
measured device and commit. It does not establish performance in SUMMIT, an
external Unity sample, another GPU, or a release Player. External
GraphicsSamples validation and new AMD measurements remain separate work.
