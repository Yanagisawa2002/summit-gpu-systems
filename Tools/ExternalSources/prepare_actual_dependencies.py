"""Frozen official sources for actual external-workload validation; no execution."""
import hashlib
import json
from pathlib import Path
import shutil
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
RUN = ROOT / 'Artifacts/actual-20260910'
DOWNLOADS = RUN / 'downloads'
DEPS = RUN / 'dependencies'
HEADERS = {'User-Agent': 'SUMMIT-external-source-validation'}

def fetch(url, target):
    if target.exists():
        raise ValueError('Refusing to overwrite download: '+str(target))
    with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=90) as response, target.open('wb') as out:
        length = int(response.headers.get('Content-Length', 0))
        if length > 1024**3:
            raise ValueError('Unexpected archive size')
        shutil.copyfileobj(response, out)
    return hashlib.sha256(target.read_bytes()).hexdigest()

def api(path):
    with urllib.request.urlopen(urllib.request.Request('https://api.github.com/'+path, headers=HEADERS), timeout=60) as response:
        return json.load(response)

def main():
    entries = []
    source_lock = json.loads((ROOT/'PublicBenchmarks/External/sources.lock.json').read_text())
    frozen = [(s['id'], s['repository'].split('github.com/')[1].removesuffix('.git'), s['commit'])
              for s in source_lock['sources'] if s['id'] in ('cabana', 'arborx')]
    frozen += [('kokkos', 'kokkos/kokkos', '4.7.02'), ('benchmark', 'google/benchmark', 'v1.9.1')]
    for name, repository, revision in frozen:
        commit = revision if len(revision)==40 else api('repos/'+repository+'/commits/'+revision)['sha']
        url = f'https://codeload.github.com/{repository}/zip/{commit}'
        target = DOWNLOADS/(name+'.zip')
        sha = fetch(url, target)
        with zipfile.ZipFile(target) as archive:
            expanded = sum(i.file_size for i in archive.infolist())
            if expanded > 512*1024**2:
                raise ValueError('Unexpected source expansion')
            destination=DEPS/name
            destination.mkdir()
            prefix=archive.infolist()[0].filename.split('/')[0]+'/'
            for item in archive.infolist():
                relative=Path(item.filename.removeprefix(prefix))
                if not relative.parts or item.is_dir():
                    continue
                if relative.is_absolute() or '..' in relative.parts:
                    raise ValueError('Invalid archive path')
                output=destination/relative
                output.parent.mkdir(parents=True,exist_ok=True)
                output.write_bytes(archive.read(item))
        entries.append(dict(id=name,repository=repository,revision=revision,commit=commit,url=url,sha256=sha,downloadBytes=target.stat().st_size,expandedBytes=expanded))
        (RUN/'dependency-source-receipt.json').write_text(json.dumps(entries,indent=2)+'\n')
        print(name, commit, sha, expanded, flush=True)
    release=api('repos/Kitware/CMake/releases/tags/v3.31.10')
    asset=next(a for a in release['assets'] if a['name']=='cmake-3.31.10-windows-x86_64.zip')
    checksum=next(a for a in release['assets'] if a['name']=='cmake-3.31.10-SHA-256.txt')
    checksum_path=DOWNLOADS/checksum['name']
    fetch(checksum['browser_download_url'],checksum_path)
    target=DOWNLOADS/asset['name']
    sha=fetch(asset['browser_download_url'],target)
    expected=next(line.split()[0] for line in checksum_path.read_text().splitlines() if line.endswith(asset['name']))
    if sha!=expected: raise ValueError('CMake publisher SHA mismatch')
    with zipfile.ZipFile(target) as archive:
        expanded=sum(i.file_size for i in archive.infolist())
        archive.extractall(DEPS)
    entries.append(dict(id='cmake',version='3.31.10',url=asset['browser_download_url'],sha256=sha,publisherSha256=expected,downloadBytes=target.stat().st_size,expandedBytes=expanded))
    (RUN/'dependency-source-receipt.json').write_text(json.dumps(entries,indent=2)+'\n')
    print('cmake',sha,expanded,flush=True)

if __name__=='__main__': main()
