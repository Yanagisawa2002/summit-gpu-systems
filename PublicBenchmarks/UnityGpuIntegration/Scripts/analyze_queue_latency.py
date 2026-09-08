"""Audit every completion against its GPU history and independent original oracle.

The primary independent unit is a paired replicate, not one queued task. This
script does not discard failed/noisy processes or select a different video pair.
"""
import argparse
import csv
import hashlib
import json
import math
import statistics as st
from pathlib import Path


def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def quantile(v, q):
    v = sorted(v)
    x = (len(v)-1)*q
    i = int(x)
    return v[i] + (v[min(i+1, len(v)-1)]-v[i])*(x-i)


def gmean(v):
    return math.exp(st.mean(math.log(x) for x in v))


def save(p, value):
    p.write_text(json.dumps(value, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')


def analyze(root, oracles, out):
    out.mkdir(parents=True, exist_ok=False)
    matrix = read(root/'matrix.json')
    protocol = read(root/'protocol.json')
    assert matrix['status'] == 'completed' and len(matrix['runs']) == 20
    expected_order=[]
    for rep in range(5):
        order=['off-old-full','off-new-full','on-new-full','on-old-full'] if rep%2==0 else ['on-old-full','on-new-full','off-new-full','off-old-full']
        expected_order.extend(f'{rep}-{entry}' for entry in order)
    assert [r['id'] for r in matrix['runs']] == expected_order
    rows, jobs, lookup, evidence = [], [], {}, []
    identities = set()
    for entry in matrix['runs']:
        assert entry['status'] == 'completed'
        folder = root/entry['id']
        result = read(folder/'player/result.json')
        receipt = read(folder/'receipt.json')
        config = result['config']
        rep, arm, capture = entry['replicate'], entry['arm'], entry['capture']
        assert config['arm'] == arm and config['seed'] == protocol['seeds'][rep] and config['replicate'] == rep
        assert config['sourceSha'] == matrix['sourceSha'] == receipt['sourceSha']
        assert receipt['status'] == 'completed' and receipt['capture'] == capture
        assert result['status'] == 'completed' and result['verified'] and result['completed'] == 384
        assert not result['development'] and not result['nativeGpuTimingAvailable'] and not result['osPresentationAvailable']
        assert result['jobCount'] == 384 and result['maxInFlight'] == 1 and result['warmupJobs'] == 8 and result['arrivalRate'] == 60
        assert result['device'] == 'AMD Radeon AI PRO R9700' and result['graphicsApi'] == 'Direct3D12'
        oracle = oracles/f'{rep}-hotspot-dynamic/expected.bin'
        assert (folder/'player/actual.history.bin').read_bytes() == oracle.read_bytes()
        assert sha(oracle) == result['oracleSha256'] == receipt['oracleSha256']
        assert len(result['jobs']) == 384
        frequency, epoch = result['clockFrequency'], result['startTicks']
        assert frequency > 0 and epoch > 0
        assert result['firstMarkerRepaintTicks'] >= epoch
        previous = epoch
        for i, j in enumerate(result['jobs']):
            assert j['job'] == i and j['verified']
            assert j['queryCount'] == 9 and j['activeCount'] == 262144 and j['readbackBytes'] == 160
            assert j['changedSlots'] == (0 if i == 0 else 2621)
            assert j['uploadedBytes'] == (0 if i == 0 else 262144*20)
            assert math.isclose(j['arrivalMs'], i*1000/60, abs_tol=1e-9)
            assert previous <= j['submitTicks'] <= j['readbackObservedTicks'] <= j['verifiedTicks']
            for tick, ms in [('submitTicks', 'submitMs'), ('readbackObservedTicks', 'readbackObservedMs'), ('verifiedTicks', 'resultReadyMs')]:
                assert math.isclose(j[ms], (j[tick]-epoch)*1000/frequency, abs_tol=1e-8)
            assert j['submitMs'] >= j['arrivalMs'] and j['latencyMs'] >= 0
            assert math.isclose(j['latencyMs'], j['resultReadyMs']-j['arrivalMs'], abs_tol=1e-8)
            previous = j['verifiedTicks']
            jobs.append(dict(run=entry['id'], replicate=rep, capture=capture, arm=arm, **j))
        assert result['finishTicks'] == result['jobs'][-1]['verifiedTicks']
        assert result['finishMs'] == result['jobs'][-1]['resultReadyMs'] == entry['finishMs']
        identities.add(sha(oracle))
        last_completed = 0
        for obs in result['observations']:
            assert last_completed <= obs['completed'] <= obs['submitted'] <= obs['released'] <= 384
            assert 0 <= obs['submitted']-obs['completed'] <= 1
            assert obs['outstanding'] == obs['released']-obs['completed']
            assert obs['released'] == min(384, int(math.floor(obs['elapsedMs']*60/1000))+1)
            if obs['completed']:
                assert obs['ticks'] >= result['jobs'][obs['completed']-1]['verifiedTicks']
            last_completed = obs['completed']
        assert last_completed == 384
        assert result['maxOutstanding'] == max(o['outstanding'] for o in result['observations'])
        latency = [j['latencyMs'] for j in result['jobs']]
        row = dict(id=entry['id'], replicate=rep, capture=capture, arm=arm, seed=config['seed'], finishMs=result['finishMs'],
                   meanLatencyMs=st.mean(latency), p95LatencyMs=quantile(latency, .95), p99LatencyMs=quantile(latency, .99),
                   maximumLatencyMs=max(latency), maxOutstanding=result['maxOutstanding'],
                   workObservations=sum(o['elapsedMs'] <= result['finishMs'] for o in result['observations']),
                   unfocusedWorkObservations=sum(not o['focused'] for o in result['observations'] if o['elapsedMs'] <= result['finishMs']),
                   sourceSha=config['sourceSha'], oracleSha256=sha(oracle), verifiedJobs=384)
        rows.append(row)
        lookup[capture, rep, arm] = row
        for path in sorted(folder.rglob('*')):
            if path.is_file():
                evidence.append(dict(path=path.relative_to(root).as_posix(), bytes=path.stat().st_size, sha256=sha(path)))
    assert len(lookup) == 20 and len(identities) == 5
    comparisons = []
    for capture in [False, True]:
        baseline = [lookup[capture, r, 'old-full'] for r in range(5)]
        candidate = [lookup[capture, r, 'new-full'] for r in range(5)]
        ratios = [a['finishMs']/b['finishMs'] for a, b in zip(baseline, candidate)]
        logs = [math.log(x) for x in ratios]
        half = 2.7764451051977987*st.stdev(logs)/math.sqrt(5)
        ci = [math.exp(st.mean(logs)-half), math.exp(st.mean(logs)+half)]
        av, bv = [[x['finishMs'] for x in arm] for arm in [baseline, candidate]]
        cvs = [100*st.stdev(v)/st.mean(v) for v in [av, bv]]
        drift = 100*abs(av[-1]/av[0]-1)
        tail = gmean([a['p95LatencyMs']/b['p95LatencyMs'] for a, b in zip(baseline, candidate)])
        gates = dict(ciLower=ci[0] > protocol['ciLowerMinimum'],
                     baselineCv=cvs[0] <= protocol['cvLimitPercent'], candidateCv=cvs[1] <= protocol['cvLimitPercent'],
                     baselineDrift=drift <= protocol['baselineDriftLimitPercent'],
                     p95Latency=tail >= protocol['p95LatencyRatioMinimum'])
        comparisons.append(dict(capture=capture, baselineMeanFinishMs=st.mean(av), candidateMeanFinishMs=st.mean(bv),
                                finishRatio=gmean(ratios), ci95=ci, processRatios=ratios,
                                baselineCvPercent=cvs[0], candidateCvPercent=cvs[1], baselineDriftPercent=drift,
                                p95LatencyRatio=tail, gates=gates, passes=all(gates.values())))
    effects = []
    for arm in ['old-full', 'new-full']:
        ratios = [lookup[True, r, arm]['finishMs']/lookup[False, r, arm]['finishMs'] for r in range(5)]
        ratio = gmean(ratios)
        effects.append(dict(arm=arm, onOffFinishRatio=ratio, replicateRatios=ratios,
                            passes=100*abs(ratio-1) <= protocol['captureEffectFinishRatioTolerancePercent']))
    verdict = all(c['passes'] for c in comparisons+effects)
    focus_uniform = all(r['unfocusedWorkObservations']==0 for r in rows)
    result = dict(status='passes-frozen-task-latency-gates' if verdict else 'inconclusive',
                  frozenStatisticalGatesPassed=verdict, publicationEligible=verdict and focus_uniform, sourceSha=matrix['sourceSha'], protocolSha256=sha(root/'protocol.json'),
                  verifiedProcesses=20, verifiedJobs=20*384, comparisons=comparisons, captureEffects=effects,
                  focusContextUniform=focus_uniform,
                  processesWithUnfocusedWork=[r['id'] for r in rows if r['unfocusedWorkObservations']],
                  recordedPair=[lookup[True, 0, a] for a in ['old-full', 'new-full']],
                  fullFrameSmoothnessClaim=False,
                  caveat='Finite synthetic hotspot stream at 60 jobs/s with one in-flight job; task delivery includes host and readback cost. Does not show a general speedup, maximum GPU throughput or smoother displayed frames.')
    save(out/'analysis.json', result)
    save(out/'evidence-manifest.json', evidence)
    for filename, data in [('processes.csv', rows), ('jobs.csv', jobs)]:
        with (out/filename).open('w', newline='', encoding='utf-8') as f:
            writer = csv.DictWriter(f, fieldnames=list(data[0]));writer.writeheader();writer.writerows(data)
    print(json.dumps(result))
    return result


if __name__ == '__main__':
    p = argparse.ArgumentParser();p.add_argument('matrix', type=Path);p.add_argument('oracles', type=Path);p.add_argument('output', type=Path)
    a = p.parse_args();analyze(a.matrix, a.oracles, a.output)
