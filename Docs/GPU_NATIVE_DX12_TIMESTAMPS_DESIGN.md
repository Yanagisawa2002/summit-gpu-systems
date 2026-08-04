# Native DX12 GPU Interval Timestamps: Design

Date: 2026-07-30
Status: ABI-v2 implementation smoke-validated; no formal performance claim

## Problem

Unity whole-frame timing was unavailable in the first one-PID GPU-primitives
smoke run, even though the workload correctness gates passed. CPU enqueue time
does not measure GPU execution, and profiler markers do not themselves provide
an automation-friendly elapsed interval.

The instrumentation goal is therefore narrow and falsifiable:

> Measure the raw GPU timestamp interval around a known command-buffer region
> on Unity's D3D12 direct graphics queue without blocking or allocating during
> the measured window.

## Non-goals

This package does not initially provide:

- asynchronous compute-queue timing;
- copy-queue timing;
- cross-queue interval comparison;
- CPU/GPU clock calibration;
- shader ISA, occupancy, cache, bandwidth, or atomic metrics;
- production telemetry for every frame;
- a portable Vulkan, Metal, console, or Linux implementation;
- an automatic claim that an observed interval equals whole-frame improvement.

Those are separate projects or profiler captures.

## Queue and timestamp semantics

D3D12 timestamp frequency is queue-specific. Direct and compute queues use
`D3D12_QUERY_HEAP_TYPE_TIMESTAMP`; copy queues have a separate optional heap
type. This implementation supports only the direct graphics queue and must
obtain the frequency from that same queue.

Every valid interval uses:

- `D3D12_QUERY_TYPE_TIMESTAMP` for both endpoints;
- two initialized query indices from one query heap;
- `EndQuery` in command-stream order for the begin and end timestamps;
- `ResolveQueryData` after both endpoints;
- a readback destination at an 8-byte-aligned offset;
- a positive frequency from the direct queue.

Timestamp results are unsigned 64-bit ticks. Conversion is:

```text
ticks = endTick - beginTick
nanoseconds = ticks * 1,000,000,000.0 / frequency
milliseconds = ticks * 1,000.0 / frequency
```

The wrapper must retain raw ticks and frequency, not only a rounded duration.

## Unity integration

The managed layer records native callbacks into a caller-owned
`CommandBuffer`. A typical measured command stream is:

```text
native begin timestamp event
GPU workload commands
native end + resolve event
native completion-fence event
```

Unity invokes the event callback on its render thread. The native plugin must
use Unity's current D3D12 command-recording state rather than submit an
unrelated private queue, because timestamps from a different queue would not
bracket the workload.

The three measurement events and workload are pre-recorded in one caller-owned
command buffer. The completion event flushes preceding commands and obtains
Unity's direct graphics queue. Its callback calls
`ID3D12CommandQueue::Signal` on a plugin-owned `ID3D12Fence`. A result becomes
readable only after
`GetCompletedValue() >= fenceValue`.

The private fence therefore proves that the same queue executed the preceding
end timestamp and `ResolveQueryData`. Unity's next-frame fence is not used as a
proxy for query readiness. This design creates no private command queue and
does not instrument Unity's async-compute or copy queues.

The callback payload is an ABI-stable unmanaged descriptor. No callback may
retain a pointer to movable managed memory. Per-event data lives in pinned,
unmanaged, or native-owned preallocated storage until Unity has consumed it.

## Managed/native ABI

The native and managed sides share:

- ABI version;
- structure size;
- explicit enum numeric values;
- sequential field order and fixed packing;
- fixed-width integer and pointer-width fields;
- token slot and generation;
- event kind;
- native status/error code.

The native side rejects unknown ABI versions or structure sizes before reading
later fields. Managed tests assert `Marshal.SizeOf` and every field offset.
Boolean fields cross the ABI as fixed-width integers rather than C++ `bool` or
marshaled `bool`.

The reviewed native ABI uses `SummitGpuTimestamps.dll`, C exports, `__stdcall`,
and eight-byte structure packing. ABI v2 requires capability mask `31`
(`0x1F`):

