"""Bounded source preparation after coordinator admission for the actual host.

Windows uses Invoke-Stage.ps1; Linux requires its separate held host lease/lock.
"""
import argparse
import json
import shutil
import urllib.request
import zipfile
from pathlib import Path
from extract_upstream import extract
from provenance import capture, sha

ROOT = Path(__file__).resolve().parents[3]
RUN = ROOT / "Artifacts/whole-task-md-20260915"
PINS = {"arborx": ("arborx/ArborX", "375875dfb6b2e7631b1ba599cd26ee5c1e68ab90"),
        "kokkos": ("kokkos/kokkos", "6739bc623081648af9e752b616d9671527922cbf")}


def fetch(url, target, cap=100*1024*1024):
    if target.exists():
        raise ValueError("Refusing to overwrite " + str(target))
    request = urllib.request.Request(url, headers={"User-Agent": "SUMMIT-public-whole-task-validation"})
    with urllib.request.urlopen(request, timeout=60) as response, target.open("xb") as output:
        size = 0
        while chunk := response.read(1024*1024):
            size += len(chunk)
            if size > cap:
                raise ValueError("Download exceeds declared cap")
            output.write(chunk)


def unzip(archive, destination, strip=True):
    with zipfile.ZipFile(archive) as z:
        entries = z.infolist()
        if sum(x.file_size for x in entries) > 512*1024*1024:
            raise ValueError("Source expansion exceeds 512 MiB")
        prefix = entries[0].filename.split("/")[0] + "/" if strip else ""
        destination.mkdir(parents=True, exist_ok=False)
        for entry in entries:
            path = Path(entry.filename.removeprefix(prefix))
            if not path.parts or entry.is_dir():
                continue
            if path.is_absolute() or ".." in path.parts or ":" in str(path):
                raise ValueError("Unsafe archive path")
            target = destination / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(z.read(entry))


def stage_unity():
    host = RUN / "unity-host"
    host.mkdir(exist_ok=False)
    mappings = []
    def copy(origin, target):
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(origin, target)
        mappings.append({"source": origin.relative_to(ROOT).as_posix(), "target": target.relative_to(host).as_posix(), "sha256": sha(target)})
    for package in ("com.summit.gpu-primitives", "com.summit.gpu-direct-binning", "com.summit.gpu-sensor-pipeline", "com.summit.gpu-timestamps"):
        origin = ROOT / "Packages" / package
        for path in sorted((origin / "Runtime").rglob("*")):
            if path.is_file():
                copy(path, host / "Packages" / package / path.relative_to(origin))
        copy(origin / "package.json", host / "Packages" / package / "package.json")
    for origin, target in ((ROOT / "PublicBenchmarks/External/Adapters", "Assets/Adapters"),
                           (ROOT / "PublicBenchmarks/WholeTaskMolecularDynamics/Unity", "Assets/WholeTaskMD")):
        for path in sorted(origin.rglob("*")):
            if path.is_file():
                copy(path, host / target / path.relative_to(origin))
    (host / "Packages/manifest.json").write_text(json.dumps({"dependencies": {"com.unity.modules.jsonserialize": "1.0.0"}}, indent=2))
    (host / "ProjectSettings").mkdir()
    (host / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 6000.5.2f1\n")
    (RUN / "player-staging.json").write_text(json.dumps(mappings, indent=2) + "\n")


def main(native_only=False):
    RUN.mkdir(parents=True, exist_ok=True)
    downloads = RUN / "downloads"
    downloads.mkdir(exist_ok=False)
    deps = RUN / "dependencies"
    deps.mkdir(exist_ok=False)
    receipts = []
    for name, (repo, pin) in PINS.items():
        url = f"https://codeload.github.com/{repo}/zip/{pin}"
        target = downloads / (name + ".zip")
        fetch(url, target)
        unzip(target, deps / name)
        receipts.append({"id": name, "repository": repo, "pin": pin, "url": url, "sha256": sha(target),
                         "archive": target.relative_to(ROOT).as_posix(), "downloadBytes": target.stat().st_size,
                         "sourceManifest": capture(ROOT, [(deps / name).relative_to(ROOT).as_posix()])})
        (RUN / "dependencies.json").write_text(json.dumps(receipts, indent=2) + "\n")
        print(name, pin, flush=True)
    if native_only:
        extract(RUN / "generated")
        return
    base = "https://github.com/Kitware/CMake/releases/download/v3.31.10/"
    archive = "cmake-3.31.10-windows-x86_64.zip"
    sums = downloads / "cmake-SHA-256.txt"
    fetch(base + "cmake-3.31.10-SHA-256.txt", sums)
    fetch(base + archive, downloads / archive)
    expected = next(line.split()[0] for line in sums.read_text().splitlines() if line.endswith(archive))
    if sha(downloads / archive) != expected:
        raise ValueError("CMake publisher checksum mismatch")
    unzip(downloads / archive, deps / "cmake")
    receipts.append({"id": "cmake", "version": "3.31.10", "sha256": expected})
    (RUN / "dependencies.json").write_text(json.dumps(receipts, indent=2) + "\n")
    extract(RUN / "generated")
    stage_unity()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--native-only", action="store_true", help="Pinned sources/header only; use the assigned host's existing CMake")
    main(parser.parse_args().native_only)
