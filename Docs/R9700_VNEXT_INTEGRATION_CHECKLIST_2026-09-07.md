# R9700 vNext integration gates

Baseline: `3faad555c6044f966403261d627d8d6ac232d2d9`. This is an execution checklist, not a statement that validation has passed. Final results belong in the integration report.

## Merge and provenance

- Verify each worker report against its branch HEAD, committed file diff, clean worktree, implemented scope, and actual test results. Do not merge unfinished work.
- Merge primitives, adaptive/autotuning, query, index, residency, and scheduler into the isolated integration branch, adjusting order only for real dependencies.
- Preserve historical evidence and old executable baselines. Keep unmeasured candidates optional.
- Record merged SHAs, integration changes, test commands/results, skipped gates, and unrun performance comparisons.
- Update main with `--ff-only` only if it remains clean, on main, and at the baseline immediately before the update. Never push.

## Cross-package contracts

### Primitive candidates and calibration

- Retain Auto/Portable/WaveOps APIs and defaults. Candidate identities must be extensible, validated, and independently capability checked.
- Unknown candidate IDs, unsupported wave modes, legacy schemas, mismatched graphics/driver/Unity/compiler/shader/build identities, and untrusted key domains must take the documented fallback.
- Stored source commit text alone must not establish runtime shader compatibility.
- Verify partial tiles, stable ordering, key-bit boundaries, counts, final output placement, and valid fallback under the full dependent package suite.
- Exercise matrix misses and repeated distribution transitions through actual RecordAdaptive calls; account for feature gathering, selector costs, switching, and persistent scratch.

### Incremental index and range query

- Distinguish stable payload slot capacity, active live count, and reserved CSR slot capacity. Removing a low slot must not hide a higher stable ID.
- All query candidates must skip tombstones and preserve full payload/hash semantics. Work-list sizing must account for reserved CSR spans and duplicate query coverage.
- Compare old query and new candidates against an independent CPU oracle across full rebuild and incremental transitions, including payload-only updates, additions/removals, static-heavy scenes, teleports, dense cells, empty queries, boundaries, and partial chunks.
- Force small work-list/capacity conditions to verify bounded overflow handling and safe fallback; stale scratch must not contaminate subsequent recordings.
- External GPU input buffers must have explicit ownership, key-validation, recording/submission, and lifetime contracts.

### Residency and scheduling

- Residency planning must reject invalid requests without partial mutation, bound uploads, keep pending/unavailable pages visibly nonresident, and preserve retry/frame semantics.
- Verify plan leases do not alias across in-flight frames and that fence-protected consumers prevent slot reuse. Exercise cache overflow, fairness, teleports, and both baseline and optimized policies.
- Verify scheduler delayed sample validity, cold start/outliers, burst backpressure, background progress, dependency ordering, cycle rejection, and bounded steady-state allocation.
- Keep same-queue scheduling default; async requires measured evidence and dependency fences. No dispatch preemption, sparse resources, or dedicated copy queue claims.

## Combined validation

1. Repository layout, PowerShell syntax, JSON/assembly definitions, and applicable CPU provenance tests.
2. Serialized Unity Null Device import and complete EditMode CPU contract suite. Record GPU skips separately.
3. Serialized Unity/DX12 complete EditMode suite, including new cross-package integration tests. Inspect XML and shader/compiler errors; an exit code alone is insufficient.
4. Serialized short executable correctness smoke for new harnesses/candidates as needed to validate runtime build paths, native timing availability, and comparison completeness. This is not formal performance acceptance.
5. Review final diff and clean commit; report actual environment, counts, failures, skips, and any unsupported wave variants explicitly.

Every Unity import, build, test, and GPU execution must hold the shared `Invoke-SerializedValidation.ps1` mutex until all child processes exit.

## Deferred comparison protocol

Prepare directly executable staged commands after worker CLI contracts are final. Separate discovery/calibration from frozen evaluation. Capture source/build/shader identities, Unity/compiler/driver/API/device, cold/warm state, identical deterministic workloads and oracles, counterbalanced repetitions, CPU/GPU average and P99, GC, memory, dispatch/setup costs, and sample integrity. Do not run the long formal matrix or claim speedups during implementation integration.
