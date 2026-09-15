"""Audit complete recorded CSR/state outputs and process-level comparisons.

No timing inference from file size, no ignored failures, no intra-process pseudo-n.
"""
import argparse
import array
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct
from freeze import ROOT, RUN, verify


def read_csr(path):
    data = path.read_bytes()
    if data[:8] != b"SUMDCSR1":
        raise ValueError("CSR ABI: " + str(path))
    n, m = struct.unpack_from("<II", data, 8)
    if len(data) != 16+4*(n+1+m):
        raise ValueError("CSR byte length")
    values = array.array("I")
    values.frombytes(data[16:])
    offsets, ids = values[:n+1], values[n+1:]
    if offsets[0] != 0 or offsets[-1] != m or any(offsets[i+1] < offsets[i] for i in range(n)) or any(x >= n for x in ids):
        raise ValueError("CSR structural contract")
    return offsets, ids


def equal_csr(actual, expected):
    a, b = read_csr(actual), read_csr(expected)
    if a[0] != b[0]:
        raise ValueError("Complete offset mismatch: " + str(actual))
    for q in range(len(a[0])-1):
        lo, hi = a[0][q:q+2]
        if sorted(a[1][lo:hi]) != sorted(b[1][lo:hi]):
            raise ValueError(f"ID/multiplicity mismatch at row {q}: {actual}")
        if q in a[1][lo:hi]:
            raise ValueError("Self-collision not excluded")
    return {"rows": len(a[0])-1, "ids": len(a[1])}


def read_state(path):
    data = path.read_bytes()
    if data[:8] != b"SUMDSTA1":
        raise ValueError("State ABI")
    n, = struct.unpack_from("<I", data, 8)
    if len(data) != 12+n*9*4:
        raise ValueError("State byte length")
    values = array.array("f")
    values.frombytes(data[12:])
    return [values[f*n*3:(f+1)*n*3] for f in range(3)]


def equal_state(actual, expected):
    a, b = read_state(actual), read_state(expected)
    maxima = []
    for field, (x, y) in enumerate(zip(a, b)):
        if len(x) != len(y):
            raise ValueError("State shape")
        abs_tol = .002 if field == 2 else .00002
        rel_tol = .000002 if field == 0 else .00002
        errors = [abs(i-j) for i, j in zip(x, y)]
        if any(not math.isfinite(i) or not math.isfinite(j) or e > abs_tol+rel_tol*abs(j) for i, j, e in zip(x, y, errors)):
            raise ValueError(f"Full state precision field {field}: {actual}")
        maxima.append(max(errors, default=0))
    return maxima


def rows_for(directory):
    output = directory / "output"
    if (output / "result.json").exists():
        value = json.loads((output / "result.json").read_text())
        if value["status"] != "completed":
            raise ValueError("Incomplete Player report")
        rows = value["rows"]
    else:
        with (output / "timings.csv").open() as source:
            raw = list(csv.DictReader(source))
        rows = [{k: (v if k == "phase" else v == "true" if k == "verified" else float(v)) for k, v in r.items()} for r in raw]
    if not rows or not all(r["verified"] for r in rows):
        raise ValueError("Missing/failed per-repetition full-output validation")
    if any(not math.isfinite(r["hostWallMs"]) or r["hostWallMs"] <= 0 for r in rows):
        raise ValueError("Invalid timing")
    return rows


