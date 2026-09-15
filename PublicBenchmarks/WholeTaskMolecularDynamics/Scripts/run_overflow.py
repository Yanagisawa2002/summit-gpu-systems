"""Observed bounded CUDA capacity check; run only in an admitted hardware stage."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
from input_contract import CASES, validate_case
from provenance import ROOT, RUN, capture, git_identity, inside, sha, verify_build, verify_capture, write_new


def check_report(report, tile, count, pid, uuid):
    if (report.get("expectedCapacityRejection") is not True or report.get("maxIds") != 1
            or report.get("tile") != tile or report.get("goldenIds") != count or count <= 1
            or report.get("processPid") != pid or report.get("gpuUuid") != uuid):
        raise ValueError("The assigned CUDA process did not prove the exact expected capacity rejection")


def recheck(record, root=ROOT, run=RUN, require_clean=False):
    from analyze import read_csr
    if (record.get("schemaVersion") != 1 or record.get("status") != "validated"
            or record.get("exitCode") != 0 or record.get("tile") not in (128, 256)
            or record.get("case") != "tail-129" or record.get("performanceEligible") is not False
            or record.get("checkerSha256") != sha(Path(__file__))):
        raise ValueError("A current observed bounded CUDA overflow check is required")
    verify_capture(root, record["evidence"])
    paths = {k: inside(root, v) for k, v in record["records"].items()}
    covered = {row["path"] for row in record["evidence"]["files"]}
    if not set(record["records"].values()).issubset(covered):
        raise ValueError("Overflow acceptance files are not bound to its evidence")
    build = verify_build(root, paths["build"], record["sourceCommit"], require_clean=require_clean)
    if record["records"]["executable"] not in build["products"]["requiredFiles"] or build["kind"] != "native-cuda":
        raise ValueError("Overflow executable is not the observed CUDA build product")
    source = run / "inputs/tail-129.bin"
    if paths["input"] != source or paths["golden"] != source.with_suffix(".csr"):
        raise ValueError("Overflow check does not use the canonical partial-tile fixture")
    validate_case(run / "inputs", "tail-129", *CASES["tail-129"])
    _, ids = read_csr(paths["golden"])
    check_report(json.loads(paths["report"].read_text()), record["tile"], len(ids), record["pid"], record["gpuUuid"])
    return record["tile"]


def run(tile, name, gpu_uuid):
    if os.name == "nt" or not re.fullmatch(r"GPU-[0-9a-fA-F-]{36}", gpu_uuid or ""):
        raise ValueError("This check needs the admitted Linux host and assigned full GPU UUID")
    if Path(name).name != name or name in ("", ".", ".."):
        raise ValueError("A new simple attempt name is required")
    directory = RUN / name
    directory.mkdir(parents=True, exist_ok=False)
    record = {"schemaVersion": 1, **git_identity(ROOT), "status": "started", "tile": tile,
              "case": "tail-129", "gpuUuid": gpu_uuid, "performanceEligible": False,
              "checkerSha256": sha(Path(__file__))}
    write_new(directory / "started.json", record)
    try:
        source, build_path = RUN / "inputs/tail-129.bin", RUN / "build-native-cuda.json"
        exe = RUN / "native-build-cuda/SummitMolecularNative"
        built = verify_build(ROOT, build_path, record["sourceCommit"], require_clean=False)
        if exe.relative_to(ROOT).as_posix() not in built["products"]["requiredFiles"]:
            raise ValueError("Overflow executable is not an observed build product")
        validate_case(RUN / "inputs", "tail-129", *CASES["tail-129"])
        inputs = capture(ROOT, [], [p.relative_to(ROOT).as_posix() for p in (source, source.with_suffix(".csr"), build_path, exe)])
        command = [str(exe), "audit-overflow", str(source), str(directory / "output"), str(tile), "1", "expect-rejection"]
        record["command"] = command
        with (directory / "stdout.log").open("w") as stdout, (directory / "stderr.log").open("w") as stderr:
            child = subprocess.Popen(command, cwd=ROOT, env=dict(os.environ, CUDA_VISIBLE_DEVICES=gpu_uuid), stdout=stdout, stderr=stderr)
            record["pid"] = child.pid
            try:
                write_new(directory / "launched.json", record)
                record["exitCode"] = child.wait(timeout=30)
            except BaseException:
                child.kill()
                record["exitCode"] = child.wait()
                raise
        verify_capture(ROOT, inputs)
        if record["exitCode"] != 0:
            raise ValueError("The bounded CUDA capacity check failed")
        paths = {"input": source, "golden": source.with_suffix(".csr"), "build": build_path, "executable": exe,
                 "report": directory / "output/overflow-check.json", "stdout": directory / "stdout.log", "stderr": directory / "stderr.log"}
        record.update(status="validated", records={k: p.relative_to(ROOT).as_posix() for k,p in paths.items()},
                      evidence=capture(ROOT, [], [p.relative_to(ROOT).as_posix() for p in paths.values()]))
        recheck(record)
    except BaseException as error:
        record.update(status="rejected", error=str(error))
        write_new(directory / "acceptance.json", record)
        raise
    write_new(directory / "acceptance.json", record)
    print("PASS expected bounded capacity rejection", tile)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tile", type=int, choices=(128, 256))
    parser.add_argument("name")
    parser.add_argument("--gpu-uuid", required=True)
    args = parser.parse_args()
    run(args.tile, args.name, args.gpu_uuid)
