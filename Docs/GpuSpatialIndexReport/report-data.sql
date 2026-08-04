-- Frozen presentation dataset for the portable GPU spatial-index report.
--
-- Reviewed values come from:
--   Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/summary.csv
--   Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/paired-deltas.csv
--   Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/*.txt
--   Reports/GpuSpatialIndex/formal-wave-amd-r9700-1280x720-30hz-3x900/quality-summary.txt
--
-- This file reproduces the frozen report-facing rows. It does not import or
-- recompute the raw benchmark files. Percentages use fractional storage
-- because the artifact renderer applies percent formatting.

-- Dataset: headline
SELECT
    0.28293 AS gpu_average_improvement,
    0.25951 AS gpu_p99_improvement,
    0.99859079 AS candidate_reduction,
    0 AS correctness_violations;

-- Dataset: crossover
WITH crossover(
    query_count,
    query_label,
    candidate_reduction,
    gpu_average_improvement,
    "Baseline-relative cost",
    "Index benefit",
    gpu_p99_improvement,
    direction
) AS (
    VALUES
        (256, '256 queries', 0.99858468, -0.13653, -0.13653, NULL, -0.00580, 'No reproducible whole-frame benefit'),
        (1024, '1,024 queries', 0.99859079, 0.28293, NULL, 0.28293, 0.25951, '3/3 paired improvements')
)
SELECT * FROM crossover ORDER BY query_count;

-- Dataset: paired_q1024
WITH paired_q1024(
    round,
    mode_order,
    brute_gpu_average_ms,
    index_gpu_average_ms,
    gpu_average_improvement,
    brute_gpu_p99_ms,
    index_gpu_p99_ms,
    gpu_p99_improvement
) AS (
    VALUES
        (1, 'off -> brute -> index', 22.433, 18.117, 0.192395132171355, 28.156, 23.670, 0.159326608893309),
        (2, 'brute -> index -> off', 17.090, 10.855, 0.364833235810415, 28.065, 17.208, 0.386851950828434),
        (3, 'index -> off -> brute', 42.626, 30.566, 0.282925913761554, 60.480, 44.785, 0.259507275132275)
)
SELECT * FROM paired_q1024 ORDER BY round;

-- Dataset: quality
WITH quality(ordinal, quality_check, result, confidence) AS (
    VALUES
        (1, 'Process, timing, and production gate', '9/9 processes; 900/900 samples; exact production signature', 'High'),
        (2, 'GPU query-count equivalence', '6/6 workload runs had zero per-query mismatch; CPU-oracle hash 17954676537386309527', 'High'),
        (3, 'Indexed structure invariants', '3/3 indexed runs passed sort, cell-range, bounds, XOR, sum, and unique-count gates', 'High'),
        (4, 'Measurement readback', '0 bytes/frame; warmup validation read back 32 bytes', 'High'),
        (5, '1,024-query performance direction', 'GPU average and P99 improved in 3/3 pairs', 'Medium'),
        (6, 'Scheduled update delivery', 'Raw reports contain nonzero drops, up to 321', 'Guardrail'),
        (7, 'Exact whole-frame effect size', 'Off-baseline drift 86.448%', 'Low-to-medium'),
        (8, 'q256/q1024 operating boundary', 'Successive Player builds; q256 binary hash not retained', 'Low'),
        (9, 'NVIDIA validation', 'Not tested', 'Unverified')
)
SELECT * FROM quality ORDER BY ordinal;
