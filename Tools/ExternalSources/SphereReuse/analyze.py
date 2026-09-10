"""Audit full native/GPU outputs and the predeclared three-arm process means."""
import argparse
import csv
import math
import statistics
import struct
import sys
from pathlib import Path

from prepare import ROOT, OLD, RUN, sha, save
from freeze import ORDERS, read

# Reuse the original binary ABI decoder, not a spatial-query CPU model.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from analyze_actual_replay import csr, reference


def gpu_audit(folder, references, expected_rows, validation, source=None, build=None):
    report = read(folder / 'gpu/result.json')
    if report['status'] != 'completed' or report['manifestSha256'] != sha(OLD / 'input-manifest.json'):
        raise ValueError('Incomplete GPU report or changed input: ' + str(folder))
    if len(report['rows']) != expected_rows:
        raise ValueError('Missing GPU repetitions: ' + str(folder))
    if source and (report['sourceCommit'] != source or report['buildGuid'] != build):
        raise ValueError('GPU source/build identity changed')
    audit = []
    for row in report['rows']:
        if not row['verified'] or (validation and (row['phase'] != 'validation' or row['hostWallMs'] != -1)):
            raise ValueError('Unverified GPU output or timed correctness')
        ref_name = f'{row["name"]}-t{max(row["step"], 0):02}' if report['kind'] == 'cabana' else 'arborx-default'
        path = folder / 'gpu' / f'{row["name"]}-{row["phase"]}-{row["step"]}.csr'
        data = path.read_bytes()
        if data[:8] != b'SMCSR001':
            raise ValueError('GPU output ABI changed')
        rows, total = struct.unpack_from('<II', data, 8)
        actual = csr(data, rows, total, 16)
        if actual != references[ref_name] or actual[2] != row['checksum'] or (rows, total) != (row['queries'], row['ids']):
            raise ValueError('Full offsets/IDs/multiplicity/checksum mismatch: ' + str(path))
        audit.append({'path': path.relative_to(ROOT).as_posix(), 'sha256': sha(path), 'rows': rows, 'ids': total, 'verified': True})
    if {ROOT / a['path'] for a in audit} != set((folder / 'gpu').glob('*.csr')):
        raise ValueError('Unaccounted GPU outputs')
    if len(audit) != len({a['path'] for a in audit}):
        raise ValueError('Duplicate GPU output records')
    return report, audit


