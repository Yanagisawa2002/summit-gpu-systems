# Adaptive Direct/Radix CSR selection — RTX 4090 formal result

## Outcome

T08 closes **POSITIVE for the frozen five-cell selector claim, with one
explicit stricter-tail limitation**. On an NVIDIA GeForce RTX 4090 / D3D12,
the disjoint holdout retained three Radix winners at `N=1,048,576, C=16` and
two Direct winners at `C=4,096/65,536`. Offline selector replay predicted the
accepted forced-backend winner in `5/5` cells. Every cell passed the frozen
material gate: at least `5%` paired-median GPU-average improvement,
`0.005 ms` absolute reduction, `6/8` winning average-time pairs, same-sign
AB/BA medians, and median GPU P99 improvement no worse than `-2%`.

This is not a universal backend threshold. It is a device-bound, exact
workload-cell policy. `RecordAdaptive` CPU record/submission overhead was not
timed and none of the forced-backend percentages may be described as facade
speedup.

| Holdout cell | Selected backend | GPU-average speedup | Absolute reduction | GPU P99 speedup | Avg-time pairs | AB / BA sign | Decision |
|---|:---:|---:|---:|---:|:---:|:---:|:---:|
| 1.05M / C16 / single-bin, unseen key 5 | Radix | 67.68% | 1.0484 ms | 21.56% | 8/8 | + / + | GO |
| 1.05M / C16 / hotset4 | Radix | 56.17% | 0.6170 ms | 17.73% | 8/8 | + / + | GO |
| 1.05M / C16 / uniform | Radix | 45.69% | 0.4351 ms | 8.91% | 8/8 | + / + | GO; strict tail mixed |
| 1.05M / C4096 / uniform | Direct | 88.29% | 1.5308 ms | 28.57% | 8/8 | + / + for Direct | GO |
| 1.05M / C65536 / uniform | Direct | 93.84% | 1.9156 ms | 37.15% | 8/8 | + / + for Direct | GO |

The signed CSV convention is “positive means Radix faster.” The table converts
the two Direct cells to Direct-relative positive improvements so that every
row reports the selected winner consistently.

## What was consolidated

`GpuAdaptiveSpatialBinner` was already the public Direct/Radix facade, so T08
did not delete `com.summit.gpu-direct-binning`. GPU-driven hierarchy and sensor
packages still use its precounted/direct specialized entry points. The old
standalone Direct-vs-reference microbenchmark remains a genuine 3/3 NO-GO;
that is separate from Direct winning as one backend of this crossover.

The bounded consolidation made these changes:

- one facade now owns one `GpuPrimitives` arena and injects it into both
  backends; forced executions through one facade must not overlap;
- benchmark Direct/Radix A/B records through the same facade and uses the same
  output buffers and canonical CSR oracle;
- logical `UnionScratchBytes` counts shared primitive scratch once, while
  isolated Direct/Radix path properties still include the arena they use;
- selector schema v3 stores one to eight copied, non-overlapping Radix cells;
- workload size and concentration always match exactly; a single-bin cell may
  accept any caller-validated key, while the hint must still provide the real
  in-range key; and
- invalid schema, device, API, primitive backend, marker mode, domain, hint,
  or overlapping cell definitions fail closed to Direct.

At the formal 1.05M capacity, sharing removed `17,334,404` logical bytes from
the complete facade's buffer union. The calculated union fell by
`33.19%–33.69%` across C16–C65536. These are exact logical buffer payloads,
not measured VRAM allocation and not a performance result.

## Disjoint calibration and holdout

The short calibration used six cells, independent seeds `20261111–20261116`,
four super-rounds, and 240 samples per block. Radix was accepted in four cells
(`262K/C16 single-bin` and all three `1.05M/C16` distributions); Direct was
accepted at `1.05M/C4096` and `C65536` uniform. That satisfied the frozen
trigger requiring at least two accepted cells per backend. Calibration values
were used only to freeze the policy surface and are not portfolio numbers.

The formal holdout then used new seeds `20261221–20261225`, four super-rounds,
and 900 samples per block. The single-bin seed changed the generated key from
calibration key 8 to holdout key 5. The schema-v3 any-valid-key rule still
selected Radix and passed correctness, which is evidence against merely
memorizing the calibration key. The other Radix cells are exact
`N/C/concentration` registrations; Direct is the fail-closed default outside
them.

