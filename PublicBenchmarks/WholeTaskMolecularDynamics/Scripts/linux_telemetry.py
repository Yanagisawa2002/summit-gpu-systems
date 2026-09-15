"""Read-only Linux qualification; offline parsing is not host calibration.

No affinity, cgroup, GPU setting, or process other than an owned nvidia-smi
query is changed. All counters and rejection reasons are retained. Monitoring
overhead is not subtracted from the application's task wall time.
"""
import math
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import threading
import time
import xml.etree.ElementTree as ET
from provenance import sha, verify_capture

LINUX_MONITOR = {"implementation": "linux-cgroupv2-nvidia-smi-v1",
                 "preflightSeconds": 5, "samplePeriodSeconds": 1.,
                 "maxCollectionSeconds": .5, "maxSampleGapSeconds": 2.,
                 "minimumDuringSamples": 2, "backgroundCpuLimitCores": .25,
                 "backgroundCpuLimitPercentOfCapacity": 5., "idleGpuLimitPercent": 3.,
                 "minimumAvailableMemoryBytes": 8 * 2**30,
                 "gpuProcessPolicy": "reject foreign or unresolved PID", "throttlingPolicy": "reject any increase"}
CALIBRATION_REQUIRED = "Live Linux monitoring calibration is required; offline fixtures are insufficient"
CPU_STAT = ("usage_usec", "user_usec", "system_usec", "nr_periods", "nr_throttled", "throttled_usec")
MEM_EVENTS = ("low", "high", "max", "oom", "oom_kill")


def cpu_list(text):
    cpus = set()
    for item in text.strip().split(","):
        if not re.fullmatch(r"\d+(?:-\d+)?", item):
            raise ValueError("Missing/malformed CPU set")
        a, *tail = item.split("-")
        low, high = int(a), int(tail[0]) if tail else int(a)
        if high < low or high > 65535:
            raise ValueError("Invalid CPU range")
        cpus.update(range(low, high + 1))
    return sorted(cpus)


def unsigned(text):
    if not re.fullmatch(r"\d+", text.strip()):
        raise ValueError("Unavailable/noninteger counter: " + text.strip())
    return int(text)


def key_values(text, required):
    result = {}
    for line in text.splitlines():
        fields = line.split()
        if len(fields) != 2 or fields[0] in result:
            raise ValueError("Malformed/duplicate accounting field")
        result[fields[0]] = unsigned(fields[1])
    if not set(required).issubset(result):
        raise ValueError("Required accounting counters unavailable")
    return result


def cpu_max(text):
    fields = text.split()
    if len(fields) != 2:
        raise ValueError("Malformed cpu.max")
    period = unsigned(fields[1])
    quota = None if fields[0] == "max" else unsigned(fields[0])
    if period == 0 or quota == 0:
        raise ValueError("Invalid CPU quota/period")
    return {"quotaUsec": quota, "periodUsec": period,
            "quotaCores": None if quota is None else quota / period}


def resolve_cgroup(cgroup_text, mountinfo):
    paths = [line[3:] for line in cgroup_text.splitlines() if line.startswith("0::")]
    if len(paths) != 1 or not paths[0].startswith("/") or ".." in PurePosixPath(paths[0]).parts or " (deleted)" in paths[0]:
        raise ValueError("An intact cgroup-v2 membership is required")
    member = PurePosixPath(paths[0])
    candidates = []
    unescape = lambda text: re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), text)
    for line in mountinfo.splitlines():
        before, sep, after = line.partition(" - ")
        if not sep or not after.startswith("cgroup2 "):
            continue
        fields = before.split()
        if len(fields) < 6:
            raise ValueError("Malformed cgroup mountinfo")
        root, mount = PurePosixPath(unescape(fields[3])), PurePosixPath(unescape(fields[4]))
        if member.is_relative_to(root):
            leaf = mount / member.relative_to(root)
        elif member == PurePosixPath("/"):
            # A cgroup namespace may expose its delegated root as 0::/.
            leaf = mount
        else:
            continue
        candidates.append({"membership": str(member), "mountRoot": str(root), "mount": str(mount), "leaf": str(leaf)})
    if len(candidates) != 1:
        raise ValueError("Cannot resolve one unambiguous visible cgroup-v2 mount")
    return candidates[0]