def process_summary(item, limits):
    directory = RUN / "runs" / item["name"]
    receipt = json.loads((directory / "process.json").read_text())
    if not receipt["completed"] or receipt["exitCode"] != 0 or receipt["diagnostic"] or not receipt.get("performanceEligible") or not receipt.get("validationCompleted"):
        raise ValueError("Invalid formal process")
    rows = rows_for(directory)
    measured = [r for r in rows if r["phase"] == "measured"]
    if len(rows) != item["warmups"]+item["measured"] or len(measured) != item["measured"]:
        raise ValueError("Frozen repetition count mismatch")
    audits = []
    for output in sorted((directory / "output").glob("*.csr")):
        audits.append({"path": output.relative_to(ROOT).as_posix(), **equal_csr(output, RUN / "inputs" / (item["case"]+".csr"))})
    states = []
    for output in sorted((directory / "output").glob("*.state")):
        states.append({"path": output.relative_to(ROOT).as_posix(), "maxAbs": equal_state(output, RUN / "inputs" / (item["case"]+".state"))})
    if len(audits) != 2 or len(states) != 2:
        raise ValueError("Frozen first/last complete outputs missing")
    values = [r["hostWallMs"] for r in measured]
    cv = statistics.stdev(values)/statistics.mean(values)
    middle = len(values)//2
    first, last = statistics.mean(values[:middle]), statistics.mean(values[-middle:])
    drift_ratio = max(first/last, last/first)
    numeric = [k for k, v in measured[0].items() if k.endswith("Ms") and isinstance(v, (int, float))]
    return {**item, "meanMs": statistics.mean(values), "minMs": min(values), "maxMs": max(values),
            "rawMeasuredMs": values, "firstUseMs": rows[0]["firstUseMs"],
            "coefficientOfVariation": cv, "firstLastHalfRatio": drift_ratio,
            "stabilityEligible": cv <= limits["maxWithinProcessCoefficientOfVariation"] and drift_ratio <= limits["maxFirstHalfLastHalfCostRatioEitherDirection"],
            "phaseMeans": {k: statistics.mean(r[k] for r in measured) for k in numeric},
            "processSettings": receipt["processSettings"], "csrAudit": audits, "stateAudit": states}


def paired_ratio(numerator, denominator):
    count = len(numerator)
    if count != len(denominator) or count not in (4, 6):
        raise ValueError("This prepared analysis supports four or six independent paired processes")
    logs = [math.log(a/b) for a, b in zip(numerator, denominator)]
    center = statistics.mean(logs)
    critical = {4: 3.182446305284263, 6: 2.570581835636314}[count]
    radius = critical*statistics.stdev(logs)/math.sqrt(count)
    return {"geometricCostRatio": math.exp(center), "nominal95": [math.exp(center-radius), math.exp(center+radius)],
            "processPairs": count, "degreesOfFreedom": count - 1, "ratios": [a/b for a, b in zip(numerator, denominator)]}


def formal():
    frozen = json.loads((RUN / "frozen-run.json").read_text())
    verify(frozen)
    plan = frozen["plan"]
    limits = plan["performanceStability"]
    if plan["statistics"] != {"method": "paired-log-process-means", "confidence": .95}:
        raise ValueError("Implement and review the chosen statistical method before execution")
    processes = [process_summary(item, limits) for item in frozen["processes"]]
    comparisons = []
    stable = all(p["stabilityEligible"] for p in processes)
    for case in sorted({p["case"] for p in processes}):
        values = {arm: {p["round"]: p["meanMs"] for p in processes if p["case"] == case and p["arm"] == arm}
                  for arm in plan["armArtifacts"]}
        stable &= all(max(v.values())/min(v.values()) <= limits["maxAcrossProcessCostRatio"] for v in values.values())
        for pair in plan["comparisons"]:
            numerator, denominator = pair["numerator"], pair["denominator"]
            if values[numerator].keys() != values[denominator].keys():
                raise ValueError("Independent process block pairing differs")
            blocks = sorted(values[numerator])
            comparisons.append({"case": case, **pair,
                                **paired_ratio([values[numerator][i] for i in blocks], [values[denominator][i] for i in blocks])})
    result = {"status": "complete", "performanceEligible": stable, "processes": processes, "comparisons": comparisons,
              "limitations": ["One assigned machine; only the explicitly selected implementations and execution settings.",
                              "One independent simulation step from captured CPU state, not long-time physical stability.",
                              "Backend-resident completion, not CPU-return latency or scene frame time.",
                              "Nominal small-sample intervals are descriptive estimates; no cross-hardware inference."]}
    destination = RUN / "analysis"
    destination.mkdir(exist_ok=False)
    (destination / "confirmation.json").write_text(json.dumps(result, indent=2)+"\n")
    print(json.dumps(comparisons, indent=2))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=("formal", "inspect"))
    p.add_argument("directory", nargs="?", type=Path)
    a = p.parse_args()
    if a.action == "formal":
        formal()
    else:
        rows = rows_for(a.directory)
        for row in rows:
            print(json.dumps(row))
