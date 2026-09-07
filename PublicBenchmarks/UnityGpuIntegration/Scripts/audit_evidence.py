"""Independent raw-evidence and process-ratio audit; does not import analyze.py."""
import hashlib
import json
import math
import statistics
import struct
import sys
from pathlib import Path

def read(p): return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def digest(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def audit(root):
    root=Path(root);protocol=read(root/'formal-v1/protocol.json');analysis=read(root/'analysis-v1/analysis.json')
    build=read(root/'build-release-v1/build-attestation.json');release=read(root/'build-release-v1/release-build.json')
    freeze=read(root/'freeze.json')
    assert build['status']=='built' and not build['sourceDirty'] and not build['development']
    assert build['sourceSha']==freeze['sourceSha']==release['sourceCommit']
    assert digest(root/'formal-v1/protocol.json')==freeze['protocolSha256']
    for f in build['files']:
        path=root/'build-release-v1'/f['path']
        assert path.stat().st_size==f['bytes'] and digest(path)==f['sha256']
    all_processes=[]; ids=[]; native_scopes=0; total_frames=0; lookup={}; intervals=[]; gpu_coverage=[]
    for cell in read(root/'formal-v1/matrix.json'):
        folder=root/'formal-v1'/cell['id'];r=read(folder/'result.json');p=read(folder/'process.json');c=r['config']
        oracle=root/'oracles-v1'/cell['id']/'expected.bin';expected=oracle.read_bytes()
        assert len(expected)==384*10*16
        assert cell['status']==p['status']==r['status']=='completed' and p['exitCode']==0
        assert r['buildGuid']==release['buildGuid'] and c['sourceSha']==build['sourceSha']
        assert r['formalPerformanceEvidence'] and not r['development'] and r['graphicsApi']=='Direct3D12'
        assert c['seed']==protocol['processSeeds'][c['processReplicate']] and len(r['runs'])==16
        assert digest(folder/'config.json')==p['configSha256']
        ids.append(r['processId']);intervals.append((r['startedUtc'],r['endedUtc']))
        tuples=set()
        for a in r['runs']:
            fs=a['frames'];assert len(fs)==384 and a['verified'] and a['verifiedDigestWords']==3840
            assert (folder/f"{a['block']}-{a['position']}-{a['arm']}.history.bin").read_bytes()==expected
            tuples.add((a['block'],a['arm']))
            assert a['arm']==protocol['arms'][protocol['orders'][(a['block']+c['processReplicate'])%4][a['position']]]
            for i,f in enumerate(fs):
                assert f['frame']==i and f['qpcEnd']>f['qpcStart'] and f['drawVertices']==262144 and f['overlayVertices']==54 and f['drawCalls']==2
                sentinel=struct.unpack_from('<4I',expected,(i*10+9)*16)
                assert sentinel==(i,f['activeCount'],262144,f['changedSlots'])
                scene,index,query=[f[k] for k in ['sceneGpu','indexGpu','queryGpu']]
                assert scene['beginTicks']<=index['beginTicks']<index['endTicks']<=query['beginTicks']<query['endTicks']<=scene['endTicks']
                for n in [scene,index,query]:
                    assert n['status']=='Ready' and n['sourceFrame']==f['unityFrame'] and n['frequency']==scene['frequency']
                    assert math.isclose(n['milliseconds'],(n['endTicks']-n['beginTicks'])*1000/n['frequency'],rel_tol=1e-12)
                native_scopes+=3;total_frames+=1
            lookup[c['scenario'],c['processReplicate'],a['block'],a['arm']]=statistics.mean(f['engineCadenceMs'] for f in fs[64:])
        assert len(tuples)==16
        all_processes.append(dict(id=cell['id'],processId=r['processId'],startedUtc=r['startedUtc'],endedUtc=r['endedUtc'],resultSha256=digest(folder/'result.json')))
    assert len(ids)==len(set(ids))==15 and total_frames==92160 and native_scopes==276480
    intervals.sort()
    assert all(a[1]<=b[0] for a,b in zip(intervals,intervals[1:]))
    checked=0
    for comparison in analysis['comparisons']:
        if comparison['metric']!='engineCadenceMs': continue
        scene= comparison['scene']; baseline=comparison['baseline'];candidate=comparison['candidate']
        logs=[]
        for rep in range(5):
            logs.append(sum(math.log(lookup[scene,rep,b,baseline]/lookup[scene,rep,b,candidate]) for b in range(4))/4)
        mean=sum(logs)/5
        sd=math.sqrt(sum((x-mean)**2 for x in logs)/4)
        ci=[math.exp(mean-2.7764451051977987*sd/math.sqrt(5)),math.exp(mean+2.7764451051977987*sd/math.sqrt(5))]
        assert math.isclose(math.exp(mean),comparison['speedRatio'],rel_tol=1e-12)
        assert all(math.isclose(x,y,rel_tol=1e-12) for x,y in zip(ci,comparison['ci95']))
        checked+=1
    assert checked==15 and len(analysis['failures'])==0
    output=dict(status='passed',measuredSourceSha=build['sourceSha'],processes=all_processes,
                formalProcessCount=15,arms=240,frames=total_frames,nativeScopes=native_scopes,
                independentlyRecomputedCadenceContrasts=checked,binaryInventoryVerified=True,
                intervalNonOverlapVerified=True,fullDigestHistoryVerified=True,formalFailures=0)
    (root/'independent-audit.json').write_text(json.dumps(output,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in output.items() if k!='processes'}))

if __name__=='__main__':audit(sys.argv[1])
