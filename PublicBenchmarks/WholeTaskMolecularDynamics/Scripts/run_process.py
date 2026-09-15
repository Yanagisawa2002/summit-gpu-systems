"""One explicitly requested process, inside the admitted hardware stage.

No background scheduling, hidden retry, sample replacement, or global settings.
"""
import argparse
import datetime
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from provenance import capture, git_identity, sha, verify_build, write_new

NATIVE_ARMS = {"arborx": ("arborx", "serial"), "grid": ("grid", "serial"),
               "arborx-openmp": ("arborx", "openmp"), "grid-openmp": ("grid", "openmp"),
               "arborx-cuda": ("arborx", "cuda"),
               "tiled128": ("tiled128", "cuda"), "tiled256": ("tiled256", "cuda")}

ROOT = Path(__file__).resolve().parents[3]
RUN = ROOT / "Artifacts/whole-task-md-20260915"


def windows_process_details(child):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    process_mask, system_mask = ctypes.c_size_t(), ctypes.c_size_t()
    handle = ctypes.c_void_p(int(child._handle))
    if not kernel.GetProcessAffinityMask(handle, ctypes.byref(process_mask), ctypes.byref(system_mask)):
        raise ctypes.WinError(ctypes.get_last_error())
    return {"affinityMask": hex(process_mask.value), "systemAffinityMask": hex(system_mask.value),
            "priorityClass": kernel.GetPriorityClass(handle)}


def digest(path):
    return sha(path)


def verify_execution(arm, threads, gpu_uuid, pid, config_text):
    fields = dict(line.split("=", 1) for line in config_text.splitlines() if "=" in line)
    backend = NATIVE_ARMS[arm][1]
    space = {"serial": "Serial", "openmp": "OpenMP", "cuda": "Cuda"}[backend]
    concurrency = int(fields.get("reportedConcurrency", -1))
    if fields.get("selectedExecutionSpace") != space or concurrency < 1:
        raise ValueError("Actual native execution space/concurrency differs from the requested arm")
    if backend in ("serial", "openmp") and concurrency != threads:
        raise ValueError("Actual CPU concurrency differs from the explicitly requested worker count")
    if backend == "cuda" and (fields.get("selectedGpuUuid") != gpu_uuid or int(fields.get("processPid", -1)) != pid):
        raise ValueError("Actual CUDA device/PID differs from the assigned observed process")
    return {"executionSpace": space, "reportedConcurrency": concurrency,
            "gpuUuid": fields.get("selectedGpuUuid"), "processPid": fields.get("processPid")}


