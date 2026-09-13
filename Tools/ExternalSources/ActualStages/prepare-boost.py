from pathlib import Path
import hashlib, json, tarfile, urllib.request, shutil
root=Path.cwd()/'Artifacts/actual-20260910'
url='https://archives.boost.io/release/1.87.0/source/boost_1_87_0.tar.gz'
archive=root/'downloads/boost_1_87_0.tar.gz'
if archive.exists(): raise RuntimeError('Archive exists; retain it and use a distinct attempt')
with urllib.request.urlopen(url,timeout=90) as response,archive.open('wb') as output: shutil.copyfileobj(response,output)
sha=hashlib.sha256(archive.read_bytes()).hexdigest()
assert sha=='f55c340aa49763b1925ccf02b2e83f35fdcf634c9d5164a2acb87540173c741d'
print('Boost publisher SHA verified',archive.stat().st_size,flush=True)
with tarfile.open(archive) as source:
    members=source.getmembers()
    expanded=sum(m.size for m in members)
    assert expanded<3*1024**3
    source.extractall(root/'dependencies',filter='data')
receipt={'id':'boost','version':'1.87.0','commit':'c89e6267665516192015a9e40955e154466f4f68','url':url,'sha256':sha,'publisherSha256':sha,'downloadBytes':archive.stat().st_size,'expandedBytes':expanded}
(root/'boost-source-receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(receipt,flush=True)
