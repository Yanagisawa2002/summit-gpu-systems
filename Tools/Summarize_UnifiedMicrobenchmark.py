#!/usr/bin/env python3
"""Frozen five-process paired microbenchmark analysis (stdlib only).
Frames are observations within blocks, never independent CI replicates.
"""
import argparse
import csv
import hashlib
import json
import math
import statistics as st
from collections import defaultdict
from pathlib import Path

METRICS = ('gpuTotalMs', 'gpuIndexMs', 'gpuQueryMs', 'cpuRecordMs', 'cpuSubmitMs', 'cpuRecordSubmitMs')


def mean(v):
    return st.fmean(v)


def cv(v):
    return st.stdev(v) / mean(v) if len(v) > 1 and mean(v) > 0 else 0.0


def percentile(v, p):
    a = sorted(v)
    x = (len(a) - 1) * p
    lo = int(x)
    return a[lo] + (a[min(lo + 1, len(a) - 1)] - a[lo]) * (x - lo)


def paired_ci(log_ratios):
    if len(log_ratios) != 5:
        raise ValueError('Exactly five process aggregates are required')
    center = mean(log_ratios)
    half = 2.7764451051977987 * st.stdev(log_ratios) / math.sqrt(5)
    return math.exp(center), math.exp(center - half), math.exp(center + half)


def compare(groups, baseline, candidate, metric):
    process_logs, p95_logs, p99_logs = [], [], []
    arm_process_means = {baseline: [], candidate: []}
    block_cvs, raw_cvs, drifts = [], [], []
    paired_blocks = []
    for process in range(5):
        blocks = sorted({b for (p, b, a) in groups if p == process and a == baseline})
        if not blocks:
            raise ValueError('Missing process baseline')
        local = {baseline: [], candidate: []}
        log_mean, log_95, log_99 = [], [], []
        for block in blocks:
            a = [r[metric] for r in groups[(process, block, baseline)]]
            b = [r[metric] for r in groups[(process, block, candidate)]]
            if not a or len(a) != len(b) or min(a + b) <= 0:
                raise ValueError('Missing, unequal or nonpositive paired observations')
            local[baseline].append(mean(a)); local[candidate].append(mean(b))
            log_mean.append(math.log(mean(a) / mean(b)))
            log_95.append(math.log(percentile(a, .95) / percentile(b, .95)))
            log_99.append(math.log(percentile(a, .99) / percentile(b, .99)))
            paired_blocks.append(dict(process=process, block=block, baselineMean=mean(a), candidateMean=mean(b),
                                      speedRatio=math.exp(log_mean[-1]), p95Ratio=math.exp(log_95[-1]), p99Ratio=math.exp(log_99[-1])))
        process_logs.append(mean(log_mean)); p95_logs.append(mean(log_95)); p99_logs.append(mean(log_99))
        for arm in (baseline, candidate):
            arm_process_means[arm].append(mean(local[arm]))
            block_cvs.append(cv(local[arm]))
            raw_cvs.append(cv([r[metric] for (p, _, a), rows in groups.items() if p == process and a == arm for r in rows]))
        drifts.append(abs(local[baseline][-1] / local[baseline][0] - 1))
    for arm in (baseline, candidate):
        block_cvs.append(cv(arm_process_means[arm]))
    drifts.append(abs(arm_process_means[baseline][-1] / arm_process_means[baseline][0] - 1))
    ratio, lower, upper = paired_ci(process_logs)
    p95, p95_lower, p95_upper = paired_ci(p95_logs)
    p99, p99_lower, p99_upper = paired_ci(p99_logs)
    stable = max(block_cvs) <= .05 and max(drifts) <= .15
    failures = []
    if max(block_cvs) > .05: failures.append('CV > 5%')
    if max(drifts) > .15: failures.append('baseline drift > 15%')
    if lower <= 1: failures.append('mean speed 95% CI includes no gain')
    if p95 < 1.01: failures.append('p95 speed ratio < 1.01')
    verdict = 'confirmed-per-cell' if not failures else 'stable-regression' if stable and upper < 1 else 'inconclusive'
    return dict(baseline=baseline, candidate=candidate, metric=metric, baselineMeanMs=mean(arm_process_means[baseline]),
                candidateMeanMs=mean(arm_process_means[candidate]), speedRatio=ratio, ci95Low=lower, ci95High=upper,
                p95SpeedRatio=p95, p95Ci95Low=p95_lower, p95Ci95High=p95_upper,
                p99SpeedRatio=p99, p99Ci95Low=p99_lower, p99Ci95High=p99_upper,
                maxUnitCv=max(block_cvs), maxObservationCv=max(raw_cvs), maxBaselineDrift=max(drifts),
                verdict=verdict, gateFailures='; '.join(failures), processLogRatios=process_logs, pairedBlocks=paired_blocks)


