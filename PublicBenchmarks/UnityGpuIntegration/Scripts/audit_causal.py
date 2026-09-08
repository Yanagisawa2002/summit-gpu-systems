"""Diagnostic controls: validate raw work/history and report descriptive distributions only."""
import csv
import hashlib
import json
import math
import struct
import sys
from collections import defaultdict
from pathlib import Path
from analyze import align_engine, lifecycle_valid, summary

def read(p): return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def audit(root):
    root=Path(root);matrix=root/'diagnostic-v1';cells=read(matrix/'matrix.json');freeze=read(matrix/'freeze.json')
    build=read(root/'build-release-v1/build-attestation.json');release=read(root/'build-release-v1/release-build.json')
    assert len(cells)==9 and build['status']=='built' and not build['sourceDirty'] and not build['development']
    assert build['sourceSha']==freeze['sourceSha']==release['sourceCommit']
    assert sha(root/'build-release-v1/build-attestation.json')==freeze['buildAttestationSha256']
    for entry in build['files']:
        f=root/'build-release-v1'/entry['path'].replace('\\','/')
        assert f.stat().st_size==entry['bytes'] and sha(f)==entry['sha256']
    groups=[];frames=[];processes=[];work={};intervals=[];scopes=0;long_frames=[]
    for cell,declared in zip(cells,freeze['cells']):
        folder=matrix/cell['id'];r=read(folder/'result.json');receipt=read(folder/'process.json');c=r['config'];mode=c['nativeProbeMode']
        assert cell['id']==declared['id'] and cell['status']==r['status']==receipt['status']=='completed'
        assert not r['formalPerformanceEvidence'] and not r['development'] and r['graphicsApi']=='Direct3D12'
        assert c['sourceSha']==build['sourceSha'] and r['buildGuid']==release['buildGuid']
        assert receipt['processId']==r['processId'] and receipt['exitCode']==0
        assert sha(matrix/(cell['id']+'.json'))==declared['configSha256'] and sha(folder/'config.json')==receipt['configSha256']
        oracle=root/'oracles'/(c['scenario']+'.bin')
        if not oracle.exists():oracle=Path(c['oracle'])
        expected=oracle.read_bytes();assert len(expected)==61440 and sha(oracle)==declared['oracleSha256']
        assert c['seed']==920071 and c['frames']==384 and c['warmup']==64 and c['blocks']==1 and c['processReplicate']==0
        count={'none':0,'whole':1536,'three':4608}[mode]
        assert r['nativeFrequencyEvents']==int(mode!='none')
        assert all(r[k]==count for k in ['nativeBeginEvents','nativeEndEvents','nativeCompletionEvents'])
        mapped,alignment=align_engine(r); pf={p['unityFrame']:p for p in r['processFrames']}
        assert alignment['ambiguousTimingRecords']==0
        process=dict(id=cell['id'],pid=r['processId'],startedUtc=r['startedUtc'],endedUtc=r['endedUtc'],resultSha256=sha(folder/'result.json'),alignment=alignment,
                     probeEvents={k:r[k] for k in ['nativeFrequencyEvents','nativeBeginEvents','nativeEndEvents','nativeCompletionEvents']},presentation=receipt.get('presentation'),osPresentationAvailable=False)
        csvpath=folder/'presentmon.csv'
        if csvpath.exists():
            with csvpath.open(encoding='utf-8-sig',newline='') as f: pm=list(csv.DictReader(f))
            process['presentationCsvRows']=len(pm)
            process['presentationPidCounts']={pid:sum(x.get('ProcessID')==pid for x in pm) for pid in {x.get('ProcessID') for x in pm}}
            # A successful exit or mere CSV is not evidence of complete logical-frame mapping.
        processes.append(process);intervals.append((r['startedUtc'],r['endedUtc']))
        assert [a['arm'] for a in r['runs']]==['old-full','new-full','new-incremental','old-incremental']
        for a in r['runs']:
            assert a['verified'] and a['verifiedDigestWords']==3840 and len(a['frames'])==384
            assert (folder/f"0-{a['position']}-{a['arm']}.history.bin").read_bytes()==expected
            assert a['oracleSha256']==declared['oracleSha256']
            if c['scenario']=='streaming-switch': assert lifecycle_valid(a['contentEvents'])
            else: assert not a['contentEvents']
            cp=read(folder/f"checkpoint-0-{a['position']}-{a['arm']}.json");expected_cp=a.copy();expected_cp['checkpointMilliseconds']=0
            assert cp==expected_cp
            rows=[];signature=[]
            for i,f in enumerate(a['frames']):
                assert f['frame']==i and f['qpcEnd']>f['qpcStart']
                assert (f['queryCount'],f['drawVertices'],f['overlayVertices'],f['drawCalls'],f['historyDispatches'])==(9,262144,54,2,1)
                assert struct.unpack_from('<4I',expected,(i*10+9)*16)==(i,f['activeCount'],262144,f['changedSlots'])
                assert f['indexRecordedDispatches']==(13 if a['arm'].endswith('incremental') else 11)
                assert f['queryRecordedDispatches']==(3 if a['arm'].startswith('new') else 2)
                signature.append(tuple(f[k] for k in ['activeCount','changedSlots','uploadedBytes','indexRecordedDispatches','queryRecordedDispatches']))
                for k in ['sceneGpu','indexGpu','queryGpu']:
                    n=f[k];enabled=mode=='three' or mode=='whole' and k=='sceneGpu'
                    if enabled:
                        assert n['status']=='Ready' and n['token']>0 and n['endTicks']>n['beginTicks'] and n['frequency']>0
                        assert n['sourceFrame']==f['unityFrame'] and n['resultFrame']>=n['sourceFrame']
                        assert math.isclose(n['milliseconds'],(n['endTicks']-n['beginTicks'])*1000/n['frequency'],rel_tol=1e-12)
                        scopes+=1
                    else: assert n['status']=='NotRequested' and n['milliseconds']==-1 and all(n[x]==0 for x in ['token','beginTicks','endTicks','frequency'])
                if mode=='three':
                    s,n,q=(f[k] for k in ['sceneGpu','indexGpu','queryGpu'])
                    assert s['beginTicks']<=n['beginTicks']<n['endTicks']<=q['beginTicks']<q['endTicks']<=s['endTicks']
                t=mapped.get(f['unityFrame']);p=pf[f['unityFrame']]
                assert t is not None
                assert p['targetFrameRate']==-1 and p['vSyncCount']==0 and p['runInBackground']
                row=dict(scene=c['scenario'],probe=mode,arm=a['arm'],frame=i,unityFrame=f['unityFrame'],measured=f['measured'],focused=p['focused'],
                    cadenceMs=f['engineCadenceMs'],recordCpuMs=f['recordCpuMs'],nativeSceneMs=f['sceneGpu']['milliseconds'] if mode!='none' else None,
                    presentWaitMs=t['presentWaitMs'] if t else None,engineCpuMs=t['cpuFrameMs'] if t else None,
                    engineGpuMs=t['gpuFrameMs'] if t and t['gpuFrameMs']>0 else None,
                    mainThreadMs=t['mainThreadMs'] if t else None,renderThreadMs=t['renderThreadMs'] if t else None,
                    engineMapped=t is not None,engineGpuZero=t is not None and t['gpuFrameMs']==0,
                    completeEqualsPresent=t is not None and t['cpuTimeFrameComplete']==t['cpuTimePresentCalled'])
                rows.append(row);frames.append(row)
                if row['measured'] and row['cadenceMs']>1000/60:long_frames.append(row)
            key=c['scenario'],a['arm']
            if key in work: assert work[key]==signature
            else: work[key]=signature
            steady=rows[64:]
            groups.append(dict(scene=c['scenario'],probe=mode,arm=a['arm'],frames=len(rows),steadyFrames=len(steady),
                focusedSteady=sum(x['focused'] for x in steady),missingEngine=sum(not x['engineMapped'] for x in steady),
                gpuZero=sum(x['engineGpuZero'] for x in steady),gpuPositive=sum(x['engineGpuMs'] is not None for x in steady),
                zeroWithCompleteEqualsPresent=sum(x['engineGpuZero'] and x['completeEqualsPresent'] for x in steady),
                **{k:summary([x[k] for x in steady]) for k in ['cadenceMs','recordCpuMs','presentWaitMs','engineCpuMs','engineGpuMs','nativeSceneMs','mainThreadMs','renderThreadMs']}))
    assert len({p['pid'] for p in processes})==9 and len(frames)==13824 and scopes==18432
    intervals.sort();assert all(a[1]<=b[0] for a,b in zip(intervals,intervals[1:]))
    out=dict(status='diagnostic-correctness-passed',formalConclusion='not-tested',sourceSha=build['sourceSha'],processes=processes,groups=groups,
             logicalFrames=len(frames),verifiedArms=36,nativeScopes=scopes,longFrames=long_frames,
             equalWorkAcrossModes=True,fullOracleHistory=True,binaryHashesVerified=True,
             limitations=['One process per scene/probe mode; no independent replicate CI or stable causal conclusion.',
                'Latin probe ordering only partially balances time/cache effects; all four arm orderings are identical across probe modes.',
                'PresentMon access-denied attempt affects first process startup; no usable OS presentation capture.',
                'Application focus is sampled per Update; no causal focus intervention or compositor/driver attribution.',
                'Engine GPU zero remains unavailable; scope timing and engine/Present-wait boundaries differ.'])
    (root/'causal-audit.json').write_text(json.dumps(out,indent=2),encoding='utf-8')
    with (root/'causal-frames.csv').open('w',newline='',encoding='utf-8') as f:
        w=csv.DictWriter(f,fieldnames=list(frames[0]));w.writeheader();w.writerows(frames)
    print(json.dumps({k:v for k,v in out.items() if k not in ['processes','groups','longFrames']},indent=2))

if __name__=='__main__':audit(sys.argv[1])
