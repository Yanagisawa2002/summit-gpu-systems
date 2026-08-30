# SUMMIT GPU Autotuning

This package turns measured timing distributions into device-keyed runtime
profiles. The original primitive-backend API remains available: unsupported or
stale `GpuAutotuneProfile` data falls back to `GpuPrimitiveBackend.Auto`, while
matching profiles may select Portable or WaveOps per primitive workload.

## GPU-driven instance policy

`GpuDrivenInstancePolicySelector` adds a higher-level, engine-native policy for
four explicit axes:

- state upload: `None`, `Dirty`, or `Full`;
- output: the caller-required `CulledTail` or `VisibleOnly` contract;
- culling: `Flat` or `Hierarchy`;
- primitive backend: `Portable` or `WaveOps`.

Profiles use inclusive integer count ranges and integer basis points. Their
selection dimensions include active/dirty/visible work, upload call count,
upload amplification, hierarchy candidate-pair ratio, cluster count, and view
count. This prevents a contiguous dirty update from being conflated with a
fragmented one, or a coherent hierarchy from being extrapolated to an
unmeasured topology with the same final visible rate.

A profile is accepted only when its schema, policy contract, Unity/package
versions, processor type, operating system, complete GPU fingerprint, pipeline
fingerprint, shader fingerprint, calibration protocol, and measurement/marker
contract fingerprint exactly match the current environment. This CPU identity
is part of the contract because upload and submission tails are CPU-sensitive.
UTC generation time, the 40-hex source commit, and 64-hex measurement and
holdout evidence identifiers are validated before use. The profile and every
rule must carry accepted holdout evidence. Invalid ranges, duplicate IDs,
ambiguous overlapping enter ranges, hierarchy rules for `CulledTail`, and exit
ranges that are not strictly wider than enter ranges reject the complete
profile.

Missing, mismatched, or unvalidated profiles produce a usable fail-safe selector:
`Full` upload + caller-required output (default `CulledTail`) + `Flat` culling +
`Portable` primitives. A valid profile is still only a performance proposal.
Every frame applies hard semantic and capability gates:

- `None` requires an exact resident-state revision and count match;
- `Dirty` requires facts captured from a real, still-live and consumable
  `GpuInstanceDirtyUploadPlan` whose exact receipt says `DirtyRanges`, plus
  an exact nonzero match between the plan's expected resident/base revision and
  the current resident revision, plus matching source revision, active count,
  dirty record count, range count, uploaded count, and upload-call accounting.
  Detached, legacy-unbound, zero-resident, or stale-base facts cannot authorize
  an upload, and a range-capacity plan that selected `Full` remains `Full`;
- `Hierarchy` requires `VisibleOnly`, an exactly covered legal cluster topology,
  current cluster/visibility/candidate-estimate revisions, a candidate estimate
  that is a superset of final visible pairs, and sufficient instance, cluster,
  view, and pair capacities;
- `WaveOps` requires explicit current-device support.

Manual overrides may replace fallback axes even when a profile is missing, no
rule matches, or profile hysteresis is still pending. They always pass through
the same hard gates and cannot bypass them; an invalid observation itself never
accepts overrides. Callers keep one `GpuDrivenInstancePolicyState` per
independent stream. Rules use a consecutive-frame enter threshold and a wider
exit range; hard gate failures reset that history immediately. Selector
creation performs all string/profile validation and compilation, so a warmed
`Select` call performs no string work and allocates no managed memory.

Calibration and holdout measurements must remain separate. The profile is also
bound to the exact measurement contract used to create those results. Only
candidates that pass frozen holdout correctness and tail-latency gates belong
in a runtime profile; a rejected candidate should be represented by the safe
baseline for that measured range rather than promoted as an optimization.
