import unittest
from analyze import summary, paired, align_engine

P=dict(blocks=4,cvLimitPercent=5,baselineDriftLimitPercent=15,p95SpeedRatioMinimum=1.01)
class AnalysisTests(unittest.TestCase):
    def test_hand_calculated_ratio_and_unit(self):
        pairs=[[(summary([20,20]),summary([10,10])) for _ in range(4)] for _ in range(5)]
        r=paired(pairs,P)
        self.assertAlmostEqual(r['speedRatio'],2)
        self.assertEqual(r['ci95'],[2,2])
        self.assertEqual(r['processes'],5)
        self.assertEqual(r['baselineCvPercent'],0)
        self.assertEqual(r['status'],'passes-frozen-metric-gates')
    def test_missing_process_or_block_cannot_pass(self):
        p=[[(summary([20]),summary([10]))]*4]*4
        self.assertEqual(paired(p,P)['status'],'inconclusive')
        p.append(p[0][:-1])
        self.assertEqual(paired(p,P)['status'],'inconclusive')
    def test_drift_and_variability_reject_mean_gain(self):
        p=[[(summary([20*(1+.1*b)*(1+.1*i)]),summary([10])) for b in range(4)] for i in range(5)]
        r=paired(p,P)
        self.assertFalse(r['gates']['baselineDrift'])
        self.assertFalse(r['gates']['baselineCv'])
    def test_quantiles_and_tail_counts(self):
        r=summary([0,10,20,30,40])
        self.assertEqual(r['p50'],20);self.assertEqual(r['p95'],38)
        self.assertEqual(r['over16_67ms'],3)
    def test_async_source_alignment_not_observation(self):
        r=dict(qpcFrequency=10,engineCpuTimerFrequency=10,
               processFrames=[dict(qpc=100,unityFrame=1),dict(qpc=200,unityFrame=2),dict(qpc=300,unityFrame=3)],
               engineTimings=[dict(frameStartTimestamp=150,observedUnityFrame=99,gpuFrameMs=5)])
        m,a=align_engine(r)
        self.assertEqual(m[2]['gpuFrameMs'],5);self.assertNotIn(99,m)
        r['engineTimings'].append(dict(frameStartTimestamp=151))
        self.assertEqual(align_engine(r)[0],{})
        r['engineCpuTimerFrequency']=11
        self.assertEqual(align_engine(r)[1]['status'],'unavailable')
if __name__=='__main__': unittest.main()
