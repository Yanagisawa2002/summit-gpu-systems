# Dirty-range upload retained evidence

This directory retains compact evidence for the formal RTX 4090 full-versus-
dirty instance-state upload matrix at tested implementation commit
`7d9b2c94b22274586410a73ee0fb7252bd9f26ed`.

- `matrix-summary.csv`: exact logical-upload and CPU submission P95 results.
- `frame-tail-summary.csv`: CPU total/main P95 and P99 plus optional GPU-frame
  availability, computed from the retained ignored raw-frame run.
- `scenario-quality.csv`: process IDs and per-scenario completeness gates.
- `validation-summary.csv`: block-final full/dirty GPU state-hash parity.
- `device.json`: measured CPU, GPU, graphics API, and Unity version.
- `runner-receipt.json`: source, test, Player, and generated-report receipts.
- `SHA256SUMS.txt`: hashes of the compact retained files.

Formal correctness/provenance evidence is valid. The six `0/1/10%` sparse
cells reduced logical upload volume and CPU submission P95. The two explicit
`100%` dirty controls requested the same bytes as full upload and regressed CPU
submission P95; they are retained as negative controls for automatic policy
selection. Raw frames and the generated Player remain ignored.

See
`Docs/GPU_DRIVEN_INSTANCES_DIRTY_RANGE_UPLOAD_RTX4090_FORMAL_2026-08-30.md`
for interpretation and claim boundaries.
