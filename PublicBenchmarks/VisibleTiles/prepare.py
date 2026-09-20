"""Stage a self-contained Unity project from exact reviewed source files.
No download, Unity launch, GPU operation, or mutation of an existing directory.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
SAMPLE = "PublicBenchmarks/VisibleTiles"
CORE = "Integrations/NYCGIS/Assets/Shaders/NYCGISDemo"
INPUTS = {
    f"{SAMPLE}/Source/Fixture.cs": "Assets/Fixture.cs",
    f"{SAMPLE}/Source/VisibleTilesDemo.cs": "Assets/VisibleTilesDemo.cs",
    f"{SAMPLE}/Source/Build.cs": "Assets/Editor/Build.cs",
    f"{SAMPLE}/Shaders/VisibleTilesDraw.hlsl": "Assets/Resources/VisibleTilesDraw.hlsl",
    f"{SAMPLE}/Shaders/VisibleTilesDraw.shader": "Assets/Resources/VisibleTilesDraw.shader",
    f"{CORE}/Bfp2GpuClusterCull.compute": "Assets/Resources/Culling/Bfp2GpuClusterCull.compute",
    f"{CORE}/Bfp2GpuClusterCullWave.compute": "Assets/Resources/Culling/Bfp2GpuClusterCullWave.compute",
    f"{CORE}/Bfp2NormalConeVisibility.hlsl": "Assets/Resources/Culling/Bfp2NormalConeVisibility.hlsl",
    "LICENSE.md": "LICENSE.md",
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def within(root, path):
    resolved = (root/path).resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise ValueError(f"Path escapes root: {path}")
    return resolved


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def stage(root, output):
    root, output = root.resolve(), output.resolve()
    if output.is_relative_to(root):
        raise ValueError("Stage outside the repository to keep the source checkout unchanged.")
    if output.exists():
        raise FileExistsError("Output must be a NEW directory: " + str(output))
    # Validate every source and identity BEFORE creating output. Exact working-file
    # SHA256 values remain explicit even where Git checkout newline rules differ.
    data = {src: within(root, src).read_bytes() for src in INPUTS}
    source_commit = git(root, "rev-parse", "HEAD")
    dirty = bool(git(root, "status", "--porcelain"))
    manifest = {"schema": 1, "sourceCommit": source_commit, "sourceDirty": dirty,
                "status": "Staged source only; not a Unity build or GPU acceptance", "files": []}
    output.mkdir(parents=True, exist_ok=False)
    for src, dst in INPUTS.items():
        target = within(output, dst)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data[src])
        manifest["files"].append({"source": src, "path": dst, "sha256": sha(data[src])})
    generated = {
        "Packages/manifest.json": json.dumps({"dependencies": {
            "com.unity.modules.imgui": "1.0.0",
            "com.unity.modules.imageconversion": "1.0.0",
            "com.unity.modules.jsonserialize": "1.0.0"}}, indent=2)+"\n",
        "ProjectSettings/ProjectVersion.txt": "m_EditorVersion: 6000.5.2f1\n",
    }
    for dst, text in generated.items():
        target = within(output, dst)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(text.encode("utf-8"))
    (output/"Assets/Resources/staging.json").write_text(json.dumps(manifest, indent=2)+"\n", encoding="utf-8")
    verify(root, output)
    return manifest


def verify(root, output):
    manifest = json.loads((output/"Assets/Resources/staging.json").read_text(encoding="utf-8"))
    files = manifest["files"]
    if len(files) != len(INPUTS) or {f["source"]: f["path"] for f in files} != INPUTS:
        raise ValueError("Staging manifest source set changed.")
    for item in files:
        if sha(within(root, item["source"]).read_bytes()) != item["sha256"]:
            raise ValueError("Source changed: " + item["source"])
        if sha(within(output, item["path"]).read_bytes()) != item["sha256"]:
            raise ValueError("Staged bytes changed: " + item["path"])
    return manifest


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--verify", action="store_true", help="Read-only: compare all original and staged bytes")
    a = p.parse_args()
    manifest = verify(ROOT, a.output.resolve()) if a.verify else stage(ROOT, a.output)
    print(json.dumps({"status": "PASS", "scope": "source staging only",
                      "sourceCommit": manifest["sourceCommit"], "files": len(manifest["files"])}, indent=2))


if __name__ == "__main__":
    main()
