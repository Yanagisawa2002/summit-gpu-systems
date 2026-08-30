# External BRG Shooter benchmark contract

Status: source lock, Unity 6000.5 migration, thin adapter, and a bounded E1
smoke are complete. The smoke pair is invalid for performance claims because
representative image hashes differ and GPU-frame timing coverage is below the
frozen threshold. No external performance claim is accepted.

## Current bounded result

The pinned fixture was migrated in an isolated local checkout to Unity
`6000.5.2f1` / URP `17.5.0`. All five package dependencies resolved, the
adapter compiled, and a Windows D3D12 Development Player built with zero build
errors. Local file-package transport was used for this development smoke and
is explicitly ineligible for sealed evidence.

One E1 smoke per path completed with 5 warmup, 5 convergence, and 30 measured
frames on an RTX 4090:

- Both receipts used the same state hash `F055132364219845` and produced
  non-black images with the same 209,939 non-black pixels.
- `gpu-systems` reported exactly 32,768 visible instances and zero contract
  violations/error flags.
- The representative image hashes differed (`75019FF1348A3B60` versus
  `0DAD069480C1C5C0`). GPU-frame timing coverage was 76.7% for `engine-brg`
  and 80.0% for `gpu-systems`, below the required 95%.
- The diagnostic whole-frame deltas favored `gpu-systems`, but they are not
  publishable because correctness/availability gates take precedence.

Two bounded attempts to add a separate settled validation frame exposed unsafe
runner placement: first a BRG upload, then a synchronous readback, could stall
inside the SRP render callback. Both Players were terminated without receipts;
those attempts are not evidence. The stable receipt-writing harness is kept,
the failed validation-frame variants are not.

Therefore T10 closes as `INVALID / INCOMPLETE FORMAL`, not `POSITIVE` and not
a performance `NEGATIVE`. Use `Compare-ExternalBrgPair.ps1` to reproduce the
fail-closed pair decision. A future attempt must fix validation scheduling,
reach timing coverage, use commit-pinned Git UPM transport, and then run the
counterbalanced formal matrix from scratch.

## Purpose

This benchmark tests whether the scene-independent GPU-driven instance packages
integrate into a Unity project that was authored independently from this
repository. The host is Unity Technologies' small `brg-shooter` sample, pinned
by `UPSTREAM_BENCHMARK_LOCK.json`.

The result is an integration and interoperability measurement inside Unity. It
is not an industry-standard benchmark, a Unity endorsement, or evidence that
one engine or product is superior to another.

## Source and publication boundary

The Unity sample is a local fixture under the Unity Companion License. This
repository does not vendor or redistribute the fixture and must not publish a
modified fork of it. The public source contains only independently authored
adapter, runner, lock, and receipt files. A runner consumes a user-provided or
locally materialized checkout at the pinned commit, verifies its original
license notice and Git blobs, and keeps generated migration/build outputs out
of source control.

The Unity Companion License and upstream notice are authoritative. This
engineering boundary is not legal advice. Public reports must describe this as
Unity package integration, must retain attribution, and must not frame it as
competitive analysis.

Validate a local fixture before migration or measurement:

```powershell
pwsh -NoProfile -File `
  .\ExternalBenchmarks\BRGShooter\Tools\Test-UpstreamBenchmarkLock.ps1 `
  -FixtureRoot C:\path\to\brg-shooter
```

The validator reads canonical bytes from the pinned Git blobs rather than the
checkout-normalized working tree. A commit, ancestry, object type, blob SHA-1,
byte-count, or SHA-256 mismatch fails closed.

## Demo and benchmark surfaces

- `Demo` preserves the playable upstream scene and is used for human visual
  compatibility. It is never a formal timing path.
- `Benchmark` is enabled only by an explicit command-line flag. It disables
  input, random spawning, variable time steps, audio, and overlays; uses fixed
  generated state and cameras; and launches a visible windowed Player.
- The adapter is introduced through a generated manifest overlay. Opening the
  pinned fixture must not silently turn its working tree into publishable
  source.

## Compared paths

Both paths consume the same authoritative persistent `GpuInstanceState`, mesh,
camera, update ordinals, and visible-set oracle.

1. `engine-brg`: a Burst/Jobs `BatchRendererGroup` baseline with persistent
   buffers and DOTS-instanced shader properties.
2. `gpu-systems`: package GPU visibility/grouping/indirect arguments plus the
   measured upload/culling policy.

Output mode is a caller semantic constraint. Upload and culling are the PR7
instance-policy axes. The primitive backend is independently supplied by the
PR1 device resolver; it is not recalibrated by this matrix.

The shared shader source must contain a BRG `DOTS_INSTANCING_ON` variant and a
GPU grouped-index variant. A traditional-instancing shader is not a valid BRG
baseline. Adapter conversion, state generation, validation, and report writing
are outside the timed submission window or charged identically to both paths.

## Frozen workload cells

| Cell | State | Role |
| --- | --- | --- |
| U0 | Upstream 3,200 floor cells, up to 16,384 debris, one camera, all visible, fully dynamic | Original-sample compatibility and Full + Flat negative control |
| E1 | 131,072 generated cubes, one camera, 25% visible, 1% dirty | Sparse-update portability cell |
| E2 | 131,072 generated cubes, one camera, 25% visible, 10% dirty | Moderate-update portability cell |
| E3 | 131,072 generated cubes, one camera, 100% visible, 100% dirty | Dense Full + Flat negative control |

E1-E3 are explicit benchmark extensions and must never be described as the
unmodified upstream game.

## Measurement and correctness

- Windows x64, Direct3D 12, Unity `6000.5.2f1`, visible windowed Player.
- Separate fresh Player processes with counterbalanced `ABBA;BAAB` blocks.
- 60 warmup frames, 120 convergence frames per block, 900 measured frames per
  block.
- Report CPU state preparation, upload planning/recording, BRG callback or
  indirect submission, total CPU, main/render-thread frame, whole-frame GPU,
  and package-native GPU scope separately.
- CPU/native/submission metrics require complete coverage. GPU frame values use
  literal `unavailable`, at least 95% coverage per block, and at least 90%
  jointly valid paired coverage.
- Timed managed allocations, measurement readback, and slot waits are zero.
- CPU-oracle membership, exact draw instance counts, deterministic state hash,
  non-black output, and representative image parity all pass.
- Source, migration overlay, package commit, Unity revision, package lock,
  Player payload, device/driver, and raw evidence files receive SHA-256
  receipts.

## Decision gate

A portability claim requires both E1 and E2 to improve CPU submission P95 by
at least 20% and 0.20 ms, while GPU-frame P99 is no worse by more than both 5%
and 0.25 ms. Correctness, provenance, availability, and allocation gates are
mandatory. U0 and E3 remain in the report even when negative.

- A U0/E3 fallback is expected safety behavior, not an optimization win.
- CPU improvement with material GPU-tail regression is a NO-GO.
- An E1-only gain scopes the claim to very sparse repeated-instance workloads.
- Migration-only or output differences invalidate the A/B comparison.

## Definition of done

- Upstream lock validates before and after every generated fixture migration.
- Migration overlay is reviewable and contains no optimization.
- The migrated upstream Demo builds and receives separate visual acceptance.
- Baseline instrumentation lands before the optimized adapter.
- Adapter unit/oracle/output/allocation tests pass.
- U0/E1/E2/E3 evidence is checkpointed, resumable, and complete.
- The report publishes positive, neutral, and negative cells with exact metric
  scopes; no cross-project claim appears before all gates pass.
