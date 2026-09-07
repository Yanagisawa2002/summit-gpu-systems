"""Standard-library analysis of the frozen protocol. Raw failed runs are never removed."""
import argparse
import bisect
import csv
import hashlib
import json
import math
import statistics as st
from pathlib import Path

ARMS = ['old-full', 'new-full', 'old-incremental', 'new-incremental']
NATIVE = ['sceneGpu', 'indexGpu', 'queryGpu']
ENGINE = ['cpuFrameMs', 'mainThreadMs', 'renderThreadMs', 'presentWaitMs', 'gpuFrameMs']

def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def quantile(values, p):
    values = sorted(values)
    if not values:
        return None
    x = (len(values)-1)*p
    lo = int(x)
    return values[lo] + (values[min(lo+1, len(values)-1)]-values[lo])*(x-lo)

def summary(values):
    values = [float(x) for x in values if x is not None and math.isfinite(x)]
    return dict(count=len(values), mean=st.mean(values) if values else None,
                p50=quantile(values, .5), p95=quantile(values, .95), p99=quantile(values, .99),
                maximum=max(values) if values else None, over16_67ms=sum(x>1000/60 for x in values))

def gmean(values):
    return math.exp(st.mean(math.log(x) for x in values))

def cv(values):
    return 100*st.stdev(values)/st.mean(values) if len(values)>1 and st.mean(values)>0 else None

def paired(process_blocks, protocol):
    """Each item holds paired block summaries for ONE independent process."""
    complete = len(process_blocks)==5 and all(len(p)==protocol['blocks'] for p in process_blocks)
    if not complete:
        return dict(status='inconclusive', reason='Five complete independent processes required', processes=len(process_blocks))
    ratios = [gmean([a['mean']/b['mean'] for a,b in p]) for p in process_blocks]
    log_ratios = [math.log(x) for x in ratios]
    center = st.mean(log_ratios)
    half = 2.7764451051977987*st.stdev(log_ratios)/math.sqrt(5)
    interval = [math.exp(center-half), math.exp(center+half)]
    av = [st.mean(a['mean'] for a,b in p) for p in process_blocks]
    bv = [st.mean(b['mean'] for a,b in p) for p in process_blocks]
    drift = max(abs(p[-1][0]['mean']/p[0][0]['mean']-1)*100 for p in process_blocks)
    p95ratio = gmean([gmean([a['p95']/b['p95'] for a,b in p]) for p in process_blocks])
    gates = dict(meanCiLowerAboveOne=interval[0]>1,
                 baselineCv=cv(av)<=protocol['cvLimitPercent'], candidateCv=cv(bv)<=protocol['cvLimitPercent'],
                 baselineDrift=drift<=protocol['baselineDriftLimitPercent'], p95Ratio=p95ratio>=protocol['p95SpeedRatioMinimum'])
    return dict(status='passes-frozen-metric-gates' if all(gates.values()) else 'inconclusive',
                processes=5, processPairedRatios=ratios, speedRatio=math.exp(center), ci95=interval,
                baselineCvPercent=cv(av), candidateCvPercent=cv(bv), baselineDriftPercent=drift,
                p95SpeedRatio=p95ratio, gates=gates)

def align_engine(result):
    """A frame begins before its own Update. Never use the observation frame."""
    frames = result.get('processFrames', [])
    freq = result.get('qpcFrequency', 0)
    if not freq or freq != result.get('engineCpuTimerFrequency'):
        return {}, dict(status='unavailable', reason='Clock frequencies unavailable or unequal')
    ticks = [p['qpc'] for p in frames]
    if not ticks or any(a>=b for a,b in zip(ticks,ticks[1:])):
        return {}, dict(status='unavailable', reason='QPC Update windows missing or unordered')
    candidates = {}; rejected = 0
    for timing in result.get('engineTimings', []):
        stamp = timing['frameStartTimestamp']
        i = bisect.bisect_left(ticks, stamp)
        if i==0 or i==len(ticks):
            rejected += 1
            continue
        frame = frames[i]['unityFrame']
        candidates.setdefault(frame, []).append(timing)
    mapped = {f: ts[0] for f,ts in candidates.items() if len(ts)==1}
    duplicates = sum(len(ts) for ts in candidates.values() if len(ts)>1)
    return mapped, dict(status='aligned' if mapped else 'unavailable', matched=len(mapped),
                        outsideWindows=rejected, ambiguousTimingRecords=duplicates,
                        rule='frameStart QPC -> first subsequent Update QPC -> source Unity frame')

