# Dynamic GPU-Resident Sensor Pipeline Benchmark Plan

## Goal and claim boundary

This S4 worktree evaluates a reusable dynamic sensor-data pipeline, not a
city-scene rendering feature. It compares two producers that feed the same
GPU-resident spatial index and the same payload-consuming range-query
consumer:

- **A - `cpu-produced-uploaded`**: the CPU deterministically generates a
  changing sample frame and its cell keys, then records two
  `CommandBuffer.SetBufferData` uploads.
- **B - `gpu-produced-resident`**: a compute kernel generates the bit-exact
  same changing sample frame and keys directly in the resident GPU buffers.

Both paths then execute the same trusted Direct CSR backend:

```text
producer -> count -> exclusive scan -> scatter -> range query -> frame digest
```

This experiment may support a claim about eliminating per-update host
generation and logical host-to-GPU upload from this synthetic pipeline. It
does **not** measure or claim:

- PCIe transfer duration or bandwidth;
- a hidden copy queue or independent upload queue;
- async-compute overlap or end-to-end sensor latency;
- a live LiDAR/camera driver, ROS ingestion, or sensor fidelity;
- city-scene GPU frame time, visible image quality, FPS, or P99 frame pacing;
- NVIDIA behavior or cross-vendor portability results.

The current native timestamp ABI brackets Unity's current D3D12 main graphics
command list. Therefore every GPU number is named a
**main-graphics-command-list interval**, not a copy-engine or end-to-end
measurement.

## Fixed data and algorithm contract

The spatial grid is fixed at `64 x 64 x 64`, so:

```text
C = 262144 cells
logicalStateCount = 64
```

Each sample is a 16-byte `uint4(x, y, z, payload)`. Each key and stable ID is
one `uint`. The CPU and HLSL generators use the same unchecked 32-bit mixing
function and whole-cell modular translations. Logical state `s` always
produces the same samples and keys on both producers.

Stable IDs are initialized once outside measurement. Every generated key is
mathematically constrained to `[0, C)`. The timed path uses the trusted
`RecordGuaranteedInRange` Direct CSR backend with explicit WaveOps; no
adaptive selector or runtime key scan is included.

The common consumer performs inclusive 3D range queries by traversing CSR
cell offsets and `binnedIds`, fetching the original position and payload, and
reducing an order-independent digest. The consumer therefore verifies real
payload access rather than only reading cell counts.

## Frozen AMD workloads

The discovery and formal matrix contains two cells:

| Scenario | Elements (`N`) | Cells (`C`) | Queries (`Q`) | States | Seed |
|---|---:|---:|---:|---:|---:|
| `dynamic-n262144-q64` | 262,144 | 262,144 | 64 | 64 | 20260731 |
| `dynamic-n1048576-q256` | 1,048,576 | 262,144 | 256 | 64 | 20260732 |

Discovery uses 240 measured frames per block. Formal confirmation uses 900
measured frames per block. No workload, query radius, seed, backend, or
validation rule may be changed between A and B within a run.

## Position-balanced execution

Each workload is one Player process with a control block before and after the
paired measurements. Four super-rounds produce eight adjacent A/B pairs:

```text
control/pre
super-round 1: A B | B A
super-round 2: B A | A B
super-round 3: A B | B A
super-round 4: B A | A B
control/post
```

Every block uses:

1. 15 cooldown frames;
2. 60 case-local warmup frames;
3. 240 discovery or 900 formal measured frames;
4. complete timestamp-result draining;
5. staging-slot and graphics-fence completion;
6. validation outside the native timestamp interval.

Logical state is block-local `sampleIndex % 64`; every measured block visits
all 64 states. A four-slot persistent CPU staging ring is reused only after
its graphics fence passes, so a managed array is never mutated while Unity or
the GPU may still consume it.

## Timing and byte metrics

The report keeps the following metrics separate:

- `gpuRegionElapsedMs`: native D3D12 timestamp interval on the main graphics
  command list;
- `cpuProducerMs`: CPU deterministic sample/key generation;
- `cpuPipelineRecordMs`: time spent recording `RecordCpuProduced` or
  `RecordGpuProduced`; the CPU case includes the two upload commands plus the
  shared pipeline commands and is **not** a `SetBufferData`-only metric;
- `cpuCommandRecordMs`: full command-buffer preparation, including timestamp
  commands and the lifetime fence;
- `submissionCpuMs`: host time spent in `Graphics.ExecuteCommandBuffer`;
- `logicalUploadBytes`: requested CPU upload payload for A;
- `gpuProducerLogicalWriteBytes`: logical device-buffer writes for B.

