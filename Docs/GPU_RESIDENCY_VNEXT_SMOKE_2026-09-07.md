# Residency vNext correctness and smoke evidence, 2026-09-07

Implementation branch: `codex/r9700-vnext-residency-20260907`, based on
`3faad555c6044f966403261d627d8d6ac232d2d9`. Tests below ran on the working tree
before its implementation commit. The smoke player's existing buildCommit field
therefore records the baseline HEAD, not a claim that the baseline contains this
implementation. These are correctness/smoke records, not frozen performance evidence.

- Unity 6000.5.2f1, Windows 11 10.0.26200, AMD Radeon AI PRO R9700,
  Direct3D12 feature level 12.2. Native timestamp backend available, ABI 2.
- Serialized DX12 EditMode filter `Summit.GpuResidencyManager`: **15 passed,
  0 failed, 0 skipped**. Includes CPU planner regression, zero steady-state managed
  allocation, queued-frame snapshot/in-flight pin protection, out-of-order record
  rejection, payload-record retry, empty demand, budgeted GPU rebuild digests, and
  DX12 upload dispatch ceiling/overflow boundaries (the boundary addition was revalidated
  in a final 15-test DX12 run after the player smoke).
- Standalone .NET 10.0.10 harness: atomic input/frame retry, overflow boundaries,
  budgets, independent delta/mapping oracle, 180-frame scan/heap differential trace,
  prefetch reuse, oversubscribed fairness, priority aging, and teleport checks passed.
- CPU smoke: 384/4096/32768 slots, scan and heap, forward/reverse process orders,
  prefilled caches, 16 warmup + 8 measured frames per case. **0 managed allocated
  bytes** in all measured plan/metrics/retire loops; paired outputs/volumes match.
- New GPU scan/heap smoke entry point built and ran: 384 slots, 4096 virtual pages,
  256 requested pages, 256 points/page, 2 warmup + 64 measured frames per block,
  AB/BA (4 blocks, 256 samples). **8/8 CPU digest validations passed**, timestamp
  warmup ready, no measurement readback. Unity planning allocation sum/max: **0 B**.
- Pair summarizer validated both AB and BA pairs, identical per-sample input/hit/miss/
  logical upload volume and matching block hashes. No performance gate or default
  promotion was applied. The short timing samples vary with ordering; they are not
  evidence of a formal GPU speedup.
- `Tools/Test-RepositoryLayout.ps1` passed (8 packages, 127 portable shader/C# files).

Local raw evidence (ignored by Git):
`TestResults/residency-dx12-final.xml`, `TestResults/editmode-unity.log`,
`Reports/ResidencyPlanner/results-0.json`, `results-1.json` and metadata sidecars,
`Reports/ResidencyLruSmokeFinal/lru-comparison.json` and its scenario directory.
All Unity/build/GPU commands used the shared `Invoke-SerializedValidation.ps1` lock.

Not run: full formal CPU/GPU matrices, long fairness/stress traces, large-slot GPU
performance sweeps, or cross-queue external-consumer stress. Formal timing is deferred
until integration and the user's evaluation decision. Cross-queue correctness relies
on the documented fence chain; the executed queued-frame tests use the graphics queue.
The claim remains an application-level GraphicsBuffer cache; no sparse-resource claim.
