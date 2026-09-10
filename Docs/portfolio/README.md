# Portfolio figure

This is the historical NYCGIS representation case. Its logical output and
dedicated GPU-scope results do not measure the current integration or whole
engine frames. Start with the current [complete-task case and adoption path](../WHOLE_TASK_DECISIONS.md).
The [September 10 external task comparison](../EXTERNAL_ACTUAL_RESULTS_2026-09-10.md)
has its own real input, GPU correctness and cross-backend cost evidence; none of
those measurements are used to redraw this historical figure.

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
