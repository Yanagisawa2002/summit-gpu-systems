# NVIDIA RTX 4090 autotuning evidence

Formal source commit: `43ffac93ff549afd39675b34efb958991a8f2da9`

This directory retains the accepted device profile, disjoint-round evaluation, summaries, device metadata, correctness rows, and run status. Raw per-frame data, generated Players, and session files containing machine-local absolute paths remain excluded from Git.

Excluded session artifact hashes are preserved without copying their path-bearing contents:

- `config.json`: `C36D10A8866F17D0DC5337A4B0708009BF7F077FF2293DECD37CBB39A7685AA2`
- `runner-config.json`: `CB13F1D589FF6E4895EE14CB892552B33F11EF1B6341444B933A92092530B882`

See `../../../Docs/GPU_CROSS_VENDOR_AUTOTUNING_NVIDIA_RTX4090_REPORT_2026-08-29.md` for the reviewed interpretation.
