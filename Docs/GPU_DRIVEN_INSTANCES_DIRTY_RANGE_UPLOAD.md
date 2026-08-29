# GPU-driven instance dirty-range upload

This stage isolates dynamic instance-state transfer from visibility and draw
selection. It adds a project-neutral upload API and a focused engine-native
full-versus-dirty benchmark without changing the existing culling pipeline or
the retained CPU-versus-GPU macrobenchmark.

## Package contract

`GpuInstanceStateUploader` consumes an authoritative persistent
`NativeArray<GpuInstanceState>` and caller-provided half-open dirty ranges.
It provides two explicit paths:

- `RecordFull` records one command for all active records;
- `RecordDirty` sorts and unions ranges, then records only the normalized
  ranges.

The dirty path is intentionally not an automatic performance selector.
One-hundred-percent dirty state remains one explicit dirty-range command, so
the later policy PR can decide between full and dirty paths from measured
device/workload evidence. The only implicit full path is a correctness fallback
when the caller exceeds the uploader's predeclared range capacity; it is marked
as `RangeCapacityExceeded` and never drops an update.

Planning uses a Persistent `NativeArray` scratch prefix and Unity Collections
native sort. In steady state it performs no managed allocation. Inputs may be
unordered, overlapping, adjacent, duplicated, or empty. A fixed caller-selected
gap can bridge clean records when reducing command count is worth known
over-upload; the frozen benchmark uses zero bridging so requested bytes equal
the exact dirty union.

Every receipt separates:

- input ranges and exact dirty records;
- normalized upload commands and uploaded records;
- bridged clean records;
- logical bytes requested through Unity's upload API;
- full-upload reason and whether the dirty count is exact.

Logical upload bytes are not claimed to be PCIe, driver-staging, or physical
copy-engine traffic.

## Lifetime and safety

Full and dirty measured paths both call `CommandBuffer.SetBufferData` with a
Persistent NativeArray. The source slot stays alive and immutable until an
`AllGPUOperations` fence after upload, compute culling, indirect-argument copy,
and indirect draws has passed. Eight preallocated slots avoid modifying a
source still referenced by submitted work. A busy ring is reported as a slot
wait instead of silently reusing memory.

Immediate `UploadFull` and `UploadDirty` methods exist for initialization and
out-of-band validation only. All range bounds are checked before a command is
recorded. Zero active records and zero dirty ranges record zero commands and
zero bytes.

Version `0.2.0` requires Unity `6000.5` because the public/persistent source
contract uses Unity Collections `6.5.0`.

## Frozen dynamic input

The benchmark uses `seeded-striped-contiguous-16-v1`:

- instance counts: 10,000 and 100,000;
- moving fractions: 0%, 1%, 10%, and 100%;
- at most sixteen deterministic, address-dispersed contiguous ranges;
- 100% becomes one contiguous full-buffer range;
- each logical ordinal derives a small absolute position delta from the same
  immutable base, so updates do not accumulate across staging-slot reuse.

Only `PositionRadius.xyz` changes. Radius, application ID, draw group, LOD
contract, and view mask remain identical. Full and dirty variants use the same
ordinal, range-plan hash, visible-only pipeline, mesh, material, render target,
and engine indirect draws.

## Evidence protocol

Each scenario runs full and dirty variants in one Player using two
counterbalanced super-rounds. Case-local warm-up and block resets happen outside
timed rows. Timed output records state-update, command-record, enqueue, and total
CPU submission time; exact ranges/calls/bytes; update hash; staging-slot wait;
managed allocation; and Unity CPU total/main frame timing where available.

Validation happens after completion fences and outside measured rows. It reads
back the entire instance-state buffer and requires the same 48-byte-record state
hash as the prepared slot. The integration test separately executes both full
and dirty workloads at the same ordinal and requires exact final state-hash
parity.

Run focused tests:

```powershell
& .\Tools\Run-UnityEditModeTests.ps1 `
  -UseGraphics -ForceDirect3D12 `
  -TestFilter 'GpuInstanceStateUploaderTests|GpuDrivenInstanceUpload'
```

Run the focused matrix from a clean commit:

```powershell
& .\Tools\Run-GpuDrivenInstanceDirtyRangeBenchmark.ps1
```

The focused benchmark can support a CPU submission and logical-upload claim for
the tested Unity/D3D12 device. It does not measure physical bus traffic, replace
the broader frame macrobenchmark, establish a SUMMIT scene result, or prove the
best automatic threshold. Hierarchical culling and policy selection remain
separate follow-up PRs.
