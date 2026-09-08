"""Prepare or verify immutable external material. There is deliberately no run action."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[2]
LOCK = ROOT / "PublicBenchmarks/External/sources.lock.json"

def verify_files(entry, directory):
    for item in entry["files"]:
        path = directory / item["path"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
            raise ValueError("Source hash mismatch: " + str(path))

def fetch(entry, directory):
    for item in entry["files"]:
        target = directory / item["path"]
        if target.exists():
            if hashlib.sha256(target.read_bytes()).hexdigest() != item["sha256"]:
                raise ValueError("Refusing to overwrite divergent source: " + str(target))
            continue
        with urlopen(Request(item["url"], headers={"User-Agent": "SUMMIT-source-prepare"}), timeout=60) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("Downloaded source hash mismatch")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    verify_files(entry, directory)

def verify_checkout(entry, source):
    # Read-only checks. Never reset, clean, fetch, or edit a supplied worktree.
    head = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if head != entry["commit"]:
        raise ValueError("Checkout is not the pinned upstream commit")
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=all"], text=True)
    if dirty:
        raise ValueError("External checkout must be clean, including untracked assets")
    verify_files(entry, source)
    for item in entry["tree"]:
        data = (source / item["path"]).read_bytes()
        blob = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        if blob != item["gitBlob"]:
            raise ValueError("Pinned scene/asset/source content mismatch: " + item["path"])

def stage_boids(entry, source, destination):
    source, destination = source.resolve(), destination.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError("Source and destination must be separate, non-nested directories")
    if destination.exists():
        raise ValueError("Destination must be new; no existing project is overwritten")
    if os.name == "nt" and len(str(destination)) > 100:
        raise ValueError("Use a short staging path such as C:/src/boids-summit for Unity's generated files")
    verify_checkout(entry, source)
    # Copy only committed project files, never a local Library/cache or ignored
    # credentials. Validate every byte against the pinned tree before writing.
    tree = subprocess.check_output(["git", "-C", str(source), "ls-tree", "-rz", "HEAD", "--", "EntitiesSamples"])
    upstream, copies = [], []
    for record in tree.decode("utf-8").split("\0"):
        if not record:
            continue
        metadata, name = record.split("\t", 1)
        mode, kind, blob = metadata.split()
        if mode not in ("100644", "100755") or kind != "blob":
            raise ValueError("Unsupported linked/submodule source in official project: " + name)
        original = source / name
        relative = Path(name).relative_to("EntitiesSamples")
        if original.is_symlink():
            raise ValueError("Source file must not be a symlink: " + name)
        data = original.read_bytes()
        if hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest() != blob:
            raise ValueError("Committed project bytes differ (including line endings): " + name)
        upstream.append({"path": relative.as_posix(), "gitBlob": blob, "sha256": hashlib.sha256(data).hexdigest()})
        copies.append((original, relative))
    if not upstream:
        raise ValueError("No committed EntitiesSamples project found")
    additions = []
    for folder in ("Adapters", "Boids"):
        additions.append((ROOT / "PublicBenchmarks/External" / folder, Path("Assets/SummitExternal") / folder))
    manifest = json.loads((source / "EntitiesSamples/Packages/manifest.json").read_text(encoding="utf-8"))
    for package in ("com.summit.gpu-primitives", "com.summit.gpu-direct-binning", "com.summit.gpu-sensor-pipeline"):
        if package in manifest["dependencies"]:
            raise ValueError("Official project already declares a SUMMIT package: " + package)
        additions.append((ROOT / "Packages" / package, Path("Packages") / package))
        manifest["dependencies"][package] = "file:" + package
    added_paths = []
    for original_root, staged_root in additions:
        if not original_root.is_dir():
            raise ValueError("Missing local consumer dependency: " + str(original_root))
        for original in sorted(original_root.rglob("*")):
            if original.is_symlink():
                raise ValueError("Consumer dependencies must not contain symlinks")
            if original.is_file():
                relative = staged_root / original.relative_to(original_root)
                copies.append((original, relative))
                added_paths.append(relative)
    planned = [relative.as_posix().casefold() for _, relative in copies]
    if len(set(planned)) != len(planned):
        raise ValueError("Consumer files collide with the official project")
    if os.name == "nt" and any(len(str(destination / relative)) > 240 for _, relative in copies):
        raise ValueError("Staged project paths are too long; choose a shorter output directory")
    for original, relative in copies:
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
    for item in upstream:
        if hashlib.sha256((destination / item["path"]).read_bytes()).hexdigest() != item["sha256"]:
            raise ValueError("Upstream source changed during staging: " + item["path"])
    manifest_path = destination / "Packages/manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    added_paths.append(Path("Packages/manifest.json"))
    added_files = [{"path": path.as_posix(), "sha256": hashlib.sha256((destination / path).read_bytes()).hexdigest()}
                   for path in sorted(added_paths)]
    identity = "".join(item["path"] + "\0" + item["sha256"] + "\n" for item in added_files)
    receipt = {"upstreamCommit": entry["commit"], "performanceStatus": "Unmeasured", "defaultAction": "prepare-only",
               "scene": "Assets/Boids/Boids.unity", "consumer": "BoidsSphereConsumer (disabled until explicit opt-in)",
               "unityVersion": "6000.2.10f1", "upstreamFilesChanged": ["Packages/manifest.json (add SUMMIT file dependencies only)"],
               "sourceCommit": subprocess.check_output(["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip(),
               "upstreamFiles": upstream, "addedFiles": added_files,
               "addedFilesSha256": hashlib.sha256(identity.encode("utf-8")).hexdigest()}
    (destination / "summit-external-attestation.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["fetch-references", "verify-references", "verify-checkout", "stage-boids"])
    parser.add_argument("source", choices=["cabana", "arborx", "entities-boids"])
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    lock = json.loads(LOCK.read_text(encoding="utf-8"))
    if lock["schemaVersion"] != 1:
        raise ValueError("Unsupported source-lock schema")
    entry = next(s for s in lock["sources"] if s["id"] == args.source)
    if args.action == "fetch-references": fetch(entry, args.directory)
    elif args.action == "verify-references": verify_files(entry, args.directory)
    elif args.action == "verify-checkout": verify_checkout(entry, args.directory)
    else:
        if args.source != "entities-boids" or args.output is None:
            raise ValueError("stage-boids requires entities-boids and --output")
        stage_boids(entry, args.directory, args.output)
    print("Prepared/verified only; no benchmark, Player, GPU work, or counter collection executed.")

if __name__ == "__main__": main()
