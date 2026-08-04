# Native DX12 Timestamp ABI-v2 Smoke Evidence

Date: 2026-07-30
Status: instrumentation smoke accepted; formal A/B not run

## Result

The ABI-v2 timestamp bridge passed its local instrumentation smoke on the AMD
direct-graphics-queue path. All `300/300` native intervals were ready, all
`8/8` workload validation rows passed, and algorithm measurement readback was
`0` bytes.

This is evidence that the timestamp lifecycle and same-queue private completion
fence worked for this smoke. It is not a kernel-performance result.

## Environment and provenance

| Field | Value |
| --- | --- |
| GPU | AMD Radeon AI PRO R9700 |
| Driver | 32.0.31035.1003 |
| Unity | 6000.5.2f1 |
| Graphics API | Direct3D 12 |
| Source commit | `5f4bc3bc29dce05df926b7bb3b49642d524a3562` |
| Branch | `codex/gpu-native-dx12-timestamps` |
| Native ABI | 2 |
| Capability flags | 31 (`0x1F`) |
| Native DLL SHA-256 | `5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA` |
| Player executable SHA-256 | `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE` |

The required capability mask is direct queue, nonblocking polling, raw ticks,
stable payloads, and a plugin-owned private completion fence.

## Smoke matrix

- one Player process;
- one counterbalanced round;
- 65,536 elements;
- deterministic seed `20260730`;
- five local warmup frames;
- 60 measured frames per case;
- empty control;
- exclusive scan: portable and wave-ops;
- 32-bit radix sort: portable and wave-ops.

Five cases times 60 frames produced 300 measured native intervals. The smoke
used one pre-recorded command buffer per prepared scope/case, ordered as:

```text
begin timestamp
workload
end timestamp + ResolveQueryData
completion callback -> Signal(plugin-owned fence)
```

The fence is signaled on Unity's direct graphics queue. Nonblocking result
polling accepts mapped query data only after the private fence reaches the
scope's value and the generation-specific readback sentinels have been
replaced.

## Gate outcome

| Gate | Outcome |
| --- | ---: |
| ABI/fail-closed EditMode tests | 48/48 passed, 0 skipped |
| Native intervals | 300/300 ready |
| Workload validation | 8/8 passed |
| Native acquire failures | 0 |
| Native result failures | 0 |
| Native timeouts | 0 |
| Pending rows at completion | 0 |
| Terminal native state | none |
| Algorithm measurement readback | 0 bytes |
| Timestamp instrumentation readback | 16 bytes per ready sample |

The direct-queue timestamp frequency was stable at 100,000,000 ticks per
second across all 300 rows.

## Claim boundary

This run had one round. It does not satisfy the frozen minimum of three complete
counterbalanced rounds, so no scan/radix speedup, P50/P99 improvement, or
resume-level performance claim is accepted from it. It also says nothing about
Unity async-compute/copy queues, NVIDIA hardware, whole-frame FPS, shader ISA,
occupancy, cache behavior, bandwidth, or driver optimization.

Earlier short runs under `Reports/GpuTimestamps/schema4-smoke`,
`schema4-smoke-r2`, and `schema4-smoke-r3` are excluded. They predated ABI v2's
private completion fence or failed the readiness gate; their timing values must
not be reused.

## Local source artifacts

- `Reports/GpuTimestamps/editmode-abi2/results.xml`
- `Reports/GpuTimestamps/schema4-smoke-r4/run-summary.txt`
- `Reports/GpuTimestamps/schema4-smoke-r4/quality-summary.txt`
- `Reports/GpuTimestamps/schema4-smoke-r4/raw-frames.csv`
- `Reports/GpuTimestamps/schema4-smoke-r4/validation.csv`
- `Reports/GpuTimestamps/schema4-smoke-r4/runner-config.json`

Reports remain local benchmark evidence and are not modified by this document.
