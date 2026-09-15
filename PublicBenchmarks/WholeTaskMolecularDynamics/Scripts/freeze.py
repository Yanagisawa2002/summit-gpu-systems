"""Freeze an explicitly reviewed plan and observed build/input artifact closure.

No default schedule: discovery must establish actual backends, repetition
duration, monitoring, and independent processes before a plan is frozen.
"""
import argparse
import json
import math
from pathlib import Path
from input_contract import CASES, validate_inputs
from extract_upstream import PIN as UPSTREAM_PIN, SHA as UPSTREAM_SHA
from background import WINDOWS_MONITOR
from linux_telemetry import LINUX_MONITOR
from provenance import (ROOT, RUN, SOURCE_TREES, capture, file_record, git_identity,
                        inside, sha, utc, verify_build, verify_capture, write_new)


def validate_plan(plan):
    if plan.get("schemaVersion") != 2:
        raise ValueError("An explicit version-2 confirmation plan is required")
    for field in ("taskBoundary", "primaryMetric", "statistics", "performanceStability",
                  "failureRules", "buildReceipts", "armArtifacts", "processes", "validationReceipts", "comparisons"):
        if not plan.get(field):
            raise ValueError("Missing confirmation plan field: " + field)
    if plan.get("primaryCase") != "original-10":
        raise ValueError("Keep the upstream default primary case")
    if plan.get("monitor") not in (WINDOWS_MONITOR, LINUX_MONITOR):
        raise ValueError("The selected monitoring profile is not implemented")
    if plan["monitor"] == LINUX_MONITOR and not all(plan.get("linuxMonitoring", {}).get(k) for k in ("gpuUuid", "calibrationReceipt")):
        raise ValueError("Linux freeze requires an assigned GPU and an actual live calibration receipt")
    if {"tiled128", "tiled256"}.intersection(plan["armArtifacts"]):
        if not all(plan.get("ordinaryGpuAcceptance", {}).get(k) for k in ("scalarReceipts", "overflowReceipts")):
            raise ValueError("Selected ordinary GPU tiles require independent scalar and bounded-overflow acceptance receipts")
    for key in ("maxWithinProcessCoefficientOfVariation", "maxFirstHalfLastHalfCostRatioEitherDirection", "maxAcrossProcessCostRatio"):
        limit = plan["performanceStability"].get(key)
        if not isinstance(limit, (int, float)) or not math.isfinite(limit) or limit <= 0:
            raise ValueError("Missing/invalid frozen stability limit: " + key)
    names = set()
    for item in plan["processes"]:
        if item["name"] in names or Path(item["name"]).name != item["name"] or item["name"] in (".", ".."):
            raise ValueError("Process names must be unique simple directory names")
        names.add(item["name"])
        if item["case"] not in ("small-6", "original-10", "large-20", "perturbed-10"):
            raise ValueError("Discovery and singular membership fixtures are not confirmation cases")
        if item["arm"] not in plan["armArtifacts"] or item["warmups"] < 0 or item["measured"] < 2:
            raise ValueError("Invalid planned arm/repetition count")
    if set(x["arm"] for x in plan["processes"]) != set(plan["armArtifacts"]):
        raise ValueError("Every selected arm requires scheduled processes")
    if plan["statistics"] != {"method": "paired-log-process-means", "confidence": .95}:
        raise ValueError("The requested analysis method is not implemented")
    cases = {item["case"] for item in plan["processes"]}
    if "original-10" not in cases:
        raise ValueError("Primary case is absent from schedule")
    for case in cases:
        blocks = None
        for arm in plan["armArtifacts"]:
            actual = [item["round"] for item in plan["processes"] if item["case"] == case and item["arm"] == arm]
            if len(actual) not in (4, 6) or len(set(actual)) != len(actual):
                raise ValueError("Require four or six distinct independent process blocks for every case/arm")
            if blocks is not None and set(actual) != blocks:
                raise ValueError("Paired process blocks differ across arms")
            blocks = set(actual)
    for pair in plan["comparisons"]:
        if any(pair[key] not in plan["armArtifacts"] for key in ("numerator", "denominator")) or pair["numerator"] == pair["denominator"]:
            raise ValueError("Invalid predeclared comparison")


