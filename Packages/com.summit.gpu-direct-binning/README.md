# GPU Systems Direct CSR Binning

`com.summit.gpu-direct-binning` records a scene-independent GPU pipeline that
turns a `uint` key/value stream into a compressed-sparse-row (CSR) bin layout:

```text
clear -> count valid keys -> exclusive scan -> initialize write heads
      -> atomic scatter
```

The runtime is deliberately direct-only. It contains no radix backend,
adaptive selector, scene policy, or performance claim.

## Contract

For `N` input elements and `C` bins:

- `keys[N]` contains unsigned bin identifiers.
- `values[N]` contains opaque unsigned payloads.
- `binCounts[C]` receives the number of valid elements in each bin.
- `binOffsets[C + 1]` receives exclusive CSR offsets.
- `binnedValues[N]` receives valid payloads grouped by bin.
- `diagnostics[2]` receives invalid-key and invariant diagnostics.

A key is valid when `key < C`. Invalid keys are excluded. `binOffsets[C]` is
the total valid count, and bin `b` occupies
`[binOffsets[b], binOffsets[b + 1])`.

Atomic scatter does not define ordering within a bin. Correctness means exact
counts, offsets, membership, key association, and invalid-key accounting, not
input-order preservation.

`RecordWithDiscardKey` adds an explicit non-error sentinel outside `[0, C)`.
Matching elements contribute no count, offset, diagnostic, or output write;
other out-of-range keys remain errors. This is intended for visibility,
filtering, and sparse producer pipelines that should not materialize rejected
payloads.

The diagnostic words are:

| Word | Meaning |
| ---: | --- |
| 0 | Number of keys greater than or equal to `C` |
| 1 | `GpuDirectBinningErrorFlags` bit mask |

`InvalidKeyEncountered` accompanies a nonzero invalid-key count.
`ScatterDestinationOutOfRange` is an internal-invariant failure and should
always remain clear.

## API

Construct the fixed-capacity recorder outside measurement:

```csharp
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;

using var binner = new GpuDirectSpatialBinner(
    elementCapacity,
    binCapacity);

binner.Record(
    commandBuffer,
    keys,
    values,
    binCounts,
    binOffsets,
    binnedValues,
    diagnostics,
    elementCount,
    binCount,
    GpuPrimitiveBackend.Auto);
```

All buffers must be structured `uint` buffers with sufficient capacity. The
writable buffers must be distinct from one another and from both inputs.
`keys` and `values` may be the same buffer. In-place scatter is rejected.
Target, stride, capacity, and aliasing are validated before command recording.

The binner owns persistent `uint[binCapacity]` write-head scratch. By default
it also owns a `GpuPrimitives` instance sized to `binCapacity`, because only
the bin-count array is scanned. A caller may inject a live primitives instance
whose capacity covers `binCapacity`; its lifetime must exceed the binner's
lifetime. Constructor and `Record` perform an allocation-free capacity/liveness
preflight before recording work.

Memory accounting:

- `InternalScratchBytes`: the owned write-head payload.
- `PrimitiveScratchBytes`: the referenced primitives scratch payload.
- `ScratchBytes` and `ResidentBytes`: both logical payloads.
- `OwnedScratchBytes`: excludes injected primitives storage.

Shared primitive scratch must be counted once in process-wide totals. Driver
allocation alignment and shader assets are excluded.

`Record` allocates no managed arrays or GPU buffers and performs no readback.
Do not overlap executions that share one binner's write-head scratch.

`RecordWithDiscardKeyWithoutDiagnosticClear` exists for a producer that owns
and clears the same diagnostic buffer before classification. The ordinary
discard-key method retains the safe self-clearing behavior.

### Precounted GPU prefix and indirect scatter

`RecordPrecountedPrefixIndirect` is the lower-layer handoff for a GPU producer
that has already emitted all three of the following:

- `binCounts[C]`;
- a dense `keys`/`values` prefix;
- the prefix length in one word of a structured GPU buffer.

The binner does not clear diagnostics and does not rerun a fixed-size count
pass. It records this sequence:

```text
exclusive scan caller binCounts
    -> GPU count to DispatchIndirect arguments
    -> initialize write heads and validate the terminal count
    -> indirect validation of the dense-prefix keys
    -> indirect scatter of only the dense prefix
```

The caller supplies two distinct writable `Structured | IndirectArguments`
uint buffers and a four-byte-aligned byte offset for each. Both offsets must
leave space for the D3D12 dispatch ABI: `{ groupCountX, 1, 1 }`. The validation
buffer is only the first indirect dispatch's input. Validation writes only the
distinct scatter buffer, which is the second indirect dispatch's input. The
two resources must not alias: simultaneously treating one D3D12 resource as
an indirect argument input and UAV output is not a valid synchronization
contract. The GPU element-count input is a structured uint buffer and supports
a word offset. A zero count produces `{ 0, 1, 1 }` in both buffers and no
validation or scatter threads. `keys`, `values`, and `binnedValues` must cover
the binner's fixed `ElementCapacity`, because their active GPU prefix is not
known to the CPU at recording time.

This path treats the producer data as a checked contract. It ORs, but never
clears, these additional flags:

| Flag | Meaning and action |
| --- | --- |
| `PrecountedElementCountOutOfRange` | GPU count exceeds `ElementCapacity`; X dispatch is forced to zero. |
| `PrecountedCountMismatch` | counts do not sum to the GPU count, a bin range exceeds capacity, or scatter exceeds a declared bin range; pre-dispatch failures force X to zero. |
| `IndirectDispatchDimensionOutOfRange` | X would exceed the D3D12 65,535-group limit; X is forced to zero. |

Invalid dense-prefix keys and scatter overflow retain the existing
`InvalidKeyEncountered` and `ScatterDestinationOutOfRange` bits. Diagnostic
word 0 remains the invalid-key count; producer count/range failures are flags
in word 1 and do not change word 0. The caller owns diagnostic initialization
and must inspect it after GPU completion. Invalid keys atomically force the
shared indirect X argument to zero in a validation dispatch before scatter,
so they cannot produce a partially written CSR. Existing `Record*` methods
retain their previous clear/count behavior and signatures.

## Zero elements and profiling

With `elementCount == 0`, counts, all `C + 1` offsets, and diagnostics become
zero while `binnedValues` remains untouched. Non-null dummy input and output
buffers are still required because Unity does not allocate zero-length
`GraphicsBuffer` instances.

Recorded scopes are:

- `Summit.GpuDirectBinning/DirectSpatialBinning`
- `Summit.GpuDirectBinning/Clear`
- `Summit.GpuDirectBinning/Count`
- `Summit.GpuPrimitives/ExclusiveScan`
- `Summit.GpuDirectBinning/Prepare`
- `Summit.GpuDirectBinning/Scatter`
- `Summit.GpuDirectBinning/PrecountedPrefixIndirect`
- `Summit.GpuDirectBinning/PrepareIndirectDispatch`
- `Summit.GpuDirectBinning/Prepare/PrecountedPrefix`
- `Summit.GpuDirectBinning/Validate/PrecountedPrefixIndirect`
- `Summit.GpuDirectBinning/Scatter/PrecountedPrefixIndirect`

Profiler scopes identify regions but do not prove performance. Any claim still
requires direct GPU timestamps, deterministic validation, named hardware and
driver, counterbalanced order, and retained negative results.
