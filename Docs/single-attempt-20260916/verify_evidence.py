"""Verify frozen documentation evidence only; does not execute Unity or a GPU."""
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    output = HERE / 'validation.json'
    if output.exists():
        raise FileExistsError('Keep the single validation receipt; no retry.')
    report = {'started_utc': datetime.now(timezone.utc).isoformat(),
              'scope': 'Frozen documentation, source references and evidence integrity only',
              'gpu_tests_run': 0, 'checks': [], 'status': 'FAILED'}
    try:
        candidate = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
        report['frozen_candidate_sha'] = candidate
        mapping = json.loads((HERE / 'source-map.json').read_text())
        report['base_sha'] = mapping['base_sha']
        command = ['git', 'diff', mapping['base_sha'], candidate, '--check']
        check = subprocess.run(command, cwd=ROOT, text=True, capture_output=True)
        report['checks'].append({'command': command, 'exit': check.returncode,
                                 'stdout': check.stdout, 'stderr': check.stderr})
        if check.returncode:
            raise ValueError('git diff --check failed')
        for source in mapping['sources']:
            path = ROOT / source['path']
            if sha(path) != source['working_file_sha256']:
                raise ValueError('Source bytes changed: ' + source['path'])
            for revision in (mapping['base_sha'], candidate):
                actual = subprocess.check_output(['git', 'rev-parse', revision + ':' + source['path']], cwd=ROOT, text=True).strip()
                if actual != source['base_git_blob']:
                    raise ValueError('Source blob changed: ' + source['path'])
            lines = path.read_text(encoding='utf-8-sig').splitlines()
            for excerpt in source['excerpts']:
                if lines[excerpt['start']-1:excerpt['end']] != excerpt['lines']:
                    raise ValueError('Source excerpt mismatch: ' + source['path'])
        report['checks'].append({'check': 'base/candidate blobs, current file SHA256 and numbered source excerpts', 'sources': len(mapping['sources']), 'exit': 0})
        manifest = json.loads((HERE / 'frozen-evidence.json').read_text())
        for row in manifest['files']:
            if sha(ROOT / row['path']) != row['sha256']:
                raise ValueError('Frozen evidence hash mismatch: ' + row['path'])
        report['checks'].append({'check': 'frozen evidence SHA256', 'files': len(manifest['files']), 'exit': 0})
        link_count = 0
        for path in (HERE / 'DECISION.md', ROOT / 'PublicBenchmarks/WholeTaskMolecularDynamics/README.md'):
            for target in re.findall(r'\]\(([^)]+)\)', path.read_text(encoding='utf-8')):
                if '://' in target or target.startswith('#'):
                    continue
                if not (path.parent / target.split('#')[0]).resolve().exists():
                    raise ValueError('Missing local link: ' + target)
                link_count += 1
        report['checks'].append({'check': 'relative documentation links', 'links': link_count, 'exit': 0})
        inventory = json.loads((HERE / 'preflight.json').read_text())
        raw = (HERE / 'remote-closeout.txt').read_text(encoding='utf-8-sig')
        required = ['OWNED_PROJECT_PROCESSES_REMAINING=0', 'REMOTE_TASK_DIRECTORY_PRESENT=false',
                    'LOCK_REACQUIRE_AFTER_RELEASE=true', 'FINAL_READ_ONLY_CHECK_COMPLETE=true']
        if inventory['gpu_experiment_runs'] != 0 or not all(token in raw for token in required):
            raise ValueError('Skip/closeout receipt inconsistency')
        for row in manifest['files']:
            data = (ROOT / row['path']).read_text(encoding='utf-8-sig')
            if re.search(r'connect[.][a-z0-9.-]+[.]seetacloud[.]com|autodl-container-[a-z0-9]+|GPU-[0-9a-f]{8}-|[c]gliu|[.]dpapi', data, re.I):
                raise ValueError('Private infrastructure identity in public evidence: ' + row['path'])
        report['checks'].append({'check': 'skip/closeout consistency and public infrastructure identity scan', 'exit': 0})
        report['status'] = 'PASSED'
    except Exception as error:
        report['error'] = str(error)
    report['ended_utc'] = datetime.now(timezone.utc).isoformat()
    output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))
    return 0 if report['status'] == 'PASSED' else 1

if __name__ == '__main__':
    sys.exit(main())
