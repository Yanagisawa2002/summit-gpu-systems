# Dirty staging restore crossover — RTX 4090 formal result

## Outcome

Commit `c77b80ec541072418ee3153c875aa97b8993b8cb` removes redundant CPU
state reconstruction in the focused dirty-upload benchmark. When an eight-slot
staging slot is reused, the old adapter restored every record changed during
that slot's prior use and then overwrote the current dirty records. The new
adapter retains two trusted sorted range buffers and restores only
`previousDirty - currentDirty`, using bulk `NativeArray.Copy` spans.

T06 closes **POSITIVE within its declared scope**. The sparse `0/1/10%` cells
retained strong same-run CPU-submission P95 savings, while the two explicit
`100%` dirty controls moved from `27.20%/30.71%` regressions in the prior
formal run to `0.83%/7.58%` improvements in this run. At `100%`, dirty still
uploads the same logical bytes and records the same upload-call count as Full,
so the measured policy boundary remains Full rather than a new claim that
dense dirty upload is universally better.

This optimization is deliberately narrow: it changes the formal benchmark's
deterministic staging-state reconstruction adapter. It does **not** change the
public `GpuInstanceStateUploader`, measure PCIe traffic, or add a production
scene update system.

| Workload | Full / dirty logical bytes | CPU submission P95 full / dirty | Dirty saving | Full / dirty staging records written |
|:---|---:|---:|---:|---:|
| 10K, 0% moving | 480,000 / 0 | 0.224510 / 0.064210 ms | 71.40% | 10,000 / 0 |
| 10K, 1% moving | 480,000 / 4,800 | 0.227105 / 0.075410 ms | 66.80% | 10,100 / 100 |
| 10K, 10% moving | 480,000 / 48,000 | 0.290115 / 0.146905 ms | 49.36% | 11,000 / 1,000 |
| 10K, 100% moving | 480,000 / 480,000 | 0.905815 / 0.898315 ms | 0.83% | 20,000 / 10,000 |
| 100K, 0% moving | 4,800,000 / 0 | 1.655705 / 0.062200 ms | 96.24% | 100,000 / 0 |
| 100K, 1% moving | 4,800,000 / 48,000 | 1.672320 / 0.148005 ms | 91.15% | 101,000 / 1,000 |
| 100K, 10% moving | 4,800,000 / 480,000 | 2.143560 / 0.850250 ms | 60.33% | 110,000 / 10,000 |
| 100K, 100% moving | 4,800,000 / 4,800,000 | 7.722805 / 7.137210 ms | 7.58% | 200,000 / 100,000 |

Logical bytes are bytes requested through Unity's upload API. Staging records
written count deterministic CPU-side state generation performed by this
benchmark. Neither metric is a measurement of driver staging, physical bus
traffic, or copy-engine bandwidth.

## Mechanism and correctness contract

The range generator already guarantees sorted, coalesced, non-overlapping
ranges. The new linear set-difference walk therefore:

1. rotates persistent current/previous range arrays without a timed allocation;
2. skips every prior record that the current update will immediately overwrite;
3. restores only uncovered spans with bulk native-array copies;
4. applies the current updates and reports exact state-record writes.

The difference routine has targeted coverage for partial overlap, including
the exact restored index set and the deliberately untouched overlap. A reused
slot with identical dirty topology verifies that restoration is skipped while
the final GPU state hash remains exact. Boundary and existing adapter tests
continue to cover empty and non-empty plans.

At `1/10/100%` moving, prior forced-Dirty evidence wrote exactly twice the
changed record count (`restore + overwrite`). The new run writes exactly once
the changed count. At `0%`, both versions write zero dirty records.

## Crossover and policy evidence

The retained PR7 calibration/holdout profile was generated from disjoint seeds
at commit `8f1bbe931e3d72ea5fc0ca99f846e06ed0e0cd81`, which is an ancestor of
this commit. Its accepted upload decisions are:

| Dirty ratio | Calibration / holdout total-CPU mean improvement | Accepted decision |
|---:|---:|:---|
| 0% | 12.27% / 12.05% | None |
| 1% | 10.20% / 11.57% | Dirty |
| 10% | 10.02% / 10.59% | Dirty |
| 100% | -0.05% / 0.19% | Full measured baseline |

The `100%` candidate failed the frozen `>=2%` mean-improvement and `>=55%`
paired-win requirements in calibration and holdout. The receipt records
`candidateAccepted=false`, `selectedDecisionSource=measured-baseline`, and
`selectedUploadMode=Full`. Current selector tests also require dense/range-
capacity fallback to Full.

T06 did not regenerate or silently retune that profile. The new benchmark
adapter changes CPU staging preparation, not the public uploader's equal-byte
dense tradeoff. A current-commit calibration/holdout regeneration belongs to
the selected-system T09 formal, where a new immutable profile must earn its
decision again.

## Evidence and provenance

- Unity `6000.5.2f1`, Windows 11, Direct3D 12, NVIDIA GeForce RTX 4090,
  seed `20260830`.
- Targeted EditMode tests: `3/3`; full D3D12 EditMode suite: `768/768`, zero
  failed or skipped.
- Formal scenarios: `8/8`; measured rows: `57,600/57,600`; exact block-final
  GPU state validations: `64/64`.
- Timed managed-allocation rows: `0`; staging-slot wait frames: `0`;
  measurement-readback bytes: `0`; all completion fences passed.
- Formal source began and ended clean at `c77b80e...`; source hashes and the
  freshly built Player payload remained stable through the run.
- Source snapshot SHA-256:
  `063C779E8D230B92D413F494E53F0C47C68128219CECAEEAE8CF57FB352CBA05`.
- Player payload SHA-256:
  `81C801E318706BEA4759BF71BD10A9C152A475A836D34D505572C0C66CD6E218`.
- Formal runner-config SHA-256:
  `F8A05111E9472750117738423BCA83EEE043A99C8EF27F66FF105F2F97A4C591`.
- Formal matrix-summary SHA-256:
  `059EFF3D523F6E71A7BB1F015A0D38DFE4B90CB274C1196C063A3350EED7DCAF`.
- Targeted/full EditMode XML SHA-256:
  `0951B3F1744AD68F85F73E9CCBBE7C000EF14A2116B7FFA7563617E7DBB8EBF9` /
  `463B78F69325AF9BDE8BF2EB12B9FF022FDFD46D6A5C956C00579B20A31286EA`.

Raw artifacts are retained under `artifacts/t06-dirty-formal-c77b80e` and test
receipts under `artifacts/t06-dirty-restore-c77b80e`. The preceding formal
negative control remains under the PR5 retained evidence and raw report tree.

## Evidence boundary

Allowed: on this frozen RTX 4090 protocol, overlap-aware benchmark staging
halved dirty-side state writes for reused non-empty ranges, preserved all exact
state validations, retained sparse dirty-upload wins, and eliminated the old
dense-control regression in the focused formal run.

Not allowed: presenting this adapter change as a new public upload algorithm;
claiming physical transfer reduction at `100%`; comparing old and new absolute
milliseconds as if they were a same-process A/B; claiming a frame-rate or GPU
latency improvement; or generalizing beyond this device, API, seeded layout,
and benchmark contract.

The old and new full baselines drifted across separate Player builds and runs.
Accordingly, the defensible performance comparison is Full versus Dirty within
each sealed run. The cross-run evidence is used only for the regression sign,
the exact record-count invariant, and the need for an explicit policy boundary.
