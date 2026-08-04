# Packed SoA and Producer-Consumer Fusion V2: AMD R9700

## Decision

The frozen v2 formal holdout produced a mixed result. The 1,048,576-element
workload passed the predeclared engineering gate; the 262,144-element workload
improved every GPU-average pair but failed the 0.005 ms absolute-reduction gate.
Therefore a cross-workload GPU performance claim is not usable.

| Workload | GPU average | Absolute average | GPU P99 | GPU-average wins | Classification |
|---|---:|---:|---:|---:|---|
| 262,144 elements / 64 queries | 6.22% faster | 0.00223 ms lower | 5.81% faster | 8/8 | neutral or inconclusive |
| 1,048,576 elements / 256 queries | 7.16% faster | 0.00667 ms lower | 9.74% faster | 8/8 | engineering gate passed |

All 439 EditMode tests passed, all correctness rows passed, all measurement
frames performed zero workload readback, and evidence validity was accepted.

## What changed

- Replaced expanded per-element AoS payloads with four pair-packed 16-bit SoA
  streams for X, Y, Z, and quantized intensity.
- Derived spatial keys and identity values at use sites instead of materializing
  separate buffers.
- Fused sample production with the Direct CSR count stage.
- Reused exclusive bin offsets as in-place atomic scatter cursors, leaving
  cumulative bin ends for consumers and removing a separate write-head buffer.
- Kept the WaveOps primitive backend and deterministic CPU/GPU validation.

The algorithmic accounting shows 60% fewer producer logical writes, 50% fewer
pipeline-element materialized writes, and 50% fewer addressed spatial-build
reads. These are typed-operation models, not measured DRAM or PCIe traffic.

## Method

- AMD Radeon AI PRO R9700, Direct3D 12, driver 32.0.31035.1003.
- Four super-rounds and eight position-balanced A/B pairs per workload.
- 60 warm-up and 900 measured frames per block, plus two preconditioning blocks.
- Native DX12 timestamp interval on the main graphics command list.
- Exact packed-sample, CSR membership, and digest validation outside timing.
- Frozen gate required positive paired behavior, at least 5% relative and
  0.005 ms absolute average reduction, and GPU P99 no worse than -2%.

## Claim boundary

It is safe to cite the 1,048,576-element result with the synthetic-workload,
AMD-only, and kernel-region qualifiers. It is not safe to state that the
optimization has a proven cross-workload benefit, measured bandwidth reduction,
end-to-end sensor latency improvement, FPS gain, live-sensor validation, or
NVIDIA validation.

## Resume-ready wording

Implemented pair-packed 16-bit SoA sensor attributes and producer/CSR fusion in
a GPU-resident DX12 pipeline, deriving keys at use sites and reusing exclusive
offsets as in-place scatter cursors. On an AMD Radeon AI PRO R9700, the frozen
1.05M-element holdout improved GPU average by 7.16% and GPU P99 by 9.74% with
8/8 paired wins, while reducing modeled producer writes by 60% and materialized
pipeline writes/addressed build reads by 50%. A 262K holdout improved 8/8 pairs
but remained neutral under the predeclared absolute-time gate, so no universal
cross-workload speedup is claimed.
