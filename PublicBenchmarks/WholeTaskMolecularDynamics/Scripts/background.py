"""Low-rate Windows CPU/GPU background observations, independent of task timers.

PDH counters are observations of utilization, not a proof that interference is
absent. They are not kernel profiling or GPU duration estimates.
"""
import ctypes as C
import json
import os
import re
import threading
import time

WINDOWS_MONITOR = {"implementation": "windows-pdh-v1", "preflightSeconds": 5,
                   "samplePeriodSeconds": .25, "backgroundCpuLimitPercent": 5,
                   "backgroundGpuEngineLimitPercent": 3}


class FileTime(C.Structure):
    _fields_ = [("low", C.c_uint32), ("high", C.c_uint32)]
    def ticks(self):
        return (self.high << 32) | self.low


class CounterUnion(C.Union):
    _fields_ = [("longValue", C.c_int32), ("doubleValue", C.c_double), ("largeValue", C.c_int64)]


class CounterValue(C.Structure):
    _fields_ = [("status", C.c_uint32), ("value", CounterUnion)]


class CounterItem(C.Structure):
    _fields_ = [("name", C.c_wchar_p), ("formatted", CounterValue)]


class Background:
    def __init__(self):
        self.kernel = C.WinDLL("kernel32", use_last_error=True)
        self.pdh = C.WinDLL("pdh")
        self.pdh.PdhOpenQueryW.argtypes = [C.c_wchar_p, C.c_size_t, C.POINTER(C.c_void_p)]
        self.pdh.PdhAddEnglishCounterW.argtypes = [C.c_void_p, C.c_wchar_p, C.c_size_t, C.POINTER(C.c_void_p)]
        self.pdh.PdhCollectQueryData.argtypes = [C.c_void_p]
        self.pdh.PdhGetFormattedCounterArrayW.argtypes = [C.c_void_p, C.c_uint32, C.POINTER(C.c_uint32), C.POINTER(C.c_uint32), C.c_void_p]
        self.pdh.PdhCloseQuery.argtypes = [C.c_void_p]
        self.query, self.counter = C.c_void_p(), C.c_void_p()
        self.rows, self.errors = [], []
        self.child = None
        self.halt = threading.Event()
        self.thread = None
        self.check(self.pdh.PdhOpenQueryW(None, 0, C.byref(self.query)))
        self.check(self.pdh.PdhAddEnglishCounterW(self.query, r"\GPU Engine(*)\Utilization Percentage", 0, C.byref(self.counter)))
        self.previous = self.cpu()
        self.previous_own = 0
        self.pdh.PdhCollectQueryData(self.query)

    @staticmethod
    def check(code):
        if code:
            raise OSError("PDH status " + hex(code & 0xffffffff))

    def cpu(self):
        idle, kernel, user = FileTime(), FileTime(), FileTime()
        if not self.kernel.GetSystemTimes(C.byref(idle), C.byref(kernel), C.byref(user)):
            raise C.WinError(C.get_last_error())
        return idle.ticks(), kernel.ticks()+user.ticks()

    def own_cpu(self):
        if self.child is None:
            return 0
        created, exited, kernel, user = FileTime(), FileTime(), FileTime(), FileTime()
        if not self.kernel.GetProcessTimes(C.c_void_p(int(self.child._handle)), C.byref(created), C.byref(exited), C.byref(kernel), C.byref(user)):
            raise C.WinError(C.get_last_error())
        return kernel.ticks()+user.ticks()

    def gpu(self):
        self.check(self.pdh.PdhCollectQueryData(self.query))
        size, count = C.c_uint32(), C.c_uint32()
        code = self.pdh.PdhGetFormattedCounterArrayW(self.counter, 0x200 | 0x8000, C.byref(size), C.byref(count), None)
        if (code & 0xffffffff) not in (0, 0x800007D2):
            self.check(code)
        buffer = C.create_string_buffer(size.value)
        self.check(self.pdh.PdhGetFormattedCounterArrayW(self.counter, 0x200 | 0x8000, C.byref(size), C.byref(count), buffer))
        values = C.cast(buffer, C.POINTER(CounterItem))
        rows, valid = [], 0
        for i in range(count.value):
            item = values[i]
            if item.formatted.status not in (0, 1):
                continue
            valid += 1
            percent = item.formatted.value.doubleValue
            if percent <= 0:
                continue
            match = re.search(r"pid_(\d+)_", item.name)
            pid = int(match.group(1)) if match else None
            rows.append({"name": item.name, "pid": pid, "percent": percent,
                         "taskOwned": self.child is not None and pid == self.child.pid})
        if valid == 0:
            raise RuntimeError("GPU engine counters unavailable; do not encode as zero")
        return rows

    def sample(self, phase):
        now = time.perf_counter_ns()
        current, own = self.cpu(), self.own_cpu()
        idle = current[0]-self.previous[0]
        total = current[1]-self.previous[1]
        own_delta = own-self.previous_own
        gpu = self.gpu()
        cpu = 100*(total-idle)/total if total else None
        owned = 100*own_delta/total if total else None
        row = {"phase": phase, "perfCounterNs": now, "utcSeconds": time.time(),
               "systemCpuPercent": cpu, "taskCpuPercent": owned,
               "backgroundCpuPercent": max(0, cpu-owned) if cpu is not None else None,
               "backgroundGpuEngineMaxPercent": max((r["percent"] for r in gpu if not r["taskOwned"]), default=0),
               "engines": gpu}
        self.previous, self.previous_own = current, own
        self.rows.append(row)
        return row

    def preflight(self, seconds=5):
        for _ in range(round(seconds/.25)):
            time.sleep(.25)
            self.sample("before")
        return self.quiet([r for r in self.rows if r["phase"] == "before"])

    @staticmethod
    def quiet(rows):
        return bool(rows) and all(r["backgroundCpuPercent"] is not None and r["backgroundCpuPercent"] <= 5
                                  and r["backgroundGpuEngineMaxPercent"] <= 3 for r in rows)

    def start(self, child):
        self.child = child
        def observe():
            while not self.halt.wait(.25):
                try:
                    self.sample("during")
                except Exception as error:
                    self.errors.append(str(error))
                    return
        self.thread = threading.Thread(target=observe, daemon=True)
        self.thread.start()

    def finish(self):
        self.halt.set()
        if self.thread is not None:
            self.thread.join()
        try:
            self.sample("at-process-exit" if self.child is not None else "preflight-exit")
        except Exception as error:
            self.errors.append(str(error))
        self.pdh.PdhCloseQuery(self.query)
        return {"samplePeriodSeconds": .25, "rules": {"backgroundCpuMaxPercent": 5, "backgroundGpuEngineMaxPercent": 3,
                 "action": "retain all samples; any violation makes the entire confirmation campaign ineligible"},
                "rows": self.rows, "errors": self.errors,
                "observedBackgroundEligible": not self.errors and self.quiet(self.rows),
                "limitation": "Periodic process-lifetime observations include and bracket short task intervals; unsampled bursts cannot be excluded."}
