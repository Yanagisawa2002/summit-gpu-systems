"""Calibrate only from an observed, fully validated CUDA process on the host.

Run after admission/correctness. This neither launches experiments nor edits
their records. A rejected calibration is retained; it never qualifies a host.
"""
import argparse
import copy
import csv
import json
from pathlib import Path
from linux_telemetry import CALIBRATION_REQUIRED, LINUX_MONITOR, gpu_xml, interval
from provenance import ROOT, capture, inside, sha, verify_build, verify_capture, write_new


def evaluate(process, background, config_text, timings):
    if (not process.get("completed") or process.get("exitCode") != 0 or not process.get("validationCompleted")
            or process.get("diagnostic") or process.get("arm") not in ("arborx-cuda", "tiled128", "tiled256")):
        raise ValueError("Calibration needs a successfully validated real CUDA process")
    # The bootstrap's sole expected error is absence of a prior calibration.
    # Throttling, parser failures, sampling gaps, and short coverage remain fatal.
    if background.get("errors") not in ([], [CALIBRATION_REQUIRED]):
        raise ValueError("Unexpected monitoring failures remain in bootstrap evidence")
    if background.get("profile") != LINUX_MONITOR or background.get("failedObservations"):
        raise ValueError("Missing monitor profile or failed raw observations in bootstrap evidence")
    config = dict(line.split("=", 1) for line in config_text.splitlines() if "=" in line)
    if (config.get("selectedExecutionSpace") != "Cuda" or int(config.get("processPid", -1)) != process["pid"]
            or config.get("selectedGpuUuid") != process.get("gpuUuid")):
        raise ValueError("CUDA device/PID identity differs from the observed process")
    clock_before, clock_now, clock_after = [int(config[key]) for key in ("steadyBeforeNs", "monotonicNs", "steadyAfterNs")]
    if not clock_before <= clock_now <= clock_after or clock_after-clock_before > 1_000_000:
        raise ValueError("C++ task clock is not calibrated to Linux monotonic time")
    rows = background.get("rows", [])
    before = [r for r in rows if r["phase"] == "before"]
    during = [r for r in rows if r["phase"] == "during"]
    if len(before) < 2 or before[-1]["time"] - before[0]["time"] < LINUX_MONITOR["preflightSeconds"]:
        raise ValueError("Live quiet preflight coverage is incomplete")
    if len(during) < LINUX_MONITOR["minimumDuringSamples"]:
        raise ValueError("Insufficient live benchmark samples")
    intervals, normalized = [], []
    observed_gpu, covered_tasks = 0, 0
    task_windows = []
    if len(timings) != process["warmups"]+process["measured"]:
        raise ValueError("Calibration has incomplete task rows")
    for task in timings:
        start, end = int(task["taskStartNs"]), int(task["taskEndNs"])
        if task["verified"] != "true" or end <= start:
            raise ValueError("Task output/clock validation failed")
        task_windows.append((start,end))
    for raw in rows:
        if gpu_xml(raw["rawGpuXml"], process["gpuUuid"]) != raw["gpu"]:
            raise ValueError("Parsed GPU observations differ from the retained device XML")
        row = copy.deepcopy(raw)
        # Mapping is established by CUDA's own PID+device UUID and actual NVML
        # records. Different host/container PIDs remain unresolved, never guessed.
        row["ownedGpuPids"] = [] if row["phase"] == "before" else [process["pid"]]
        normalized.append(row)
        if row["phase"] == "during":
            observed_gpu += int(any(p["pid"] == process["pid"] and p["usedMiB"] > 0 for p in row["gpu"]["processes"]))
            sample_start, sample_end = row["sampleStartNs"], int(row["time"]*1e9)
            covered_tasks += int(any(start <= sample_end and end >= sample_start for start,end in task_windows))
    if observed_gpu < 2 or covered_tasks < 2:
        raise ValueError("CUDA PID ownership or actual task-window sampling coverage is insufficient")
    intervals = [interval(a,b) for a,b in zip(normalized, normalized[1:])]
    if not intervals or not all(row["passed"] for row in intervals) or sum(row["ownedCpuSeconds"] for row in intervals) <= 0:
        raise ValueError("Live CPU/GPU accounting, background, memory or throttling failed qualification")
    return {"hostIdentity": rows[0]["identity"], "gpuPidMode": "observed-same-pid-namespace",
            "checks": {"cpuAccounting": True, "gpuPidMapping": True, "samplingOverhead": True, "taskCoverage": True},
            "observedGpuSamples": observed_gpu, "taskOverlappingSamples": covered_tasks,
            "intervals": intervals, "bootstrapErrorsRetained": background.get("errors", [])}


