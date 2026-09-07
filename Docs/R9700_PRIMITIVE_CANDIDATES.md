# R9700 primitive candidates v1

These are executable discovery candidates, not measured winners. Existing `Auto`,
`Portable`, `WaveOps`, constants and call sites keep their behavior without an
explicit constructor `candidateId`. No autotuning defaults are changed.

| ID suffix (prefix `primitives-v1-`) | Threads | Elements/thread | Tile | Radix bits | Availability |
| --- | ---: | ---: | ---: | ---: | --- |
| portable-t128-e4-r4 | 128 | 4 | 512 | 4 | imported kernel check |
| portable-t128-e4-r8 | 128 | 4 | 512 | 8 | imported kernel check |
| wave-t128-e4-r4 | 128 | 4 | 512 | 4 | DX12/Vulkan + imported kernels + probe |
| wave-t256-e2-r4 | 256 | 2 | 512 | 4 | DX12/Vulkan + imported kernels + probe |
| wave32-t128-e4-r4 | 128 | 4 | 512 | 4 | disabled: SM6.6 importer not validated |
| wave64-t128-e4-r4 | 128 | 4 | 512 | 4 | disabled: SM6.6 importer not validated |

The bounded set compares register work, group geometry and radix width without
a full Cartesian sweep. Only the portable bin-owner scatter has an 8-bit
candidate: expanding the wave scatter's per-bin collectives to 256 is deliberately
excluded. The portable scatter emits independently per bin in input order. Wave
scatter computes per-stripe wave ranks and carries earlier stripes explicitly.

Scan reduces consecutive values in registers, scans thread/wave totals with a
work-efficient shared tree, then emits each thread's local prefixes. Reduction
uses register accumulation, optional wave sum and a shared reduction tree.
Radix histograms are digit-major; a hierarchical scan of the entire histogram
replaces the legacy serial loop over groups, including global bin bases.

## API and lifetime

```csharp
var descriptor = GpuPrimitiveCandidates.Get("primitives-v1-wave-t128-e4-r4");
using var primitives = new GpuPrimitives(capacity, candidateId: descriptor.Id);
primitives.RecordExclusiveScan(commands, input, output, count); // configured candidate
primitives.RecordRadixSortKeyBits(commands, keys, values, keysOut, valuesOut, count, keyBits);
primitives.RecordReduceSum(commands, input, oneWordOutput, count);
```

The constructor runs a synchronous, untimed wave probe and rejects unsupported
identities. `IsSupported` only checks import/device support and compiled group
dimensions; `TryProbeWaveSize` additionally executes and checks all probe lanes.
Do not run either initialization or capacity growth inside timing scopes.
`Record*` performs no allocation/readback. Instances and caller buffers must stay
alive until commands complete; scratch cannot be shared by concurrent queues.

On configured instances only `Auto` routes scan, stable-compaction's scan and
radix through the candidate. Explicit `Portable` and `WaveOps` retain their
legacy paths. Histogram and append compaction always retain legacy selection.
Reduction is new, requires an explicit candidate, wraps modulo 2^32, writes zero
for empty input, and requires distinct input/output buffers. A null `CandidateId`
means legacy selection, not a frozen implementation identity. Calibration names
`Portable`/`WaveOps` are exposed as `PortableDefaultId`/`WaveDefaultId`.

Radix keys must fit in the low `keyBitCount` bits (1..32); contents are not
validated by a recorder. Passes use ceil(bits/digitWidth), and parity always
places the final result in the requested output pair. Sorting is stable, inputs
remain read-only and output pairs must not alias inputs or each other. Empty
scan/radix leaves output untouched; empty compaction publishes zero count.
Partial groups and full uint overflow follow legacy contracts.

`ScratchBytes` includes both candidate scratch and retained legacy fallback
storage. `CandidateScratchBytes` reports the incremental allocation. Keeping
fallback buffers is intentional and must not be hidden in a comparison.

## Wave evidence and limitation

On AMD Radeon AI PRO R9700 / Unity 6000.5.2f1 / DX12, both automatic-wave probes
returned 64. This describes the probe shader's execution, not forced wave size
or an ISA claim for every kernel. Kernel thread-group dimensions are checked
through `GetKernelThreadGroupSizes` against imported compiled kernels.

