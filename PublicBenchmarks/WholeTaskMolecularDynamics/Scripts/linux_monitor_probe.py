"""Bounded read-only host probe. Requires coordinator host/resource admission.

It observes existing CPU/cgroup/GPU state; it launches no CUDA/CPU workload and
never produces a calibrated or performance-eligible result.
"""
import argparse
from pathlib import Path
import time
from linux_telemetry import LinuxBackground, LINUX_MONITOR
from provenance import sha, write_new


def probe(gpu_uuid, output, seconds):
    output.mkdir(parents=True, exist_ok=False)
    record = {"status": "failed", "scope": "read-only host observation, not calibration",
              "performanceEligible": False, "profile": LINUX_MONITOR,
              "sourceSha256": sha(Path(__file__).with_name("linux_telemetry.py"))}
    monitor = None
    try:
        monitor = LinuxBackground(gpu_uuid)
        monitor.read("before")
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            time.sleep(LINUX_MONITOR["samplePeriodSeconds"])
            monitor.read("before")
        record.update(status="observed", qualification=monitor.qualify())
    except Exception as error:
        record["error"] = str(error)
        raise
    finally:
        if monitor:
            record.update(rows=monitor.rows, failedObservations=monitor.failed_observations)
        write_new(output / "probe.json", record)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gpu-uuid", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=6)
    args = parser.parse_args()
    if not 5 <= args.seconds <= 20:
        parser.error("Probe duration must be 5-20 seconds")
    probe(args.gpu_uuid, args.output, args.seconds)
