"""Audit fixed independent query/transport runs; retain losses and failed gates.

Standard library only. Ratios are scan time / indexed-arm time, so >1 favors
the indexed arm. Intervals are nominal per-comparison, not familywise guarantees.
"""
import argparse
import csv
import hashlib
import json
import math
import statistics as st
import struct
from pathlib import Path


def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def save(p, obj):
    p.write_text(json.dumps(obj, indent=2)+'\n', encoding='utf-8')


def quantile(v, q):
    v=sorted(v);x=(len(v)-1)*q;i=int(x)
    return v[i]+(v[min(i+1,len(v)-1)]-v[i])*(x-i)


def digest_rows(p):
    return list(struct.iter_unpack('<4I', p.read_bytes()))


def compare(reference, candidate, context_ok=True):
    assert len(reference)==len(candidate)==5 and all(v>0 and math.isfinite(v) for v in reference+candidate)
    logs=[math.log(a/b) for a,b in zip(reference,candidate)]
    mid=st.mean(logs);half=2.7764451051977987*st.stdev(logs)/math.sqrt(5)
    ci=[math.exp(mid-half),math.exp(mid+half)]
    cv=[st.stdev(v)/st.mean(v)*100 for v in [reference,candidate]]
    drift=[abs(v[-1]/v[0]-1)*100 for v in [reference,candidate]]
    gates=dict(cv=all(x<=5 for x in cv),drift=all(x<=15 for x in drift),context=context_ok,ciExcludesOne=ci[0]>1 or ci[1]<1)
    decision=('indexed-faster' if ci[0]>1 else 'scan-faster') if all(gates.values()) else 'inconclusive'
    return dict(scanMeanMs=st.mean(reference),indexedMeanMs=st.mean(candidate),scanOverIndexed=math.exp(mid),ci95=ci,
                scanCvPercent=cv[0],indexedCvPercent=cv[1],scanDriftPercent=drift[0],indexedDriftPercent=drift[1],
                gates=gates,decision=decision,scanValues=reference,indexedValues=candidate)


