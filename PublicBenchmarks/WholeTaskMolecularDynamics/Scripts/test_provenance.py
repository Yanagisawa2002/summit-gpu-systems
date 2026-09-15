"""Tiny filesystem regressions; no dependency, compiler, or numerical workload."""
import json
from pathlib import Path
import struct
import tempfile
import unittest
from input_contract import validate_case, validate_inputs
from freeze import artifact_closure, validate_plan
from background import WINDOWS_MONITOR
from provenance import capture, file_record, sha, tool_records, verify_build, verify_capture, verify_staging


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="summit-md-provenance-")
        self.root = Path(self.temp.name).resolve()
        self.addCleanup(self.temp.cleanup)

    def put(self, relative, data=b"fixture"):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def test_missing_and_empty_trees_rejected(self):
        with self.assertRaises(ValueError):
            capture(self.root, ["missing"])
        (self.root / "empty").mkdir()
        with self.assertRaises(ValueError):
            capture(self.root, ["empty"])
        with self.assertRaises(ValueError):
            validate_inputs(self.root / "missing")

    def test_required_binary_missing_rejected(self):
        self.put("build/CMakeCache.txt")
        with self.assertRaises(ValueError):
            capture(self.root, ["build"], ["build/SummitMolecularNative"])

    def test_changed_and_added_source_rejected(self):
        self.put("source/main.cpp", b"first")
        manifest = capture(self.root, ["source"])
        verify_capture(self.root, manifest)
        self.put("source/extra.cpp")
        with self.assertRaises(ValueError):
            verify_capture(self.root, manifest)
        (self.root / "source/extra.cpp").unlink()
        self.put("source/main.cpp", b"second")
        with self.assertRaises(ValueError):
            verify_capture(self.root, manifest)

    def test_containment_rejected(self):
        with self.assertRaises(ValueError):
            file_record(self.root, "../outside")

    def test_freeze_cannot_accept_absent_inputs(self):
        plan = {"schemaVersion": 2, "primaryCase": "original-10", "taskBoundary": "fixture",
                "primaryMetric": "fixture", "statistics": {"method": "paired-log-process-means", "confidence": .95},
                "monitor": WINDOWS_MONITOR, "performanceStability": {
                    "maxWithinProcessCoefficientOfVariation": .2,
                    "maxFirstHalfLastHalfCostRatioEitherDirection": 1.15, "maxAcrossProcessCostRatio": 1.5},
                "failureRules": ["fixture"],
                "buildReceipts": {"native-serial": "build.json"}, "validationReceipts": ["validation.json"],
                "armArtifacts": {arm: {"build": "native-serial", "executable": "missing-app"} for arm in ("arborx", "grid")},
                "comparisons": [{"numerator": "grid", "denominator": "arborx"}],
                "processes": [{"name": f"r{block}-{arm}", "round": block, "case": "original-10",
                               "arm": arm, "warmups": 0, "measured": 2}
                              for block in range(1, 5) for arm in ("arborx", "grid")]}
        validate_plan(plan)
        with self.assertRaisesRegex(ValueError, "Canonical inputs directory is required"):
            artifact_closure(self.root, self.root / "run", plan, "fixture-commit")
        plan["armArtifacts"]["tiled128"] = {"build": "native-cuda", "executable": "missing-cuda"}
        with self.assertRaisesRegex(ValueError, "independent scalar and bounded-overflow"):
            validate_plan(plan)

    def staging(self):
        self.put("source/main.cs")
        self.put("host/Assets/main.cs")
        (self.root / "host/Packages").mkdir()
        rows = [{"source": "source/main.cs", "target": "Assets/main.cs", "sha256": sha(self.root / "source/main.cs")}]
        self.put("staging.json", json.dumps(rows).encode())

    def test_stale_staged_source_rejected(self):
        self.staging()
        verify_staging(self.root, "staging.json", "host")
        self.put("source/main.cs", b"updated")
        with self.assertRaises(ValueError):
            verify_staging(self.root, "staging.json", "host")

    def test_unmapped_staged_code_rejected(self):
        self.staging()
        self.put("host/Assets/injected.compute")
        with self.assertRaises(ValueError):
            verify_staging(self.root, "staging.json", "host")

    def receipt(self):
        self.put("src/main.cpp")
        self.put("build/app", b"synthetic product - never executed")
        log = self.put("build.log", b"synthetic receipt fixture - no build")
        tool = self.put("compiler", b"synthetic tool - never executed")
        record = {"schemaVersion": 2, "completed": True, "sourceCommit": "fixture-commit", "sourceClean": True,
                  "inputs": capture(self.root, ["src"]), "products": capture(self.root, ["build"], ["build/app"]),
                  "tools": tool_records([tool]),
                  "commands": [{"exitCode": 0, "log": "build.log", "logSha256": sha(log)}]}
        path = self.put("receipt.json", json.dumps(record).encode())
        return path

    def test_build_identity_and_mutation_rejected(self):
        receipt = self.receipt()
        verify_build(self.root, receipt, "fixture-commit")
        with self.assertRaises(ValueError):
            verify_build(self.root, receipt, "other-commit")
        self.put("build/app", b"different")
        with self.assertRaises(ValueError):
            verify_build(self.root, receipt, "fixture-commit")

    def test_changed_build_source_rejected(self):
        receipt = self.receipt()
        self.put("src/main.cpp", b"new")
        with self.assertRaises(ValueError):
            verify_build(self.root, receipt, "fixture-commit")

    def test_missing_golden_and_nonfinite_state_rejected(self):
        self.put("tiny.bin", b"SUMD0001" + struct.pack("<IfIfffIfff", 1, 3., 0, 0., 0., 0., 0, 0., 0., 0.))
        self.put("tiny.csr", b"SUMDCSR1" + struct.pack("<IIII", 1, 0, 0, 0))
        with self.assertRaises(FileNotFoundError):
            validate_case(self.root, "tiny", 1, False)
        self.put("tiny.state", b"SUMDSTA1" + struct.pack("<I9f", 1, *([0.] * 9)))
        validate_case(self.root, "tiny", 1, False)
        self.put("tiny.state", b"SUMDSTA1" + struct.pack("<I9f", 1, *([float("nan")] * 9)))
        with self.assertRaises(ValueError):
            validate_case(self.root, "tiny", 1, False)


if __name__ == "__main__":
    unittest.main()
