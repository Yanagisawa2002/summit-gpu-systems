# GPU Systems D3D12 Timestamps

`com.summit.gpu-timestamps` provides queue-local GPU interval timing for Unity
work submitted to the Direct3D 12 direct graphics queue. It is intended to
replace unavailable or excessively coarse whole-frame timing in isolated GPU
algorithm benchmarks.

This is an experimental instrumentation package. It does not make a
performance claim by itself.

The validated implementation uses ABI v2 and requires capability mask `31`
(`0x1F`). Its fifth capability is a plugin-owned D3D12 completion fence,
signaled on the same direct graphics queue after query resolution. This fence,
not a Unity frame fence, is the readiness proof for mapped timestamp data.

## Exact scope

The initial supported path is deliberately narrow:

- Windows x64 Player;
- Direct3D 12;
- Unity render-thread plugin events recorded on the direct graphics queue;
- begin/end timestamp pairs from one queue and one timestamp frequency;
- a same-queue private completion fence after query resolution;
- nonblocking result polling through a preallocated, generation-safe slot ring.

Unsupported platforms, graphics APIs, missing plugin binaries, incompatible ABI
versions, or unavailable timestamp queues must return an explicit unsupported
status. They must not return `0 ms`, silently fall back to CPU wall time, or
pretend that whole-frame timing is a direct interval measurement.

Compute work is measured only when it is submitted through the instrumented
direct graphics command stream. This package does not initially measure Unity
async-compute or copy queues.

## What a result means

A valid result is the elapsed timestamp interval between two ordered events on
the same D3D12 direct command queue:

```text
durationMilliseconds =
    (endTick - beginTick) * 1000.0 / timestampFrequency
```

The queue frequency is in ticks per second. Conversion uses floating-point
arithmetic. The result is queue elapsed time for the bracketed region.

The begin event, workload, end/resolve event, and completion event are recorded
in one caller-owned command buffer. The completion callback signals the
plugin-owned fence from Unity's direct graphics queue after preceding commands
have been flushed in order.

It is not:

- shader ISA instruction timing;
- wave occupancy or VGPR pressure;
- cache hit rate, bandwidth, or atomic count;
- CPU submission duration;
- end-to-end frame latency;
- proof that one shader caused every stall inside the interval.

Use RGP, RGA, PIX, or another vendor/API profiler for those questions.

## Token and slot contract

Each measurement scope owns an immutable token containing enough identity to
detect stale slot reuse. A token is valid for one begin/end pair and one result.
The expected lifecycle is:

```text
reserved -> command sequence recorded -> submitted -> resolve recorded
         -> direct-queue completion fence signaled -> pending -> ready/consumed
         \-> cancelled (reserved only; never after submission)
```

The native plugin owns a fixed 1,024-slot ring. A successful ready consume
recycles its slot; cancellation recycles only a reserved scope. Call
`MarkSubmitted` immediately before queueing the pre-recorded command buffers.
Once submitted, cancellation is rejected and disposal never recycles the slot
or native payload before completion, including callbacks already queued in
Unity. Measurement recording and polling must not resize the ring, block for
GPU completion, or reuse a slot whose generation is still live.

Each slot has a cache-line-sized readback region poisoned with
generation-specific sentinels before submission. A completed private fence plus
replaced sentinels is required before ticks are accepted. A failed check
quarantines the slot. Ring exhaustion is an explicit status/drop; it is not
permission to overwrite in-flight work.

Callers must retain source-frame, ready-frame, token, raw begin/end ticks,
frequency, duration, and status in raw evidence.

## Fixed instrumentation overhead

An empty begin/end bracket has real cost: plugin callbacks, timestamp writes,
query resolution, and queue ordering. Formal runs must interleave empty-control
scopes with workload scopes and report the control P50/P95/P99.

Raw workload intervals are always preserved. Fixed overhead is never silently
subtracted. If an analysis also reports an adjusted estimate, it must:

- label it as adjusted rather than measured;
- identify the control statistic and pairing rule;
- show raw and adjusted values together;
- preserve negative estimates instead of silently clamping them to zero;
- avoid claims when the workload interval is near the overhead/noise floor.

## Validation

Managed tests cover:

- native ABI version and struct layout;
- enum values, field offsets, packing, and calling convention;
- token equality, generation, and stale-token rejection;
- exact tick-to-time conversion;
- unsupported-platform and missing-plugin behavior;
- ring exhaustion and nonblocking pending/ready transitions through a fake
  backend where the wrapper exposes the test seam.

Native integration gates cover queue frequency, ordered begin/end ticks,
generation-safe slot reuse, no measurement-time allocation, and empty-control
overhead. A skipped native test is not a passed Windows/DX12 native gate.

See the host repository documents:

- `Docs/GPU_NATIVE_DX12_TIMESTAMPS_DESIGN.md`
- `Docs/GPU_NATIVE_DX12_TIMESTAMPS_VALIDATION_PLAN.md`

## Validated AMD instrumentation smoke

The ABI-v2 path completed a local one-round instrumentation smoke on:

- AMD Radeon AI PRO R9700, driver `32.0.31035.1003`;
- Unity `6000.5.2f1`, Direct3D 12;
- source commit `5f4bc3bc29dce05df926b7bb3b49642d524a3562`;
- native DLL SHA-256
  `5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA`.

All `300/300` native intervals were ready, all `8/8` workload validation rows
passed, and algorithm measurement readback remained `0` bytes. The associated
ABI/fail-closed EditMode suite passed `48/48` with zero skips.

This smoke validates instrumentation ordering and lifecycle only. It has one
round, does not satisfy the frozen three-round A/B gate, and provides no formal
kernel-speedup claim. Earlier short runs made before the private completion
fence were introduced are excluded from evidence.

## Claim boundary

The validated statement is:

> Implemented and smoke-validated an ABI-v2 Windows/DX12 timestamp bridge for
> Unity direct-graphics-queue command regions, using a plugin-owned same-queue
> completion fence, generation-safe readback, and explicit unsupported fallback.

Do not claim measured kernel speedups, cross-queue timing, NVIDIA validation,
async-compute timing, or driver-level optimization from this smoke.

## Primary references

- Microsoft, [Direct3D 12 timing](https://learn.microsoft.com/windows/win32/direct3d12/timing)
- Microsoft, [`ResolveQueryData`](https://learn.microsoft.com/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-resolvequerydata)
- Microsoft, [`ID3D12CommandQueue::Signal`](https://learn.microsoft.com/windows/win32/api/d3d12/nf-d3d12-id3d12commandqueue-signal)
- Unity, [`CommandBuffer.IssuePluginEventAndData`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.CommandBuffer.IssuePluginEventAndData.html)
- Unity, [low-level native rendering extensions](https://docs.unity3d.com/6000.0/Documentation/Manual/low-level-native-plugin-rendering-extensions.html)