def proc_cpu(text, cpus):
    result = {}
    for line in text.splitlines():
        fields = line.split()
        if fields and fields[0] in {"cpu" + str(i) for i in cpus}:
            if len(fields) < 9:
                raise ValueError("Incomplete per-CPU /proc/stat")
            ticks = [unsigned(x) for x in fields[1:9]]  # guest is already in user/nice
            result[fields[0]] = {"total": sum(ticks), "idle": ticks[3] + ticks[4], "steal": ticks[7]}
    if set(result) != {"cpu" + str(i) for i in cpus}:
        raise ValueError("Affinity CPU counters unavailable")
    return result


def process_stat(text):
    # comm can contain spaces and closing parentheses; fields after the last ') '
    # begin at Linux proc_pid_stat field 3 (state).
    fields = text.rsplit(") ", 1)[1].split()
    return {"ticks": unsigned(fields[11]) + unsigned(fields[12]), "startTicks": unsigned(fields[19])}


def gpu_xml(text, uuid):
    document = ET.fromstring(text)
    devices = document.findall("gpu")
    if len(devices) != 1 or devices[0].findtext("uuid") != uuid:
        raise ValueError("GPU UUID/selection mismatch")
    gpu = devices[0]
    def number(path, unit, maximum=None):
        raw = gpu.findtext(path, "").strip()
        match = re.fullmatch(r"(\d+(?:\.\d+)?)\s*" + re.escape(unit), raw)
        if not match:
            raise ValueError("Unavailable GPU field: " + path)
        value = float(match[1])
        if not math.isfinite(value) or (maximum is not None and value > maximum):
            raise ValueError("Invalid GPU field: " + path)
        return value
    processes = gpu.find("processes")
    if processes is None or (processes.text or "").strip():
        raise ValueError("GPU process list unavailable or uninterpretable")
    rows = []
    for child in processes:
        if child.tag != "process_info":
            raise ValueError("Unknown GPU process record")
        pid = unsigned(child.findtext("pid", ""))
        kind = child.findtext("type", "").strip()
        memory = child.findtext("used_memory", "").strip()
        if pid == 0 or kind not in ("C", "G", "C+G") or not re.fullmatch(r"\d+(?:\.\d+)? MiB", memory):
            raise ValueError("Unresolved GPU process identity/type/memory")
        rows.append({"pid": pid, "type": kind, "usedMiB": float(memory.split()[0]),
                     "name": child.findtext("process_name", "")})
    driver = document.findtext("driver_version", "").strip()
    if not driver:
        raise ValueError("GPU driver identity unavailable")
    return {"uuid": uuid, "driver": driver, "gpuPercent": number("utilization/gpu_util", "%", 100),
            "memoryPercent": number("utilization/memory_util", "%", 100),
            "memoryUsedMiB": number("fb_memory_usage/used", "MiB"),
            "memoryTotalMiB": number("fb_memory_usage/total", "MiB"), "processes": rows}


