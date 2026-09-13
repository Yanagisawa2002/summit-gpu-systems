"""Offline audit of every actual GPU CSR and all predeclared process pairs."""
import csv
import json
import math
import statistics
import struct
from pathlib import Path
from prepare_actual_replay import ROOT, ACTUAL, digest, write_new


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def reference(path):
    data = path.read_bytes()
    if data[:8] == b"SMLCL001":
        n, rows = struct.unpack_from("<II", data, 8)
        cursor, total = 16 + 72 + 28 * n, n
    elif data[:8] == b"SMSPH001":
        n, rows, total = struct.unpack_from("<III", data, 8)
        cursor = 20 + 16 * n + 16 * rows
    else:
        raise ValueError("Unknown upstream snapshot ABI")
    return csr(data, rows, total, cursor)


def csr(data, rows, total, cursor):
    if len(data) != cursor + 4 * (rows + 1 + total):
        raise ValueError("Incomplete or trailing CSR bytes")
    offsets = struct.unpack_from(f"<{rows+1}I", data, cursor)
    ids = struct.unpack_from(f"<{total}I", data, cursor + 4 * (rows + 1))
    if offsets[0] != 0 or offsets[-1] != total or any(a > b for a, b in zip(offsets, offsets[1:])):
        raise ValueError("Non-monotone/incomplete CSR")
    membership = tuple(tuple(sorted(ids[offsets[q]:offsets[q+1]])) for q in range(rows))
    checksum = sum(((q + 1) * 0x9e3779b1) ^ (value + 1) for q, row in enumerate(membership) for value in row) & ((1 << 64) - 1)
    return offsets, membership, str(checksum)


