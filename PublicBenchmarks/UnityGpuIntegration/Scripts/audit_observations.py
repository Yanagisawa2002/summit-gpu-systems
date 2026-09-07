"""Compare first and subsequent Unity timing snapshots without inventing missing GPU time."""
import argparse
from collections import defaultdict,Counter
import hashlib
import json
from pathlib import Path
from analyze import align_engine,summary

def audit(root,output):
    root,output=Path(root),Path(output);result=[]
    for file in sorted(root.glob('observations-*/result.json')):
        r=json.loads(file.read_text());observations=r['engineObservations'];groups=defaultdict(list)
        for o in observations:
            if o['frameStartTimestamp']:groups[o['frameStartTimestamp']].append(o)
        changed=[];zero_recovered=[];positive_changed=[];coverage=[]
        for stamp,obs in groups.items():
            obs.sort(key=lambda o:(o['observationFrame'],o['slot']))
            values={(o['cpuFrameMs'],o['gpuFrameMs'],o['cpuTimePresentCalled'],o['cpuTimeFrameComplete']) for o in obs}
            if len(values)>1:changed.append(dict(frameStartTimestamp=stamp,observations=obs))
            if obs[0]['gpuFrameMs']==0 and any(o['gpuFrameMs']>0 for o in obs[1:]):zero_recovered.append(stamp)
            if obs[0]['gpuFrameMs']>0 and any(o['gpuFrameMs']!=obs[0]['gpuFrameMs'] for o in obs[1:]):positive_changed.append(stamp)
        mapped,alignment=align_engine(r)
        for a in r['runs']:
            fs=a['frames'];steady=fs[r['config']['warmup']:]
            first=0;eventual=0;count=0;last_lags=Counter()
            for f in steady:
                t=mapped.get(f['unityFrame'])
                if not t:continue
                count+=1;obs=groups[t['frameStartTimestamp']]
                first+=obs[0]['gpuFrameMs']>0;eventual+=any(o['gpuFrameMs']>0 for o in obs)
                last_lags[str(max(o['observationFrame'] for o in obs)-f['unityFrame'])]+=1
            coverage.append(dict(arm=a['arm'],verified=a['verified'],frames=count,firstPositive=first,eventualPositive=eventual,lastObservationLagHistogram=dict(last_lags)))
        result.append(dict(path=str(file),sha256=hashlib.sha256(file.read_bytes()).hexdigest(),status=r['status'],formal=r['formalPerformanceEvidence'],
                           returnedRecords=len(observations),uniqueKeys=len(groups),duplicateObservations=len(observations)-len(groups),changedKeys=len(changed),
                           zeroRecoveredKeys=zero_recovered,positiveChangedKeys=positive_changed,changedKeyDetails=changed,
                           observationCountHistogram=dict(Counter(str(len(o)) for o in groups.values())),alignment=alignment,arms=coverage,
                           collectorCpuMs=summary([p['collectorCpuMs'] for p in r['processFrames']]),
                           collectorAllocatedBytes=summary([p['collectorAllocatedBytes'] for p in r['processFrames']]),
                           apiReturnCountHistogram=dict(Counter(str(p['returnedTimings']) for p in r['processFrames']))))
    output.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps([{k:v for k,v in r.items() if k not in ['changedKeyDetails','path','sha256']} for r in result],indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('output');a=p.parse_args();audit(a.root,a.output)