def interval(a, b, profile=LINUX_MONITOR):
    reasons = []
    if a["identity"] != b["identity"]:
        raise ValueError("Host/cgroup/quota/cpuset/affinity/GPU identity changed")
    wall = b["time"] - a["time"]
    if not 0 < wall <= profile["maxSampleGapSeconds"]:
        raise ValueError("Zero, backwards, or unobserved time interval")
    def delta(x, y):
        value = y - x
        if not math.isfinite(value) or value < 0:
            raise ValueError("Accounting counter decreased or process identity changed")
        return value
    if a["clockTicks"] != b["clockTicks"] or a["clockTicks"] <= 0:
        raise ValueError("CPU accounting clock changed or is unavailable")
    own = delta(a["ownedSeconds"], b["ownedSeconds"])
    usage = delta(a["groups"][0]["cpu"]["usage_usec"], b["groups"][0]["cpu"]["usage_usec"]) / 1e6
    if sum(delta(a["cpu"][key]["total"], b["cpu"][key]["total"]) for key in a["cpu"]) == 0:
        raise ValueError("No elapsed per-CPU ticks; cannot classify as idle")
    host_busy = sum(delta(a["cpu"][key]["total"], b["cpu"][key]["total"])
                    - delta(a["cpu"][key]["idle"], b["cpu"][key]["idle"]) for key in a["cpu"]) / a["clockTicks"]
    if host_busy < 0:
        raise ValueError("Idle CPU growth exceeds elapsed CPU accounting")
    # Snapshot timing/tick quantization can differ by a few ticks; larger
    # subtraction inconsistencies reject instead of clamping them to idle.
    slack = 2 / a["clockTicks"] + max(a["collectionSeconds"], b["collectionSeconds"])
    if own > usage + slack or own > host_busy + slack:
        reasons.append("owned CPU accounting cannot be reconciled")
    cg_background = max(0., usage - own) / wall
    host_background = max(0., host_busy - own) / wall
    capacity = b["identity"]["capacityCores"]
    if not math.isfinite(capacity) or capacity <= 0:
        raise ValueError("Actual CPU capacity is unavailable")
    background_percent = 100 * max(cg_background, host_background) / capacity
    if max(cg_background, host_background) > profile["backgroundCpuLimitCores"] or background_percent > profile["backgroundCpuLimitPercentOfCapacity"]:
        reasons.append("background CPU exceeds frozen limits")
    for previous, current in zip(a["groups"], b["groups"]):
        for key in CPU_STAT:
            growth = delta(previous["cpu"][key], current["cpu"][key])
            if key in ("nr_throttled", "throttled_usec") and growth:
                reasons.append("cgroup CPU throttling: " + current["path"])
        for key in MEM_EVENTS:
            growth = delta(previous["memoryEvents"][key], current["memoryEvents"][key])
            if key != "low" and growth:
                reasons.append("cgroup memory pressure/event: " + key)
    if any(delta(a["cpu"][key]["steal"], b["cpu"][key]["steal"]) for key in a["cpu"]):
        reasons.append("CPU steal time increased")
    if min(a["availableMemoryBytes"], b["availableMemoryBytes"]) < profile["minimumAvailableMemoryBytes"]:
        reasons.append("insufficient actual memory headroom")
    if max(a["collectionSeconds"], b["collectionSeconds"]) > profile["maxCollectionSeconds"]:
        reasons.append("telemetry collection is too slow")
    gpu = b["gpu"]
    foreign = [p for row in (a, b) for p in row["gpu"]["processes"] if p["pid"] not in row["ownedGpuPids"]]
    if foreign:
        reasons.append("foreign or unresolved GPU process")
    if any(not any(p["pid"] in row["ownedGpuPids"] for p in row["gpu"]["processes"])
           and row["gpu"]["gpuPercent"] > profile["idleGpuLimitPercent"] for row in (a, b)):
        reasons.append("unattributed GPU utilization")
    return {"seconds": wall, "ownedCpuSeconds": own, "cgroupCpuSeconds": usage,
            "cgroupBackgroundCores": cg_background, "affinityBackgroundCores": host_background,
            "backgroundPercentOfCapacity": background_percent, "capacityCores": capacity,
            "gpuPercent": gpu["gpuPercent"], "foreignGpuProcesses": foreign,
            "passed": not reasons, "reasons": reasons}


