import unittest
from analyze_query_boundary import compare, quantile


class ComparisonTests(unittest.TestCase):
    def test_stable_loss_is_not_hidden(self):
        r=compare([1]*5,[2]*5)
        self.assertEqual(r['decision'],'scan-faster')
        self.assertEqual(r['scanOverIndexed'],.5)

    def test_large_noisy_gain_cannot_override_cv_gate(self):
        r=compare([10,10,20,10,10],[1]*5)
        self.assertGreater(r['ci95'][0],1)
        self.assertFalse(r['gates']['cv'])
        self.assertEqual(r['decision'],'inconclusive')

    def test_unfocused_business_cannot_be_promoted(self):
        r=compare([2]*5,[1]*5,False)
        self.assertEqual(r['decision'],'inconclusive')

    def test_independent_unit_must_be_five_processes(self):
        with self.assertRaises(AssertionError):compare([1]*128,[2]*128)

    def test_tail_interpolates_per_process(self):
        self.assertAlmostEqual(quantile([0,10,20,30,40],.95),38)


if __name__=='__main__':unittest.main()
