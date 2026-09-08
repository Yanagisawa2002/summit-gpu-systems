"""Read-only upstream audit. Produces source locks, never builds or runs a workload."""
import hashlib
import json
from pathlib import Path
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[2]
SOURCES = [
    ("cabana", "ECP-copa/Cabana", "dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802", "BSD-3-Clause", "external-library-native-benchmark",
     ["LICENSE", "CMakeLists.txt", "benchmark/CMakeLists.txt", "benchmark/core/CMakeLists.txt", "benchmark/Cabana_BenchmarkUtils.hpp", "benchmark/core/Cabana_LinkedCellPerformance.cpp", "core/src/impl/Cabana_CartesianGrid.hpp", "core/src/Cabana_LinkedCellList.hpp"], ["benchmark/"]),
    ("arborx", "arborx/ArborX", "375875dfb6b2e7631b1ba599cd26ee5c1e68ab90", "BSD-3-Clause", "external-library-native-benchmark",
     ["LICENSE", "CMakeLists.txt", "benchmarks/CMakeLists.txt", "benchmarks/bvh_driver/CMakeLists.txt", "benchmarks/bvh_driver/bvh_driver.cpp", "benchmarks/bvh_driver/benchmark_registration.hpp", "benchmarks/utils/ArborXBenchmark_PointClouds.hpp", "src/geometry/algorithms/ArborX_Intersects.hpp", "src/geometry/algorithms/ArborX_Distance.hpp", "src/geometry/ArborX_Sphere.hpp"], ["benchmarks/bvh_driver/"]),
    ("entities-boids", "Unity-Technologies/EntityComponentSystemSamples", "6786a741ee1f118ed14cecfa02beae8e926937b0", "Unity-Companion-License", "external-official-application-sample",
     ["LICENSE.md", "EntitiesSamples/ProjectSettings/ProjectVersion.txt", "EntitiesSamples/Packages/manifest.json", "EntitiesSamples/Packages/packages-lock.json", "EntitiesSamples/Assets/Boids/Scripts/BoidAuthoring.cs", "EntitiesSamples/Assets/Boids/Scripts/BoidTargetAuthoring.cs", "EntitiesSamples/Assets/Boids/Scripts/BoidSchoolAuthoring.cs", "EntitiesSamples/Assets/Boids/Scripts/BoidSystem.cs"], ["EntitiesSamples/Assets/Boids/", "EntitiesSamples/ProjectSettings/", "EntitiesSamples/Packages/"]),
]

def get(url):
    with urlopen(Request(url, headers={"User-Agent": "SUMMIT-source-contract-audit"}), timeout=60) as response:
        return response.read()

def main():
    destination = ROOT / "PublicBenchmarks/External"
    cache = ROOT / "Artifacts/external-references"
    lock = {"schemaVersion": 1, "performanceStatus": "Unmeasured", "defaultAction": "prepare-only", "sources": []}
    for key, repo, commit, license_id, kind, paths, tree_prefixes in SOURCES:
        tree = json.loads(get(f"https://api.github.com/repos/{repo}/git/trees/{commit}?recursive=1"))
        if tree.get("truncated"):
            raise RuntimeError("Incomplete upstream tree")
        blobs = {t["path"]: t for t in tree["tree"] if t["type"] == "blob"}
        # Resolve Cabana's license by actual pinned path, not GitHub's NOASSERTION.
        if key == "cabana" and "LICENSE" not in blobs:
            paths[0] = "LICENSE.txt"
        if key == "arborx":
            paths.extend(p for p in blobs if p.endswith("ArborX_Predicates.hpp"))
            paths.extend(p for p in blobs if p.endswith("ArborX_PredicateHelpers.hpp"))
        entry = {"id": key, "repository": f"https://github.com/{repo}.git", "commit": commit,
                 "classification": kind, "license": license_id, "files": [], "tree": []}
        for path in paths:
            blob = blobs[path]
            url = f"https://raw.githubusercontent.com/{repo}/{commit}/{path}"
            data = get(url)
            if hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest() != blob["sha"]:
                raise RuntimeError("Upstream blob mismatch: " + path)
            target = cache / key / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            entry["files"].append({"path": path, "url": url, "sha256": hashlib.sha256(data).hexdigest(), "gitBlob": blob["sha"], "bytes": len(data)})
            if path.startswith("LICENSE"):
                notice = destination / "Notices" / f"{key}.txt"
                notice.parent.mkdir(parents=True, exist_ok=True)
                notice.write_bytes(data)
        for path, blob in sorted(blobs.items()):
            if any(path.startswith(prefix) for prefix in tree_prefixes):
                entry["tree"].append({"path": path, "gitBlob": blob["sha"], "bytes": blob.get("size")})
        lock["sources"].append(entry)
        print(f"Pinned {key}: {commit}; fetched {len(paths)} small sources; assets remain remote")
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "sources.lock.json").write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