The logical producer payload is `20 * N` bytes per update:

| Scenario | A logical upload/update | B logical producer write/update |
|---|---:|---:|
| `N=262144` | 5,242,880 bytes | 5,242,880 bytes |
| `N=1048576` | 20,971,520 bytes | 20,971,520 bytes |

These are API/data-contract bytes, not observed PCIe or DRAM traffic.

The same-process benchmark allocates the CPU staging ring so that A and B can
alternate safely. It reports:

- `actualBenchmarkHostStagingBytes`: the ring actually allocated by the
  harness;
- `cpuCaseRequiredHostStagingBytes`: the isolated A-path requirement;
- `gpuCaseRequiredHostStagingBytes = 0`: the isolated B-path requirement.

The last value is a logical isolated-path requirement, not a measured
process-memory reduction. GPU buffer payload bytes and scratch sizes are also
logical `GraphicsBuffer` accounting, not driver-reported VRAM residency.

## Correctness and readback contract

Correctness is checked before measurement, after every adjacent A/B pair, and
after measurement:

- the last resident key buffer in each captured block is scanned outside the
  timing interval; invalid count and hash must both be zero;
- each block snapshots the 64-entry per-state frame-digest ring outside the
  timing interval;
- the GPU compares all 64 CPU/GPU state digests and reads back only one
  16-byte comparison digest;
- every comparison must report `mismatchCount == 0`.

The phrase "64/64 states validated" refers to digest comparison. Key
validation is explicitly a final-resident-state scan plus the generator's
range proof; it is not misreported as 64 separate timed key scans.

Measured workload readback is exactly zero bytes per frame. Validation
readback is 32 bytes per comparison and occurs outside measurement. Native
timestamp instrumentation reads 16 bytes per completed sample and is reported
separately from workload readback.

## Evidence and provenance gates

Evidence validity is independent from performance direction. Negative or
neutral results remain valid and must be retained.

A frozen discovery or formal report is valid only when:

- the worktree starts from a named, clean commit;
- Unity is `6000.5.2f1`, D3D12 device index 0 is selected, and device metadata
  identifies the tested AMD adapter;
- source, tool, package, timestamp DLL, Player payload, commit, and report
  hashes remain stable through the run;
- four complete super-rounds and eight adjacent pairs are present;
- all final-state key checks and all 64-entry digest comparisons pass;
- measurement workload readback is zero;
- native timestamp ABI 2 and its required capability flags pass, every
  measured sample has a valid result, and control intervals pass the frozen
  sanity gate;
- configuration records
  `uploadQueueCoverageVerified=false`,
  `asyncComputeClaim=false`, and `copyQueueClaim=false`.

Formal acceptance additionally runs the exact sensor-package and benchmark
NUnit fixtures in-process, rejects failed, skipped, or inconclusive cases, and
binds that fresh EditMode result to the same clean source hashes and commit.

The summarizer reports per-pair and median A-to-B changes for GPU average,
GPU P99, CPU producer time, pipeline-record time, command-record time, and
submission time. A strong GPU-performance claim additionally requires a
position-balanced improvement that clears the frozen summarizer gates.
Failure to clear a speed gate does not invalidate a proven upload-elimination
or correctness result.

## Commands

Full Unity/DX12 correctness gate:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe' `
  -batchmode `
  -force-d3d12 `
  -force-device-index 0 `
  -projectPath 'C:\Users\EdwinLiu\Downloads\SUMMIT-gpu-dynamic-sensor' `
  -runTests `
  -testPlatform EditMode `
  -testResults 'C:\tmp\s4-editmode-results.xml' `
  -logFile 'C:\tmp\s4-editmode.log'
```

The frozen discovery preset runs both cells from the clean implementation
commit and writes all evidence outside the worktree:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\Run-GpuSensorPipelineBenchmark.ps1 `
  -OutputDirectory 'C:\tmp\summit-s4-discovery-matrix' `
  -MatrixPreset 'amd-r9700-dynamic-discovery-v1' `
  -DeviceIndex 0 `
  -SuperRounds 4 `
  -WarmupFrames 60 `
  -SampleFrames 240 `
  -CooldownFrames 15 `
  -StagingSlotCount 4
```

If discovery is correct and directionally useful, the frozen formal matrix
uses `MatrixPreset amd-r9700-dynamic-v1`,
`FormalAcceptanceMode`, and `SampleFrames 900`. The formal runner must produce
its own clean-commit EditMode evidence and must reject dirty source, incomplete
timestamps, unstable payloads, or missing provenance.