An actual Unity import of `[WaveSize(32)]` and `[WaveSize(64)]` failed with:
`attribute WaveSize only valid for shader model 6.6 and higher.` The toolchain
used SM6.0. Keyword-only requests were insufficient: requested32 probed64 and
the requested64 variant was unavailable. These failures are preserved in the
worker validation report. Actual attribute-bearing shader sources remain in
`Packages/com.summit.gpu-primitives/Experimental~/ExplicitWaves`; they are excluded
from normal Unity import so unsupported shaders cannot break player builds.
Enabling them requires a validated SM6.6 Unity importer, DXIL/ISA evidence and
full candidate tests. An unavailable candidate is never relabeled as supported.

References: [Unity feature keywords](https://docs.unity3d.com/6000.5/Documentation/Manual/SL-ShaderCompileTargets.html)
describe capability variants; [Microsoft WaveSize specification](https://microsoft.github.io/DirectX-Specs/d3d/HLSL_SM_6_6_WaveSize.html)
defines the SM6.6 attribute and DXIL wave-size metadata.

## Correctness and comparisons

Run `Tools/Run-UnityEditModeTests.ps1 -UseGraphics -ForceDirect3D12 -TestFilter
Summit.GpuPrimitives.Tests` under the shared serialization wrapper. Tests compare
independent CPU scan, modulo reduction, nonbinary-predicate compaction and stable
sort oracles at empty, wave, group, tile and three-level hierarchy boundaries.
Radix tests cover 1/4/5/7/8/9/12/13/16/17/24/25/31/32-bit pass boundaries, duplicates,
hot bins, maximum valid keys, untouched sentinels and input preservation.

```powershell
# Short correctness/player smoke (one fixture, no performance acceptance).
./Tools/Run-R9700PrimitiveCandidates.ps1 -SerializationScript '<control>/Invoke-SerializedValidation.ps1' -AllowMissingGpuTiming
# After integration only: native timestamp comparison, seven fixed cells.
./Tools/Run-R9700PrimitiveCandidates.ps1 -SerializationScript '<control>/Invoke-SerializedValidation.ps1' -Comparison
```

The fixture fixes distribution, seed (runner default), count and key bits. Each
cell compares identical inputs and stable CPU outputs in one counterbalanced
player process. Reduction compares only new candidates; the other three
operations also include current Portable/WaveOps. `radix-sort-32` is retained as
the historical operation name; `keyBitCount` is authoritative for partial keys.
Explicit candidate filters reject unsupported/unknown IDs. `candidates` enumerates
the bounded catalog and records unavailable entries in `candidate-capabilities.csv`.

`candidate-resources.csv` joins raw samples by caseId and reports threads, tile,
elements/thread, radix width, exact dispatch count per primitive invocation,
instance/candidate scratch and observed probe width. All scan hierarchy and
histogram setup dispatches are inside timing. The shared benchmark's total
resident bytes include every coexisting candidate instance plus caller buffers.
Logical bytes are an algorithmic estimate, not measured memory traffic; driver
alignment, register pressure and ISA occupancy are not inferred. Source provenance
hashes now include every primitive runtime C#/compute/HLSL file. The legacy formal
acceptance preset still requires its original uniform 32-bit workload.

`Summarize-R9700PrimitiveCandidates.ps1` is invoked after every fixture. It reuses
all native timestamp integrity, sample completeness, counterbalance and block
statistic checks from the existing summarizer, then checks the full bounded
candidate/baseline matrix. `candidate-summary.csv` contains per-round GPU and
submission CPU average/P99, main submission-thread allocation average/P99, scratch
and dispatch counts, and same-round differences against WaveOps (Portable if
unavailable). Reduction uses portable-t128-e4-r4 as an explicit comparison
reference, not a selected default. Allocation counts cover command submission
and timestamp MarkSubmitted only; they are not process-wide GC or recorder cost.

Comparison mode requires a clean committed integration worktree, complete native
timings, and stable source/player hashes across all cells. Any Unity-generated
tracked settings normalization must be reviewed and committed before running the
comparison; unexplained post-build drift is rejected. Smoke summaries remain
labeled `smoke-not-for-selection`, even when native GPU timing is available.
