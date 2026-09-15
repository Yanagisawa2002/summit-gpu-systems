"""Reject missing/incompatible live calibration without running a real process."""
import copy
import unittest
from linux_telemetry import CALIBRATION_REQUIRED, LINUX_MONITOR, gpu_xml
from test_linux_telemetry import snapshots, XML, UUID
from monitor_calibration import evaluate, recheck


class CalibrationTests(unittest.TestCase):
    def test_offline_or_failed_process_does_not_calibrate(self):
        with self.assertRaisesRegex(ValueError, "real CUDA process"):
            evaluate({"completed": True, "exitCode": 0, "arm": "arborx"}, {}, "", [])
        with self.assertRaisesRegex(ValueError, "successful live"):
            recheck({"schemaVersion": 1, "status": "source-tests-passed"})

    def test_parser_failure_cannot_be_reclassified_as_bootstrap(self):
        process = {"completed": True, "exitCode": 0, "validationCompleted": True, "diagnostic": False,
                   "arm": "tiled128", "pid": 123, "gpuUuid": "GPU-fixture"}
        with self.assertRaisesRegex(ValueError, "Unexpected monitoring failures"):
            evaluate(process, {"errors": ["GPU process list unavailable"]}, "", [])

    def test_synthetic_coverage_device_mapping_and_accounting(self):
        # Arithmetic/shape fixture only. No calibration receipt is written and
        # no CUDA process or host counter is observed by this test.
        process = {"completed": True, "exitCode": 0, "validationCompleted": True, "diagnostic": False,
                   "arm": "tiled128", "pid": 123, "gpuUuid": UUID, "warmups": 0, "measured": 2}
        rows = []
        for tick in range(1, 10):
            row, _ = snapshots()
            busy = min(2, max(0, tick-6))
            row.update(time=float(tick), sampleStartNs=int((tick-.1)*1e9),
                       phase="before" if tick <= 6 else "during" if tick <= 8 else "at-process-exit")
            row["cpu"]["cpu0"].update(total=100*tick, idle=80+100*(tick-1-busy))
            row["ownedSeconds"] += busy
            row["groups"][0]["cpu"]["usage_usec"] = busy*1_000_000
            xml = XML if tick not in (7, 8) else XML.replace("<processes/>",
                "<processes><process_info><pid>123</pid><type>C</type><used_memory>10 MiB</used_memory></process_info></processes>")
            row.update(rawGpuXml=xml, gpu=gpu_xml(xml, UUID))
            rows.append(row)
        background = {"profile": LINUX_MONITOR, "errors": [CALIBRATION_REQUIRED], "rows": rows}
        config = f"selectedExecutionSpace=Cuda\nprocessPid=123\nselectedGpuUuid={UUID}\nsteadyBeforeNs=1\nmonotonicNs=2\nsteadyAfterNs=3"
        timings = [{"taskStartNs": int((tick-.2)*1e9), "taskEndNs": tick*1_000_000_000, "verified": "true"} for tick in (7, 8)]
        self.assertEqual(evaluate(process, background, config, timings)["taskOverlappingSamples"], 2)
        broken = copy.deepcopy(timings)
        for task in broken:
            task.update(taskStartNs=1, taskEndNs=2)
        with self.assertRaisesRegex(ValueError, "sampling coverage"):
            evaluate(process, background, config, broken)
        with self.assertRaisesRegex(ValueError, "device/PID"):
            evaluate(process, background, config.replace("processPid=123", "processPid=124"), timings)


if __name__ == "__main__":
    unittest.main()
