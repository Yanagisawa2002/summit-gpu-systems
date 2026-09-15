# Query consumers and selected complete task

Source baseline: public `summit-gpu-systems`
`b8d63bff657bae54eaca09864f92fb065becb30c`. This work uses no company SUMMIT
source, assets, or data. Source-only inspection preceded hardware admission.

## Existing callers

* `PublicBenchmarks/External/Actual/ExternalReplayPlayer.cs:120`:
  `RunSphere` replays an ArborX random-library-driver snapshot, builds a complete
  CPU CSR, and calls `Consume` (checksum). This is a benchmark, with no force,
  simulation, rendering, or entity update downstream. Its old measurements
  establish neither application benefit nor device execution time.
* `PublicBenchmarks/External/Boids/BoidsSphereConsumer.cs:47`:
  `SubmitSnapshot` reads ECS entities and moving target transforms. Its async
  callbacks resolve every ID into `MatchedEntities`. This optional observer
  displays counts; it does not feed original flocking/position updates. Its
  `N*Q` allocation and whole-capacity ID readback cannot be promoted as a
  production Boids requirement or as evidence for removing needed memberships.
* `PublicBenchmarks/External/Adapters/GpuSphereWorkloadAdapter.cs:38`:
  the public adapter reserves `N*batchSize` IDs. Reuse still confirms the index
  through an eight-byte readback and submits each batch separately. The recorded
  125.82 ms interval merges command construction, submission, wait and readback;
  it is not a kernel or PCIe measurement. The legacy API and old evidence remain
  unchanged in this investigation.

## Selected public consumer

[ArborX molecular dynamics example at the existing pin](https://github.com/arborx/ArborX/blob/375875dfb6b2e7631b1ba599cd26ee5c1e68ab90/examples/molecular_dynamics/example_molecular_dynamics.cpp)
has the required dataflow:

1. Lines 61–107 generate a 10×10×10, four-particle-per-cell lattice (4,000
   particles), spacing 1.7, and XorShift64 velocities with seed 5374857.
2. Lines 109–117 construct an ArborX index and query radius 3, excluding the
   original particle's index (not every duplicate coordinate).
3. Lines 119–155 consume all neighbor IDs in a float Lennard-Jones force sum.
4. Lines 184–194 use those forces to update velocity and then position with
   timestep 0.005 and mass 1.

The exact source and BSD notice are retained under `sources/`. Source SHA256:
`fc292b286270367980fbb1cc99a4944230138b064d4075c264472b7690828b16`.
The native harness extracts the actual setup/force/update blocks from that file;
it does not substitute random library-driver data. This is a **fixed-version
public single-step application example**, not a production molecular dynamics
simulation, multi-step stability study, or physical-accuracy validation.

The example also computes an unused potential-energy diagnostic. Its line 179
assigns `local_energy = ...` rather than accumulating; its value depends on
neighbor/reduction traversal and is never read. The selected minimal task is the
force-to-state dependency slice; it excludes this dead diagnostic consistently
in every arm. No claimed force/update behavior depends on it. The unmodified
example is retained and may be compiled separately as a source smoke check.

## Residency and output contract

The original force/update consumers run in the chosen Kokkos execution space;
they do not require CPU CSR. For the selected snapshot API, each task begins
with the same captured CPU positions and velocities. Each backend pays its own
copy/upload, index creation, neighbor traversal, force calculation, integration
and completion. It ends with complete updated positions, velocities and forces
ready in that backend's memory for its next simulation consumer. A GPU path
must establish actual device completion, not merely CPU submission. Full CPU
readbacks used only by the independent validator occur after the task timer;
they are reported separately. This boundary is not CPU-return latency.

The existing public adapter path necessarily materializes CPU CSR; all that
cost remains in its comparison. A GPU consumer can avoid this intermediate if
all members (including multiplicity), self-exclusion, float sphere boundary and
force/update semantics are preserved. Native backends need not upload their
output to Unity. One-time allocation and reusable-owner task timings are kept
separate; no infinite amortization or cross-task prepared index is assumed.

## Initial design, before diagnosis

* Baselines: original ArborX query + unchanged force/update blocks; reasonable
  CPU uniform-cell CSR + the same blocks; public SUMMIT rebuild-per-batch and
  index-reuse paths + a scalar port of the force/update consumer.
* Diagnose separately: CPU validation/encoding/upload, command recording,
  submission, explicit completion wait, readback/copy and force/update time.
  D3D12 timestamps are collected in diagnostic processes only, including an
  empty-scope control. No timestamp estimate is obtained by subtracting host
  intervals.
* Candidate choice follows diagnosis. A bounded GPU consumer is a possibility,
  not a frozen winner. Membership correctness and resource bounds gate it.
* Original default is the primary application case. Smaller/larger lattice,
  perturbed, boundary and duplicate-coordinate fixtures are separately labeled
  extensions. Distinct-ID coincident particles are membership tests only since
  the original force law is singular at zero separation.

The final protocol, evidence and report will state actual backend support,
resource use, limits and any NO-GO. Serial alone cannot establish superiority
over ArborX's best available parallel/GPU implementation.
