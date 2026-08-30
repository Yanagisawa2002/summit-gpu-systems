# Batched hierarchical multi-view culling — RTX 4090 formal result

## Outcome

Commit `bd102c902ea1d9b5b8a64c013ccda1e3483fabd8` changes the
hierarchical visible-pair path from per-pair global reservations and histogram
updates to workgroup-batched reservations and, for the common small-bin case,
workgroup-local histogram accumulation. It also retains the preceding fused
initialization/coarse-classification change and folds the remaining hierarchy
diagnostic merge into indirect-argument construction.

T07 closes **POSITIVE for the measured large-workload cells**. In the sealed
`1,048,576 instances x 4 views` formal matrix, Hierarchical beat Flat in all
four visibility cells: GPU-scope mean improved `32.50%–53.41%`, GPU P95
improved `12.37%–16.26%`, every paired block was positive, and every frame- and
enqueue-P99 guardrail passed. The matrix contained `36,000/36,000` measured
rows and `16/16` exact validations.

The policy boundary remains important. At `65,536 instances x 4 views x 5%`
visibility, Hierarchical was `0.31%` slower by GPU mean, `8.11%` slower at GPU
P95, and pairwise unstable. That cell remains **NO-GO**, so Flat is the
measured fallback. Unmeasured small-workload visibility cells remain unknown.

| Visible | Flat / hierarchical GPU mean | Mean speedup | GPU P95 speedup | Frame P99 regression | Enqueue P99 regression | Paired range | Decision |
|---:|---:|---:|---:|---:|---:|---:|:---|
| 5% | 0.6744 / 0.3874 ms | 42.56% | 15.67% | -13.94% | -2.22% | 39.19% to 44.63% | GO |
| 25% | 1.7876 / 0.8329 ms | 53.41% | 16.26% | -13.64% | -9.72% | 51.67% to 54.90% | GO |
| 75% | 3.2319 / 2.1163 ms | 34.52% | 12.37% | -7.15% | -3.43% | 29.23% to 42.22% | GO |
| 100% | 4.0645 / 2.7436 ms | 32.50% | 13.84% | -12.59% | +2.93% | 30.52% to 33.60% | GO |

The frozen material gate required paired-median speedup of at least `1%`,
every pair positive, native GPU P95 non-regression, and no more than `5%`
regression in either frame P99 or enqueue P99.

## Why the old path failed

The retained PR6 formal run showed useful GPU-mean work reduction at
`1.05M x 4 views`, but hierarchy enqueue P99 regressed in all four cells and
failed the `+5%` guardrail at 5% and 75% visibility. A paired-block audit of
two retained formal runs found hierarchy enqueue mean worse in `32/32` pair
blocks (`+4.23%` to `+18.33%`), while P99 signs varied across seeds. That is
consistent with both a real fixed submission cost and a noisy tail, rather
than a single bad outlier.

The old hierarchy chain issued separate clears for six buffers, separate
validation and coarse-classification kernels, per-visible-pair global append
reservations, per-pair global histogram atomics, and a separate diagnostic
merge. Sparse visibility reduced fine work, but the fixed dispatch chain and
global atomic pressure remained.

## Two bounded mechanism attempts

### Candidate 1 — fused setup and coarse classification

Commit `8e2c016d230e25db79ac07b74f3acd0e4d9b0e55`:

- replaced six clear dispatches with one `InitializeHierarchyFrame` kernel;
- fused hierarchy validation and cluster/view classification into
  `ValidateAndClassifyClusters`;
- wrote one complete cluster visibility mask per cluster instead of globally
  OR-ing each visible cluster/view pair.

It removed six compute dispatches from the previous chain and passed the then
targeted `12/12` plus full `768/768` D3D12 EditMode suites. Its formal result
was nevertheless **NEGATIVE** under the frozen gate: enqueue P99 regressed
`20.50%`, `26.57%`, and `7.82%` at 5%, 25%, and 100%, while the 75% cell also
contained a negative paired GPU block. This result was retained; the candidate
was not rerun until it passed.

### Candidate 2 — batched reservation and histogram

Commit `bd102c902ea1d9b5b8a64c013ccda1e3483fabd8`:

- computes visibility and selected LOD once per instance/view pair and packs
  the four selected LODs into two `uint` words;
- accumulates visible offsets inside each 64-thread workgroup and performs one
  global visible-span reservation per active group, rather than one global
  reservation per visible pair;
- when `viewCount x drawGroupCount <= 64` (32 bins in the formal matrix),
  accumulates bin counts in group-shared memory and emits at most one global
  count update per active bin and workgroup;
- preserves a correctness-tested direct-global fallback above 64 bins; and
- merges binner diagnostics while building hierarchical indirect arguments,
  removing one additional dispatch.

Together with Candidate 1, the final chain records seven fewer compute
dispatches than the pre-T07 hierarchy path. The new
`GlobalBinCountFallbackAboveSharedCapacityMatchesOracle` test exercises
`32 views x 4 draws = 128 bins`, so the optimization does not silently assume
the formal matrix's 32-bin shape.

## Work reduction and the 100% result

