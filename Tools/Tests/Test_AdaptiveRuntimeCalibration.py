import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('calibration', Path(__file__).resolve().parents[1] / 'AdaptiveRuntimeCalibration.py')
calibration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(calibration)


def fixture(phase='discovery', seed=1):
    features = dict(workloadId='fixture/single', elementCount=128, binCount=16, concentration=1,
                    occupiedBinCount=1, maximumBinOccupancy=128, singleBinKey=7)
    variants = ['Direct', 'Radix', 'Radix', 'Direct'] if phase == 'discovery' else ['Direct', 'Radix', 'Adaptive', 'Adaptive', 'Radix', 'Direct']
    samples = []
    for variant in variants:
        for segment in range(6):
            for observation in range(3):
                samples.append(dict(sequence=len(samples), repeat=0, segment=segment, observation=observation,
                    variant=variant, selectedBackend='Radix' if variant == 'Adaptive' else variant,
                    reason='CalibratedCell' if variant == 'Adaptive' else 'forced-reference', switched=observation == 0,
                    features=features, gpuMs=1.0 if variant == 'Direct' else .8,
                    featureCpuMs=.01, uploadCpuMs=.01, recordCpuMs=.01, selectorCpuMs=.001,
                    switchStateCpuMs=.0001, submitCpuMs=.01, pipelineCpuMs=.04, managedAllocatedBytes=0,
                    timestampToken=len(samples)+1, timestampFrequency=1000000, timestampFence=1,
                    timestampBeginTicks=100, timestampEndTicks=1100, timestampElapsedTicks=1000,
                    timestampFlags=0, sourceFrame=1, resultFrame=2, deviceGeneration=1))
    controls = [dict(samples[0], variant='Control', timestampFlags=1) for _ in range(6)]
    return dict(schemaVersion=1, protocol=calibration.PROTOCOL, phase=phase, passed=True,
                runId='run-'+str(seed), seed=seed, framesPerSegment=3, repeats=1,
                primitiveCandidateId='WaveOps', samples=samples, controlSamples=controls,
                validationCount=12+len(variants)*6, measurementReadbackBytes=0,
                device=dict(schemaVersion=2, vendorId=0x1002, deviceId=0x7551, graphicsApi='Direct3D12',
                            graphicsVersion='DX12', driverVersion='driver-a'),
                environment=dict(unityVersion='unity-a', compilerIdentity='compiler-a', shaderIdentity='shader-a', buildIdentity='build-a'),
                persistentGpuScratchBytes=1024, cpuFeatureScratchBytes=64)

class CalibrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
    def tearDown(self):
        self.temp.cleanup()
    def write(self, name, data):
        path = self.root/name
        path.write_text(json.dumps(data), encoding='utf-8')
        return path
    def test_freeze_and_untouched_evaluation(self):
        source = self.write('discovery.json', fixture())
        matrix_path = self.root/'matrix.json'
        matrix = calibration.freeze([source], matrix_path)
        self.assertEqual(matrix['rows'][0]['backend'], 1)
        evaluation = fixture('evaluation', 2)
        evaluation['matrixSha256'] = hashlib.sha256(matrix_path.read_bytes()).hexdigest()
        summary = calibration.summarize(self.write('evaluation.json', evaluation), matrix_path, self.root/'summary.json')
        self.assertEqual(summary['variants']['Adaptive']['samples'], 36)
        self.assertEqual(len(summary['transitions']), 36)
        self.assertEqual(matrix_path.read_text(), json.dumps(matrix, indent=2)+'\n')
    def test_smoke_never_becomes_performance_calibration(self):
        with self.assertRaises(ValueError): calibration.freeze([self.write('smoke.json', fixture('smoke'))], self.root/'matrix.json')
    def test_reject_duplicate_reports(self):
        source = self.write('d.json', fixture())
        with self.assertRaises(ValueError): calibration.freeze([source, source], self.root/'matrix.json')
    def test_reject_incompatible_builds(self):
        second = fixture(seed=2); second['environment']['buildIdentity'] = 'changed'
        with self.assertRaises(ValueError): calibration.freeze([self.write('a.json', fixture()), self.write('b.json', second)], self.root/'matrix.json')
    def test_p99_guard_keeps_direct(self):
        report = fixture()
        next(s for s in report['samples'] if s['variant'] == 'Radix')['gpuMs'] = 3
        matrix = calibration.freeze([self.write('d.json', report)], self.root/'matrix.json')
        self.assertEqual(matrix['rows'][0]['backend'], 0)
    def test_reject_reused_evaluation_seed_and_changed_matrix(self):
        matrix_path = self.root/'matrix.json'
        calibration.freeze([self.write('d.json', fixture())], matrix_path)
        evaluation = fixture('evaluation', 1)
        evaluation['matrixSha256'] = hashlib.sha256(matrix_path.read_bytes()).hexdigest()
        with self.assertRaises(ValueError): calibration.summarize(self.write('e.json', evaluation), matrix_path, self.root/'summary.json')
        evaluation['seed'] = 2; evaluation['matrixSha256'] = 'unrelated'
        with self.assertRaises(ValueError): calibration.summarize(self.write('e.json', evaluation), matrix_path, self.root/'summary.json')
    def test_missing_driver_controls_and_bad_raw_timestamps_are_rejected(self):
        for field in ('driver', 'control', 'ticks', 'partial', 'failed'):
            report = fixture()
            if field == 'driver': report['device']['driverVersion'] = ''
            elif field == 'control': report['controlSamples'] = []
            elif field == 'ticks': report['samples'][0]['timestampEndTicks'] = 0
            elif field == 'partial': report['samples'].pop()
            else: report['passed'] = False
            with self.assertRaises(ValueError): calibration.freeze([self.write('d.json', report)], self.root/'matrix.json')
    def test_output_cannot_overwrite_frozen_evidence(self):
        source = self.write('d.json', fixture()); output = self.root/'matrix.json'
        calibration.freeze([source], output)
        with self.assertRaises(FileExistsError): calibration.freeze([source], output)

if __name__ == '__main__':
    unittest.main()
