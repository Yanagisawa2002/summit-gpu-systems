"""Offline contract checks. Never invokes upstream code or measures performance."""
import hashlib
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]

class ExternalContracts(unittest.TestCase):
    def test_source_lock_and_license_notices(self):
        lock=json.loads((ROOT/'PublicBenchmarks/External/sources.lock.json').read_text(encoding='utf-8'))
        self.assertEqual(lock['defaultAction'],'prepare-only')
        self.assertEqual(lock['performanceStatus'],'Unmeasured')
        self.assertEqual({s['id'] for s in lock['sources']},{'cabana','arborx','entities-boids'})
        for source in lock['sources']:
            self.assertRegex(source['commit'],r'^[a-f0-9]{40}$')
            paths=[f['path'] for f in source['files']]
            self.assertEqual(len(paths),len(set(paths)))
            for f in source['files']:
                self.assertIn('/'+source['commit']+'/',f['url'])
                self.assertRegex(f['sha256'],r'^[a-f0-9]{64}$')
                self.assertFalse(Path(f['path']).is_absolute())
                self.assertNotIn('..',Path(f['path']).parts)
            license_file=next(f for f in source['files'] if f['path'].startswith('LICENSE'))
            notice=ROOT/'PublicBenchmarks/External/Notices'/f"{source['id']}.txt"
            self.assertEqual(hashlib.sha256(notice.read_bytes()).hexdigest(),license_file['sha256'])
        entities=next(s for s in lock['sources'] if s['id']=='entities-boids')
        self.assertEqual(entities['classification'],'external-official-application-sample')
        for path in ('EntitiesSamples/Assets/Boids/Boids.unity','EntitiesSamples/Assets/Boids/Subscenes/Simulation.unity','EntitiesSamples/Packages/manifest.json','EntitiesSamples/Packages/packages-lock.json'):
            self.assertIn(path,{x['path'] for x in entities['tree']})

    def test_real_hlsl_artifact_identity(self):
        root=ROOT/'Integrations/HlslKernelPipeline/Artifact~'
        manifest=json.loads((root/'consumer-manifest.json').read_text(encoding='utf-8'))
        identity=''
        for f in sorted(manifest['files'],key=lambda x:x['path']):
            actual=hashlib.sha256((root/f['path']).read_bytes()).hexdigest()
            self.assertEqual(actual,f['sha256'])
            identity+=f['path']+'\0'+actual+'\n'
        self.assertEqual(hashlib.sha256(identity.encode()).hexdigest(),manifest['assetSha256'])
        self.assertEqual(manifest['performanceStatus'],'Unmeasured')

if __name__=='__main__': unittest.main()