def write_csv(path, rows):
    with path.open('w', newline='', encoding='utf-8-sig') as f:
        if not rows: return
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader(); w.writerows(rows)


def summarize(source, destination):
    destination.mkdir(parents=True, exist_ok=False)
    provenance = json.loads((source / 'provenance.json').read_text(encoding='utf-8-sig'))
    if provenance['status'] != 'complete' or provenance['phase'] != 'Formal':
        raise ValueError('Only a complete Formal run can produce confirmation statistics')
    reports, rows = [], []
    for process in range(5):
        r = json.loads((source / f'process-{process}.json').read_text(encoding='utf-8-sig'))
        c = r['configuration']
        if r['status'] != 'complete' or c['processIndex'] != process or c['phase'] != 'formal' or len(r['validation']) != 18:
            raise ValueError('Incomplete process or correctness failure')
        if (c['warmup'], c['querySamples'], c['indexSamples']) != (8, 30, 24):
            raise ValueError('Configuration differs from frozen protocol')
        if c['sourceCommit'] != provenance['sourceCommit']:
            raise ValueError('Source identity mismatch')
        reports.append(r)
        for row in r['rows']:
            if row['kind'] == 'control': continue
            for native in row['native']:
                if native['status'] != 'Ready' or native['frequency'] <= 0 or native['endTicks'] < native['beginTicks']:
                    raise ValueError('Invalid native timestamp')
                delta = (native['endTicks'] - native['beginTicks']) * 1000 / native['frequency']
                if not math.isclose(delta, native['elapsedMs'], rel_tol=1e-8, abs_tol=1e-10):
                    raise ValueError('Native tick conversion mismatch')
            if row['measured']:
                row['cpuRecordSubmitMs'] = row['cpuRecordMs'] + row['cpuSubmitMs']
                rows.append(row)
    identities = {(r['device'], r['driver'], r['unityVersion'], r['graphicsApi']) for r in reports}
    if len(identities) != 1: raise ValueError('Mixed hardware/runtime identity')
    cells = defaultdict(lambda: defaultdict(list))
    for row in rows:
        cells[(row['kind'], row['scenario'])][(row['process'], row['block'], row['backend'])].append(row)
    if len(cells) != 18: raise ValueError('Fixed 12+6 matrix incomplete')
    comparisons, aggregates = [], []
    for (kind, scenario), groups in sorted(cells.items()):
        arms = ['CellSerial', 'PointChunks', 'PointChunksWave', 'BatchedPointScanWave'] if kind == 'query' else [
            'full-direct-waveops', 'incremental-original', 'incremental-gpu-driven']
        nblocks, nsamples = (4, 30) if kind == 'query' else (6, 24)
        if len(groups) != 5 * nblocks * len(arms): raise ValueError('Unbalanced block/arm count')
        for process in range(5):
            for block in range(nblocks):
                for arm in arms:
                    rr = groups[(process, block, arm)]
                    if len(rr) != nsamples or sorted(x['frame'] for x in rr) != list(range(8, 8 + nsamples)):
                        raise ValueError('Dropped or repeated frames')
            schedules = [[groups[(process, block, arm)][0]['order'] for arm in arms] for block in range(nblocks)]
            for arm in range(len(arms)):
                for position in range(len(arms)):
                    if sum(s[arm] == position for s in schedules) != nblocks // len(arms):
                        raise ValueError('Unbalanced arm positions')
        for (process, block, arm), rr in sorted(groups.items()):
            for metric in METRICS:
                if kind == 'query' and metric == 'gpuIndexMs': continue
                vals = [r[metric] for r in rr]
                aggregates.append(dict(kind=kind, scenario=scenario, process=process, block=block, backend=arm, metric=metric,
                                       n=len(vals), meanMs=mean(vals), p50Ms=percentile(vals,.5), p95Ms=percentile(vals,.95), p99Ms=percentile(vals,.99), cv=cv(vals)))
        pairs = [('CellSerial', arm) for arm in arms[1:]] if kind == 'query' else [
            ('full-direct-waveops','incremental-original'), ('full-direct-waveops','incremental-gpu-driven'),
            ('incremental-original','incremental-gpu-driven')]
        for baseline, candidate in pairs:
            for metric in METRICS:
                if kind == 'query' and metric in ('gpuIndexMs', 'gpuQueryMs'): continue
                result = compare(groups, baseline, candidate, metric)
                comparisons.append(dict(kind=kind, scenario=scenario, **result))
    scalar_comparisons = [{k:v for k,v in c.items() if k not in ('processLogRatios','pairedBlocks')} for c in comparisons]
    write_csv(destination / 'comparisons.csv', scalar_comparisons)
    write_csv(destination / 'block-statistics.csv', aggregates)
    scalar_rows = [{k:v for k,v in row.items() if k not in ('indexState','queryDigests','native')} for row in rows]
    write_csv(destination / 'measurements.csv', scalar_rows)
    summary = dict(schemaVersion=1, status='complete', sourceCommit=provenance['sourceCommit'], sourceDigest=provenance['sourceDigest'],
                   device=list(identities)[0], independentProcesses=5, measuredRows=len(rows), matrixCells=len(cells),
                   methods='Paired block arithmetic means -> log ratios -> mean within each process; t(4) 95% CI over five process log ratios. Tail ratios use within-block empirical quantiles then the same aggregation. CV gates on within-process block means and across-process means for both arms; raw observation CV also disclosed. Baseline drift is maximum first/last chronological block change within each process and first/last process mean change. Unadjusted per-cell CIs, no familywise guarantee.',
                   tailLimit='30 query or 24 index observations per block; p99 is interpolation near the maximum and has insufficient rare-tail support. No p99 improvement claim.',
                   comparisons=comparisons)
    (destination / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    lines = ['# Frozen SUMMIT microbenchmark results', '', f"Source: `{summary['sourceCommit']}`. Five independent Release Player processes; {len(rows)} measured observations in 18 fixed cells.", '',
             summary['methods'], '', summary['tailLimit'], '',
             'GPU intervals include every dispatch/reset/indirect argument update in the recorded operation. CPU waits are not GPU time; empty controls are retained without subtraction. Upload, setup, async readback request/wait/copy, CPU record and submission are separate in raw evidence.', '',
             '## Query candidate versus CellSerial', '', '| Cell | Baseline ms | Candidate ms | Speed ratio [95% CI] | p95 ratio | Max unit CV | Drift | Verdict |', '|---|---:|---:|---|---:|---:|---:|---|']
    selected = [c for c in comparisons if c['kind']=='query' and c['candidate']=='BatchedPointScanWave' and c['metric']=='gpuTotalMs']
    selected += [c for c in comparisons if c['kind']=='index' and c['candidate']=='incremental-gpu-driven' and c['metric']=='gpuTotalMs']
    for i,c in enumerate(selected):
        if i==12:
            lines += ['', '## Index complete operation, including unchanged CellSerial consumer', '', '| Cell / baseline | Baseline ms | Candidate ms | Speed ratio [95% CI] | p95 ratio | Max unit CV | Drift | Verdict |', '|---|---:|---:|---|---:|---:|---:|---|']
        label = c['scenario'] + (' / '+c['baseline'] if c['kind']=='index' else '')
        lines.append(f"| {label} | {c['baselineMeanMs']:.6f} | {c['candidateMeanMs']:.6f} | {c['speedRatio']:.3f} [{c['ci95Low']:.3f}, {c['ci95High']:.3f}] | {c['p95SpeedRatio']:.3f} | {c['maxUnitCv']:.1%} | {c['maxBaselineDrift']:.1%} | {c['verdict']} |")
    lines += ['', 'All reference-arm, maintenance-only, consumer-only and CPU results, gate failures and raw variability are in comparisons.csv and summary.json. No default backend is changed.', '',
              'BatchedPointScanWave scans authoritative CSR membership and reuses sample/hash work across the query batch. It does not cull cells, so these fixed fixtures do not establish universal local-query or sparse-workload superiority. It records two dispatches and uses no scratch.', '',
              'GpuDriven still records 13 index dispatch commands. GPU-generated zero-X indirect dispatches avoid unused shader work; State[15] reports nonempty dispatches (3 idle / 6 changed / 11 rebuild). Detection still launches all capacity groups. State[14] records actually inspected input slots, including the static snapshot contract. Changed-member maintenance inspects 2*changed slots instead of 2*capacity when no rebuild occurs. Extra resident storage is 4*capacity+120 bytes. Complete-operation results include the original CellSerial consumer and frame digest.', '',
              'Formal samples are used once. Failed stability gates remain inconclusive; no resampling, cache clearing, driver changes or default promotion.']
    (destination / 'report.en.md').write_text('\n'.join(lines)+'\n', encoding='utf-8')
    hashes = []
    for path in sorted(source.rglob('*')):
        if path.is_file(): hashes.append(dict(path=str(path.resolve()), sha256=hashlib.sha256(path.read_bytes()).hexdigest(), bytes=path.stat().st_size))
    (destination / 'raw-sha256.json').write_text(json.dumps(hashes, indent=2), encoding='utf-8')
    print(json.dumps(dict(status='complete', processes=5, cells=len(cells), rows=len(rows), output=str(destination.resolve()))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    summarize(args.input, args.output)
