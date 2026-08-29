# GPU-driven visible-only retained evidence

This directory retains the compact, reviewed evidence for the formal RTX 4090
visibility sweep at commit `f9f1f56d332c2642a2b916e6267b1f2e1c16d1bb`.

- `matrix-summary.csv`: accepted timing metrics and paired decision ranges.
- `scenario-quality.csv`: per-process completion and sample counts.
- `validation.csv`: warm-up/final CPU-oracle hashes for both variants.
- `device.json`: graphics device and native timestamp capabilities.
- `runner-receipt.json`: source, test, native plugin, and Player receipts.

The baseline is `CulledTail`; the candidate is
`VisibleOnly` with explicit discard-key count/scatter. Timed scopes contain no
benchmark-output readback. See
`Docs/GPU_DRIVEN_VISIBLE_ONLY_NVIDIA_RTX4090_FORMAL_2026-08-29.md` for the
protocol, interpretation, limitations, and reproduction command.

Raw frame rows and the generated Player are intentionally excluded from Git.
