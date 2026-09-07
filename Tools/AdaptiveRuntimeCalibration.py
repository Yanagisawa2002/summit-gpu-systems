#!/usr/bin/env python3
"""Freeze discovery evidence and summarize untouched adaptive evaluation (stdlib only)."""
import argparse
import hashlib
import json
import math
from pathlib import Path
from collections import defaultdict

PROTOCOL = 'r9700-adaptive-runtime-v1'
FEATURE_FIELDS = ('workloadId', 'elementCount', 'binCount', 'concentration',
                  'occupiedBinCount', 'maximumBinOccupancy', 'singleBinKey')

def key(features):
    return tuple(features[name] for name in FEATURE_FIELDS)

def percentile(values, fraction):
    values = sorted(values)
    if not values or any(not math.isfinite(x) or x < 0 for x in values):
        raise ValueError('Samples must be finite, non-negative and non-empty')
    return values[max(0, math.ceil(len(values) * fraction) - 1)]

def read_report(path, phase):
    report = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    if (report.get('schemaVersion') != 1 or report.get('protocol') != PROTOCOL or
        report.get('phase') != phase or report.get('passed') is not True or
        report.get('measurementReadbackBytes') != 0 or not report.get('samples')):
        raise ValueError('Failed, wrong-phase, incomplete or incompatible report: ' + str(path))
    device, env = report['device'], report['environment']
    if (device.get('schemaVersion') != 2 or device.get('vendorId') != 0x1002 or
        device.get('deviceId') != 0x7551 or device.get('graphicsApi') != 'Direct3D12' or
        not device.get('driverVersion') or not device.get('graphicsVersion') or
        any(not env.get(f) for f in ('unityVersion', 'compilerIdentity', 'shaderIdentity', 'buildIdentity'))):
        raise ValueError('Missing independent device/driver/compiler/shader/build identity')
    frames, repeats = report['framesPerSegment'], report['repeats']
    variants = 4 if phase == 'discovery' else 6
    if (frames < 3 or repeats < 1 or len(report['samples']) != frames * repeats * variants * 6 or
        report.get('validationCount', 0) != 12 + repeats * variants * 6):
        raise ValueError('Incomplete counterbalanced trace or CSR validation')
    if len(report.get('controlSamples', [])) != 6 or any(s.get('timestampFlags') != 1 for s in report['controlSamples']):
        raise ValueError('Missing pre/post empty-scope controls')
    for i, sample in enumerate(report['samples']):
        if (sample['sequence'] != i or not sample.get('timestampToken') or
            not sample.get('timestampFrequency') or not sample.get('timestampFence')):
            raise ValueError('Incomplete GPU timestamp sequence')
        for metric in ('gpuMs', 'featureCpuMs', 'uploadCpuMs', 'recordCpuMs', 'selectorCpuMs',
                       'switchStateCpuMs', 'submitCpuMs', 'pipelineCpuMs'):
            percentile([sample[metric]], 0.5)
        key(sample['features'])
    for sample in report['samples'] + report['controlSamples']:
        if (sample.get('timestampEndTicks', 0) < sample.get('timestampBeginTicks', 0) or
            sample.get('timestampElapsedTicks') != sample.get('timestampEndTicks', 0) - sample.get('timestampBeginTicks', 0) or
            not sample.get('timestampFrequency') or not sample.get('deviceGeneration') or
            sample.get('resultFrame', -1) < sample.get('sourceFrame', 0)):
            raise ValueError('Invalid raw timestamp evidence')
    return report

def freeze(paths, output, minimum_improvement=1.0, maximum_p99_regression=2.0):
    if (not math.isfinite(minimum_improvement) or not math.isfinite(maximum_p99_regression) or
        minimum_improvement < 0 or maximum_p99_regression < 0):
        raise ValueError('Selection thresholds cannot be negative')
    groups = defaultdict(list)
    reference = None
    evidence, seeds, run_ids = [], [], []
    for path in paths:
        report = read_report(path, 'discovery')
        identity = (report['device'], report['environment'], report['primitiveCandidateId'])
        if reference is not None and identity != reference:
            raise ValueError('Discovery reports do not share identical device/build/candidate identity')
        if report['runId'] in run_ids:
            raise ValueError('Duplicate discovery run cannot inflate calibration sample counts')
        reference = identity
        run_ids.append(report['runId']); seeds.append(report['seed'])
        evidence.append(hashlib.sha256(Path(path).read_bytes()).hexdigest())
        for sample in report['samples']:
            if sample['variant'] not in ('Direct', 'Radix') or sample['selectedBackend'] != sample['variant']:
                raise ValueError('Discovery must use forced executable candidates')
            groups[key(sample['features'])].append(sample)
    if reference is None:
        raise ValueError('Discovery reports required')
    rows = []
    for feature_key, samples in sorted(groups.items()):
        direct = [s['gpuMs'] for s in samples if s['variant'] == 'Direct']
        radix = [s['gpuMs'] for s in samples if s['variant'] == 'Radix']
        if min(len(direct), len(radix)) < 12:
            raise ValueError('Each cell requires at least 12 native samples per validated candidate')
        baseline = percentile(direct, .5)
        median = percentile(radix, .5)
        improvement = (baseline - median) * 100 / baseline if baseline else 0
        select_radix = (baseline > 0 and improvement >= minimum_improvement and median < baseline and
                        percentile(radix, .99) <= percentile(direct, .99) * (1 + maximum_p99_regression / 100))
        rows.append(dict(features=dict(zip(FEATURE_FIELDS, feature_key)), backend=1 if select_radix else 0,
                         primitiveCandidateId=reference[2], validationPassed=True,
                         evidenceId='sha256:' + '+'.join(evidence),
                         calibrationSamples=min(len(direct), len(radix)),
                         calibrationDirectMedianMs=baseline, calibrationRadixMedianMs=median,
                         calibrationDirectP99Ms=percentile(direct, .99), calibrationRadixP99Ms=percentile(radix, .99)))
    matrix = dict(schemaVersion=3, matrixId='r9700-adaptive-runtime-matrix-v3', revision=1, phase='frozen',
                  calibrationRunId='+'.join(run_ids), calibrationSeeds=sorted(set(seeds)),
                  device=reference[0], environment=reference[1], profilerMarkersEnabled=False, rows=rows,
                  protocol=PROTOCOL, minimumMedianImprovementPercent=minimum_improvement,
                  maximumP99RegressionPercent=maximum_p99_regression,
                  validationScope='Exact histograms and workload IDs only; frozen evaluation required; no production default promotion')
    write_new(output, matrix)
    return matrix