def correctness(attempt, references):
    audit = []
    reports = []
    for kind, rows in (('arborx', 2), ('cabana', 40)):
        report, entries = gpu_audit(RUN / f'runs/validation-{kind}-{attempt}', references, rows, True)
        reports.append(report); audit.extend(entries)
    folder = RUN / f'runs/validation-api-{attempt}/gpu'
    api = read(folder / 'api-checks.json')['checks']
    api_report = read(folder / 'result.json')
    if api_report['status'] != 'completed' or not api or api[-1]['name'] != 'all-fixtures-completed' or not all(c['passed'] for c in api):
        raise ValueError('Untimed API fixtures failed')
    if len({r['buildGuid'] for r in reports + [api_report]}) != 1:
        raise ValueError('Correctness gates used different Players')
    return {'attempt': attempt, 'buildGuid': api_report['buildGuid'], 'apiChecks': api,
            'apiRawFiles': [{'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)} for p in sorted(folder.glob('*.csr'))],
            'fullCsrFiles': audit, 'arborxCompleteRepetitions': 2, 'cabanaCompleteSnapshots': 40,
            'method': 'Decode every GPU offset and every within-row sorted ID with multiplicity against the unchanged upstream full result; no sampling or spatial CPU model'}


def checked_samples(rows):
    if [(r['phase'], int(r['step'])) for r in rows] != [('warmup' if i < 0 else 'measured', i) for i in range(-10, 10)]:
        raise ValueError('Missing/reordered/selected warmups or measurements')
    if not all(math.isfinite(float(r['hostWallMs'])) and float(r['hostWallMs']) > 0 for r in rows):
        raise ValueError('Invalid host clock samples')
    samples = [r for r in rows if r['phase'] == 'measured']
    phases = {key: statistics.mean(float(r[key]) for r in samples) for key in samples[0] if key.endswith('Ms')}
    if 'uploadMs' in phases:
        phases['aggregationAndClockRemainderMs'] = phases['hostWallMs'] - sum(phases[k] for k in ('uploadMs', 'submitReadbackMs', 'consumeMs'))
    return {'meanMs': phases['hostWallMs'], 'minMaxMs': [min(float(r['hostWallMs']) for r in samples), max(float(r['hostWallMs']) for r in samples)],
            'measuredMs': [float(r['hostWallMs']) for r in samples], 'phasesMeanMs': phases}


def ratio(pairs, numerator):
    values = [p[numerator]['meanMs'] / p['new']['meanMs'] for p in pairs]
    logs = [math.log(r) for r in values]
    middle = statistics.mean(logs)
    margin = 3.182446305 * statistics.stdev(logs) / math.sqrt(4)
    return {'numerator': numerator, 'denominator': 'new', 'perRound': values,
            'geometricMean': math.exp(middle), 'nominalLogStudentT95CI': [math.exp(middle - margin), math.exp(middle + margin)], 'df': 3}


def main(attempt, validate_only):
    manifest = read(OLD / 'input-manifest.json')
    references = {f['name']: reference(Path(f['path'])) for f in manifest['files']}
    validation = correctness(attempt, references)
    if validate_only:
        save(RUN / f'correctness-audit-{attempt}.json', validation)
        print(f'PASS {len(validation["apiChecks"])} GPU API checks; independently audited 2 ArborX + 40 Cabana full outputs')
        return
    frozen = read(RUN / 'frozen-run.json')
    for entry in frozen['files'] + frozen['toolchain']:
        if sha(ROOT / entry['path']) != entry['sha256']:
            raise ValueError('Frozen identity changed: ' + entry['path'])
    if attempt != frozen['newPlayer']['attempt'] or validation['buildGuid'] != frozen['newPlayer']['buildGuid']:
        raise ValueError('Formal/correctness build identity differs')
    expected_names = {p['name'] for p in frozen['processes']}
    if {p.name for p in (RUN / 'runs').glob('r[0-9][0-9]-*')} != expected_names:
        raise ValueError('Extra/missing formal processes')
    audit = list(validation['fullCsrFiles'])
    pairs = []
    last_finish = ''
    for number, order in enumerate(ORDERS, 1):
        pair = {'round': number, 'order': order}
        for arm in order:
            name = f'r{number:02}-{arm}'
            folder = RUN / 'runs' / name
            plan = next(p for p in frozen['processes'] if p['name'] == name)
            receipt = read(folder / 'process.process.json')
            if not receipt['exited'] or receipt['exitCode'] != 0 or receipt.get('processorAffinityHex') != 'FF' or receipt.get('priorityClass') != 'Normal':
                raise ValueError('Process failure or inherited execution environment changed')
            if Path(receipt['executable']) != ROOT / plan['executable'] or receipt['arguments'] != plan['arguments'] or receipt['sha256'] != sha(ROOT / plan['executable']):
                raise ValueError('Frozen command/executable mismatch')
            if receipt['startedUtc'] < last_finish:
                raise ValueError('Process order/serialization mismatch')
            last_finish = receipt['finishedUtc']
            if arm == 'native':
                with (folder / 'native.csv').open() as stream:
                    rows = list(csv.DictReader(stream))
                if any(r['case'] != 'arborx-default' or r['verified'] != 'true' or r['checksum'] != references['arborx-default'][2] for r in rows):
                    raise ValueError('Native full-result correctness failed')
            else:
                identity = frozen[arm + 'Player']
                report, entries = gpu_audit(folder, references, 20, False, identity['sourceCommit'], identity['buildGuid'])
                if any(report[k] != v for k, v in frozen['device'].items()):
                    raise ValueError('GPU/CPU device environment changed')
                audit.extend(entries); rows = report['rows']
                if arm == 'new':
                    if [r['pointGeneration'] for r in rows] != list(range(1, 21)):
                        raise ValueError('Each whole task must prepare its own point generation')
                    for r in rows:
                        if any(r[k] <= 0 for k in ('pointUploadMs', 'indexBuildAndSyncMs', 'queryUploadMs', 'querySubmitReadbackMs')):
                            raise ValueError('Missing charged point/index/query phase')
                        if not math.isclose(r['uploadMs'], r['pointUploadMs'] + r['queryUploadMs'], abs_tol=1e-7) or not math.isclose(r['submitReadbackMs'], r['indexBuildAndSyncMs'] + r['querySubmitReadbackMs'], abs_tol=1e-7):
                            raise ValueError('New phase accounting mismatch')
            pair[arm] = checked_samples(rows)
            pair[arm]['processReceipt'] = (folder / 'process.process.json').relative_to(ROOT).as_posix()
        pairs.append(pair)
    destination = RUN / 'analysis'
    destination.mkdir(exist_ok=False)
    save(destination / 'full-csr-audit.json', {'method': validation['method'], 'files': audit,
        'totalFiles': len(audit), 'totalRowsCompared': sum(p['rows'] for p in audit), 'totalIdsCompared': sum(p['ids'] for p in audit),
        'apiChecks': len(validation['apiChecks']), 'apiRawFiles': validation['apiRawFiles']})
    summary = {'sourceCommit': frozen['sourceCommit'], 'frozenRunSha256': sha(RUN / 'frozen-run.json'),
        'inputManifestSha256': sha(OLD / 'input-manifest.json'), 'scope': 'Full snapshot staging through complete CSR host consumption; cross-backend Serial CPU versus Mono/D3D12 GPU',
        'statistics': 'Four same-round independent process means per arm, 10 warmup + 10 measured each, all samples retained; nominal log-ratio Student-t 95% CI, df=3, no cross-device generalization',
        'rounds': pairs, 'grandMeanMs': {arm: statistics.mean(p[arm]['meanMs'] for p in pairs) for arm in ('native', 'old', 'new')},
        'oldOverNew': ratio(pairs, 'old'), 'nativeOverNew': ratio(pairs, 'native')}
    save(destination / 'comparisons.json', summary)
    print({k: summary[k] for k in ('grandMeanMs', 'oldOverNew', 'nativeOverNew')})
    print('PASS complete GPU CSR files:', len(audit), 'IDs:', sum(p['ids'] for p in audit))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('--attempt', default='r1')
    parser.add_argument('--validate-only', action='store_true')
    args = parser.parse_args()
    main(args.attempt, args.validate_only)
