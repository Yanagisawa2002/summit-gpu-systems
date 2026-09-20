"""File-safety/provenance tests use synthetic source bytes, not a Unity run."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("prepare", Path(__file__).resolve().parents[1]/"prepare.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class StagingTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)/"repo"
        self.root.mkdir()
        for src in prepare.INPUTS:
            p = self.root/src
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"fixture only\r\n")
        def git(*args):
            subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)
        git("init")
        git("add", ".")
        git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture")
        self.out = Path(self.tmp.name)/"project"

    def test_exact_bytes(self):
        receipt = prepare.stage(self.root, self.out)
        self.assertEqual(len(receipt["files"]), 9)
        self.assertFalse(receipt["sourceDirty"])
        for src, dst in prepare.INPUTS.items():
            self.assertEqual((self.root/src).read_bytes(), (self.out/dst).read_bytes())

    def test_existing_output_untouched(self):
        self.out.mkdir()
        sentinel = self.out/"keep"
        sentinel.write_text("unchanged")
        with self.assertRaises(FileExistsError):
            prepare.stage(self.root, self.out)
        self.assertEqual(sentinel.read_text(), "unchanged")
        self.assertEqual(len(list(self.out.iterdir())), 1)

    def test_inside_repository_rejected(self):
        with self.assertRaises(ValueError):
            prepare.stage(self.root, self.root/"generated")
        self.assertFalse((self.root/"generated").exists())

    def test_missing_source_creates_nothing(self):
        (self.root/next(iter(prepare.INPUTS))).unlink()
        with self.assertRaises(FileNotFoundError):
            prepare.stage(self.root, self.out)
        self.assertFalse(self.out.exists())

    def test_staged_tamper(self):
        prepare.stage(self.root, self.out)
        (self.out/"Assets/Fixture.cs").write_text("tampered")
        with self.assertRaises(ValueError):
            prepare.verify(self.root, self.out)

    def test_source_tamper(self):
        prepare.stage(self.root, self.out)
        (self.root/next(iter(prepare.INPUTS))).write_text("tampered")
        with self.assertRaises(ValueError):
            prepare.verify(self.root, self.out)

    def test_manifest_tamper(self):
        prepare.stage(self.root, self.out)
        p = self.out/"Assets/Resources/staging.json"
        m = json.loads(p.read_text())
        m["files"][0]["path"] = "../escape"
        p.write_text(json.dumps(m))
        with self.assertRaises(ValueError):
            prepare.verify(self.root, self.out)


if __name__ == "__main__":
    unittest.main()
