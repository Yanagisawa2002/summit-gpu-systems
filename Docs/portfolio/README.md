# Portfolio figure

The before/after flow explains the change in representation, with the reported logical output sizes shown on a zero-based scale.

## Reproduce

From the repository root:

```bash
python -m pip install -r Docs/portfolio/requirements.txt
python Docs/portfolio/render.py
```

The renderer verifies source SHA-256 hashes (CRLF normalized to LF) before plotting the reviewed values in `figure.json`. If a source changes, review and refresh the snapshot before regenerating. It writes SVG and PNG with matching content.

## Sources

- [Docs/GPU_PERFORMANCE_ENGINEERING_PORTFOLIO_INDEX_2026-07-31.md](../../Docs/GPU_PERFORMANCE_ENGINEERING_PORTFOLIO_INDEX_2026-07-31.md)

The flow/memory/timing illustrations are schematics. Only explicitly labeled measurements represent recorded experiments. Confidence intervals are copied from source reports, not recomputed.