class LinuxBackground:
    def __init__(self, gpu_uuid, calibration=None, gpu_work=False):
        if os.name == "nt" or not re.fullmatch(r"GPU-[0-9a-fA-F-]{36}", gpu_uuid or ""):
            raise ValueError("Linux and one explicit full GPU UUID are required")
        import resource
        self.resource = resource
        self.gpu_uuid, self.gpu_work = gpu_uuid, gpu_work
        self.smi = shutil.which("nvidia-smi")
        if not self.smi:
            raise ValueError("nvidia-smi is unavailable")
        self.location = resolve_cgroup(Path("/proc/self/cgroup").read_text(), Path("/proc/self/mountinfo").read_text())
        self.clock_ticks = os.sysconf("SC_CLK_TCK")
        self.rows, self.errors, self.child, self.child_start = [], [], None, None
        self.failed_observations, self.pending_observation = [], {}
        self.preparation_rows = []
        self.halt, self.thread = threading.Event(), None
        self.calibration, self.calibrated = calibration, False
        self.ever_owned_gpu = False

    def groups(self):
        result = []
        directory, mount = Path(self.location["leaf"]), Path(self.location["mount"])
        if (directory / "cgroup.type").read_text().strip() != "domain":
            raise ValueError("Whole-process accounting requires an ordinary domain cgroup")
        while True:
            if not (directory / "cpu.max").is_file():
                if directory != mount or not result or self.location["mountRoot"] != "/":
                    raise ValueError("Required delegated cgroup controls unavailable")
                break  # Actual visible hierarchy root has no parent resource cap.
            limit_text = (directory / "memory.max").read_text().strip()
            result.append({"path": str(directory), "quota": cpu_max((directory / "cpu.max").read_text()),
                           "cpus": cpu_list((directory / "cpuset.cpus.effective").read_text()),
                           "cpu": key_values((directory / "cpu.stat").read_text(), CPU_STAT),
                           "memoryCurrent": unsigned((directory / "memory.current").read_text()),
                           "memoryLimit": None if limit_text == "max" else unsigned(limit_text),
                           "memoryEvents": key_values((directory / "memory.events").read_text(), MEM_EVENTS)})
            if directory == mount:
                break
            directory = directory.parent
        return result

    def owned_cpu(self):
        # Account for already-reaped children and the live benchmark without
        # double counting exit races. Never use disappearance as zero CPU usage.
        for _ in range(4):
            before = self.resource.getrusage(self.resource.RUSAGE_CHILDREN)
            completed = before.ru_utime + before.ru_stime
            live = 0.
            if self.child is not None and self.child.returncode is None:
                try:
                    stat = process_stat(Path(f"/proc/{self.child.pid}/stat").read_text())
                    if self.child_start is not None and stat["startTicks"] != self.child_start:
                        raise ValueError("Owned benchmark PID was reused")
                    self.child_start = stat["startTicks"]
                    live = stat["ticks"] / self.clock_ticks
                except FileNotFoundError:
                    self.child.poll()
                    time.sleep(.001)  # Give the owner wait() a bounded exit-race handoff.
                    continue
            after = self.resource.getrusage(self.resource.RUSAGE_CHILDREN)
            if completed != after.ru_utime + after.ru_stime:
                continue
            own = self.resource.getrusage(self.resource.RUSAGE_SELF)
            return own.ru_utime + own.ru_stime + completed + live
        raise ValueError("Could not reconcile live/reaped child CPU accounting")

    def read(self, phase):
        self.pending_observation = {"phase": phase, "startedUtcSeconds": time.time()}
        try:
            return self._read(phase)
        except Exception as error:
            failure = {**self.pending_observation, "error": str(error)}
            if isinstance(error, subprocess.CalledProcessError):
                failure.update(stdout=error.stdout, stderr=error.stderr, exitCode=error.returncode)
            self.failed_observations.append(failure)
            raise

    def _read(self, phase):
        begin = time.monotonic()
        query = subprocess.run([self.smi, "-i", self.gpu_uuid, "-q", "-x"], capture_output=True, text=True,
                               env=dict(os.environ, LC_ALL="C"), timeout=2, check=True)
        self.pending_observation["rawGpuXml"] = query.stdout
        if len(query.stdout) > 2 * 1024 * 1024:
            raise ValueError("GPU telemetry exceeds its bounded record size")
        gpu = gpu_xml(query.stdout, self.gpu_uuid)
        location = resolve_cgroup(Path("/proc/self/cgroup").read_text(), Path("/proc/self/mountinfo").read_text())
        if location != self.location:
            raise ValueError("Process cgroup membership changed")
        groups = self.groups()
        affinity = sorted(os.sched_getaffinity(0))
        if not affinity or any(not set(affinity).issubset(g["cpus"]) for g in groups):
            raise ValueError("Affinity differs from effective cgroup CPU set")
        monitored = set(affinity)
        for cpu in affinity:
            monitored.update(cpu_list(Path(f"/sys/devices/system/cpu/cpu{cpu}/topology/thread_siblings_list").read_text()))
        quotas = [g["quota"]["quotaCores"] for g in groups if g["quota"]["quotaCores"] is not None]
        capacity = min([float(len(affinity)), *quotas])
        meminfo = Path("/proc/meminfo").read_text()
        available = re.search(r"^MemAvailable:\s+(\d+) kB$", meminfo, re.MULTILINE)
        if not available:
            raise ValueError("Host available-memory counter unavailable")
        headroom = min([int(available[1]) * 1024,
                        *[g["memoryLimit"] - g["memoryCurrent"] for g in groups if g["memoryLimit"] is not None]])
        identity = {"bootId": Path("/proc/sys/kernel/random/boot_id").read_text().strip(),
                    "pidNamespace": os.readlink("/proc/self/ns/pid"), "cgroup": location,
                    "affinity": affinity, "monitoredCpuIdsIncludingSiblings": sorted(monitored),
                    "capacityCores": capacity, "gpuUuid": gpu["uuid"], "driver": gpu["driver"],
                    "controls": [{k: g[k] for k in ("path", "quota", "cpus", "memoryLimit")} for g in groups]}
        owned_gpu = []
        if self.child is not None and self.calibrated and self.gpu_work:
            owned_gpu = [self.child.pid]  # Only calibrated same-PID-namespace mode.
            self.ever_owned_gpu |= any(p["pid"] == self.child.pid for p in gpu["processes"])
        row = {"phase": phase, "sampleStartNs": int(begin*1e9), "time": time.monotonic(), "utcSeconds": time.time(), "identity": identity,
               "groups": groups, "clockTicks": self.clock_ticks, "ownedSeconds": self.owned_cpu(),
               "cpu": proc_cpu(Path("/proc/stat").read_text(), sorted(monitored)), "availableMemoryBytes": headroom,
               "gpu": gpu, "ownedGpuPids": owned_gpu, "rawGpuXml": query.stdout,
               "collectionSeconds": time.monotonic() - begin}
        self.rows.append(row)
        return row

    def check_calibration(self):
        if not self.calibration:
            raise ValueError(CALIBRATION_REQUIRED)
        from provenance import ROOT
        value = self.calibration
        if (value.get("schemaVersion") != 1 or value.get("status") != "calibrated"
                or value.get("profile") != LINUX_MONITOR or value.get("sourceSha256") != sha(Path(__file__))
                or value.get("hostIdentity") != self.rows[0]["identity"]
                or value.get("gpuPidMode") != "observed-same-pid-namespace"):
            raise ValueError("Missing, stale, or incompatible live calibration")
        if value.get("checks") != {"cpuAccounting": True, "gpuPidMapping": True, "samplingOverhead": True, "taskCoverage": True}:
            raise ValueError("Live monitor checks incomplete")
        verify_capture(ROOT, value["evidence"])
        from monitor_calibration import recheck
        recheck(value)
        self.calibrated = True

    def qualify(self):
        values = [interval(a, b) for a, b in zip(self.rows, self.rows[1:])]
        return {"intervals": values, "passed": bool(values) and all(x["passed"] for x in values)}

    def preflight(self):
        if self.calibration:
            self.read("calibration-preparation")
            self.check_calibration()
            # File/build revalidation can be expensive. Preserve it as a
            # preparation phase, then begin the full quiet window afresh.
            self.preparation_rows.extend(self.rows)
            self.rows.clear()
        self.read("before")
        if self.calibrated and self.rows[0]["identity"] != self.calibration["hostIdentity"]:
            raise ValueError("Host controls changed during calibration revalidation")
        deadline = time.monotonic() + LINUX_MONITOR["preflightSeconds"]
        while time.monotonic() < deadline:
            time.sleep(LINUX_MONITOR["samplePeriodSeconds"])
            self.read("before")
        if not self.calibrated:
            raise ValueError(CALIBRATION_REQUIRED)
        return self.qualify()["passed"]

    def start(self, child):
        self.child = child
        def observe():
            while not self.halt.wait(LINUX_MONITOR["samplePeriodSeconds"]):
                try:
                    self.read("during" if child.returncode is None else "after")
                except Exception as error:
                    self.errors.append(str(error))
                    return
        self.thread = threading.Thread(target=observe, name="summit-md-linux-telemetry", daemon=True)
        self.thread.start()

    def finish(self):
        self.halt.set()
        if self.thread:
            self.thread.join()
        try:
            # Retain a real final accounting interval for short/reaped children.
            delay = max(0., LINUX_MONITOR["samplePeriodSeconds"] - (time.monotonic() - self.rows[-1]["time"]))
            time.sleep(delay)
            self.read("at-process-exit")
            qualified = self.qualify()
        except Exception as error:
            self.errors.append(str(error))
            qualified = {"passed": False, "intervals": []}
        during = sum(row["phase"] == "during" for row in self.rows)
        if self.child and during < LINUX_MONITOR["minimumDuringSamples"]:
            self.errors.append("Insufficient samples during benchmark process; freeze a sufficiently long discovery-validated duration")
        if self.child and self.gpu_work and self.calibrated and not self.ever_owned_gpu:
            self.errors.append("The benchmark GPU process was never observed with a calibrated PID mapping")
        return {"profile": LINUX_MONITOR, "rows": self.rows, "errors": self.errors, **qualified,
                "preparationRows": self.preparation_rows,
                "failedObservations": self.failed_observations,
                "hostCalibrated": self.calibrated,
                "observedBackgroundEligible": qualified["passed"] and self.calibrated and not self.errors,
                "limitation": "Visible cgroup hierarchy and periodic process-lifetime GPU/CPU observations. Hidden ancestors, unsampled bursts, and intra-task GPU phase time are not inferred."}
