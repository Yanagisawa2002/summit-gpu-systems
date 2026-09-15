"""Small synthetic records exercise rejection paths, not real host calibration."""
import copy
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from linux_telemetry import (CPU_STAT, MEM_EVENTS, cpu_list, cpu_max, gpu_xml,
                             interval, key_values, proc_cpu, process_stat, resolve_cgroup)
from linux_telemetry import LinuxBackground

UUID = "GPU-11111111-2222-3333-4444-555555555555"
XML = f"""<nvidia_smi_log><driver_version>580.76.05</driver_version><gpu>
<uuid>{UUID}</uuid><utilization><gpu_util>0 %</gpu_util><memory_util>0 %</memory_util></utilization>
<fb_memory_usage><used>100 MiB</used><total>32607 MiB</total></fb_memory_usage><processes/>
</gpu></nvidia_smi_log>"""


def snapshots():
    group = {"path": "/delegated", "cpu": {k: 0 for k in CPU_STAT}, "memoryEvents": {k: 0 for k in MEM_EVENTS}}
    first = {"time": 1., "identity": {"capacityCores": 25., "affinity": list(range(208))},
             "groups": [group], "ownedSeconds": 1., "clockTicks": 100,
             "cpu": {"cpu0": {"total": 100, "idle": 80, "steal": 0}},
             "collectionSeconds": .01, "availableMemoryBytes": 80 * 2**30,
             "gpu": gpu_xml(XML, UUID), "ownedGpuPids": []}
    last = copy.deepcopy(first)
    last["time"] = 2.
    last["cpu"]["cpu0"].update(total=200, idle=180)
    return first, last


