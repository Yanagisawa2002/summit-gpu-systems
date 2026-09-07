"""Reproduce focused checkpoint, calibration and exploratory long-frame audits."""
import json
import sys
from pathlib import Path
from analyze import align_engine

def audit(root):
    root=Path(root);long=[];checkpoints=0;calibrations=[];count=0
    for p in sorted((root/'formal-v1').glob('*/result.json')):
        r=json.loads(p.read_text());mapped,_=align_engine(r)
        available=r['allocationCounterAvailable']
        assert available==(r['allocationProbeCounterDelta']>=r['allocationProbeBytes'])
        calibrations.append({k:r[k] for k in ['processId','runtimeClrVersion','gcMode','gcMaxGeneration','incrementalGc','incrementalGcTimeSliceNanoseconds','allocationCounterAvailable','allocationProbeBytes','allocationProbeCounterDelta','allocationProbeMonoHeapDelta']})
        for a in r['runs']:
            cp=json.loads((p.parent/f"checkpoint-{a['block']}-{a['position']}-{a['arm']}.json").read_text())
            expected=a.copy();expected['checkpointMilliseconds']=0
            assert cp==expected;checkpoints+=1
            fs=a['frames']
            assert all(f['recordAllocatedBytes']>=0 if available else f['recordAllocatedBytes']==-1 for f in fs)
            for i,f in enumerate(fs):
                if f['engineCadenceMs']<=1000/60:continue
                count+=1
                if not a['arm'].startswith('new'):continue
                t=mapped.get(f['unityFrame'],{});next_t=mapped.get(f['unityFrame']+1,{})
                long.append(dict(scene=r['config']['scenario'],replicate=r['config']['processReplicate'],arm=a['arm'],block=a['block'],frame=f['frame'],measured=f['measured'],
                    cadenceMs=f['engineCadenceMs'],recordCpuMs=f['recordCpuMs'],nativeSceneMs=f['sceneGpu']['milliseconds'],
                    sourceEngineCpuMs=t.get('cpuFrameMs'),nextEngineCpuMs=next_t.get('cpuFrameMs'),sourceMainThreadMs=t.get('mainThreadMs'),nextMainThreadMs=next_t.get('mainThreadMs'),
                    sourceRenderThreadMs=t.get('renderThreadMs'),sourcePresentWaitMs=t.get('presentWaitMs'),sourceGpuFrameMs=t.get('gpuFrameMs'),
                    nearbyCompletedGcCycle=fs[min(i+1,len(fs)-1)]['gc0']>fs[max(0,i-1)]['gc0']))
    assert checkpoints==240 and len(calibrations)==15
    shares=[e['sourcePresentWaitMs']/e['cadenceMs']*100 for e in long if e['sourcePresentWaitMs'] is not None]
    result=dict(status='passed',checkpointCount=checkpoints,allCheckpointFieldsMatchFinalReportExceptOwnWriteDuration=True,
        calibrations=calibrations,allOverBudgetFrames=count,newQueryOverBudgetFrames=len(long),newQuerySteadyOverBudgetFrames=sum(e['measured'] for e in long),
        presentWaitSharePercentRange=[min(shares),max(shares)] if shares else None,newQueryLongFrames=long,
        limitations=['Present wait includes Unity Present and target-fps waits; it is not a trace of actual OS presentation.',
                     'Coroutine intervals straddle Update boundaries; adjacent engine CPU rows are included without causal attribution.',
                     'No nearby completed GC-cycle increment does not exclude incremental GC slices or other runtime work.',
                     'Three timestamp completions flush command buffers and synchronize workers. Their presence/count is held fixed, not assumed cost-free.'])
    (root/'focused-audit.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ['calibrations','newQueryLongFrames']},indent=2))

if __name__=='__main__':audit(sys.argv[1])
