"""Audit and summarize the bounded index diagnostic; no formal claim from new runs."""
import argparse,csv,hashlib,json,math,statistics as st,struct
from collections import Counter,defaultdict
from pathlib import Path

SCENES=('hotspot-dynamic','streaming-switch')
BASE=('diagnosticGraph','fullIndex','incrementalIndex','compactQuery','reservedQuery','additionalExactCsrValidation','originalTwoDraws')
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def stats(v):
    v=list(v);a=sorted(v)
    def q(p):
        x=(len(a)-1)*p;i=int(x);return a[i]+(a[min(i+1,len(a)-1)]-a[i])*(x-i)
    return dict(n=len(v),mean=st.mean(v),p50=q(.5),p95=q(.95),p99=q(.99),minimum=min(v),maximum=max(v))
def write_csv(p,rows):
    if not rows:return
    with p.open('w',newline='',encoding='utf-8-sig')as f:
        w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
def audit_run(folder,scene_root):
    r=read(folder/'result.json');cfg=r['config'];scene=cfg['scenario']
    assert r['status']=='complete' and not r['formalPerformanceEvidence'] and not r['development']
    assert (cfg['frames'],cfg['warmup'],cfg['seed'])==(384,64,927101)
    assert (r['renderWidth'],r['renderHeight'],r['renderDepthBits'],r['renderMsaa'])==(1280,720,24,1)
    assert not r['cameraHdr'] and not r['cameraMsaa']
    nscope=26 if cfg['phases'] else 7
    assert r['startupVerifiedScopes']==nscope and r['consumedNative']==384*nscope
    data=(folder/'history.bin').read_bytes();assert len(data)==384*112*4
    words=struct.unpack('<'+str(len(data)//4)+'I',data)
    oracle_path=scene_root/'oracles-v1'/('0-'+scene)/'expected.bin'
    oracle_data=oracle_path.read_bytes();oracle=struct.unpack('<'+str(len(oracle_data)//4)+'I',oracle_data)
    tokens=[]
    for f,row in enumerate(r['frames']):
        assert row['frame']==f and row['measured']==(f>=64)
        assert row['indexOrder']==f%2 and row['queryOrder']==f//2%2
        actual=words[f*112:(f+1)*112];expected=oracle[f*40:(f+1)*40]
        assert actual[:40]==expected and actual[40:80]==expected,'Every frame must match both representations and original oracle'
        assert tuple(row['state'])==actual[80:96]
        assert tuple(row['compactCsrCounters'])==actual[96:104] and tuple(row['reservedCsrCounters'])==actual[104:112]
        for c in (row['compactCsrCounters'],row['reservedCsrCounters']):
            assert c[0]==row['activeCount'] and c[7]==c[0]+c[1]
            assert all(x==0 for x in c[2:7]),'Exact membership/snapshot mismatch'
        assert row['compactCsrCounters'][1]==0 and row['compactCsrCounters'][7]<=262144
        assert row['reservedCsrCounters'][7]<=3*262144
        assert row['state'][8] in (0,1,16,17,20,21) or f==0
        assert row['uploadedBytes']==(262144*20 if row['changedSlots'] else 0)
        assert len(row['timing'])==nscope
        timings={t['name']:t for t in row['timing']};assert len(timings)==nscope
        outer=timings['diagnosticGraph']
        for t in timings.values():
            tokens.append(t['token']);assert t['status']=='Ready' and t['frequency']>0 and t['sourceFrame']==row['unityFrame']
            assert outer['beginTicks']<=t['beginTicks']<=t['endTicks']<=outer['endTicks']
            assert math.isclose(t['gpuMs'],(t['endTicks']-t['beginTicks'])*1000/t['frequency'],rel_tol=1e-8,abs_tol=1e-10)
            parent=timings['fullIndex'] if t['name'].startswith('full/') else timings['incrementalIndex'] if t['name'].startswith('incremental/') else outer
            assert parent['beginTicks']<=t['beginTicks']<=t['endTicks']<=parent['endTicks']
        if not r['allocationCounterValid']:assert row['recordAllocatedBytes']==-1
    assert len(tokens)==len(set(tokens))==384*nscope
    if scene=='streaming-switch':
        fixed={'load-request','logical-cancel-request','register','unregister','unload-request','first-use-render-submitted'}
        expected=[(16,'load-request',0),(16,'logical-cancel-request',0),(80,'load-request',0),(128,'register',0),(128,'first-use-render-submitted',0),(192,'load-request',1),(224,'unregister',0),(240,'register',1),(240,'first-use-render-submitted',1),(256,'unload-request',0),(320,'unregister',1),(352,'unload-request',1)]
        assert [(e['frame'],e['action'],e['bundle'])for e in r['contentEvents'] if e['action']in fixed]==expected
        actions=Counter(e['action']for e in r['contentEvents'])
        for action,count in [('disk-load-complete',3),('assets-ready',2),('canceled-discard-unload',1),('unload-complete',3)]:assert actions[action]==count
    else:assert not r['contentEvents']
    return r,dict(status='passed',scene=scene,phases=cfg['phases'],pid=r['pid'],sourceSha=cfg['sourceSha'],historySha256=sha(folder/'history.bin'),oracleSha256=sha(oracle_path),frames=384,exactMembershipFrames=384,nativeScopes=len(tokens),allocationCounterValid=r['allocationCounterValid'])

def analyze(scene_root,diagnostic,out):
    out.mkdir(parents=True,exist_ok=False)
    runs={};audits=[];details=[];phase_rows=[];cpu_rows=[];state_rows=[];order_rows=[];frame_rows=[]
    for scene in SCENES:
        for on in (False,True):
            folder=diagnostic/(scene+'-phases-'+('on'if on else'off'))
            r,a=audit_run(folder,scene_root);runs[(scene,on)]=r;audits.append(a)
            selected=[f for f in r['frames']if f['measured']]
            timings={name:stats(t['gpuMs']for f in selected for t in f['timing']if t['name']==name)for name in BASE}
            state=dict(scene=scene,phases=on,frames=len(selected),rebuildReasons=dict(Counter(f['state'][8]for f in selected)),
                nonemptyDispatches=dict(Counter(f['state'][15]for f in selected)),active=stats(f['activeCount']for f in selected),
                extent=stats(f['state'][10]for f in selected),physicalInvalid=stats(f['reservedCsrCounters'][1]for f in selected),
                physicalInvalidFraction=stats(f['reservedCsrCounters'][1]/f['reservedCsrCounters'][7]for f in selected),
                removedSinceRebuild=stats(f['state'][2]for f in selected),changedMembership=stats(f['state'][3]for f in selected),
                failedReservations=stats(f['state'][6]for f in selected),inspected=stats(f['state'][14]for f in selected))
            details.append(dict(scene=scene,phases=on,gpu=timings,state=state,allocation=dict(status=r['allocationCounterStatus'],probeReported=r['allocationProbeReportedBytes'],probeHeapDelta=r['allocationProbeHeapDelta'])))
            for metric in ('traceCpuMs','contentCpuMs','uploadCpuMs','recordCpuMs','fullRecordCpuMs','incrementalRecordCpuMs','compactQueryRecordCpuMs','reservedQueryRecordCpuMs','validationRecordCpuMs','renderSubmitCpuMs','pollCpuMs'):
                cpu_rows.append(dict(scene=scene,phases=on,metric=metric,**stats(f[metric]for f in selected)))
            for name in BASE:
                for order in (0,1):
                    axis='indexOrder'if name in ('fullIndex','incrementalIndex')else'queryOrder'
                    order_rows.append(dict(scene=scene,phases=on,metric=name,orderAxis=axis,order=order,**stats(t['gpuMs']for f in selected if f[axis]==order for t in f['timing']if t['name']==name)))
            for f in selected:
                t={t['name']:t['gpuMs']for t in f['timing']}
                frame_rows.append(dict(scene=scene,phases=on,frame=f['frame'],reason=f['state'][8],active=f['activeCount'],extent=f['state'][10],physicalInvalid=f['reservedCsrCounters'][1],removedSinceRebuild=f['state'][2],changedMembership=f['state'][3],failedReservations=f['state'][6],nonemptyDispatches=f['state'][15],**{n:t[n]for n in BASE}))
            if on:
                names=list(dict.fromkeys(t['name']for f in selected for t in f['timing']if'/'in t['name']))
                for group in ('all','rebuild','no-rebuild'):
                    frames=[f for f in selected if group=='all'or bool(f['state'][8])==(group=='rebuild')]
                    if not frames:continue
                    for name in names:
                        phase_rows.append(dict(scene=scene,group=group,stage=name,**stats(t['gpuMs']for f in frames for t in f['timing']if t['name']==name)))
                    for prefix,parent in [('full/','fullIndex'),('incremental/','incrementalIndex')]:
                        values=[]
                        for f in frames:
                            tt={t['name']:t['gpuMs']for t in f['timing']}
                            values.append(tt[parent]-sum(v for n,v in tt.items()if n.startswith(prefix)))
                        phase_rows.append(dict(scene=scene,group=group,stage=prefix+'enclosing-minus-subintervals',**stats(values)))
    assert sum(x['nativeScopes']for x in audits)==25344
    for scene in SCENES:
        a=runs[(scene,False)];b=runs[(scene,True)]
        assert all(x['state']==y['state'] and x['compactCsrCounters']==y['compactCsrCounters'] and x['reservedCsrCounters']==y['reservedCsrCounters'] for x,y in zip(a['frames'],b['frames']))
    formal=read(scene_root/'analysis-portable-v1/analysis.json')
    selected_comparisons=[c for c in formal['comparisons']if c['scene']in SCENES and (c['baseline'],c['candidate'])in [('old-full','old-incremental'),('new-full','new-incremental')]and c['metric']in ('engineCadenceMs','sceneGpu','indexGpu','queryGpu','recordCpuMs')]
    old_rows=list(csv.DictReader((scene_root/'analysis-portable-v1/block-statistics.csv').open(encoding='utf-8-sig')))
    old_summary=[]
    for scene in SCENES:
        for arm in ('old-full','old-incremental','new-full','new-incremental'):
            old_summary.append(dict(scene=scene,arm=arm,**{metric:st.mean(float(x['mean'])for x in old_rows if x['scene']==scene and x['arm']==arm and x['metric']==metric and x['scope']=='steady')for metric in ('engineCadenceMs','sceneGpu','indexGpu','queryGpu','recordCpuMs')}))
    result=dict(status='passed',diagnosticOnly=True,noNewFormalConfirmation=True,priorFormalComparisons=selected_comparisons,priorFormalMeans=old_summary,
        diagnostics=details,audits=audits,scope='Four fixed valid processes, one retained unchanged from v3. Descriptive paired-graph diagnostics only; no confidence intervals from correlated frames. Instrumented scope ends flush command buffers and synchronize worker threads.',
        provenance=dict(priorFormalAnalysisSha256=sha(scene_root/'analysis-portable-v1/analysis.json'),diagnosticReceiptSha256=sha(diagnostic/'receipt.json')))
    (out/'analysis.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    write_csv(out/'original-formal-means.csv',old_summary);write_csv(out/'diagnostic-frames.csv',frame_rows);write_csv(out/'stage-costs.csv',phase_rows);write_csv(out/'cpu-costs.csv',cpu_rows);write_csv(out/'order-costs.csv',order_rows)
    (out/'audit.json').write_text(json.dumps(dict(status='passed',frames=1536,exactMembershipChecks=3072,queryHistories=3072,nativeIntervals=25344,phaseStateHistoriesIdentical=True,audits=audits),indent=2),encoding='utf-8')
    print(json.dumps(dict(status='passed',frames=1536,nativeIntervals=25344,output=str(out.resolve()))))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--scene-evidence',type=Path,required=True);p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();analyze(a.scene_evidence,a.diagnostic,a.output)