- bit 0: direct queue;
- bit 1: nonblocking poll;
- bit 2: raw ticks;
- bit 3: stable payloads;
- bit 4: private completion fence.

Required exports:

- `SGT_GetAbiVersion`
- `SGT_GetRenderEventAndDataFunc`
- `SGT_GetEventIds`
- `SGT_GetSupportInfo`
- `SGT_AcquireSample`
- `SGT_MarkSubmitted`
- `SGT_CancelSample`
- `SGT_TryConsumeResult`

Reviewed shared layouts:

| Structure | Size | Required fields/offsets |
| --- | ---: | --- |
| Event IDs | 16 B | `begin` 0, `end` 4, `frequency` 8, `completion` 12 |
| Support info | 40 B | `structSize` 0, `abiVersion` 4, `supported` 8, `capabilityFlags` 12, `ringCapacity` 16, `rendererType` 20, `deviceGeneration` 24, `frequencyReady` 28, `timestampFrequency` 32 |
| Result | 80 B | `structSize` 0, `flags` 4, `token` 8, `userTag` 16, `beginTicks` 24, `endTicks` 32, `elapsedTicks` 40, `timestampFrequency` 48, `elapsedMilliseconds` 56, `fenceValue` 64, `deviceGeneration` 72, `reserved` 76 |

Native status values are ABI:

| Value | Meaning |
| ---: | --- |
| 1 | success / ready |
| 0 | pending |
| -1 | generic error |
| -2 | invalid argument |
| -3 | invalid or stale token |
| -4 | ring full |
| -5 | not initialized |
| -6 | unsupported |
| -7 | device lost |
| -8 | render callback error |
| -9 | timestamp frequency unavailable |

Result flag bit 0 identifies an empty instrumentation scope. Tests assert these
values rather than merely checking enum names.

Export names and calling conventions are explicit. Missing DLLs, missing entry
points, or bitness mismatches are translated into a diagnostic unsupported
result by `TryCreate`; they are not allowed to escape as startup exceptions.

## Token lifecycle and slot ring

The native session owns a fixed ring of 1,024 logical slots. It preallocates an
even number of query indices and a corresponding readback ring. Each logical
slot owns two query indices.

Token identity must contain at least:

- slot index;
- generation;
- session identity or an equivalent guard if tokens can cross sessions.

State transitions:

```text
free
  -> reserved
  -> descriptors recorded without queue ownership transfer
  -> submitted immediately before Unity queue submission
  -> begin recorded
  -> end and resolve recorded
  -> private completion fence signaled / pending
  -> ready and consumed or terminal failure
  -> free with incremented generation
```

Required behavior:

- stale generations never observe a newer result;
- unknown or cross-session tokens are rejected;
- duplicate submission, poll-before-submission, and poll-after-consume are explicit misuse;
- ring exhaustion does not overwrite in-flight scopes;
- pending polling is nonblocking;
- successful ready consumption atomically recycles the slot;
- only reserved samples may call `SGT_CancelSample`;
- `MarkSubmitted` happens immediately before queueing the pre-recorded command buffers;
- submitted slots and payloads remain unreusable until native completion;
- disposal cancels reserved samples only and fails closed for submitted work rather
  than allowing a queued callback to observe recycled payload storage.

## Completion and readback safety

The plugin assigns each submitted scope a monotonic, nonzero private-fence
value. Polling stays nonblocking: a completed value below the scope value
returns pending.

Each logical slot owns a 64-byte readback stride, although the resolved query
payload is 16 bytes. Before submission, its tick fields receive
generation-specific sentinels. After the private fence completes, both fields
must differ from their sentinels before the interval is accepted. A missing
completion callback, signal failure, device removal, or unchanged sentinel
produces a terminal failure and quarantines the slot.

The result's `fenceValue` is the plugin-owned completion-fence value, not a
Unity frame-fence value.

## Result model

Each result or raw benchmark row retains:

- token identity;
- source frame and order position;
- ready/result frame;
- native status;
- begin and end ticks;
- queue frequency;
- raw tick delta;
- raw duration in nanoseconds or higher precision;
- interval validity;
- number of frames spent pending;
- instrumentation implementation/version.

