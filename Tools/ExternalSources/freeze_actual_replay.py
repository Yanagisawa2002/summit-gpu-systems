"""Freeze already-built, fully validated sources/inputs/binaries before formal pairs."""
import csv
import datetime
import json
import subprocess
from pathlib import Path
from prepare_actual_replay import ROOT, ACTUAL, digest, write_new


def main():
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip():
        raise ValueError("Commit reviewable source/protocol before the formal freeze")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    for kind, count in (("cabana", 40), ("arborx", 1)):
        result = json.loads((ACTUAL / f"runs/validation-{kind}-r1/gpu/result.json").read_text())
        if result["status"] != "completed" or len(result["rows"]) != count or not all(r["verified"] and r["phase"] == "validation" for r in result["rows"]):
            raise ValueError("Actual full-GPU-CSR gate is incomplete")
        with (ACTUAL / f"native-correctness/{kind}.csv").open() as source:
            rows = list(csv.DictReader(source))
        if len(rows) != (80 if kind == "cabana" else 20) or not all(r["verified"] == "true" for r in rows):
            raise ValueError("Native replay full-output gate is incomplete")
    staging = json.loads((ACTUAL / "player-r1-staging-receipt.json").read_text(encoding="utf-8-sig"))
    for entry in staging["files"]:
        if digest(ROOT / entry["source"]) != entry["sha256"] or digest(ACTUAL / "unity-host" / entry["staged"]) != entry["sha256"]:
            raise ValueError("Player staging differs from committed source: " + entry["source"])
    paths = {ROOT / entry["source"] for entry in staging["files"]}
    for directory in ("player-r1", "inputs", "k-install"):
        paths.update(p for p in (ACTUAL / directory).rglob("*") if p.is_file())
    paths.update(p for p in (ROOT / "Tools/ExternalSources/ActualNative").rglob("*") if p.is_file())
    for relative in ("input-manifest.json", "player-r1-staging-receipt.json", "dependency-source-receipt.json", "boost-source-receipt.json", "native-generated/CabanaCapture.cpp", "native-generated/CabanaCapture.patch", "native-build/CMakeCache.txt", "cabana-build/CMakeCache.txt", "k-build/CMakeCache.txt", "cabana-build/benchmark/core/LinkedCellPerformance.exe", "native-build/CabanaCapture.exe", "native-build/CabanaReplay.exe", "native-build/ArborXNative.exe", "native-build/ArborXReplay.exe"):
        paths.add(ACTUAL / relative)
    for relative in ("Docs/EXTERNAL_ACTUAL_PROTOCOL_2026-09-10.md", "PublicBenchmarks/External/sources.lock.json", "Tools/ExternalSources/Run-ActualReplay.ps1", "Tools/ExternalSources/Invoke-ActualProcess.ps1", "Tools/ExternalSources/Invoke-ActualStage.ps1"):
        paths.add(ROOT / relative)
    toolchain = []
    for path in (Path("C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"), Path("C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/link.exe"), Path("C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe"), ACTUAL / "dependencies/cmake-3.31.10-windows-x86_64/bin/cmake.exe"):
        toolchain.append({"path": path.as_posix(), "sha256": digest(path)})
    write_new(ACTUAL / "frozen-run.json", {
        "sourceCommit": commit, "handoffCommit": "e0db6af185985c5911a4de482e555584387faa01",
        "frozenUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "protocol": "Docs/EXTERNAL_ACTUAL_PROTOCOL_2026-09-10.md", "pairOrder": ["native,gpu", "gpu,native", "native,gpu", "gpu,native"],
        "warmupsPerProcessPerCase": 10, "measuredPerProcessPerCase": 10,
        "nativeBackend": "Kokkos Serial, one execution thread", "gpuBackend": "Unity 6000.5.2f1 Mono D3D12; inherited affinity/priority, no changes",
        "toolchain": toolchain,
        "files": [{"path": p.relative_to(ROOT).as_posix(), "sha256": digest(p), "bytes": p.stat().st_size} for p in sorted(paths)]})


if __name__ == "__main__":
    main()
