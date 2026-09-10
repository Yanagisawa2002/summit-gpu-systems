"""Freeze only after real GPU correctness and a clean, locally committed source."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

from prepare import ROOT, OLD, RUN, sha, save

ORDERS = [('native', 'old', 'new'), ('old', 'new', 'native'),
          ('new', 'native', 'old'), ('new', 'old', 'native')]
PROTOCOL = 'Docs/SPHERE_REUSE_PROTOCOL_2026-09-10.md'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args]).strip()


def relative(path):
    return path.relative_to(ROOT).as_posix()


def main(attempt):
    if (RUN / 'frozen-run.json').exists():
        raise ValueError('Freeze exists; do not replace this finite protocol')
    if git('status', '--porcelain'):
        raise ValueError('Commit the reviewed implementation/protocol before freezing')
    source = git('rev-parse', 'HEAD').decode()
    previous = read(OLD / 'frozen-run.json')
    staging = sorted(RUN.glob('player-staging-*.json'))[-1]
    staged = read(staging)['files']
    for entry in staged:
        for key in ('source', 'staged'):
            if sha(ROOT / entry[key]) != entry['sha256']:
                raise ValueError('Build staging changed: ' + entry[key])
    compiled = {entry['source'] for entry in staged if Path(entry['source']).suffix in ('.cs', '.compute', '.hlsl', '.asmdef')}
    for folder in ('Adapters', 'Actual'):
        for path in (ROOT / 'PublicBenchmarks/External' / folder).rglob('*'):
            if path.suffix in ('.cs', '.compute', '.hlsl', '.asmdef') and relative(path) not in compiled:
                raise ValueError('Unbuilt source: ' + str(path))
    if not compiled.issubset(set(git('ls-files').decode().splitlines())):
        raise ValueError('Compiled sources must be committed, not ignored files')
    # The staging manifest binds physical build inputs; a clean tracked tree
    # binds them to this commit despite platform newline conversion.

    evidence = {}
    for kind, expected_rows in (('api', 0), ('arborx', 2), ('cabana', 40)):
        folder = RUN / f'runs/validation-{kind}-{attempt}'
        report = read(folder / 'gpu/result.json')
        if report['status'] != 'completed' or len(report['rows']) != expected_rows or not all(r['verified'] for r in report['rows']):
            raise ValueError('Actual GPU correctness incomplete: ' + kind)
        if kind == 'api':
            checks = read(folder / 'gpu/api-checks.json')['checks']
            if not checks or checks[-1]['name'] != 'all-fixtures-completed' or not all(c['passed'] for c in checks):
                raise ValueError('API fixtures incomplete')
        elif any(r['phase'] != 'validation' or r['hostWallMs'] != -1 for r in report['rows']):
            raise ValueError('Correctness must be untimed')
        evidence[kind] = relative(folder)
    new_report = read(ROOT / evidence['arborx'] / 'gpu/result.json')
    independent_audit = RUN / f'correctness-audit-{attempt}.json'
    if read(independent_audit)['buildGuid'] != new_report['buildGuid']:
        raise ValueError('Independent full-CSR audit must match the validated Player')
    old_report = read(OLD / 'runs/arborx-p01-gpu/gpu/result.json')
    if new_report['buildGuid'] == old_report['buildGuid']:
        raise ValueError('New build must have a distinct identity')

    files = set()
    def add(path):
        if path.is_file():
            files.add(path)
    def tree(path):
        for p in path.rglob('*'):
            add(p)
        entries = [{'path': p.relative_to(path).as_posix(), 'sha256': sha(p), 'bytes': p.stat().st_size}
                   for p in sorted(path.rglob('*')) if p.is_file()]
        return {'path': relative(path), 'files': entries,
                'treeSha256': hashlib.sha256(json.dumps(entries, sort_keys=True, separators=(',', ':')).encode()).hexdigest()}

    players = {}
    for arm, path in (('old', OLD / 'player-r1'), ('new', RUN / f'player-{attempt}')):
        players[arm] = tree(path)
        players[arm]['sourceCommit'] = previous['sourceCommit'] if arm == 'old' else source
        players[arm]['buildGuid'] = old_report['buildGuid'] if arm == 'old' else new_report['buildGuid']
        players[arm]['executable'] = relative(path / 'SummitExternalReplay.exe')
    players['new']['attempt'] = attempt
    add(OLD / 'native-build/ArborXReplay.exe')
    # Reused dependency/source/build identities, without rehashing 93k immutable
    # baseline files on every process. The final preservation audit covers all.
    for name in ('frozen-run.json', 'input-manifest.json', 'dependency-source-receipt.json',
                 'boost-source-receipt.json', 'player-r1-staging-receipt.json', 'player-staging-receipt.json'):
        add(OLD / name)
    for entry in read(OLD / 'input-manifest.json')['files']:
        add(Path(entry['path']))
    for entry in previous['files']:
        path = ROOT / entry['path']
        if path.is_relative_to(OLD) and path.suffix in ('.txt', '.cmake', '.cpp', '.hpp', '.h'):
            add(path)
    for host in (OLD / 'unity-host', RUN / 'unity-host'):
        for folder in ('Assets', 'Packages', 'ProjectSettings'):
            tree(host / folder)
    for entry in staged:
        add(ROOT / entry['source']); add(ROOT / entry['staged'])
    add(staging); add(RUN / 'cache-reuse.json'); add(RUN / 'baseline-files.json')
    for name in ('after-forest-clearance.json', 'resume-identity.json', 'environment-before-freeze.json'):
        add(RUN / name)
    add(independent_audit)
    for p in (RUN / 'stages').glob(f'build-{attempt}*'):
        add(p)
    for folder in evidence.values():
        tree(ROOT / folder)
    for p in git('ls-files', 'Tools/ExternalSources', 'PublicBenchmarks/External', PROTOCOL).decode().splitlines():
        add(ROOT / p)
    toolchain = previous['toolchain']
    for entry in toolchain:
        if sha(Path(entry['path'])) != entry['sha256']:
            raise ValueError('Original toolchain identity changed: ' + entry['path'])
    processes = []
    for number, order in enumerate(ORDERS, 1):
        for arm in order:
            name = f'r{number:02}-{arm}'
            destination = RUN / 'runs' / name
            executable = (OLD / 'native-build/ArborXReplay.exe') if arm == 'native' else ROOT / players[arm]['executable']
            arguments = ['replay', str(OLD / 'inputs/arborx-default.bin'), str(destination / 'native.csv')] if arm == 'native' else [
                '-batchmode', '-force-d3d12', '-screen-width', '64', '-screen-height', '64',
                '-actual-config', str(destination / 'config.json'), '-logFile', str(destination / 'player.log')]
            body = RUN / 'stages' / (name + '.ps1')
            with body.open('x', encoding='utf-8') as stream:
                stream.write("$ErrorActionPreference='Stop'\n./Tools/ExternalSources/SphereReuse/run.ps1 "
                             f'-Arm {arm} -Name {name} -Kind arborx -Attempt {attempt}\n')
            add(body)
            processes.append({'round': number, 'arm': arm, 'name': name, 'script': relative(body),
                              'executable': relative(executable), 'arguments': arguments,
                              'sourceCommit': source if arm == 'new' else previous['sourceCommit']})
    save(RUN / 'frozen-run.json', {
        'baseCommit': 'e54a35eff9cf19540d94a34a35fee955c433c1b1', 'sourceCommit': source,
        'oldSourceCommit': previous['sourceCommit'], 'frozenUtc': datetime.now(timezone.utc).isoformat(),
        'protocol': PROTOCOL, 'orders': ORDERS, 'warmupsPerProcess': 10, 'measuredPerProcess': 10,
        'correctnessEvidence': evidence, 'oldPlayer': players['old'], 'newPlayer': players['new'],
        'nativeExecutable': relative(OLD / 'native-build/ArborXReplay.exe'),
        'nativeBackend': 'Original ArborXReplay: Kokkos Serial, one execution thread',
        'gpuBackend': 'Unity 6000.5.2f1 Mono D3D12; inherited affinity/priority unchanged',
        'device': {k: new_report[k] for k in ('deviceName', 'deviceVersion', 'vendorId', 'deviceId', 'cpu', 'logicalCpuCount')},
        'processes': processes, 'toolchain': toolchain,
        'files': [{'path': relative(p), 'sha256': sha(p), 'bytes': p.stat().st_size} for p in sorted(files)]})
    print('Frozen source', source, 'new build', new_report['buildGuid'], 'files', len(files))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('--attempt', default='r1', choices=[f'r{i}' for i in range(1, 10)])
    main(parser.parse_args().attempt)