def run(arm, case, name, warmups, measured, diagnostic=False, formal=False, threads=1, frozen_artifact=None, monitor_profile=None,
        gpu_uuid=None, linux_calibration=None):
    if warmups < 0 or measured < 1:
        raise ValueError("Nonnegative warmups and at least one measured task required")
    if arm not in (*NATIVE_ARMS, "legacy", "reuse") or Path(name).name != name or name in ("", ".", ".."):
        raise ValueError("Unknown arm or invalid process directory name")
    if not 1 <= threads <= 16 or (arm not in ("arborx-openmp", "grid-openmp") and threads != 1):
        raise ValueError("Only OpenMP arms accept an explicit 1-16 worker count")
    if os.name != "nt" and arm in ("legacy", "reuse"):
        raise ValueError("Unity Linux execution is not enabled before its graphics capability gate")
    if formal:
        from background import WINDOWS_MONITOR
        from linux_telemetry import LINUX_MONITOR
        expected_monitor = WINDOWS_MONITOR if os.name == "nt" else LINUX_MONITOR
        if frozen_artifact is None or monitor_profile != expected_monitor:
            raise ValueError("Formal execution requires its frozen artifact and implemented monitor profile")
    if os.name != "nt" and not gpu_uuid:
        raise ValueError("Linux runs require the explicitly assigned GPU UUID for resource observations")
    directory = RUN / "runs" / name
    directory.mkdir(parents=True, exist_ok=False)
    source = RUN / "inputs" / (case + ".bin")
    if arm in NATIVE_ARMS:
        algorithm, backend = NATIVE_ARMS[arm]
        suffix = "" if backend == "serial" else "-" + backend
        exe = RUN / ("native-build" + suffix) / ("SummitMolecularNative.exe" if os.name == "nt" else "SummitMolecularNative")
        build_kind = "native-" + backend
        args = [str(exe), "run", algorithm, str(source), str(directory / "output"), str(warmups), str(measured)]
    else:
        exe = RUN / "player-r1/SummitMolecularStep.exe"
        build_kind = "unity-windows-d3d12"
        config = {"arm": arm, "input": str(source), "output": str(directory / "output"),
                  "warmups": warmups, "measured": measured, "diagnostic": diagnostic}
        (directory / "config.json").write_text(json.dumps(config, indent=2) + "\n")
        args = [str(exe), "-batchmode", "-force-d3d12", "-screen-width", "64", "-screen-height", "64",
                "-md-config", str(directory / "config.json"), "-logFile", str(directory / "player.log")]
    identity = git_identity(ROOT)
    build_receipt = RUN / ("build-" + build_kind + ".json")
    built = verify_build(ROOT, build_receipt, identity["sourceCommit"], require_clean=formal)
    if exe.relative_to(ROOT).as_posix() not in built["products"]["requiredFiles"]:
        raise ValueError("Run executable is not an observed build product")
    if frozen_artifact and (frozen_artifact["build"] != build_kind or frozen_artifact["executable"] != exe.relative_to(ROOT).as_posix()):
        raise ValueError("Requested runtime differs from the frozen arm artifact")
    receipt = {"arm": arm, "case": case, "name": name, "args": args, "diagnostic": diagnostic,
               **identity, "warmups": warmups, "measured": measured,
               "buildReceipt": build_receipt.relative_to(ROOT).as_posix(), "buildReceiptSha256": sha(build_receipt),
               "inputSha256": digest(source), "executableSha256": digest(exe),
               "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "completed": False}
    path = directory / "process.json"
    path.write_text(json.dumps(receipt, indent=2) + "\n")
    monitor, monitor_error, quiet = None, None, False
    try:
        if os.name == "nt":
            from background import Background
            monitor = Background()
        else:
            from linux_telemetry import LinuxBackground
            calibration = json.loads(Path(linux_calibration).read_text()) if linux_calibration else None
            monitor = LinuxBackground(gpu_uuid, calibration, gpu_work=NATIVE_ARMS[arm][1] == "cuda")
        quiet = monitor.preflight()
    except Exception as error:
        monitor_error = str(error)
    if formal and not quiet:
        background = monitor.finish() if monitor else {"observedBackgroundEligible": False, "errors": []}
        if monitor_error:
            background["errors"].append(monitor_error)
        (directory / "background.json").write_text(json.dumps(background, indent=2)+"\n")
        receipt.update(preflightRejected=True, launched=False, performanceEligible=False)
        path.write_text(json.dumps(receipt, indent=2)+"\n")
        raise RuntimeError("Formal background gate failed before launch; keep this rejected attempt")
    env = dict(os.environ)
    # Only task-local temporary paths; priority, affinity, power and driver unchanged.
    env["TEMP"] = env["TMP"] = str(RUN / "temp")
    env["TMPDIR"] = str(RUN / "temp")
    (RUN / "temp").mkdir(parents=True, exist_ok=True)
    env["KOKKOS_NUM_THREADS"] = env["OMP_NUM_THREADS"] = str(threads)
    if os.name != "nt" and NATIVE_ARMS[arm][1] == "cuda":
        env["CUDA_VISIBLE_DEVICES"] = gpu_uuid
        receipt["gpuUuid"] = gpu_uuid
    startup = None
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
    begin = time.monotonic()
    with (directory / "stdout.log").open("w") as stdout, (directory / "stderr.log").open("w") as stderr:
        child = subprocess.Popen(args, cwd=ROOT, env=env, stdout=stdout, stderr=stderr, startupinfo=startup)
        receipt["pid"] = child.pid
        try:
            if monitor is not None:
                monitor.start(child)
            receipt["processSettings"] = windows_process_details(child) if os.name == "nt" else {
                "inheritedAffinityCpuIds": sorted(os.sched_getaffinity(0)),
                "inheritedNice": os.getpriority(os.PRIO_PROCESS, 0)}
            receipt["processSettings"]["requestedKokkosThreads"] = threads if arm in NATIVE_ARMS else None
            path.write_text(json.dumps(receipt, indent=2) + "\n")
            code = child.wait(timeout=900)
        except subprocess.TimeoutExpired:
            # This PID is owned by this invocation. Preserve its failed attempt.
            child.kill()
            code = child.wait()
            receipt["timeout"] = True
        except Exception as error:
            child.kill()
            code = child.wait()
            receipt["runnerError"] = str(error)
    background = monitor.finish() if monitor else {"observedBackgroundEligible": False, "errors": []}
    if monitor_error:
        background["errors"].append(monitor_error)
        background["observedBackgroundEligible"] = False
    (directory / "background.json").write_text(json.dumps(background, indent=2)+"\n")
    eligible = formal and not diagnostic and background["observedBackgroundEligible"]
    receipt.update(exitCode=code, completed=True, wallSeconds=time.monotonic()-begin,
                   performanceEligible=False,
                   finishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    path.write_text(json.dumps(receipt, indent=2) + "\n")
    if code != 0:
        raise RuntimeError(f"Process failed with exit {code}: {name}")
    if formal and not eligible:
        raise RuntimeError("Formal process retained but background interference makes the campaign ineligible")
    if arm in NATIVE_ARMS:
        receipt["executionIdentity"] = verify_execution(arm, threads, gpu_uuid, child.pid,
                                                       (directory / "output/execution-space.txt").read_text())
    if arm in ("legacy", "reuse"):
        report = json.loads((directory / "output/result.json").read_text())
        if report["status"] != "completed" or len(report["rows"]) != warmups + measured or not all(r["verified"] for r in report["rows"]):
            raise RuntimeError("Full-output validation incomplete")
    # Re-read full raw first/last outputs independently of the native/Unity code.
    # Every inner repetition must also have completed its in-process full audit.
    from analyze import rows_for, equal_csr, equal_state
    rows = rows_for(directory)
    if len(rows) != warmups + measured or sum(row["phase"] == "measured" for row in rows) != measured:
        raise RuntimeError("Missing expected full-validation rows")
    audits = []
    membership_only = case == "boundary-membership"
    for step in sorted({0, measured - 1}):
        prefix = directory / "output" / ("step-" + str(step))
        audit = {"step": step, "csr": equal_csr(prefix.with_suffix(".csr"), source.with_suffix(".csr"))}
        if not membership_only:
            audit["stateMaxAbs"] = equal_state(prefix.with_suffix(".state"), source.with_suffix(".state"))
        audits.append(audit)
    receipt["validationCompleted"] = True
    receipt["performanceEligible"] = eligible
    path.write_text(json.dumps(receipt, indent=2) + "\n")
    evidence_files = [directory / "process.json", source, source.with_suffix(".csr")]
    if not membership_only:
        evidence_files.append(source.with_suffix(".state"))
    write_new(directory / "validation.json", {"schemaVersion": 2, **identity, "arm": arm, "case": case,
              "completeOutputValidation": not membership_only, "membershipOnly": membership_only,
              "processReceipt": path.relative_to(ROOT).as_posix(),
              "outputDirectory": (directory / "output").relative_to(ROOT).as_posix(),
              "executableSha256": sha(exe), "audits": audits,
              "evidence": capture(ROOT, [(directory / "output").relative_to(ROOT).as_posix()],
                                  [path.relative_to(ROOT).as_posix() for path in evidence_files])})
    print("PASS", name, f"process {receipt['wallSeconds']:.3f}s", flush=True)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("arm", choices=(*NATIVE_ARMS, "legacy", "reuse"))
    p.add_argument("case")
    p.add_argument("name")
    p.add_argument("--warmups", type=int, default=3)
    p.add_argument("--measured", type=int, default=5)
    p.add_argument("--diagnostic", action="store_true")
    p.add_argument("--threads", type=int, default=1)
    p.add_argument("--gpu-uuid")
    p.add_argument("--linux-calibration", type=Path)
    a = p.parse_args()
    run(a.arm, a.case, a.name, a.warmups, a.measured, a.diagnostic, threads=a.threads,
        gpu_uuid=a.gpu_uuid, linux_calibration=a.linux_calibration)