An unsupported, pending, stale, dropped, or failed result is not encoded as a
valid zero-duration interval.

## Instrumentation overhead

The instrumentation changes the command stream. Formal experiments include an
empty bracket that uses the same begin/end/resolve mechanism but contains no
workload dispatch.

Required reporting:

- empty-control P50/P95/P99 and sample count;
- raw workload P50/P95/P99;
- workload-minus-control only as a separately labeled derived estimate;
- percentage of workload samples below or near control P99;
- ring occupancy, result latency, drops, and invalid samples.

The raw interval is authoritative. No implementation path silently subtracts a
constant, clamps negative adjusted values, or discards high-overhead samples.

## Threading and ownership

- Session creation/destruction follows Unity graphics-device lifecycle events.
- Render callbacks record queries or signal the private completion fence; they
  do not call managed code.
- Result polling reads mapped query bytes only after the private fence reaches
  that scope's value.
- Measurement methods do not wait on a fence.
- Query heap, readback resource, event payload storage, and token state are
  preallocated.
- Device reset, graphics API change, or plugin unload moves the session to a
  terminal unavailable state.

## Required failure handling

| Condition | Required outcome |
| --- | --- |
| Non-Windows or non-x64 | Explicit unsupported result |
| Graphics API is not D3D12 | Explicit unsupported result |
| Plugin DLL missing | `TryCreate=false` plus diagnostic |
| Entry point or ABI mismatch | `TryCreate=false` plus diagnostic |
| Direct queue frequency unavailable | Session unavailable |
| Ring full | Explicit capacity/drop status |
| Invalid/stale token | Explicit invalid-token status or documented misuse exception |
| Result not complete | Nonblocking pending status |
| Completion callback/fence/sentinel failure | Terminal callback/device error; slot quarantined |
| End tick precedes begin tick unexpectedly | Invalid result retained for diagnosis |
| Device removal/reset | Terminal device-lost/unavailable status |

## Claim boundaries

Evidence from this package may support:

- direct-queue interval timing was implemented;
- query lifecycle and ABI gates passed;
- a named workload's raw interval distribution changed on a named AMD/DX12
  environment.

It does not by itself support:

- a frame-rate improvement;
- an ISA or occupancy explanation;
- async-compute timing;
- driver modification or driver-level optimization;
- NVIDIA performance;
- cross-vendor portability.

## Validated instrumentation evidence

The ABI-v2 path completed a one-round smoke at source commit
`5f4bc3bc29dce05df926b7bb3b49642d524a3562` on AMD Radeon AI PRO R9700,
driver `32.0.31035.1003`, with Unity `6000.5.2f1` and Direct3D 12. The native
DLL SHA-256 was
`5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA`.

The Player produced `300/300` ready native intervals, `8/8` passing workload
validation rows, and zero algorithm measurement readback. The ABI/fail-closed
EditMode suite passed `48/48` with zero skips.

This establishes an instrumentation smoke gate on that AMD direct-graphics
queue only. One round is not formal A/B performance evidence. Earlier short
runs that predated the private completion fence are excluded because their
completion condition did not causally prove query resolution had finished.

## Primary references

- Microsoft, [Direct3D 12 timing](https://learn.microsoft.com/windows/win32/direct3d12/timing)
- Microsoft, [`ID3D12CommandQueue::GetTimestampFrequency`](https://learn.microsoft.com/windows/win32/api/d3d12/nf-d3d12-id3d12commandqueue-gettimestampfrequency)
- Microsoft, [`ID3D12GraphicsCommandList::ResolveQueryData`](https://learn.microsoft.com/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-resolvequerydata)
- Microsoft, [`ID3D12CommandQueue::Signal`](https://learn.microsoft.com/windows/win32/api/d3d12/nf-d3d12-id3d12commandqueue-signal)
- Unity, [`CommandBuffer.IssuePluginEventAndData`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.CommandBuffer.IssuePluginEventAndData.html)
- Unity, [low-level native rendering extensions](https://docs.unity3d.com/6000.0/Documentation/Manual/low-level-native-plugin-rendering-extensions.html)
