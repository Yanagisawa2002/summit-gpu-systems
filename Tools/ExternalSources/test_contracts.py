"""Offline contract checks. Never invokes upstream code or measures performance."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import prepare

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

    def test_staging_rejects_nested_paths_before_read_or_write(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(prepare, 'verify_checkout') as verify:
                for source, destination in ((root, root), (root, root/'child'), (root/'child', root)):
                    with self.assertRaisesRegex(ValueError, 'non-nested'):
                        prepare.stage_boids({}, source, destination)
                verify.assert_not_called()

    def test_staging_preserves_tracked_project_and_attests_actual_additions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, own = root/'official', root/'summit'
            originals = {
                'Assets/Boids/Boids.unity': b'original fixture scene\n',
                'Packages/manifest.json': b'{"dependencies":{"com.unity.entities":"1.4.2"}}\n',
                'Packages/packages-lock.json': b'{"original":"lock"}\n'
            }
            tree = b''
            for relative, data in originals.items():
                path = source/'EntitiesSamples'/relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
                blob = hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
                tree += ('100644 blob '+blob+'\tEntitiesSamples/'+relative+'\0').encode()
            ignored = source/'EntitiesSamples/Library/ignored-cache'
            ignored.parent.mkdir(parents=True)
            ignored.write_bytes(b'must not stage')
            for relative in ('PublicBenchmarks/External/Adapters/A.cs', 'PublicBenchmarks/External/Boids/B.cs',
                             'Packages/com.summit.gpu-primitives/package.json',
                             'Packages/com.summit.gpu-direct-binning/package.json',
                             'Packages/com.summit.gpu-sensor-pipeline/package.json'):
                path = own/relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'fixture source\n')
            def git_output(arguments, **_):
                return tree if 'ls-tree' in arguments else 'a'*40+'\n'
            with patch.object(prepare, 'ROOT', own), patch.object(prepare, 'verify_checkout'), \
                    patch.object(prepare.subprocess, 'check_output', side_effect=git_output):
                destination = root/'staged'
                prepare.stage_boids({'commit':'b'*40}, source, destination)
                self.assertFalse((destination/'Library').exists())
                self.assertEqual((destination/'Assets/Boids/Boids.unity').read_bytes(), originals['Assets/Boids/Boids.unity'])
                self.assertEqual((destination/'Packages/packages-lock.json').read_bytes(), originals['Packages/packages-lock.json'])
                manifest = json.loads((destination/'Packages/manifest.json').read_text())
                self.assertEqual(manifest['dependencies']['com.unity.entities'], '1.4.2')
                receipt = json.loads((destination/'summit-external-attestation.json').read_text())
                self.assertEqual(len(receipt['upstreamFiles']), 3)
                for item in receipt['addedFiles']:
                    self.assertEqual(item['sha256'], hashlib.sha256((destination/item['path']).read_bytes()).hexdigest())
                (own/'PublicBenchmarks/External/Boids/B.cs').write_bytes(b'changed local content\n')
                prepare.stage_boids({'commit':'b'*40}, source, root/'staged2')
                changed = json.loads((root/'staged2/summit-external-attestation.json').read_text())
                self.assertEqual(receipt['sourceCommit'], changed['sourceCommit'])
                self.assertNotEqual(receipt['addedFilesSha256'], changed['addedFilesSha256'])
                (source/'EntitiesSamples/Assets/Boids/Boids.unity').write_bytes(b'drift\n')
                with self.assertRaisesRegex(ValueError, 'Committed project bytes differ'):
                    prepare.stage_boids({'commit':'b'*40}, source, root/'rejected')
                self.assertFalse((root/'rejected').exists())

if __name__=='__main__': unittest.main()
