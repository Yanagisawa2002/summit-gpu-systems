"""Explicit foreground confirmation campaign; must run inside the admitted stage.

It never waits for queue admission or starts later by itself. It launches only
the next frozen process, in order, and stops on the first failed attempt.
"""
import argparse
import json
from freeze import ROOT, RUN, verify
from run_process import run

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--through", required=True, help="Last frozen process to launch in this bounded invocation")
    args = parser.parse_args()
    frozen = json.loads((RUN / "frozen-run.json").read_text())
    verify(frozen)
    if args.through not in [p["name"] for p in frozen["processes"]]:
        raise ValueError("Requested stop point is not in the frozen process schedule")
    for item in frozen["processes"]:
        directory = RUN / "runs" / item["name"]
        if directory.exists():
            receipt = json.loads((directory / "process.json").read_text())
            if (not receipt.get("completed") or receipt.get("exitCode") != 0 or not receipt.get("performanceEligible")
                    or not receipt.get("validationCompleted") or not (directory / "validation.json").is_file()):
                raise ValueError("Existing incomplete/failed attempt: " + item["name"])
        else:
            run(item["arm"], item["case"], item["name"], item["warmups"], item["measured"], formal=True,
                threads=item.get("threads", 1), frozen_artifact=frozen["plan"]["armArtifacts"][item["arm"]],
                monitor_profile=frozen["plan"]["monitor"],
                gpu_uuid=frozen["plan"].get("linuxMonitoring", {}).get("gpuUuid"),
                linux_calibration=ROOT / frozen["plan"]["linuxMonitoring"]["calibrationReceipt"] if "linuxMonitoring" in frozen["plan"] else None)
        if item["name"] == args.through:
            break