def analyze(root,out):
    out.mkdir(parents=True,exist_ok=False)
    matrix=read(root/'matrix.json');protocol=read(root/'protocol.json')
    assert matrix['status']=='completed' and len(matrix['runs'])==45
    assert hashlib.sha256((root/'protocol.json').read_bytes()).hexdigest().lower()==matrix['protocolSha256'].lower()
    assert protocol['cvLimitPercent']==5 and protocol['driftLimitPercent']==15
    expected_order=[]
    for rep in range(5):
        expected_order.append(f'{rep}-boundary')
        for d in range(2):
            dist=protocol['businessDistributions'][(d+rep)%2]
            for a in range(4):
                pos=a if rep%2==0 else 3-a
                expected_order.append(f'{rep}-{dist}-{protocol["arms"][(pos+rep)%4]}')
    assert [r['id'] for r in matrix['runs']]==expected_order
    boundary=[];business=[];jobs=[];boundary_lookup={};business_lookup={}
    for entry in matrix['runs']:
        assert entry['status']=='completed'
        folder=root/entry['id'];r=read(folder/'player/result.json');receipt=read(folder/'receipt.json');cfg=r['config']
        assert receipt['status']=='completed' and r['status']=='completed' and r['verified']
        assert cfg['sourceSha']==matrix['sourceSha']==receipt['config']['sourceSha']
        assert cfg['seed']==protocol['seeds'][entry['replicate']] and cfg['replicate']==entry['replicate']
        assert cfg['mode']==entry['mode'] and cfg['arm']==entry['arm'] and cfg['distribution']==entry['distribution']
        assert cfg['blocks']==3 and cfg['repeats']==16 and cfg['jobs']==128 and not cfg['corruptOracle']
        assert r['device']=='AMD Radeon AI PRO R9700' and r['api']=='Direct3D12' and not r['development'] and not r['osPresentationAvailable']
        rep=cfg['replicate']
        if cfg['mode']=='boundary':
            assert len(r['samples'])==72
            for case in protocol['boundaryCases']:
                samples=[s for s in r['samples'] if s['workload']==case]
                assert len(samples)==12
                expected=digest_rows(folder/f'player/{case}-oracle.bin')
                assert len(expected)==(1 if case=='uniform-one' else 9)
                actual=digest_rows(folder/f'player/{case}-actual.bin')
                assert actual==expected*12
                for i,s in enumerate(samples):
                    b,pos=divmod(i,4);offset=(rep+b)%4
                    arm=protocol['arms'][(offset+(pos if (rep+b)%2==0 else 3-pos))%4]
                    assert s['block']==b and s['order']==pos and s['arm']==arm
                    assert s['verified'] and s['counts']==[d[0] for d in expected] and s['queries']==len(expected)
                    assert s['repeats']==16 and s['endTicks']>s['startTicks']
                    assert math.isclose(s['submitToReadbackMs'],(s['endTicks']-s['startTicks'])*1000/r['frequency'],abs_tol=1e-8)
                for arm in protocol['arms']:
                    values=[s for s in samples if s['arm']==arm]
                    row=dict(replicate=rep,workload=case,arm=arm,hostMsPerQueryBatch=st.median(s['submitToReadbackMs']/16 for s in values),
                             recordMsPerQueryBatch=st.median(s['recordMs']/16 for s in values),focused=all(s['focused'] for s in values))
                    boundary.append(row);boundary_lookup[rep,case,arm]=row
        else:
            assert r['completed']==r['delivered']==len(r['orders'])==128
            assert (folder/'player/business-actual.bin').read_bytes()==(folder/'player/business-oracle.bin').read_bytes()
            expected=digest_rows(folder/'player/business-oracle.bin');assert len(expected)==128*9
            previous=0
            for i,o in enumerate(r['orders']):
                assert o['job']==i and o['verified'] and o['routeMask']==o['expectedRouteMask']==1<<(i%9)
                assert o['counts']==[x[0] for x in expected[i*9:i*9+9]]
                assert o['routeMask']==sum(1<<q for q in range(9) if o['counts'][q]>r['thresholds'][q])
                assert math.isclose(o['arrivalMs'],i*1000/30,abs_tol=1e-9)
                assert o['renderedDecisionMs']>=o['decisionMs']>=o['resultReadyMs']>=o['submitMs']>=max(o['arrivalMs'],previous)
                assert o['deliveredMs']>=o['decisionMs']+700
                for ticks,ms in [('submitTicks','submitMs'),('decisionTicks','decisionMs')]:
                    assert math.isclose(o[ms],(o[ticks]-r['startTicks'])*1000/r['frequency'],abs_tol=1e-8)
                assert o['submitTicks']<=o['readbackTicks']<=o['decisionTicks']
                previous=o['decisionMs'];jobs.append(dict(run=entry['id'],**o))
            orders=r['orders'];decision=[o['decisionMs']-o['arrivalMs'] for o in orders]
            row=dict(replicate=rep,distribution=cfg['distribution'],arm=cfg['arm'],verifiedOrders=128,verifiedCarts=1152,
                     decisionP50Ms=quantile(decision,.5),decisionP95Ms=quantile(decision,.95),
                     frameP95Ms=quantile([o['renderedDecisionMs']-o['arrivalMs'] for o in orders],.95),
                     transportP95Ms=quantile([o['deliveredMs']-o['arrivalMs'] for o in orders],.95),
                     over100msPercent=sum(v>100 for v in decision)/128*100,cartWaitSeconds=sum(decision)*9/1000,
                     meanServiceMs=st.mean(o['decisionMs']-o['submitMs'] for o in orders),
                     meanPrepareMs=st.mean(o['prepareMs'] for o in orders),meanUploadMs=st.mean(o['uploadMs'] for o in orders),
                     meanRecordMs=st.mean(o['recordMs'] for o in orders),unfocusedUpdates=r['unfocusedUpdates'],
                     finishMs=r['finishMs'],transportFinishMs=r['transportFinishMs'])
            assert r['finishMs']==orders[-1]['decisionMs'] and r['transportFinishMs']==max(o['deliveredMs'] for o in orders)
            business.append(row);business_lookup[rep,cfg['distribution'],cfg['arm']]=row
    boundaries=[];consumers=[]
    for case in protocol['boundaryCases']:
        for arm in ['cell','chunks','batch']:
            rows=[[boundary_lookup[rep,case,a] for rep in range(5)] for a in ['scan',arm]]
            comparison=compare(*[[r['hostMsPerQueryBatch'] for r in side] for side in rows])
            boundaries.append(dict(workload=case,arm=arm,focusedAtEverySample=all(r['focused'] for side in rows for r in side),**comparison))
    for dist in protocol['businessDistributions']:
        for arm in ['cell','chunks','batch']:
            rows=[[business_lookup[rep,dist,a] for rep in range(5)] for a in ['scan',arm]]
            for metric in ['decisionP95Ms','frameP95Ms','transportP95Ms']:
                comparison=compare(*[[r[metric] for r in side] for side in rows],context_ok=all(r['unfocusedUpdates']==0 for side in rows for r in side))
                consumers.append(dict(distribution=dist,arm=arm,metric=metric,**comparison))
    result=dict(sourceSha=matrix['sourceSha'],verifiedProcesses=45,verifiedBoundarySamples=360,verifiedBusinessOrders=5120,verifiedCarts=46080,
                boundaryComparisons=boundaries,businessComparisons=consumers,fullFrameSmoothnessClaim=False,
                independentUnit='five process replicates; 95% CIs are nominal per comparison, not multiple-comparison adjusted',
                queryScope=protocol['boundaryScope'],businessScope=protocol['businessScope'])
    save(out/'analysis.json',result)
    for name,data in [('boundary-processes.csv',boundary),('business-processes.csv',business),('orders.csv',jobs)]:
        with (out/name).open('w',newline='',encoding='utf-8') as f:
            writer=csv.DictWriter(f,fieldnames=list(data[0]));writer.writeheader();writer.writerows(data)
    print(json.dumps(dict(verifiedProcesses=45,boundaryDecisions=[(x['workload'],x['arm'],x['decision']) for x in boundaries],businessDecisions=[(x['distribution'],x['arm'],x['metric'],x['decision']) for x in consumers])))


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('matrix',type=Path);p.add_argument('output',type=Path)
    args=p.parse_args();analyze(args.matrix,args.output)
