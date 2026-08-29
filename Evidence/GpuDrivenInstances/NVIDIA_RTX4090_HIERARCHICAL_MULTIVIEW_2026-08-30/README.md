# RTX 4090 hierarchical multi-view evidence

This directory retains the compact, reviewable evidence for commit
`cf5b21b557356b522c920d591844d73ebf4299f5`.

The formal Unity/D3D12 matrix used 1,048,576 instances, four views, 64-instance
contiguous clusters, four frozen visibility cells, two ABBA/BAAB super-rounds,
and 900 samples per block. All 36,000 native timestamp rows were ready, all
flat/hierarchical output and hierarchy-statistic validations passed, and timed
main-thread allocation/readback stayed zero.

The hierarchy reduced candidate instance-view work by 95%, 75%, 25%, and 0%
at 5%, 25%, 75%, and 100% visibility. Native GPU mean improved in all four
cells, but enqueue P99 regressed beyond the frozen 5% guardrail in all four.
The formal decision is therefore conservative: keep the hierarchy as an
explicit opt-in and do not make it the default until policy selection has a
validated CPU-submission-tail model.

Raw 36,000-row frame CSVs and the freshly built Player remain ignored under
`TestResults/` and `Builds/`. The retained summaries, test receipt, device
identity, and hashes are sufficient to audit the reported claims.