def summarize(path, matrix_path, output):
    report = read_report(path, 'evaluation')
    matrix_bytes = Path(matrix_path).read_bytes()
    matrix = json.loads(matrix_bytes.decode('utf-8-sig'))
    if (hashlib.sha256(matrix_bytes).hexdigest() != report['matrixSha256'] or
        matrix['device'] != report['device'] or matrix['environment'] != report['environment'] or
        report['seed'] in matrix['calibrationSeeds']):
        raise ValueError('Evaluation matrix/identity mismatch or reused discovery seed')
    by_variant = defaultdict(list)
    forced = defaultdict(list)
    for sample in report['samples']:
        by_variant[sample['variant']].append(sample)
        if sample['variant'] != 'Adaptive':
            forced[(sample['variant'], sample['repeat'], sample['segment'], sample['observation'], key(sample['features']))].append(sample)
    summary = dict(protocol=PROTOCOL, matrixSha256=report['matrixSha256'], runId=report['runId'], seed=report['seed'],
                   persistentGpuScratchBytes=report['persistentGpuScratchBytes'], cpuFeatureScratchBytes=report['cpuFeatureScratchBytes'],
                   mode='drained-per-sample latency; not saturated throughput', variants={}, transitions=[],
                   emptyControlGpuMs=dict(median=percentile([s['gpuMs'] for s in report['controlSamples']], .5),
                                          p99=percentile([s['gpuMs'] for s in report['controlSamples']], .99)))
    for variant, samples in by_variant.items():
        summary['variants'][variant] = dict(samples=len(samples), switches=sum(s['switched'] for s in samples),
            maximumManagedAllocatedBytes=max(s['managedAllocatedBytes'] for s in samples),
            reasons={reason: sum(s['reason'] == reason for s in samples) for reason in sorted({s['reason'] for s in samples})})
        for metric in ('gpuMs', 'pipelineCpuMs', 'featureCpuMs', 'uploadCpuMs', 'recordCpuMs', 'selectorCpuMs', 'switchStateCpuMs', 'submitCpuMs'):
            summary['variants'][variant][metric] = dict(median=percentile([s[metric] for s in samples], .5), p99=percentile([s[metric] for s in samples], .99))
    for sample in by_variant['Adaptive']:
        reference = forced[(sample['selectedBackend'], sample['repeat'], sample['segment'], sample['observation'], key(sample['features']))]
        if not reference:
            raise ValueError('Missing identical-input forced reference')
        summary['transitions'].append(dict(sequence=sample['sequence'], segment=sample['segment'],
            observation=sample['observation'], selectedBackend=sample['selectedBackend'], reason=sample['reason'], switched=sample['switched'],
            selectorCpuMs=sample['selectorCpuMs'], switchStateCpuMs=sample['switchStateCpuMs'],
            recordCpuDeltaVsSelectedForcedMs=sample['recordCpuMs'] - percentile([s['recordCpuMs'] for s in reference], .5),
            gpuDeltaVsSelectedForcedMs=sample['gpuMs'] - percentile([s['gpuMs'] for s in reference], .5)))
    write_new(output, summary)
    return summary

def write_new(path, document):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(document, stream, indent=2)
        stream.write('\n')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    calibrate = commands.add_parser('freeze')
    calibrate.add_argument('--reports', nargs='+', required=True)
    calibrate.add_argument('--output', required=True)
    calibrate.add_argument('--minimum-improvement', type=float, default=1.0)
    calibrate.add_argument('--maximum-p99-regression', type=float, default=2.0)
    compare = commands.add_parser('summarize')
    compare.add_argument('--report', required=True); compare.add_argument('--matrix', required=True); compare.add_argument('--output', required=True)
    args = parser.parse_args()
    if args.command == 'freeze':
        freeze(args.reports, args.output, args.minimum_improvement, args.maximum_p99_regression)
    else:
        summarize(args.report, args.matrix, args.output)

if __name__ == '__main__':
    main()
