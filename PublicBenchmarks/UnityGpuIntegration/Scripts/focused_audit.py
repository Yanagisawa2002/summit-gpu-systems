"""Offline diagnosis; retains missing/zero timing evidence and never relabels GPU scopes."""
import argparse
from collections import Counter, defaultdict
import csv
import hashlib
import json
import statistics as st
from pathlib import Path
from analyze import align_engine, summary

def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))

def audit(matrix, output):
    matrix,output=Path(matrix),Path(output);output.mkdir(parents=True,exist_ok=False)
    rows=[];sources=[];groups=defaultdict(list);totals=Counter();delays=Counter();all_collection=[]
    for cell in read(matrix/'matrix.json'):
        p=matrix/cell['id']/'result.json';r=read(p);mapped,alignment=align_engine(r)
        sources.append(dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest(),alignment=alignment))
        totals['engineRawRecords']+=len(r['engineTimings']);totals['mappingRejected']+=alignment.get('outsideWindows',0)
        totals['mappingAmbiguous']+=alignment.get('ambiguousTimingRecords',0)
        totals['duplicateStoredKeys']+=len(r['engineTimings'])-len({t['frameStartTimestamp'] for t in r['engineTimings']})
        for a in r['runs']:
            last_gc=a['frames'][0]['gc0']
            for f in a['frames']:
                t=mapped.get(f['unityFrame']);totals['logicalFrames']+=1
                delta_gc=f['gc0']-last_gc;last_gc=f['gc0']
                row=dict(scene=r['config']['scenario'],replicate=r['config']['processReplicate'],arm=a['arm'],block=a['block'],frame=f['frame'],steady=f['frame']>=r['config']['warmup'],cadenceMs=f['engineCadenceMs'],recordCpuMs=f['recordCpuMs'],recordAllocatedBytes=f['recordAllocatedBytes'],gcCollections=delta_gc,mapped=bool(t),gpuPositive=bool(t and t['gpuFrameMs']>0),gpuFrameMs=t['gpuFrameMs'] if t else None,nativeSceneMs=f['sceneGpu']['milliseconds'],engineCpuMs=t['cpuFrameMs'] if t else None,presentWaitMs=t['presentWaitMs'] if t else None,observationDelayFrames=t['observedUnityFrame']-f['unityFrame'] if t else None,frameCompleteEqualsPresent=bool(t and t['cpuTimeFrameComplete']==t['cpuTimePresentCalled']))
                rows.append(row);groups[row['scene'],row['arm']].append(row)
                if not t:totals['logicalUnmapped']+=1
                elif t['gpuFrameMs']==0:totals['engineGpuZero']+=1
                else:totals['engineGpuPositive']+=1
                if t:delays[str(row['observationDelayFrames'])]+=1
    grouped=[]
    for (scene,arm),rs in groups.items():
        steady=[r for r in rs if r['steady']]
        zero=[r for r in steady if not r['gpuPositive']];positive=[r for r in steady if r['gpuPositive']]
        gc=[r for r in steady if r['gcCollections']>0];nongc=[r for r in steady if r['gcCollections']==0]
        grouped.append(dict(scene=scene,arm=arm,count=len(rs),steadyCount=len(steady),positiveGpuCoveragePercent=100*len(positive)/len(steady),
                            zeroDelayHistogram=dict(Counter(str(r['observationDelayFrames']) for r in zero)),positiveDelayHistogram=dict(Counter(str(r['observationDelayFrames']) for r in positive)),
                            zeroCompleteEqualsPresent=sum(r['frameCompleteEqualsPresent'] for r in zero),zeroCount=len(zero),
                            cadence=summary([r['cadenceMs'] for r in steady]),recordCpu=summary([r['recordCpuMs'] for r in steady]),
                            steadyGcEvents=sum(r['gcCollections'] for r in steady),gcFrameCadence=summary([r['cadenceMs'] for r in gc]),noGcFrameCadence=summary([r['cadenceMs'] for r in nongc]),
                            recordedAllocationBytes=sum(r['recordAllocatedBytes'] for r in steady),
                            frameStartMappingMissing=sum(not r['mapped'] for r in rs)))
    result=dict(status='offline-audit-complete',totals=dict(totals),observationDelayHistogram=dict(delays),groups=grouped,sources=sources,
                limitations=['Old collector discarded every repeated frameStartTimestamp. Stored records cannot establish whether Unity later revised zero GPU values.',
                             'recordAllocatedBytes covers command recording, not phase strings, WaitForEndOfFrame, Update allocations or arm JSON serialization.',
                             'GC/cadence association is descriptive, not causal. Engine GPU zero is unavailable, never zero cost.'])
    (output/'offline-audit.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    with (output/'frame-diagnosis.csv').open('w',newline='',encoding='utf-8') as f:
        w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
    print(json.dumps({k:v for k,v in result.items() if k not in ('groups','sources')},indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('matrix');p.add_argument('output');a=p.parse_args();audit(a.matrix,a.output)
