"""Prepare or verify immutable external material. There is deliberately no run action."""
import argparse
import hashlib
import json
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
    verify_checkout(entry, source)
    if destination.exists():
        raise ValueError("Destination must be new; no existing project is overwritten")
    # Copy the complete official project. Upstream scenes, assets, manifest dependency
    # versions and lock are preserved. Only add the explicitly scoped consumer/packages.
    shutil.copytree(source / "EntitiesSamples", destination)
    for folder in ("Adapters", "Boids"):
        shutil.copytree(ROOT / "PublicBenchmarks/External" / folder, destination / "Assets/SummitExternal" / folder)
    manifest_path = destination / "Packages/manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for package in ("com.summit.gpu-primitives", "com.summit.gpu-direct-binning", "com.summit.gpu-sensor-pipeline"):
        shutil.copytree(ROOT / "Packages" / package, destination / "Packages" / package)
        manifest["dependencies"][package] = "file:" + package
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    receipt = {"upstreamCommit": entry["commit"], "performanceStatus": "Unmeasured", "defaultAction": "prepare-only",
               "scene": "Assets/Boids/Boids.unity", "consumer": "BoidsSphereConsumer (disabled until explicit opt-in)",
               "unityVersion": "6000.2.10f1", "upstreamFilesChanged": ["Packages/manifest.json (add SUMMIT file dependencies only)"],
               "sourceCommit": subprocess.check_output(["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip()}
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
