# Adaptive CSR selector retained evidence

This compact bundle retains the sealed RTX 4090 / D3D12 five-cell holdout for
tested commit `ab59ea6117c87a4464befa87775a1b1019dce458`.

- `selector-summary.csv` contains the selected winner, offline prediction,
  material metrics, and strict pairwise-tail result for every cell.
- `device.json` records the numeric device/API identity and driver.
- `runner-receipt.json` records the frozen protocol, evidence counts, source,
  Player, shader, and report hashes.
- `SHA256SUMS.txt` hashes the retained compact files.

The material selector claim passed `5/5`. The additional strict pairwise-tail
claim passed `4/5`; C16 uniform had a positive median P99 but one `-11.06%`
pair. Raw frames and the generated Player remain outside Git under
`artifacts/t08-adaptive-holdout-ab59ea6`.

See
`Docs/GPU_ADAPTIVE_BINNING_NVIDIA_RTX4090_FORMAL_2026-08-31.md` for the
protocol, interpretation, invalid-attempt history, and claim boundary.