def artifact_closure(root, run, plan, commit):
    validate_plan(plan)
    cases = validate_inputs(run / "inputs")
    builds, receipts = {}, []
    if plan["monitor"] == LINUX_MONITOR:
        calibration_path = inside(root, plan["linuxMonitoring"]["calibrationReceipt"])
        calibration = json.loads(calibration_path.read_text())
        if (calibration.get("status") != "calibrated" or calibration.get("profile") != LINUX_MONITOR
                or calibration.get("sourceSha256") != sha(root / "PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/linux_telemetry.py")
                or calibration.get("hostIdentity", {}).get("gpuUuid") != plan["linuxMonitoring"]["gpuUuid"]):
            raise ValueError("Missing/stale Linux calibration; offline test results do not qualify a host")
        verify_capture(root, calibration["evidence"])
        from monitor_calibration import recheck
        recheck(calibration, root)
        receipts.append(file_record(root, calibration_path.relative_to(root)))
    for kind, relative in plan["buildReceipts"].items():
        receipt_path = inside(root, relative)
        build = verify_build(root, receipt_path, commit)
        if build["kind"] != kind:
            raise ValueError("Build kind differs from plan: " + kind)
        builds[kind] = build
        receipts.append(file_record(root, relative))
    for arm, artifact in plan["armArtifacts"].items():
        build = builds[artifact["build"]]
        if artifact["executable"] not in build["products"]["requiredFiles"]:
            raise ValueError("Executable is not bound to its build: " + arm)
        file_record(root, artifact["executable"])
    validated, saved_states = set(), {}
    for relative in plan["validationReceipts"]:
        receipt = json.loads(inside(root, relative).read_text(encoding="utf-8-sig"))
        case = receipt.get("case")
        if case not in CASES:
            raise ValueError("Unknown canonical validation case")
        membership_only = CASES[case][1]
        if (receipt.get("schemaVersion") != 2 or receipt.get("membershipOnly") != membership_only
                or receipt.get("completeOutputValidation") != (not membership_only)):
            raise ValueError("Full-output validation receipt required: " + relative)
        if receipt.get("sourceCommit") != commit or receipt.get("arm") not in plan["armArtifacts"]:
            raise ValueError("Validation source/arm identity differs")
        artifact = plan["armArtifacts"][receipt["arm"]]
        if receipt.get("executableSha256") != sha(inside(root, artifact["executable"])):
            raise ValueError("Validation binary differs from frozen build")
        verify_capture(root, receipt["evidence"])
        # Recompute the saved complete-output audits; a boolean alone is not proof.
        from analyze import rows_for, equal_csr, equal_state
        process_path = inside(root, receipt["processReceipt"])
        process = json.loads(process_path.read_text())
        output = inside(root, receipt["outputDirectory"])
        if output.parent != process_path.parent or output.name != "output":
            raise ValueError("Validation output is not in its observed process directory")
        covered = {row["path"] for row in receipt["evidence"]["files"]}
        if receipt["processReceipt"] not in covered:
            raise ValueError("Process receipt is missing from validation identity")
        if (not process.get("completed") or process.get("exitCode") != 0 or not process.get("validationCompleted")
                or process.get("arm") != receipt["arm"] or process.get("case") != receipt["case"]
                or process.get("sourceCommit") != commit or process.get("executableSha256") != receipt["executableSha256"]):
            raise ValueError("Validation process failed or has a different identity")
        raw = run / "inputs" / (receipt["case"] + ".bin")
        if process["inputSha256"] != sha(raw):
            raise ValueError("Validation input differs from frozen canonical input")
        rows = rows_for(output.parent)
        if len(rows) != process["warmups"] + process["measured"] or sum(x["phase"] == "measured" for x in rows) != process["measured"]:
            raise ValueError("Validation repetition coverage incomplete")
        for step in sorted({0, process["measured"] - 1}):
            base = output / ("step-" + str(step))
            for suffix in ((".csr",) if membership_only else (".csr", ".state")):
                if base.with_suffix(suffix).relative_to(root).as_posix() not in covered:
                    raise ValueError("Complete raw output missing from validation identity")
            equal_csr(base.with_suffix(".csr"), raw.with_suffix(".csr"))
            if not membership_only:
                equal_state(base.with_suffix(".state"), raw.with_suffix(".state"))
                saved_states.setdefault((receipt["arm"], case), set()).add(base.with_suffix(".state").relative_to(root).as_posix())
        validated.add((receipt["arm"], case))
        receipts.append(file_record(root, relative))
    required_cases = {item["case"] for item in plan["processes"]} | {"boundary-membership", "isolated-3", "tail-129"}
    required_validation = {(arm, case) for arm in plan["armArtifacts"] for case in required_cases}
    if not required_validation.issubset(validated):
        raise ValueError("Every selected arm requires full validation of every confirmation case and boundary/empty/tail fixtures")
    tiles = {"tiled128", "tiled256"}.intersection(plan["armArtifacts"])
    if tiles:
        from scalar_oracle import recheck as check_scalar
        from run_overflow import recheck as check_overflow
        scalar_states, overflow_tiles = set(), set()
        for relative in plan["ordinaryGpuAcceptance"]["scalarReceipts"]:
            receipt = json.loads(inside(root, relative).read_text())
            if receipt.get("sourceCommit") != commit or not receipt.get("sourceClean"):
                raise ValueError("Scalar acceptance source identity differs from the clean candidate")
            scalar_states.update(check_scalar(receipt, root, run))
            receipts.append(file_record(root, relative))
        required_states = {state for (arm, case), states in saved_states.items()
                           if arm in tiles and case in required_cases for state in states}
        if not required_states.issubset(scalar_states):
            raise ValueError("Independent scalar acceptance does not cover every selected tile's complete first/last states")
        for relative in plan["ordinaryGpuAcceptance"]["overflowReceipts"]:
            receipt = json.loads(inside(root, relative).read_text())
            if receipt.get("sourceCommit") != commit or not receipt.get("sourceClean"):
                raise ValueError("Overflow acceptance source identity differs from the clean candidate")
            tile = check_overflow(receipt, root, run, require_clean=True)
            arm = "tiled" + str(tile)
            if arm not in tiles or receipt["records"]["executable"] != plan["armArtifacts"][arm]["executable"]:
                raise ValueError("Overflow acceptance does not match a selected tile executable")
            if plan["monitor"] != LINUX_MONITOR or receipt["gpuUuid"] != plan["linuxMonitoring"]["gpuUuid"]:
                raise ValueError("Overflow acceptance was not on the assigned frozen Linux GPU")
            overflow_tiles.add(arm)
            receipts.append(file_record(root, relative))
        if overflow_tiles != tiles:
            raise ValueError("Every selected ordinary GPU tile needs its own observed overflow rejection")
    run_relative = run.relative_to(root).as_posix()
    inputs = capture(root, [*SOURCE_TREES, run_relative + "/inputs", run_relative + "/generated"],
                     [run_relative + "/generated/UpstreamStep.hpp", run_relative + "/generated/extraction.json",
                      *[x["path"] for x in receipts]])
    extraction = json.loads((run / "generated/extraction.json").read_text())
    if (extraction["generatedSha256"] != sha(run / "generated/UpstreamStep.hpp")
            or extraction["commit"] != UPSTREAM_PIN or extraction["sourceSha256"] != UPSTREAM_SHA
            or sha(root / "Docs/whole-task-md-20260915/sources/example_molecular_dynamics.cpp") != UPSTREAM_SHA):
        raise ValueError("Generated header is not bound to its extraction receipt")
    return {"inputs": inputs, "cases": cases, "builds": builds}