def recheck(value, root=ROOT):
    if value.get("schemaVersion") != 1 or value.get("status") != "calibrated" or value.get("profile") != LINUX_MONITOR:
        raise ValueError("A successful live Linux calibration is required")
    if value["sourceSha256"] != sha(root / "PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/linux_telemetry.py"):
        raise ValueError("Linux monitor changed since calibration")
    if value["calibratorSha256"] != sha(Path(__file__)):
        raise ValueError("Calibration validator changed")
    verify_capture(root, value["evidence"])
    paths = {key: inside(root, relative) for key, relative in value["records"].items()}
    covered = {row["path"] for row in value["evidence"]["files"]}
    if not set(value["records"].values()).issubset(covered):
        raise ValueError("Live calibration records missing from their identity manifest")
    process = json.loads(paths["process"].read_text())
    build = inside(root, process["buildReceipt"])
    if sha(build) != process["buildReceiptSha256"]:
        raise ValueError("Calibration build receipt changed")
    built = verify_build(root, build, process["sourceCommit"], require_clean=False)
    from analyze import equal_csr, equal_state, rows_for
    validation = json.loads((paths["process"].parent / "validation.json").read_text())
    verify_capture(root, validation["evidence"])
    if (not validation.get("completeOutputValidation") or validation.get("arm") != process["arm"]
            or validation.get("case") != process["case"] or validation.get("sourceCommit") != process["sourceCommit"]
            or validation.get("executableSha256") != process["executableSha256"]):
        raise ValueError("Calibration process lacks complete state acceptance")
    executable = inside(root, Path(process["args"][0]).relative_to(root).as_posix())
    if (sha(executable) != process["executableSha256"]
            or executable.relative_to(root).as_posix() not in built["products"]["requiredFiles"]):
        raise ValueError("Calibration executable differs from the observed process")
    rows_for(paths["process"].parent)
    expected = root / "Artifacts/whole-task-md-20260915/inputs" / process["case"]
    if sha(expected.with_suffix(".bin")) != process["inputSha256"]:
        raise ValueError("Calibration canonical input changed")
    for step in sorted({0, process["measured"]-1}):
        actual = paths["configuration"].parent / ("step-" + str(step))
        equal_csr(actual.with_suffix(".csr"), expected.with_suffix(".csr"))
        equal_state(actual.with_suffix(".state"), expected.with_suffix(".state"))
    with paths["timings"].open() as source:
        timings = list(csv.DictReader(source))
    result = evaluate(process, json.loads(paths["background"].read_text()), paths["configuration"].read_text(), timings)
    if result != value["evaluation"] or any(result[key] != value[key] for key in ("hostIdentity", "gpuPidMode", "checks")):
        raise ValueError("Live calibration evidence does not reproduce its qualification")
    return result


def create(directory, output):
    directory = directory.resolve()
    paths = {"process": directory / "process.json", "background": directory / "background.json",
             "configuration": directory / "output/execution-space.txt", "timings": directory / "output/timings.csv"}
    value = {"schemaVersion": 1, "status": "rejected", "profile": LINUX_MONITOR,
             "sourceSha256": sha(Path(__file__).with_name("linux_telemetry.py")), "calibratorSha256": sha(Path(__file__)),
             "records": {key: path.relative_to(ROOT).as_posix() for key,path in paths.items()},
             "evidence": capture(ROOT, [(directory / "output").relative_to(ROOT).as_posix()],
                                 [p.relative_to(ROOT).as_posix() for p in paths.values()] + [(directory / "validation.json").relative_to(ROOT).as_posix()])}
    try:
        process = json.loads(paths["process"].read_text())
        with paths["timings"].open() as source:
            timings = list(csv.DictReader(source))
        evaluation = evaluate(process, json.loads(paths["background"].read_text()), paths["configuration"].read_text(), timings)
        value.update(status="calibrated", evaluation=evaluation,
                     **{key: evaluation[key] for key in ("hostIdentity", "gpuPidMode", "checks")})
        recheck(value)
    except Exception as error:
        value.update(status="rejected", error=str(error))
        write_new(output, value)
        raise
    write_new(output, value)
    print("Live Linux monitoring calibration passed; original raw observations retained")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    create(args.directory, args.output)
