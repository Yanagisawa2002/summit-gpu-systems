import importlib.util
import math
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('summary', Path(__file__).parents[1] / 'Summarize_UnifiedMicrobenchmark.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)

class ClusterStatistics(unittest.TestCase):
    def test_constant_paired_speed_is_exact_and_confirmed(self):
        g = {(p,b,a): [dict(gpuTotalMs=v)] * 30 for p in range(5) for b in range(4) for a,v in [('a',2.),('b',1.)]}
        c = m.compare(g, 'a', 'b', 'gpuTotalMs')
        self.assertAlmostEqual(c['speedRatio'], 2.)
        self.assertAlmostEqual(c['ci95Low'], 2.)
        self.assertEqual(c['verdict'], 'confirmed-per-cell')
    def test_correlated_frame_duplication_cannot_narrow_process_ci(self):
        g = {(p,b,a): [dict(gpuTotalMs=v)] * 30 for p in range(5) for b in range(4) for a,v in [('a',2.),('b',1.+p*.1)]}
        c = m.compare(g, 'a', 'b', 'gpuTotalMs')
        d = m.compare({k:v*20 for k,v in g.items()}, 'a', 'b', 'gpuTotalMs')
        self.assertAlmostEqual(c['ci95Low'], d['ci95Low'])
        self.assertEqual(c['verdict'], 'inconclusive')
    def test_drift_disqualifies_large_speedup(self):
        g = {(p,b,a): [dict(gpuTotalMs=v)] * 30 for p in range(5) for b in range(4) for a,v in [('a',2.+b*.3),('b',1.)]}
        c = m.compare(g, 'a', 'b', 'gpuTotalMs')
        self.assertIn('baseline drift', c['gateFailures'])
        self.assertEqual(c['verdict'], 'inconclusive')
    def test_missing_process_is_rejected(self):
        with self.assertRaises(ValueError): m.paired_ci([math.log(2)]*4)

if __name__ == '__main__': unittest.main()
