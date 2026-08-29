# GPU-driven visible-only retained evidence

This directory retains the compact, reviewed evidence for the formal RTX 4090
visibility sweep at commit `36d92e6dee55033ec0ea13caa00edf4db7468736`.

- `matrix-summary.csv`: accepted timing metrics and paired decision ranges.
- `scenario-quality.csv`: per-process completion and sample counts.
- `validation.csv`: warm-up/final CPU-oracle hashes for both variants.
- `device.json`: graphics device and native timestamp capabilities.
- `runner-receipt.json`: source, test, native plugin, and Player receipts.

The baseline is `CulledTail`; the candidate is
`VisibleOnly` with explicit discard-key count/scatter. Timed scopes contain no
benchmark-output readback. Visible membership uses the exact-count,
seeded-coprime permutation recorded in each scenario config. See
`Docs/GPU_DRIVEN_VISIBLE_ONLY_NVIDIA_RTX4090_FORMAL_2026-08-29.md` for the
protocol, interpretation, limitations, and reproduction command.

Raw frame rows and the generated Player are intentionally excluded from Git.
