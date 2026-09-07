"""Generate a standalone diagnostic project from pinned production sources.
Only timestamp hooks and class names change in the profiled index adapters.
The original PublicBenchmarks tree and production packages are never edited.
"""
import argparse,hashlib,json,re,shutil,os
from pathlib import Path

def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def generate(content, project_path=None):
    here=Path(__file__).resolve().parent; root=here.parents[1]; project=Path(project_path).resolve() if project_path else here/'Project'
    if project.exists(): raise RuntimeError('Fresh generated project required; do not overwrite evidence')
    source=root/'PublicBenchmarks/UnityGpuIntegration'
    assets=project/'Assets'; runtime=assets/'Runtime'; resources=assets/'Resources'
    for p in (runtime,resources,assets/'Editor',project/'ProjectSettings',project/'Packages',assets/'StreamingAssets/IntegrationContent'): p.mkdir(parents=True,exist_ok=True)
    inputs=[]
    def read(p):
        inputs.append(dict(path=str(p.relative_to(root)),sha256=sha(p)))
        return p.read_text(encoding='utf-8-sig')
    for name in ('IntegrationFixture.cs','IntegrationContent.cs'):
        (runtime/name).write_text(read(source/'Assets/Runtime'/name),encoding='utf-8')
    for name in ('IntegrationParticles.shader',):
        (resources/name).write_text(read(source/'Assets/Resources'/name),encoding='utf-8')
    for name,target in [('IndexCostPlayer.cs',runtime),('IndexCostHistory.compute',resources),('IndexCostBuild.cs',assets/'Editor')]:
        (target/name).write_text(read(here/name),encoding='utf-8')
    incremental=read(root/'Packages/com.summit.gpu-sensor-pipeline/Runtime/GpuSensorIncrementalIndex.cs')
    a=incremental.index('    public enum GpuSensorIndexExecutionMode'); b=incremental.index('    /// <summary>',a)
    incremental=incremental[:a]+incremental[b:]
    incremental=incremental.replace('GpuSensorIncrementalIndex','CostProfiledIncrementalIndex')
    incremental=incremental.replace('"GpuSensorPipeline/CostProfiledIncrementalIndex"','"GpuSensorPipeline/GpuSensorIncrementalIndex"')
    needle='        public const int DiagnosticWordCount = 16;'
    assert needle in incremental
    incremental=incremental.replace(needle,'        public Action<CommandBuffer, string, bool> DiagnosticMarker;\n'+needle)
    pattern=r'(?m)^(\s*)Dispatch\(commands, (\w+), ([^;]+)\);'
    def wrap(m):
        pad,k,g=m.groups()
        return f'{pad}DiagnosticMarker?.Invoke(commands, "incremental/{k}", true);{pad}Dispatch(commands, {k}, {g});{pad}DiagnosticMarker?.Invoke(commands, "incremental/{k}", false);'
    incremental,count=re.subn(pattern,wrap,incremental); assert count==13,count
    (runtime/'CostProfiledIncrementalIndex.cs').write_text(incremental,encoding='utf-8')
    full=read(root/'Packages/com.summit.gpu-sensor-pipeline/Runtime/GpuSensorFullRebuildIndex.cs')
    full=full.replace('GpuSensorFullRebuildIndex','CostProfiledFullIndex').replace('GpuDirectSpatialBinner','CostProfiledDirectBinner')
    full=full.replace('        private readonly ComputeShader shader;', '        public Action<CommandBuffer,string,bool> DiagnosticMarker;\n        private readonly ComputeShader shader;')
    full=full.replace('            commands.SetComputeIntParam(shader, "_Capacity", Capacity);','            DiagnosticMarker?.Invoke(commands,"full/SnapshotKeys",true);\n            commands.SetComputeIntParam(shader, "_Capacity", Capacity);')
    full=full.replace('            binner.Record(commands,','            DiagnosticMarker?.Invoke(commands,"full/SnapshotKeys",false);\n            binner.DiagnosticMarker = DiagnosticMarker;\n            binner.Record(commands,')
    (runtime/'CostProfiledFullIndex.cs').write_text(full,encoding='utf-8')
    binner=read(root/'Packages/com.summit.gpu-direct-binning/Runtime/GpuDirectSpatialBinner.cs').replace('GpuDirectSpatialBinner','CostProfiledDirectBinner')
    binner=binner.replace('        public const int ThreadGroupSize = 256;','        public Action<CommandBuffer,string,bool> DiagnosticMarker;\n        public const int ThreadGroupSize = 256;')
    for method,begin in [('BeginSample','true'),('EndSample','false')]:
        pattern=r'(private void '+method+r'\(\s*CommandBuffer commands,\s*string sampleName\)\s*\{)'
        binner,n=re.subn(pattern, r'\1\n            if (!sampleName.Contains("DirectSpatialBinning")) DiagnosticMarker?.Invoke(commands, sampleName.Replace("Summit.GpuDirectBinning/", "full/"), '+begin+');',binner)
        assert n==1
    pattern=r'(            primitives.RecordExclusiveScan\([\s\S]+?scanBackend\);)'
    binner,n=re.subn(pattern,r'            DiagnosticMarker?.Invoke(commands,"full/Scan",true);\n\1\n            DiagnosticMarker?.Invoke(commands,"full/Scan",false);',binner); assert n==1
    (runtime/'CostProfiledDirectBinner.cs').write_text(binner,encoding='utf-8')
    manifest=json.loads(read(source/'Packages/manifest.json'))
    for package in manifest['dependencies']:
        if package.startswith('com.summit.'):
            manifest['dependencies'][package]='file:'+Path(os.path.relpath(root/'Packages'/package,project/'Packages')).as_posix()
    (project/'Packages/manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    (project/'ProjectSettings/ProjectVersion.txt').write_text(read(source/'ProjectSettings/ProjectVersion.txt'),encoding='utf-8')
    (project/'LICENSE.md').write_text(read(root/'LICENSE.md'),encoding='utf-8')
    content=Path(content)
    bundles=[]
    for name in ('content-0','content-1'):
        p=content/name
        if not p.is_file(): raise RuntimeError('Existing fixed AssetBundles required: '+str(p))
        shutil.copyfile(p,assets/'StreamingAssets/IntegrationContent'/name)
        bundles.append(dict(name=name,source=str(p.resolve()),bytes=p.stat().st_size,sha256=sha(p)))
    generated=[dict(path=str(p.relative_to(project)),sha256=sha(p)) for p in sorted(project.rglob('*')) if p.is_file()]
    (project/'generation.json').write_text(json.dumps(dict(sourceInputs=inputs,transforms='Class renames; incremental 13 begin/end timestamp observer hooks; full keys and existing direct-binner stages plus scan timestamp observers. No shader or index arithmetic edits.',bundles=bundles,generated=generated),indent=2),encoding='utf-8')
    print(project)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--content',required=True);p.add_argument('--project');a=p.parse_args();generate(a.content,a.project)
