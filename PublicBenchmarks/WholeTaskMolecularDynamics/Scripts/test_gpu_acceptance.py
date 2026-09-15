"""Pure rejection checks only; no GPU binary or workload is executed."""
import copy
import unittest
from run_overflow import check_report, recheck
from scalar_oracle import recheck as scalar_recheck
from run_process import verify_execution


class GpuAcceptanceTests(unittest.TestCase):
    def test_capacity_count_device_and_pid_are_required(self):
        report = {"expectedCapacityRejection": True, "maxIds": 1, "tile": 128,
                  "goldenIds": 10, "processPid": 99, "gpuUuid": "GPU-fixture"}
        check_report(report, 128, 10, 99, "GPU-fixture")
        for key, value in (("expectedCapacityRejection", False), ("goldenIds", 9), ("tile", 256),
                           ("processPid", 100), ("gpuUuid", "different"), ("maxIds", 64)):
            broken = copy.deepcopy(report)
            broken[key] = value
            with self.assertRaises(ValueError):
                check_report(broken, 128, 10, 99, "GPU-fixture")

    def test_missing_or_rejected_numerical_receipts_cannot_pass(self):
        for verifier in (recheck, scalar_recheck):
            with self.assertRaises(ValueError):
                verifier({"status": "rejected"})

    def test_actual_backend_workers_and_cuda_identity_are_checked(self):
        verify_execution("grid-openmp", 8, None, 9, "selectedExecutionSpace=OpenMP\nreportedConcurrency=8")
        with self.assertRaisesRegex(ValueError, "worker count"):
            verify_execution("arborx-openmp", 8, None, 9, "selectedExecutionSpace=OpenMP\nreportedConcurrency=1")
        config = "selectedExecutionSpace=Cuda\nreportedConcurrency=100\nselectedGpuUuid=GPU-assigned\nprocessPid=9"
        verify_execution("tiled128", 1, "GPU-assigned", 9, config)
        with self.assertRaisesRegex(ValueError, "device/PID"):
            verify_execution("tiled256", 1, "GPU-other", 9, config)
        with self.assertRaisesRegex(ValueError, "execution space"):
            verify_execution("arborx-cuda", 1, "GPU-assigned", 9, config.replace("Cuda", "Serial"))


if __name__ == "__main__":
    unittest.main()