## Strict pairwise-tail boundary

An additional, stricter diagnostic required the selected winner to win GPU
P99 in at least `7/8` pairs and keep its worst individual pair at or above
`-10%`. Four cells passed. The C16 uniform cell won P99 in `7/8` pairs but its
worst pair was `-11.06%`, so:

- `selectorPolicyClaimUsable=1` under the preregistered material/median-P99
  gate;
- `selectorPolicyTailClaimUsable=0` under the extra worst-pair tail gate; and
- no claim may say that every cell or every pair improved tail latency.

The cell's median GPU P99 still improved `8.91%`, so the frozen T08 gate was
not retroactively changed. No threshold was retuned and no additional run was
launched to erase this negative result.

## Correctness and provenance

- Final evidence commit: `ab59ea6117c87a4464befa87775a1b1019dce458` on
  `codex/gpu-adaptive-csr-consolidation-20260831`.
- Unity `6000.5.2f1`, Windows 11, Direct3D 12, NVIDIA GeForce RTX 4090
  (`vendor 0x10DE`, `device 0x2684`), driver `32.0.15.9186`.
- Fresh formal EditMode suite: `774/774`, zero failed, skipped, or
  inconclusive.
- Formal matrix: `81,000/81,000` raw rows; `20/20` exact CPU-oracle/hash
  validations; `0` timed measurement-readback bytes.
- All five cells used eight same-process, counterbalanced AB/BA pairs with
  complete native D3D12 timestamp results.
- Source started and ended clean; source hashes and the freshly built
  281-file Player payload remained stable through the matrix.
- Source snapshot SHA-256:
  `B6AB2C58F1075F5C6B1C9F84B8817BC8F6AF95739763A7C0C2CDF41D089CD9A6`.
- Player payload SHA-256:
  `1AF7C3DC4D68D540314E12188D88F8AC0F48B824CA3E0F08329406EC0626EF83`.
- Direct/Radix shader SHA-256:
  `9D6CC10ACD0F07090DF5FC887FA2C48ECD483062983C82AFA3ADEBFB3C013915` /
  `28BD143F4EF018ECB1F168834F1ACEC5D4E238E794778F22FE18CAC8D8CA7D2E`.
- Runtime API SHA-256:
  `DE29E468E8CC9F2C520952B34C7A717177CE785A5E091770572F09555A6539CF`.
- Final runner-config / matrix-summary SHA-256:
  `9E5F3941D57EBEC0093461EF1B8FD42C78D07DBF240072E4CF2507F73FA7E026` /
  `68C9E51896565A406B4DA4F16541A6C793DACD682787FD307D6D0359FE90F5B8`.
- Formal EditMode XML SHA-256:
  `EF804A8C463A0E6944C5F8275E5F938FA469A067480BF0A8905A395B88EF857E`.

Raw evidence is retained outside the Git worktree under
`artifacts/t08-adaptive-holdout-ab59ea6`.

The preceding `artifacts/t08-adaptive-holdout-10ec7c8` attempt completed all
GPU cells but is **invalid formal evidence**. PowerShell 7 deserialized the
runner's ISO timestamp string to `DateTime`; the old summarizer converted it
back with locale formatting and falsely rejected an identical timestamp. The
hash and UTC ticks matched, but the summarizer fix was not part of that
attempt's source snapshot. Commit `ab59ea6` compares UTC ticks, and the clean
rerun above is the only promoted result.

## Evidence boundary

Allowed: on this RTX 4090/D3D12/Unity build and exact five-cell holdout, an
offline, device-bound policy replay selected the accepted Direct/Radix forced
winner in `5/5` cells. Selected GPU-average improvements were
`45.69%–93.84%`, with selected median GPU P99 improvements of
`8.91%–37.15%`, exact correctness, and explicit Direct fallback outside the
three registered Radix cells.

Not allowed: claiming a universal threshold; claiming all NVIDIA GPUs,
drivers, APIs, sizes, distributions, or keys; claiming `RecordAdaptive` itself
delivered these percentages; claiming its CPU overhead is zero; claiming
measured VRAM savings; hiding the C16-uniform strict-tail miss; or converting
GPU-scope timings into FPS/frame-time claims. T09 owns selected-system CPU
record/submission overhead and combined-policy evidence.