def verify(frozen, root=ROOT, run=RUN):
    identity = git_identity(root)
    if frozen.get("schemaVersion") != 2 or not identity["sourceClean"] or identity["sourceCommit"] != frozen["sourceCommit"]:
        raise ValueError("Freeze requires the unchanged clean candidate commit")
    verify_capture(root, frozen["planIdentity"])
    closure = artifact_closure(root, run, frozen["plan"], frozen["sourceCommit"])
    if closure != frozen["closure"]:
        raise ValueError("Frozen artifact/build/input closure changed")


def create(plan_path):
    identity = git_identity(ROOT)
    if not identity["sourceClean"]:
        raise ValueError("Commit validated sources before freeze; untracked source is not eligible")
    plan_path = Path(plan_path).resolve()
    plan = json.loads(plan_path.read_text(encoding="utf-8-sig"))
    frozen = {"schemaVersion": 2, "frozenAtUtc": utc(), **identity, "plan": plan,
              "planIdentity": capture(ROOT, [], [plan_path.relative_to(ROOT).as_posix()]),
              "processes": plan["processes"],
              "closure": artifact_closure(ROOT, RUN, plan, identity["sourceCommit"])}
    verify(frozen)
    write_new(RUN / "frozen-run.json", frozen)
    print("Frozen", len(plan["processes"]), "processes with observed build/input provenance")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("create", "verify"))
    parser.add_argument("--plan", type=Path)
    args = parser.parse_args()
    if args.action == "create":
        if args.plan is None:
            parser.error("create requires --plan; a source draft is not a confirmation plan")
        create(args.plan)
    else:
        verify(json.loads((RUN / "frozen-run.json").read_text()))
        print("Frozen source/build/input identities unchanged")
