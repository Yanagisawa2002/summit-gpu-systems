# SUMMIT GPU Direct Binning

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

Profiler scopes identify regions but do not prove performance. Any claim still
requires direct GPU timestamps, deterministic validation, named hardware and
driver, counterbalanced order, and retained negative results.
