# Historical NYCGIS portfolio figure

[Rendering case and source map](../NO_COPY_VISIBLE_TILES.md) · [Evidence index](../../Evidence/README.md) · [Run or inspect](../RUNNING.md)

This figure explains one representation change: pass visible-tile descriptors instead of copying every visible index. The before/after flow is schematic; the reported logical-output payloads use a zero-based scale.

Its 385.7 MB and 8.04 MB values and dedicated single-camera timing note belong to the historical NYCGIS experiment. They do not measure the current integration checkout, the query planner, total physical VRAM, DRAM traffic or whole-engine frame time.

The presentation refresh does not change `figure.json`, its source report, the renderer, or the retained SVG/PNG. The [historical identity note](../RemediationIntegration20260908.md#historical-evidence-identity) distinguishes the report pin from original measured-source/binary attestation.

## Reproduce the figure, not the experiment

From the repository root:

```bash
python -m pip install -r Docs/portfolio/requirements.txt
python Docs/portfolio/render.py
```

The renderer verifies source SHA-256 hashes after CRLF-to-LF normalization before plotting the reviewed values in `figure.json`. If a source changes, review and refresh the snapshot before regenerating. It writes SVG and PNG with matching content. Successful figure generation is not a new GPU measurement.

## Sources and separate experiments

[Historical GPU performance portfolio index](../GPU_PERFORMANCE_ENGINEERING_PORTFOLIO_INDEX_2026-07-31.md) supplies this figure's recorded values.

The [complete-task decision case](../WHOLE_TASK_DECISIONS.md), [external baseline](../EXTERNAL_ACTUAL_RESULTS_2026-09-10.md) and [sphere reuse comparison](../SPHERE_REUSE_RESULTS_2026-09-10.md) retain their own input, output and timing contracts. None of their measurements is used to redraw this historical figure.

Only explicitly labeled measurements represent recorded experiments. This page does not recompute confidence intervals or certify missing source/build evidence.
