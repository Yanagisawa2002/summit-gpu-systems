"""Recheck the September 2026 native campaign from its complete portable archive.

Reads recorded data only. Does not execute binaries, contact the host, qualify
performance, or claim a fresh check of externally installed compiler binaries.
"""
import argparse
import copy
import csv
import hashlib
import io
import json
import math
from pathlib import Path, PurePosixPath
import re
import struct
import tarfile

from analyze import equal_csr, equal_state, read_csr, read_state
from input_contract import CASES
from linux_telemetry import interval
from monitor_calibration import evaluate
from run_overflow import check_report
from run_process import verify_execution
from scalar_oracle import compare, consume

CPU = "8032841a2bb7968fbf6e8e06a540fad123c77a63"
CUDA = "9932ec879798345e69cea79f65615a68d7115b33"
UUID = "GPU-27a668ff-c748-4395-c3e1-32c396c485a0"
RUN = "Artifacts/whole-task-md-20260915"
ARMS = {"arborx", "grid", "arborx-openmp", "grid-openmp", "arborx-cuda", "tiled128", "tiled256"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def contained(name):
    path = PurePosixPath(name)
    require(name and not path.is_absolute() and ".." not in path.parts and "\\" not in name,
            "Noncontained logical path")
    return path.as_posix()


class File:
    def __init__(self, snapshot, name):
        self.snapshot, self.name = snapshot, contained(name)

    def read_bytes(self):
        return self.snapshot.blobs[self.snapshot.files[self.name]["sha256"]]

    def read_text(self):
        return self.read_bytes().decode("utf-8-sig")

    def __str__(self):
        return self.name


class Snapshot:
    def __init__(self, archive):
        self.blobs = {}
        with tarfile.open(archive, "r:xz") as bundle:
            members = bundle.getmembers()
            require(len(members) <= 20000 and all(m.isfile() for m in members), "Archive shape limit")
            require(sum(m.size for m in members) <= 1024**3, "Archive expansion limit")
            require(len({m.name for m in members}) == len(members), "Duplicate archive member")
            index = json.loads(bundle.extractfile("index.json").read())
            require(index.get("schemaVersion") == 1 and index.get("format") == "sha256-content-addressed-tar-v1",
                    "Evidence archive schema")
            require(sha((json.dumps(index["manifest"], indent=2) + "\n").encode("utf-8"))
                    == index["originalManifestSha256"], "Original exported file manifest differs")
            self.index = index
            entries = index["manifest"]["files"]
            self.files = {contained(row["path"]): row for row in entries}
            require(entries and len(entries) == len(self.files), "Duplicate or absent logical file")
            needed = {row["sha256"] for row in entries}
            require(all(re.fullmatch("[0-9a-f]{64}", x) for x in needed), "Invalid blob identity")
            require({m.name for m in members} == {"index.json", *("blobs/" + x for x in needed)},
                    "Missing or extra content blob")
            # Read in physical archive order: random xz seeks repeatedly
            # decompress the stream and turn a small audit into quadratic I/O.
            for member in members:
                if member.name == "index.json":
                    continue
                digest = member.name.removeprefix("blobs/")
                raw = bundle.extractfile(member).read()
                require(sha(raw) == digest, "Blob hash mismatch")
                self.blobs[digest] = raw
            for row in entries:
                require(len(self.blobs[row["sha256"]]) == row["bytes"], "Logical byte count mismatch")

    def file(self, name):
        return File(self, name)

    def json(self, name):
        return json.loads(self.file(name).read_text())

    def capture(self, root, recorded):
        actual = {}
        for tree in recorded["trees"]:
            prefix = root + "/" + contained(tree).rstrip("/") + "/"
            rows = [row for name, row in self.files.items() if name.startswith(prefix)
                    and not {".git", "__pycache__"}.intersection(PurePosixPath(name[len(prefix):]).parts)]
            require(rows, "Empty identity tree: " + tree)
            for row in rows:
                relative = row["path"][len(root) + 1:]
                actual[relative] = {"path": relative, "sha256": row["sha256"], "bytes": row["bytes"]}
        for relative in recorded["requiredFiles"]:
            row = self.files[root + "/" + contained(relative)]
            actual[relative] = {"path": relative, "sha256": row["sha256"], "bytes": row["bytes"]}
        require(actual and [actual[k] for k in sorted(actual)] == recorded["files"],
                "Captured files or directory membership differ")


def verify(snapshot):
    # Git's text checkout policy may change CRLF/LF across hosts. Raw archived
    # hashes remain mandatory; only interpreter-source comparison normalizes EOL.
    validator_names = ("analyze.py", "input_contract.py", "linux_telemetry.py", "monitor_calibration.py",
                       "run_overflow.py", "run_process.py", "scalar_oracle.py")
    for name in validator_names:
        archived = snapshot.file("gen2-cuda-r4/checkout/PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/" + name).read_bytes()
        current = Path(__file__).with_name(name).read_bytes()
        require(archived.replace(b"\r\n", b"\n") == current.replace(b"\r\n", b"\n"),
                "Offline validator differs from the observed implementation: " + name)
    matrix, processes, build_keys = set(), [], set()
    for name in sorted(snapshot.files):
        if "/" + RUN + "/runs/" not in name or not name.endswith("/process.json"):
            continue
        root = name.split("/" + RUN + "/", 1)[0]
        directory = name.rsplit("/", 1)[0]
        process = snapshot.json(name)
        arm, case = process["arm"], process["case"]
        bootstrap = process["name"] == "monitor-bootstrap-r4"
        require(arm in ARMS and case in CASES and process["warmups"] == 0, "Unexpected process")
        require(process["completed"] and process["exitCode"] == 0 and process["validationCompleted"]
                and not process["performanceEligible"] and not process["diagnostic"], "Invalid numerical process")
        require(process["sourceCommit"] == (CUDA if arm in {"arborx-cuda", "tiled128", "tiled256"} else CPU),
                "Wrong recorded build source")
        require(process["measured"] == (400 if bootstrap else 2), "Repetition count changed")
        if bootstrap:
            require(arm == "tiled128" and case == "original-10", "Unexpected calibration workload")
        else:
            require((arm, case) not in matrix, "Duplicate arm/case process")
            matrix.add((arm, case))
        validation = snapshot.json(directory + "/validation.json")
        require(validation["arm"] == arm and validation["case"] == case
                and validation["sourceCommit"] == process["sourceCommit"], "Validation identity differs")
        only = CASES[case][1]
        require(validation["membershipOnly"] == only and validation["completeOutputValidation"] == (not only),
                "Membership/state validation scope differs")
        snapshot.capture(root, validation["evidence"])
        build_path = root + "/" + process["buildReceipt"]
        require(snapshot.files[build_path]["sha256"] == process["buildReceiptSha256"], "Build receipt changed")
        build = snapshot.json(build_path)
        if build_path not in build_keys:
            require(build["completed"] and build["sourceClean"] and build["sourceCommit"] == process["sourceCommit"],
                    "Unsuccessful or incorrectly bound build")
            require(build["commands"] and all(c.get("exitCode") == 0 for c in build["commands"]), "Build commands failed")
            for command in build["commands"]:
                require(snapshot.files[root + "/" + command["log"]]["sha256"] == command["logSha256"], "Build log changed")
            snapshot.capture(root, build["inputs"])
            snapshot.capture(root, build["products"])
            build_keys.add(build_path)
        suffix = "-cuda" if arm in {"arborx-cuda", "tiled128", "tiled256"} else "-openmp" if "openmp" in arm else ""
        executable = RUN + "/native-build" + suffix + "/SummitMolecularNative"
        require(executable in build["products"]["requiredFiles"]
                and snapshot.files[root + "/" + executable]["sha256"] == process["executableSha256"]
                and validation["executableSha256"] == process["executableSha256"], "Executable binding differs")
        verify_execution(arm, 8 if "openmp" in arm else 1, UUID, process["pid"],
                         snapshot.file(directory + "/output/execution-space.txt").read_text())
        golden = root + "/" + RUN + "/inputs/" + case
        require(snapshot.files[golden + ".bin"]["sha256"] == process["inputSha256"], "Input changed")
        rows = list(csv.DictReader(io.StringIO(snapshot.file(directory + "/output/timings.csv").read_text())))
        require(len(rows) == process["measured"] and all(r["verified"] == "true" and r["phase"] == "measured" for r in rows),
                "Missing per-repetition full verification")
        require(all(math.isfinite(float(r["hostWallMs"])) and float(r["hostWallMs"]) > 0 for r in rows), "Invalid task timer")
        audits = []
        for step in (0, process["measured"] - 1):
            actual = directory + "/output/step-" + str(step)
            audit = {"step": step, "csr": equal_csr(snapshot.file(actual + ".csr"), snapshot.file(golden + ".csr"))}
            if not only:
                audit["stateMaxAbs"] = equal_state(snapshot.file(actual + ".state"), snapshot.file(golden + ".state"))
            audits.append(audit)
        require(audits == validation["audits"], "Complete saved output audit differs")
        processes.append({"arm": arm, "case": case, "bootstrap": bootstrap, "repetitions": len(rows), "audits": audits})
    require(matrix == {(a, c) for a in ARMS for c in CASES}, "Incomplete seven-arm/eight-case matrix")
    require(len(processes) == 57, "Missing or extra bootstrap process")

    root = "gen2-cuda-r4/checkout"
    scalar = []
    for case, (n, only) in CASES.items():
        if only:
            continue
        oracle = snapshot.json(root + "/" + RUN + "/scalar-r4-" + case + "/oracle.json")
        require(oracle["status"] == "validated" and oracle["case"] == case and oracle["sourceCommit"] == CUDA
                and oracle["completeStatePassed"]
                and oracle["fullMembershipPassed"] == (n <= 4000) and oracle["components"] == n * 9,
                "Independent oracle scope differs")
        require(oracle["oracleSourceSha256"] == snapshot.files[root + "/PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/scalar_oracle.py"]["sha256"],
                "Independent scalar implementation changed")
        snapshot.capture(root, oracle["evidence"])
        golden = root + "/" + RUN + "/inputs/" + case
        raw = snapshot.file(golden + ".bin").read_bytes()
        require(len(raw) == 20 + n * 28 and raw[:8] == b"SUMD0001"
                and struct.unpack_from("<IfI", raw, 8) == (n, 3., 0), "Canonical input ABI")
        points = [struct.unpack_from("<fff", raw, 20 + i * 16) for i in range(n)]
        velocities = [struct.unpack_from("<fff", raw, 20 + n * 16 + i * 12) for i in range(n)]
        offsets, ids = read_csr(snapshot.file(golden + ".csr"))
        expected = consume(points, velocities, [list(ids[offsets[i]:offsets[i+1]]) for i in range(n)])
        compare(read_state(snapshot.file(golden + ".state")), expected)
        compare(read_state(snapshot.file(root + "/" + oracle["scalarState"])), expected)
        required_states = {RUN + "/runs/cuda-r4-" + case + "-tiled" + str(tile) + "/output/step-" + str(step) + ".state"
                           for tile in (128, 256) for step in (0, 1)}
        require(len(oracle["actualStates"]) == 4 and set(oracle["actualStates"]) == required_states,
                "Both tiles' complete first/last state files required")
        errors = {p: compare(read_state(snapshot.file(root + "/" + p)), expected) for p in oracle["actualStates"]}
        require(errors == oracle["actualMaxAbs"], "Independent full-state recheck differs")
        scalar.append({"case": case, "components": n*9, "actualStates": 4, "maxAbs": [max(x[f] for x in errors.values()) for f in range(3)]})
    capacity = []
    for tile in (128, 256):
        record = snapshot.json(root + "/" + RUN + "/overflow-r4-" + str(tile) + "/acceptance.json")
        require(record["status"] == "validated" and record["exitCode"] == 0
                and record["sourceCommit"] == CUDA and record["tile"] == tile and record["case"] == "tail-129"
                and record["records"]["input"] == RUN + "/inputs/tail-129.bin"
                and record["records"]["golden"] == RUN + "/inputs/tail-129.csr", "Capacity check did not pass")
        snapshot.capture(root, record["evidence"])
        _, ids = read_csr(snapshot.file(root + "/" + record["records"]["golden"]))
        check_report(snapshot.json(root + "/" + record["records"]["report"]), tile, len(ids), record["pid"], UUID)
        capacity.append(tile)

    directory = root + "/" + RUN + "/runs/monitor-bootstrap-r4"
    process = snapshot.json(directory + "/process.json")
    background = snapshot.json(directory + "/background.json")
    timings = list(csv.DictReader(io.StringIO(snapshot.file(directory + "/output/timings.csv").read_text())))
    rejection = snapshot.json(root + "/" + RUN + "/linux-monitor-calibration-r4.json")
    snapshot.capture(root, rejection["evidence"])
    try:
        evaluate(process, background, snapshot.file(directory + "/output/execution-space.txt").read_text(), timings)
    except ValueError as error:
        require(rejection["status"] == "rejected" and str(error) == rejection["error"], "Calibration rejection differs")
    else:
        raise ValueError("Recorded rejected calibration unexpectedly qualified")
    rows = copy.deepcopy(background["rows"])
    for row in rows:
        row["ownedGpuPids"] = [] if row["phase"] == "before" else [process["pid"]]
    failures = [{"index": i, **interval(a, b)} for i, (a, b) in enumerate(zip(rows, rows[1:])) if not interval(a, b)["passed"]]
    return {"status": "verified", "logicalFiles": len(snapshot.files), "uniqueContents": len(snapshot.blobs),
            "buildInputProductManifests": len(build_keys), "matrixProcesses": len(matrix), "matrixRepetitions": 112,
            "matrixFullStateRepetitions": 98, "matrixMembershipOnlyRepetitions": 14,
            "bootstrapRepetitions": 400, "fullSavedCsrFiles": 114, "fullSavedStateFiles": 100,
            "scalarRecomputedCases": scalar, "capacityRejections": capacity, "calibration": "rejected-reproduced",
            "calibrationFailedIntervals": failures, "performanceEligible": False,
            "limits": "Recomputed every saved complete CSR/state and scalar state; retained remote all-pairs membership receipt. External tool binaries were hashed on the host; this offline check does not execute or freshly validate that toolchain.",
            "processes": processes}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = verify(Snapshot(args.archive))
    result["archiveSha256"] = sha(args.archive.read_bytes())
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2)
        stream.write("\n")
    print(json.dumps({k: v for k, v in result.items() if k not in ("processes", "scalarRecomputedCases", "calibrationFailedIntervals")}, indent=2))