def analyze(root, output):
    root, output = Path(root), Path(output)
    protocol = read(root/'protocol.json')
    output.mkdir(parents=True, exist_ok=False)
    cells, rows, failures, outliers, evidence = [], [], [], [], []
    for entry in read(root/'matrix.json'):
        folder = Path(entry['output'])
        if not (folder/'result.json').exists():
            failures.append(dict(id=entry['id'], error='Missing result', process=entry))
            continue
        result = read(folder/'result.json')
        cfg = result['config']
        engine, alignment = align_engine(result)
        errors = []
        if result['status']!='completed' or not result['formalPerformanceEvidence'] or result['development']:
            errors.append('Incomplete or non-formal/non-Release process')
        if len(result['runs']) != 4*protocol['blocks']:
            errors.append('Missing arm/block')
        work_ids = set()
        arm_audits = []
        for arm in result['runs']:
            frames = arm['frames']
            history = folder/f"{arm['block']}-{arm['position']}-{arm['arm']}.history.bin"
            identity = hashlib.sha256(json.dumps([[f[k] for k in ['frame','activeCount','changedSlots','queryCount','drawVertices','overlayVertices','drawCalls','uploadedBytes']] for f in frames]).encode()).hexdigest()
            work_ids.add(identity)
            oracle_sha = sha(cfg['oracle'])
            good = (arm['verified'] and arm['verifiedDigestWords']==protocol['frames']*10 and
                    history.exists() and sha(history)==oracle_sha==arm['oracleSha256'] and len(frames)==protocol['frames'])
            good = good and all(f['frame']==i and all(f[n]['status']=='Ready' and f[n]['sourceFrame']==f['unityFrame'] and f[n]['milliseconds']>0 for n in NATIVE) for i,f in enumerate(frames))
            good = good and all(f['queryCount']==9 and f['drawVertices']==262144 and f['overlayVertices']==54 and f['drawCalls']==2 and f['historyDispatches']==1 and f['indexRecordedDispatches']==(13 if arm['arm'].endswith('incremental') else 11) and f['queryRecordedDispatches']==(3 if arm['arm'].startswith('new') else 2) for f in frames)
            if not good:
                errors.append(f"Correctness/work/timestamp failure: {arm['block']}/{arm['arm']}")
            arm_audits.append(dict(arm=arm['arm'], block=arm['block'], correct=good, workSha256=identity, oracleSha256=oracle_sha))
            for scope in ['all-arm-frames', 'steady']:
                selected = [f for f in frames if scope=='all-arm-frames' or f['frame']>=protocol['warmup']]
                metrics = {'engineCadenceMs':[f['engineCadenceMs'] for f in selected],
                           'recordCpuMs':[f['recordCpuMs'] for f in selected]}
                metrics.update({n:[f[n]['milliseconds'] for f in selected] for n in NATIVE})
                metrics.update({'engine.'+n:[engine.get(f['unityFrame'], {}).get(n, 0) for f in selected] for n in ENGINE})
                for metric, values in metrics.items():
                    if metric.startswith('engine.'):
                        values = [x for x in values if x>0]
                    info = summary(values)
                    valid = good and info['count']>=len(selected)*protocol['minimumEngineTimingCoverage'] and info['mean'] and info['p95']
                    rows.append(dict(scene=cfg['scenario'], replicate=cfg['processReplicate'], block=arm['block'], position=arm['position'], arm=arm['arm'], scope=scope, metric=metric, eligible=bool(valid), expectedCount=len(selected), **info))
            for f in frames:
                if f['engineCadenceMs']>protocol['frameBudgetMs']:
                    outliers.append(dict(scene=cfg['scenario'],replicate=cfg['processReplicate'],arm=arm['arm'],block=arm['block'],frame=f['frame'],measured=f['measured'],engineCadenceMs=f['engineCadenceMs']))
        if len(work_ids)!=1:
            errors.append('Equal-work trajectory differs across arms/blocks')
        if errors:
            failures.append(dict(id=entry['id'], errors=errors, playerError=result.get('error')))
        cells.append(dict(id=entry['id'], scene=cfg['scenario'], replicate=cfg['processReplicate'], passed=not errors,
                          alignment=alignment, armAudits=arm_audits,
                          wholeProcessCadence=summary([f['engineIntervalMs'] for f in result['processFrames']]),
                          firstRenderedEngineMilliseconds=result['firstRenderedEngineMilliseconds'],
                          lifecycle=[dict(arm=a['arm'],block=a['block'],events=a['contentEvents']) for a in result['runs']],
                          memoryAndSetup=[{k:a[k] for k in ['arm','block','residentBytes','allocatedBefore','allocatedAfter','setupMilliseconds','asyncDrainMilliseconds']} for a in result['runs']],
                          gc=[dict(arm=a['arm'],block=a['block'],recordAllocatedBytes=sum(f['recordAllocatedBytes'] for f in a['frames']),gc0=a['frames'][-1]['gc0']-a['frames'][0]['gc0'],gc1=a['frames'][-1]['gc1']-a['frames'][0]['gc1'],gc2=a['frames'][-1]['gc2']-a['frames'][0]['gc2']) for a in result['runs']]))
        for file in sorted(folder.iterdir()):
            if file.is_file(): evidence.append(dict(path=str(file),bytes=file.stat().st_size,sha256=sha(file)))
    paired_results = []
    lookup = {(r['scene'],r['replicate'],r['block'],r['arm'],r['metric']):r for r in rows if r['scope']=='steady' and r['eligible']}
    valid_cells = {(c['scene'],c['replicate']) for c in cells if c['passed']}
    metrics = ['engineCadenceMs','recordCpuMs']+NATIVE+['engine.'+n for n in ENGINE]
    contrasts = [('old-full',a) for a in ARMS[1:]]+[('old-incremental','new-incremental'),('new-full','new-incremental')]
    for scene in protocol['scenarios']:
        for baseline,candidate in contrasts:
            for metric in metrics:
                process_blocks = []
                for rep in range(5):
                    if (scene,rep) not in valid_cells: continue
                    pairs = []
                    for block in range(protocol['blocks']):
                        a=lookup.get((scene,rep,block,baseline,metric)); b=lookup.get((scene,rep,block,candidate,metric))
                        if a and b:pairs.append((a,b))
                    if len(pairs)==protocol['blocks']:process_blocks.append(pairs)
                paired_results.append(dict(scene=scene,baseline=baseline,candidate=candidate,metric=metric,**paired(process_blocks,protocol)))
    result = dict(protocol=protocol, protocolSha256=sha(root/'protocol.json'), cells=cells, failures=failures, comparisons=paired_results,
                  caveats=[protocol['tailCaveat'],protocol['scope'],'Unadjusted descriptive comparisons; no industry-standard benchmark or default promotion.'], evidence=evidence)
    (output/'analysis.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    for name, items in [('block-statistics',rows),('over-budget-frames',outliers)]:
        with (output/(name+'.csv')).open('w',newline='',encoding='utf-8') as file:
            if items:
                writer=csv.DictWriter(file,fieldnames=list(items[0]));writer.writeheader();writer.writerows(items)
    lines=['# Public Unity integration results','',f"{sum(c['passed'] for c in cells)}/15 complete equal-work formal processes; {len(failures)} process failures.",'',
           'Ratios above one favor the candidate. Each interval uses five independent process log ratios, with four paired blocks per process.','',
           '| Scene | Candidate | Engine cadence ratio [95% CI] | Frozen gates |','|---|---|---|---|']
    for c in paired_results:
        if c['metric']=='engineCadenceMs' and c['baseline']=='old-full':
            value=f"{c['speedRatio']:.3f} [{c['ci95'][0]:.3f}, {c['ci95'][1]:.3f}]" if 'speedRatio' in c else 'unavailable'
            lines.append(f"| {c['scene']} | {c['candidate']} | {value} | {c['status']} |")
    lines += ['',*result['caveats'],'','All complete-frame and steady-block metrics are in block-statistics.csv. Raw outliers remain in over-budget-frames.csv and the original result files. Detailed GPU, CPU, drift, CV, lifecycle, memory, GC, alignment coverage, failures and input hashes are in analysis.json.']
    (output/'README.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    return result

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('matrix');parser.add_argument('output');args=parser.parse_args()
    analyze(args.matrix,args.output)
