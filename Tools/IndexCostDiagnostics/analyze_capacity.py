"""Audit the frozen software replay and report logical work, never GPU timings."""
import argparse
import csv
import hashlib
import json
import statistics
import struct
from collections import Counter
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summary(values):
    values = list(values)
    return {'min': min(values), 'mean': statistics.mean(values), 'max': max(values), 'sum': sum(values)}


def analyze(replay, output):
    if output.exists():
        raise RuntimeError('Fresh analysis output required')
    receipt = read(replay / 'receipt.json')
    assert receipt['status'] == 'complete' and not receipt['screeningPassed']
    inputs = read(replay / 'inputs-sha256.json')
    for item in inputs:
        assert sha(Path(item['path'])).lower() == item['sha256'].lower()
    assert sha(replay / 'CapacityReplay.dll').lower() == receipt['assemblySha256'].lower()
    result = {'kind': 'software mechanism and logical-work analysis, not measured GPU performance', 'cases': {}}
    audit = {'status': 'passed', 'rawGpuStateWords': 0, 'rawGpuCsrWords': 0, 'csvRows': 0, 'sourceAndAssemblyHashesVerified': True}
    for scene in ('hotspot-dynamic', 'streaming-switch'):
        history_path = next(Path(x['path']) for x in inputs if scene + '-phases-off' in x['path'])
        raw = history_path.read_bytes()
        history = struct.unpack('<' + str(len(raw) // 4) + 'I', raw)
        with (replay / (scene + '.csv')).open(encoding='utf-8-sig', newline='') as f:
            rows = [{k: v if k in ('scenario', 'policy') else int(v) for k, v in row.items()} for row in csv.DictReader(f)]
        assert len(rows) == 768
        details = {}
        for policy in ('baseline', 'empty-one'):
            subset = [r for r in rows if r['policy'] == policy]
            assert [r['frame'] for r in subset] == list(range(384))
            rebuilds = incrementals = holes = 0
            for row in subset:
                frame = row['frame']; state = [row['state' + str(i)] for i in range(16)]
                assert row['scenario'] == scene
                reason = (1 if frame == 0 else 0) | (4 if state[3] > 52428 else 0) | (8 if holes + state[4] > 65536 else 0) | (16 if state[6] else 0)
                assert state[8] == reason
                if reason:
                    rebuilds += 1; holes = 0
                else:
                    incrementals += 1; holes += state[4]
                assert (state[2], state[11], state[12]) == (holes, rebuilds, incrementals)
                assert state[15] == (11 if reason else 6 if state[3] else 3)
                assert state[6] == row['zeroCapacityOverflow'] + row['nonzeroCapacityOverflow']
                assert row['emptyAtRebuildOverflow'] <= state[6]
                assert state[10] <= 786432 and row['physicalInvalid'] == state[10] - state[9]
                assert row['consumerVisits'] == (sum(row['rangeVisits' + str(q)] for q in range(9)) if scene == 'hotspot-dynamic' else state[10])
                if policy == 'baseline':
                    assert tuple(state) == history[frame * 112 + 80:frame * 112 + 96]
                    expected = (state[9], row['physicalInvalid'], 0, 0, 0, 0, 0, state[10])
                    assert expected == history[frame * 112 + 104:frame * 112 + 112]
                    audit['rawGpuStateWords'] += 16; audit['rawGpuCsrWords'] += 8
                audit['csvRows'] += 1
            steady = subset[64:]
            categories = Counter('both' if r['zeroCapacityOverflow'] and r['nonzeroCapacityOverflow'] else 'zero-capacity-only' if r['zeroCapacityOverflow'] else 'nonzero-capacity-only' if r['nonzeroCapacityOverflow'] else 'none' for r in steady)
            metrics = ('state2', 'state3', 'state4', 'state5', 'state6', 'state9', 'state10', 'physicalInvalid', 'zeroCapacityOverflow', 'nonzeroCapacityOverflow', 'zeroCapacityCells', 'nonzeroCapacityCells', 'emptyAtRebuildOverflow', 'emptyAtRebuildCells', 'maxIncoming', 'consumerVisits')
            details[policy] = {
                'steadyFrames': 320,
                'rebuildReasons': dict(Counter(r['state8'] for r in steady)),
                'capacityOverflowCategories': dict(categories),
                'framesWithOverflowIntoEmptyAtRebuild': sum(r['emptyAtRebuildOverflow'] > 0 for r in steady),
                'logicalIndexMemberAllocationWords': 786432,
                'logicalIndexResidentBytes': 15732924,
                'fixedBatchedDispatchGroups': 3072 if scene == 'streaming-switch' else None,
                'fixedBatchedLaunchedThreads': 786432 if scene == 'streaming-switch' else None,
                'physicalInvalidFraction': summary(r['physicalInvalid'] / r['state10'] for r in steady),
                'metrics': {key: summary(r[key] for r in steady) for key in metrics},
                'queryRangeVisits': {str(q): summary(r['rangeVisits' + str(q)] for r in steady) for q in range(9)},
                'compactRangeVisits': {str(q): summary(r['compactVisits' + str(q)] for r in steady) for q in range(9)},
                'worstCellSerialLaneInnerIterations': {str(q): summary(r['worstCellSerialLane' + str(q)] for r in steady) for q in range(9)},
            }
        baseline = details['baseline']; candidate = details['empty-one']
        details['candidateConsumerVisitsIncreasePercent'] = (candidate['metrics']['consumerVisits']['sum'] / baseline['metrics']['consumerVisits']['sum'] - 1) * 100
        expected_receipt = next(x for x in receipt['results'] if x['scenario'] == scene)
        assert expected_receipt['baselineConsumerVisits'] == baseline['metrics']['consumerVisits']['sum']
        assert expected_receipt['candidateConsumerVisits'] == candidate['metrics']['consumerVisits']['sum']
        assert expected_receipt['baselineCapacityRebuilds'] == sum(n for r, n in baseline['rebuildReasons'].items() if int(r) & 16)
        assert expected_receipt['candidateCapacityRebuilds'] == sum(n for r, n in candidate['rebuildReasons'].items() if int(r) & 16)
        details['snapshotSequenceSha256'] = expected_receipt['snapshotSequenceSha256']
        if scene == 'streaming-switch':
            candidate_rows = [r for r in rows if r['policy'] == 'empty-one' and r['frame'] >= 64]
            ordinary = [r for r in candidate_rows if r['frame'] not in (128, 240)]
            assert len(ordinary) == 318
            assert all(r['state8'] == 16 and r['maxIncoming'] == 2 and r['state6'] == r['emptyAtRebuildOverflow'] == r['emptyAtRebuildCells'] for r in ordinary)
            details['emptyOneMultiplicityEvidence'] = {
                'nonRegistrationFrames': 318,
                'allNonRegistrationOverflowCellsWereEmptyAtPreviousRebuild': True,
                'eachSuchCellReceivesExactlyTwoInsertions': True,
                'overflowCellsPerNonRegistrationFrame': summary(r['emptyAtRebuildCells'] for r in ordinary),
                'registrationFrames': [{
                    'frame': r['frame'], 'emptyAtRebuildOverflow': r['emptyAtRebuildOverflow'],
                    'emptyAtRebuildCells': r['emptyAtRebuildCells'],
                    'occupiedAtRebuildOverflow': r['state6'] - r['emptyAtRebuildOverflow'],
                    'maxIncomingAcrossAllDestinations': r['maxIncoming'],
                } for r in candidate_rows if r['frame'] in (128, 240)],
                'derivation': 'Every candidate frame rebuilds. Empty-at-rebuild destinations therefore begin the next frame with head=0 and capacity=1. With max incoming=2 and overflow count equal to distinct overflowing empty cells, each of those cells received exactly two reservations. Registration multiplicity maximum is across all destinations, not specifically empty cells.',
            }
        result['cases'][scene] = details
    result['screeningPassed'] = False
    result['decision'] = 'Reject sole empty-one candidate from GPU validation; retain opt-in and existing scoped recommendation. No measured candidate slowdown or universal impossibility claim.'
    output.mkdir(parents=True)
    (output / 'analysis.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    (output / 'audit.json').write_text(json.dumps(audit, indent=2), encoding='utf-8')
    print(json.dumps(audit))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--replay', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    analyze(args.replay, args.output)
