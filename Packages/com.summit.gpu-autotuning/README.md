# SUMMIT GPU Autotuning

This package turns measured timing distributions into device-keyed runtime
profiles. The original primitive-backend API remains available: unsupported or
stale `GpuAutotuneProfile` data falls back to `GpuPrimitiveBackend.Auto`, while
matching profiles may select Portable or WaveOps per primitive workload.

## GPU-driven instance policy

`GpuDrivenInstancePolicySelector` adds a higher-level, engine-native policy for
two measured axes:

- state upload: `None`, `Dirty`, or `Full`;
- culling: `Flat` or `Hierarchy`.

Output is not a performance choice. The caller supplies the required
`CulledTail` or `VisibleOnly` semantic contract, and only rules with that exact
contract may match. Primitive selection is also independent: use the original
PR1 `GpuPrimitiveBackendResolver` for the named primitive workload, then compose
that result with the instance decision through
`GpuDrivenInstancePolicyComposition`. Its resolver overload distinguishes an
accepted measured `Portable` choice from a missing-profile fallback.
`ResolveMeasuredOrPortable` remains available when that distinction is not
needed. Missing or stale primitive evidence never promotes WaveOps from
capability alone.

Policy contract v2 retains `requiredOutputMode` and `primitiveBackend` in the
serialized rule for migration clarity. The former is a match constraint and the
latter must be `Portable`; a rule that attempts to select WaveOps is rejected.
The automatic benchmark therefore calibrates upload/culling only. In the
selected-system workflow every forced and automatic side composes the same
exact-device PR1 `exclusive-scan` choice, so it verifies the production call
chain without treating primitive selection as a third calibrated axis or
confounding upload/culling A/B comparisons. Direct use of the older selector
result and primitive override remains compatibility surface, not evidence that
the instance profile measured a primitive-backend axis.

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
`Full` upload + caller-required output (default `CulledTail`) + `Flat` culling.
The evidence-safe composition fallback is `Portable` primitives. A valid profile
is still only a performance proposal.
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
- composition accepts PR1 `WaveOps` only when the exact-device measured profile
  selected it and current runtime support is still present.

Manual upload/culling overrides may replace fallback choices even when a profile
is missing, no rule matches, or profile hysteresis is still pending. They always
pass through the same hard gates and cannot bypass them; an invalid observation
itself never accepts overrides. Callers keep one
`GpuDrivenInstancePolicyState` per independent stream. Rules use a
consecutive-frame enter threshold and a wider exit range; hard gate failures
reset that history immediately. Selector creation performs all string/profile
validation and compilation, so a warmed `Select` call performs no string work
and allocates no managed memory.

Calibration and holdout measurements must remain separate. The profile is also
bound to the exact measurement contract used to create those results. Only
candidates that pass frozen holdout correctness and tail-latency gates belong
in a runtime profile; a rejected candidate should be represented by the safe
baseline for that measured range rather than promoted as an optimization.

The formal runner writes an immutable run contract, a sealed setup receipt, and
one atomic receipt per `cell x phase`. Selected-system evidence also seals the
primitive-profile hash, workload ID, exact-device acceptance, and executed
backend; all A/B rows must report the same resolved backend. `-Resume`
revalidates commit, source, Unity, Player payload, both profiles, phase
specification, and evidence hashes before skipping any completed work.
`-RecoverInterrupted` is separately required to archive and replace an unsealed
phase directory or a dead-process lock. Missing, duplicate, unexpected,
corrupted, or foreign-contract receipts fail closed.