def main():
    frozen = load_json(ACTUAL / "frozen-run.json")
    for entry in frozen["files"]:
        if digest(ROOT / entry["path"]) != entry["sha256"]:
            raise ValueError("Frozen build/source/input changed: " + entry["path"])
    manifest = load_json(ACTUAL / "input-manifest.json")
    refs = {entry["name"]: reference(Path(entry["path"])) for entry in manifest["files"]}
    manifest_sha = digest(ACTUAL / "input-manifest.json")
    audit = []
    gpu_reports = {}
    for result_path in sorted((ACTUAL / "runs").glob("*/gpu/result.json")):
        report = load_json(result_path)
        if report["status"] != "completed" or report["manifestSha256"] != manifest_sha:
            raise ValueError("GPU run did not complete with frozen input")
        name = result_path.parents[1].name
        gpu_reports[name] = report
        validation = name.startswith("validation-")
        if not validation and report["sourceCommit"] != frozen["sourceCommit"]:
            raise ValueError("GPU measured build source identity mismatch")
        expected_rows = (40 if report["kind"] == "cabana" else 1) if validation else (80 if report["kind"] == "cabana" else 20)
        if len(report["rows"]) != expected_rows:
            raise ValueError("Incomplete GPU run")
        row_files = set()
        for row in report["rows"]:
            if not row["verified"]:
                raise ValueError("Failed actual GPU output")
            ref_name = f'{row["name"]}-t{max(row["step"],0):02}' if report["kind"] == "cabana" else "arborx-default"
            path = result_path.parent / f'{row["name"]}-{row["phase"]}-{row["step"]}.csr'
            if path in row_files:
                raise ValueError("Duplicate output record")
            row_files.add(path)
            data = path.read_bytes()
            if data[:8] != b"SMCSR001":
                raise ValueError("Actual GPU output ABI changed")
            rows, total = struct.unpack_from("<II", data, 8)
            actual = csr(data, rows, total, 16)
            if actual != refs[ref_name] or actual[2] != row["checksum"] or (rows, total) != (row["queries"], row["ids"]):
                raise ValueError("Full GPU offsets/IDs/multiplicity/checksum mismatch: " + str(path))
            audit.append({"path": path.relative_to(ROOT).as_posix(), "sha256": digest(path), "rows": rows, "ids": total, "verified": True})
        if row_files != set(result_path.parent.glob("*.csr")):
            raise ValueError("Unaccounted raw GPU output")

    comparisons = []
    for kind in ("cabana", "arborx"):
        cases = sorted({r["name"] for r in gpu_reports[f"{kind}-p01-gpu"]["rows"]})
        native = {}
        for pair in range(1, 5):
            with (ACTUAL / f"runs/{kind}-p{pair:02}-native/native.csv").open() as stream:
                native[pair] = list(csv.DictReader(stream))
            if len(native[pair]) != (80 if kind == "cabana" else 20):
                raise ValueError("Incomplete native replay")
            for row in native[pair]:
                ref_name = f'{row["case"]}-t{max(int(row["step"]),0):02}' if kind == "cabana" else "arborx-default"
                if row["verified"] != "true" or row["checksum"] != refs[ref_name][2]:
                    raise ValueError("Native full-result replay/checksum gate failed")
            times = []
            for backend in ("native", "gpu"):
                receipt = load_json(ACTUAL / f"runs/{kind}-p{pair:02}-{backend}/process.process.json")
                if not receipt["exited"] or receipt["exitCode"] != 0 or receipt.get("processorAffinityHex") != "FF" or receipt.get("priorityClass") != "Normal":
                    raise ValueError("Process failure or environment changed")
                times.append((receipt["startedUtc"], receipt["finishedUtc"], backend))
            times.sort()
            if [t[2] for t in times] != (["native", "gpu"] if pair % 2 else ["gpu", "native"]) or times[0][1] > times[1][0]:
                raise ValueError("Pair order/serial execution mismatch")
        for case in cases:
            pairs = []
            for pair in range(1, 5):
                nr = [r for r in native[pair] if r["case"] == case and r["phase"] == "measured"]
                gr = [r for r in gpu_reports[f"{kind}-p{pair:02}-gpu"]["rows"] if r["name"] == case and r["phase"] == "measured"]
                for rows in (nr, gr):
                    if [int(r["step"]) for r in rows] != list(range(10)):
                        raise ValueError("Missing/selected measured samples")
                n = [float(r["hostWallMs"]) for r in nr]
                g = [float(r["hostWallMs"]) for r in gr]
                if not all(math.isfinite(x) and x > 0 for x in n+g):
                    raise ValueError("Invalid host wall measurement")
                native_phases = {field: statistics.mean(float(r[field]) for r in nr) for field in nr[0] if field.endswith("Ms")}
                gpu_phases = {field: statistics.mean(float(r[field]) for r in gr) for field in gr[0] if field.endswith("Ms")}
                gpu_phases["aggregationAndClockRemainderMs"] = gpu_phases["hostWallMs"] - sum(gpu_phases[k] for k in ("uploadMs", "submitReadbackMs", "consumeMs"))
                pairs.append({"pair": pair, "order": "native,gpu" if pair % 2 else "gpu,native", "nativeMeanMs": statistics.mean(n), "gpuMeanMs": statistics.mean(g), "nativeMinMaxMs": [min(n), max(n)], "gpuMinMaxMs": [min(g), max(g)], "nativeOverGpu": statistics.mean(n)/statistics.mean(g), "nativePhasesMeanMs": native_phases, "gpuPhasesMeanMs": gpu_phases})
            logs = [math.log(pair["nativeOverGpu"]) for pair in pairs]
            mean = statistics.mean(logs)
            margin = 3.182446305 * statistics.stdev(logs) / math.sqrt(4)
            comparisons.append({"case": case, "kind": kind, "scope": "cross-backend snapshot staging through host-consumed complete CSR", "pairs": pairs, "nativeGrandMeanMs": statistics.mean(p["nativeMeanMs"] for p in pairs), "gpuGrandMeanMs": statistics.mean(p["gpuMeanMs"] for p in pairs), "nativeOverGpuGeomean": math.exp(mean), "nativeOverGpu95CI": [math.exp(mean-margin), math.exp(mean+margin)], "nativeProcessMeanRangeMs": [min(p["nativeMeanMs"] for p in pairs), max(p["nativeMeanMs"] for p in pairs)], "gpuProcessMeanRangeMs": [min(p["gpuMeanMs"] for p in pairs), max(p["gpuMeanMs"] for p in pairs)]})
    audit_dir = ACTUAL / "analysis"
    audit_dir.mkdir(exist_ok=False)
    write_new(audit_dir / "full-csr-audit.json", {"method": "Every actual offset and every sorted within-row ID including multiplicity, independently decoded from raw GPU bytes; no sampling or CPU model", "files": audit, "totalFiles": len(audit), "totalIdsCompared": sum(a["ids"] for a in audit), "totalRowsCompared": sum(a["rows"] for a in audit)})
    write_new(audit_dir / "comparisons.json", {"sourceCommit": frozen["sourceCommit"], "frozenRunSha256": digest(ACTUAL / "frozen-run.json"), "inputManifestSha256": manifest_sha, "statistics": "four independent paired process means, ten measured samples each; log Student-t 95% CI, df=3; all samples retained", "comparisons": comparisons})
    print(json.dumps({"gpuCsrFiles": len(audit), "idsCompared": sum(a["ids"] for a in audit), "results": [{k: c[k] for k in ("case", "nativeGrandMeanMs", "gpuGrandMeanMs", "nativeOverGpuGeomean", "nativeOverGpu95CI")} for c in comparisons]}, indent=2))


if __name__ == "__main__":
    main()
