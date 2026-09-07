import contextlib,io,re,tempfile,unittest
from pathlib import Path
import generate

class GeneratedAdapterTests(unittest.TestCase):
    def test_shader_resource_identity_and_thirteen_dispatch_observers(self):
        root=Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as t:
            p=Path(t);content=p/'content';content.mkdir()
            for name in ('content-0','content-1'):(content/name).write_bytes(b'fixture-for-generation-only')
            with contextlib.redirect_stdout(io.StringIO()):generate.generate(content,p/'project')
            text=(p/'project/Assets/Runtime/CostProfiledIncrementalIndex.cs').read_text()
            original=(root/'Packages/com.summit.gpu-sensor-pipeline/Runtime/GpuSensorIncrementalIndex.cs').read_text()
            pattern=r'Resources.Load<ComputeShader>\("([^"]+)"'
            self.assertEqual(re.findall(pattern,text),re.findall(pattern,original))
            self.assertEqual(text.count('DiagnosticMarker?.Invoke(commands, "incremental/'),26)
            self.assertEqual(text.count('Dispatch(commands,'),13)
            self.assertNotIn('GpuSensorPipeline/CostProfiled',text)
            full=(p/'project/Assets/Runtime/CostProfiledFullIndex.cs').read_text()
            self.assertIn('GpuSensorPipeline/GpuSensorSnapshotKeys',full)
            binner=(p/'project/Assets/Runtime/CostProfiledDirectBinner.cs').read_text()
            self.assertIn('GpuDirectBinning/GpuDirectBinning',binner)
            self.assertNotIn("\\'",binner)
if __name__=='__main__':unittest.main()