class LinuxTelemetryTests(unittest.TestCase):
    def test_quota_cpuset_and_namespace_mount(self):
        self.assertEqual(len(cpu_list("0-207")), 208)
        self.assertEqual(cpu_max("2500000 100000")["quotaCores"], 25)
        self.assertIsNone(cpu_max("max 100000")["quotaCores"])
        mount = "35 24 0:28 /docker/delegated /sys/fs/cgroup ro - cgroup2 cgroup rw"
        self.assertEqual(resolve_cgroup("0::/\n", mount)["leaf"], "/sys/fs/cgroup")
        self.assertEqual(resolve_cgroup("0::/docker/delegated/child", mount)["leaf"], "/sys/fs/cgroup/child")
        for text in ("", "7-3", "0,N/A"):
            with self.assertRaises(ValueError):
                cpu_list(text)
        with self.assertRaises(ValueError):
            resolve_cgroup("2:cpu:/x", mount)

    def test_missing_and_uninterpretable_observations(self):
        for broken in (XML.replace("0 %", "N/A", 1), XML.replace("<processes/>", "<processes>Not Supported</processes>")):
            with self.assertRaises(ValueError):
                gpu_xml(broken, UUID)
        with self.assertRaises(ValueError):
            gpu_xml(XML, "GPU-different")
        with self.assertRaises(ValueError):
            key_values("usage_usec 1", CPU_STAT)
        with self.assertRaises(ValueError):
            proc_cpu("cpu0 1 0 1 8 0 0 0 0", [0, 1])

    def test_owned_accounting_and_quota_denominator(self):
        a, b = snapshots()
        b["ownedSeconds"] += .5
        b["groups"][0]["cpu"]["usage_usec"] = 1_000_000
        b["cpu"]["cpu0"]["idle"] = 80
        result = interval(a, b)
        self.assertEqual(result["cgroupBackgroundCores"], .5)
        self.assertEqual(result["backgroundPercentOfCapacity"], 2.)  # .5 / 25, not / 208
        self.assertFalse(result["passed"])
        # Accounting survives a child being reaped: cumulative owned CPU stays
        # monotonic, irrespective of whether its ticks are live or in rusage.
        b["groups"][0]["cpu"]["usage_usec"] = 500_000
        b["cpu"]["cpu0"]["idle"] = 130
        self.assertTrue(interval(a, b)["passed"])

    def test_throttle_memory_and_identity_changes(self):
        for path, field in (("cpu", "nr_throttled"), ("cpu", "throttled_usec"), ("memoryEvents", "oom_kill")):
            a, b = snapshots()
            b["groups"][0][path][field] = 1
            self.assertFalse(interval(a, b)["passed"])
        a, b = snapshots()
        b["availableMemoryBytes"] = 2**30
        self.assertFalse(interval(a, b)["passed"])
        b["identity"]["capacityCores"] = 8.
        with self.assertRaises(ValueError):
            interval(a, b)

    def test_counter_resets_gaps_and_zero_ticks_rejected(self):
        a, b = snapshots()
        b["ownedSeconds"] = 0
        with self.assertRaises(ValueError):
            interval(a, b)
        a, b = snapshots()
        b["time"] = 10
        with self.assertRaises(ValueError):
            interval(a, b)
        a, b = snapshots()
        b["cpu"] = copy.deepcopy(a["cpu"])
        with self.assertRaises(ValueError):
            interval(a, b)

    def test_foreign_unknown_pid_and_unattributed_gpu(self):
        a, b = snapshots()
        b["gpu"]["processes"] = [{"pid": 54321, "type": "C", "usedMiB": 42}]
        b["ownedGpuPids"] = [123]  # Container PID != NVML PID: do not guess.
        self.assertFalse(interval(a, b)["passed"])
        b["ownedGpuPids"] = [54321]
        b["gpu"]["gpuPercent"] = 100
        self.assertTrue(interval(a, b)["passed"])
        b["gpu"]["processes"] = []
        self.assertFalse(interval(a, b)["passed"])

    def test_proc_comm_and_guest_cpu_fields(self):
        fields = ["R"] + ["0"] * 21
        fields[11], fields[12], fields[19] = "123", "456", "789"
        self.assertEqual(process_stat("5 (odd ) name) " + " ".join(fields)), {"ticks": 579, "startTicks": 789})
        self.assertEqual(proc_cpu("cpu0 10 1 3 50 2 1 1 0 7 0", [0])["cpu0"]["total"], 68)

    def test_first_observation_interference_is_not_dropped(self):
        a, b = snapshots()
        a["gpu"]["processes"] = [{"pid": 999, "type": "G", "usedMiB": 1}]
        self.assertFalse(interval(a, b)["passed"])
        a, b = snapshots()
        a["availableMemoryBytes"] = 1
        self.assertFalse(interval(a, b)["passed"])
        a, b = snapshots()
        b["cpu"]["cpu0"]["idle"] = 500
        with self.assertRaises(ValueError):
            interval(a, b)

    def test_reaped_child_cpu_is_not_replaced_with_zero(self):
        monitor = LinuxBackground.__new__(LinuxBackground)
        monitor.clock_ticks, monitor.child_start = 100, None
        monitor.child = SimpleNamespace(pid=123, returncode=None)
        monitor.child.poll = lambda: setattr(monitor.child, "returncode", 0)
        monitor.resource = SimpleNamespace(RUSAGE_CHILDREN=1, RUSAGE_SELF=0,
            getrusage=lambda which: SimpleNamespace(ru_utime=.3 if which else .1, ru_stime=.4 if which else .2))
        with patch("pathlib.Path.read_text", side_effect=FileNotFoundError):
            self.assertAlmostEqual(monitor.owned_cpu(), 1.)

    def test_quiet_window_starts_after_retained_calibration_preparation(self):
        monitor = LinuxBackground.__new__(LinuxBackground)
        monitor.rows, monitor.preparation_rows, monitor.calibrated = [], [], False
        monitor.calibration = {"hostIdentity": "synthetic"}
        clock = [0.]
        def read(phase):
            monitor.rows.append({"phase": phase, "time": clock[0], "identity": "synthetic"})
        def calibration():
            clock[0] += 10.  # Simulated expensive file hashing, not an actual delay.
            monitor.calibrated = True
        monitor.read, monitor.check_calibration = read, calibration
        monitor.qualify = lambda: {"passed": True}
        with patch("linux_telemetry.time.monotonic", side_effect=lambda: clock[0]), \
             patch("linux_telemetry.time.sleep", side_effect=lambda seconds: clock.__setitem__(0, clock[0]+seconds)):
            self.assertTrue(monitor.preflight())
        self.assertEqual(monitor.preparation_rows[0]["time"], 0.)
        self.assertEqual(monitor.rows[0]["time"], 10.)
        self.assertEqual(monitor.rows[-1]["time"]-monitor.rows[0]["time"], 5.)


if __name__ == "__main__":
    unittest.main()
