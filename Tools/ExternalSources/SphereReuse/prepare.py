"""Preserve the entire baseline; reuse a copy of its small Unity host/cache only."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[3]
OLD = ROOT / 'Artifacts/actual-20260910'
RUN = ROOT / 'Artifacts/optimization-20260910-sphere-reuse'


def sha(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def save(path, value):
    with path.open('x', encoding='utf-8') as target:
        json.dump(value, target, indent=2)


def preserve():
    files = [p for p in sorted(OLD.rglob('*')) if p.is_file()]
    records = [{'path': p.relative_to(ROOT).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha(p)} for p in files]
    save(RUN / 'baseline-files.json', {'baseCommit': 'e54a35eff9cf19540d94a34a35fee955c433c1b1', 'files': records})
    print(f'Preserved identity of {len(records)} original files / {sum(r["bytes"] for r in records)} bytes', flush=True)
    destination = RUN / 'unity-host'
    if destination.exists():
        raise ValueError('New Unity host already exists')
    shutil.copytree(OLD / 'unity-host', destination)
    save(RUN / 'cache-reuse.json', {'source': (OLD / 'unity-host').relative_to(ROOT).as_posix(), 'destination': destination.relative_to(ROOT).as_posix(), 'bytes': sum(p.stat().st_size for p in destination.rglob('*') if p.is_file()), 'oldPlayerCopied': False, 'oldFilesModified': False})


def stage_sources():
    host = RUN / 'unity-host'
    if not host.exists():
        raise ValueError('Prepare the separate host/cache first')
    records = []
    for folder in ('Adapters', 'Actual'):
        origin = ROOT / 'PublicBenchmarks/External' / folder
        for source in sorted(origin.rglob('*')):
            if not source.is_file():
                continue
            target = host / 'Assets' / folder / source.relative_to(origin)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            records.append({'source': source.relative_to(ROOT).as_posix(), 'staged': target.relative_to(ROOT).as_posix(), 'sha256': sha(source)})
    for package in ('com.summit.gpu-primitives', 'com.summit.gpu-direct-binning', 'com.summit.gpu-sensor-pipeline'):
        origin = ROOT / 'Packages' / package
        for source in [origin / 'package.json', *sorted((origin / 'Runtime').rglob('*'))]:
            if not source.is_file():
                continue
            target = host / 'Packages' / package / source.relative_to(origin)
            if sha(source) != sha(target):
                raise ValueError('Unmodified shared runtime cache differs: ' + str(source))
            records.append({'source': source.relative_to(ROOT).as_posix(), 'staged': target.relative_to(ROOT).as_posix(), 'sha256': sha(source)})
    receipt = RUN / f'player-staging-{len(list(RUN.glob("player-staging-*.json")))+1:02}.json'
    save(receipt, {'files': records})
    print('Staged current public adapter/application sources:', receipt.relative_to(ROOT).as_posix(), flush=True)


def verify_baseline():
    original = json.loads((RUN / 'baseline-files.json').read_text())['files']
    current = {p.relative_to(ROOT).as_posix() for p in OLD.rglob('*') if p.is_file()}
    if current != {r['path'] for r in original}:
        raise ValueError('Original baseline file set changed')
    for entry in original:
        if sha(ROOT / entry['path']) != entry['sha256']:
            raise ValueError('Original baseline bytes changed: ' + entry['path'])
    print(f'PASS all {len(original)} original files remain byte-identical', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('action', choices=('preserve', 'stage-sources', 'verify-baseline'))
    action = parser.parse_args().action
    {'preserve': preserve, 'stage-sources': stage_sources, 'verify-baseline': verify_baseline}[action]()