| Visible | Coarse cluster-view pairs | Candidate instance-view pairs | Visible pairs | Candidate reduction vs Flat |
|---:|---:|---:|---:|---:|
| 5% | 63,876 | 209,715 | 209,715 | 95.00% |
| 25% | 65,532 | 1,048,576 | 1,048,576 | 75.00% |
| 75% | 65,532 | 3,145,728 | 3,145,728 | 25.00% |
| 100% | 65,536 | 4,194,304 | 4,194,304 | 0.00% |

At 100% visibility there is no candidate-pair reduction, yet the sealed
same-run hierarchy path improved GPU mean by `32.50%`. The defensible
interpretation is that the final hierarchy implementation also acts as a more
efficient high-cardinality compaction/binning path because it batches global
reservations and histogram updates. This does not prove that hierarchy is
universally faster at full visibility; the conclusion is limited to this
layout, bin count, device, API, and workload scale.

## Small-workload boundary

The explicit boundary run used the same commit and protocol at
`65,536 instances x 4 views x 5%` visibility:

| Metric | Flat | Hierarchical | Delta / decision |
|:---|---:|---:|:---|
| GPU mean | 0.04630 ms | 0.04645 ms | -0.31% speedup |
| GPU P95 | 0.03789 ms | 0.04096 ms | -8.11% speedup |
| Frame P99 | — | — | +0.14% regression |
| Enqueue P99 | 0.26022 ms | 0.25114 ms | -3.49% regression |
| Paired blocks | — | — | median -4.14%; range -30.05% to +30.53% |

It produced `9,000/9,000` measured rows and `4/4` exact validations. The
result is **NO-GO / Flat fallback**, not a reason to hide the explicit
hierarchical API. The other three small-workload visibility cells were not
measured and cannot be inferred from this one.

## Correctness and provenance

- Unity `6000.5.2f1`, Windows 11, Direct3D 12, NVIDIA GeForce RTX 4090;
  formal seed `20260829`.
- Candidate 2 targeted hierarchy tests: `13/13`; full D3D12 EditMode suite:
  `769/769`, zero failed or skipped.
- Formal scenarios: `4/4`; measured rows: `36,000/36,000`; exact CPU-oracle
  validations: `16/16`.
- Timed managed-allocation rows: `0`; measurement-readback bytes: `0`; all
  completion fences passed.
- Native timestamp instrumentation read `576,000` bytes, exactly the declared
  16-byte completion channel per measured sample; these are not measurement
  readbacks from the timed workload.
- Formal source began and ended clean at `bd102c9...`; source hashes and the
  freshly built Player payload remained stable through the run.
- Source snapshot SHA-256:
  `979AEF12018CB1BC0F204C2408EE8A4DA693A5B386EF4B34248228B77390AA6E`.
- Player payload SHA-256:
  `7A4321F35DA11B85FB2870C7AD2FD136490D60A6618AAF5F9EC9F37A44E1F4E3`.
- Runtime hierarchy shader SHA-256:
  `AA89AFFD1EA36B062F1207381E1089C989B97766C22BA5A1E50FCC95F19DA486`.
- Direct-binning shader SHA-256:
  `4A7BE834609D2D598DA587B49C2D353034EDE9FFBA128A491EB56E4BA048234C`.
- Runtime API SHA-256:
  `07A656648B5C5382D9ACF53C31A3A6C0E94EA7D55A069B624382203DC717941C`.
- Formal runner-config / matrix-summary SHA-256:
  `D5A662EAC1A496335ED8FA477A029F52332F9A6CCDECC996455C7378B885E351` /
  `B871BC5B6F73ED1AB25EFAF368AAA45525BE18B485188AB143467BD75F776560`.
- Targeted/full EditMode XML SHA-256:
  `D711D23EA7631C1A72A1FD6495AC0F95575F00E8D7F38BC8070E2B131353A40F` /
  `4F8CE78EF3FD27991B6D5A3F5285966AED1D820A7F32DBB0D4C671EE58BA59DF`.
- Small-boundary runner-config / matrix-summary SHA-256:
  `0736F4060A97D718792D3D59370AD902B35E64F27200D54DF820E4FFC1CA075C` /
  `7A1E400FEB4CD944368EE97F9FDADEAD28EE57EB73B11929BFB3DC20CED427AB`.

Raw evidence is retained under:

- `artifacts/t07-hierarchy-candidate1-8e2c016` and
  `artifacts/t07-hierarchy-formal-8e2c016`;
- `artifacts/t07-hierarchy-candidate2-bd102c9` and
  `artifacts/t07-hierarchy-formal-bd102c9`; and
- `artifacts/t07-hierarchy-small-boundary-bd102c9`.

## Evidence boundary

Allowed: on this frozen RTX 4090/D3D12 protocol, the batched hierarchical path
improved GPU-scope mean by `32.50%–53.41%` and GPU P95 by
`12.37%–16.26%` across all four `1.05M x 4-view` visibility cells while
passing all frame/enqueue P99 guardrails and exact correctness checks.

Not allowed: claiming a universal frame-rate improvement; claiming the small
workload improved; filling the three unmeasured small cells; attributing the
result solely to coarse rejection; generalizing to other GPUs/APIs/layouts;
or treating absolute milliseconds from older PR6/Candidate-1 processes as a
same-process before/after comparison.

The valid performance comparison is Flat versus Hierarchical within the final
sealed run. Older runs establish the prior failure modes and guided the bounded
mechanism attempts; their absolute timings are not combined with the final run
to manufacture an additional speedup claim.
