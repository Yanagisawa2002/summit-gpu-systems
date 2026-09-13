"""Source staging and manifests only; all native/Unity execution stays in the mutex stage."""
import argparse
import difflib
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ACTUAL = ROOT / "Artifacts/actual-20260910"


def digest(path):
    return hashlib.file_digest(path.open("rb"), "sha256").hexdigest()


def write_new(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(value, indent=2) + "\n")


def verify_sources():
    checked = []
    lock = json.loads((ROOT / "PublicBenchmarks/External/sources.lock.json").read_text())
    for source in lock["sources"]:
        if source["id"] not in ("cabana", "arborx"):
            continue
        for entry in source["files"]:
            path = ACTUAL / "dependencies" / source["id"] / entry["path"]
            if digest(path) != entry["sha256"] or path.stat().st_size != entry["bytes"]:
                raise ValueError(f"Pinned upstream file differs: {path}")
            checked.append({"source": source["id"], "commit": source["commit"], **entry})
    return checked


def capture_source():
    verify_sources()
    source = ACTUAL / "dependencies/cabana/benchmark/core/Cabana_LinkedCellPerformance.cpp"
    original = source.read_text()
    anchor = "create_timer.stop( pid );"
    if original.count(anchor) != 1:
        raise ValueError("Unique upstream post-timer anchor changed")
    changed = original.replace('#include "../Cabana_BenchmarkUtils.hpp"', '#include <Cabana_BenchmarkUtils.hpp>\n#include "SnapshotIO.hpp"\n#include <cstdlib>')
    changed = changed.replace(anchor, anchor + '\n                if (auto directory = std::getenv("SUMMIT_CAPTURE_DIR"))\n                    summit_actual::capture_linked(directory, x, linked_cell_list, num_p, cutoff, grid_min, grid_max, t);')
    target = ACTUAL / "native-generated"
    target.mkdir(exist_ok=False)
    (target / "CabanaCapture.cpp").write_text(changed)
    (target / "CabanaCapture.patch").write_text("".join(difflib.unified_diff(original.splitlines(True), changed.splitlines(True), fromfile="upstream", tofile="capture")))


def manifest():
    checked = verify_sources()
    files = []
    for path in sorted((ACTUAL / "inputs").glob("*.bin")):
        files.append({"name": path.stem, "path": path.as_posix(), "sha256": digest(path), "bytes": path.stat().st_size, "kind": "cabana" if path.name.startswith("cabana-") else "arborx"})
    if sum(f["kind"] == "cabana" for f in files) != 40 or sum(f["kind"] == "arborx" for f in files) != 1:
        raise ValueError("Expected 40 original Cabana iterations and one complete default ArborX snapshot")
    write_new(ACTUAL / "input-manifest.json", {"cabanaCommit": "dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802", "arborxCommit": "375875dfb6b2e7631b1ba599cd26ee5c1e68ab90", "files": files, "verifiedUpstreamFiles": checked})


def stage_player():
    destination = ACTUAL / "unity-host"
    destination.mkdir(exist_ok=False)
    mappings = []

    def copy(source, target):
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        mappings.append({"source": source.relative_to(ROOT).as_posix(), "staged": target.relative_to(destination).as_posix(), "sha256": digest(target)})

    for package in ("com.summit.gpu-primitives", "com.summit.gpu-direct-binning", "com.summit.gpu-sensor-pipeline"):
        origin = ROOT / "Packages" / package
        for path in sorted((origin / "Runtime").rglob("*")):
            if path.is_file():
                copy(path, destination / "Packages" / package / path.relative_to(origin))
        copy(origin / "package.json", destination / "Packages" / package / "package.json")
    for folder, target in (("Adapters", "Adapters"), ("Actual", "Actual")):
        origin = ROOT / "PublicBenchmarks/External" / folder
        for path in sorted(origin.rglob("*")):
            if path.is_file():
                copy(path, destination / "Assets" / target / path.relative_to(origin))
    write_new(destination / "Packages/manifest.json", {"dependencies": {"com.unity.modules.jsonserialize": "1.0.0"}})
    (destination / "ProjectSettings").mkdir()
    (destination / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 6000.5.2f1\n")
    write_new(ACTUAL / "player-staging-receipt.json", {"editor": "6000.5.2f1", "classification": "external workload snapshot host; not an application sample", "files": mappings})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("action", choices=("capture-source", "manifest", "stage-player"))
    action = parser.parse_args().action
    {"capture-source": capture_source, "manifest": manifest, "stage-player": stage_player}[action]()
