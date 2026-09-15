"""Tiny arithmetic fixtures only; this is not numerical acceptance of native code."""
import copy
import contextlib
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import scalar_oracle
from scalar_oracle import compare, consume, f32, members


class ScalarOracleTests(unittest.TestCase):
    def test_inclusive_float_boundary_and_duplicate_identity(self):
        points = [(0.,0.,0.), (3.,0.,0.), (f32(3.+2**-22),0.,0.), (0.,0.,0.)]
        rows = members(points)
        self.assertEqual(rows[0], [1,3])
        self.assertNotIn(0, rows[0])
        with self.assertRaises(ValueError):
            consume(points, [(0.,0.,0.)]*4, rows)

    def test_known_two_particle_force_and_all_components(self):
        # At r=1/2: r^-2=4, r^-6=64, fij=16128, |Fx|=8064.
        points = [(0.,0.,0.), (.5,0.,0.)]
        result = consume(points, [(0.,0.,0.)]*2, [[1],[0]])
        self.assertEqual(result[2], [-8064.,0.,0.,8064.,0.,0.])
        self.assertAlmostEqual(result[1][0], -40.32, places=4)
        self.assertAlmostEqual(result[0][3], .7016, places=5)
        self.assertEqual(compare(result, result), [0.,0.,0.])
        corrupt = copy.deepcopy(result)
        corrupt[2][-1] = .1
        with self.assertRaisesRegex(ValueError, "component=5"):
            compare(corrupt, result)

    def test_empty_neighbours_preserve_velocity_and_still_update_position(self):
        result = consume([(0.,0.,0.)], [(1.,2.,3.)], [[]])
        self.assertEqual(result[1], [1.,2.,3.])
        self.assertEqual(result[2], [0.,0.,0.])
        self.assertAlmostEqual(result[0][-1], .015, places=7)

    def test_complete_file_receipt_binds_multiple_actual_outputs(self):
        # A two-particle synthetic file fixture, unrelated to the real case set.
        with tempfile.TemporaryDirectory(prefix="summit-scalar-fixture-") as directory:
            root = Path(directory).resolve()
            run = root / "run"
            inputs = run / "inputs"
            inputs.mkdir(parents=True)
            points, velocities = [(0.,0.,0.), (.5,0.,0.)], [(0.,0.,0.)]*2
            raw = b"SUMD0001" + struct.pack("<IfI", 2, 3., 0)
            raw += b"".join(struct.pack("<fffI", *p, i) for i,p in enumerate(points))
            raw += struct.pack("<6f", *([0.]*6))
            (inputs / "fixture.bin").write_bytes(raw)
            (inputs / "fixture.csr").write_bytes(b"SUMDCSR1" + struct.pack("<7I", 2, 2, 0, 1, 2, 1, 0))
            state = consume(points, velocities, [[1], [0]])
            payload = b"SUMDSTA1" + struct.pack("<I18f", 2, *(v for field in state for v in field))
            (inputs / "fixture.state").write_bytes(payload)
            actuals = [run / "actual-first.state", run / "actual-last.state"]
            for actual in actuals:
                actual.write_bytes(payload)
            with patch.object(scalar_oracle, "ROOT", root), patch.object(scalar_oracle, "RUN", run), \
                 patch.dict(scalar_oracle.CASES, {"fixture": (2, False)}), \
                 patch.object(scalar_oracle, "git_identity", return_value={"sourceCommit": "fixture", "sourceClean": True}), \
                 contextlib.redirect_stdout(io.StringIO()):
                scalar_oracle.validate("fixture", actuals, run / "accepted", True)
                record = json.loads((run / "accepted/oracle.json").read_text())
                self.assertEqual(scalar_oracle.recheck(record, root, run), {p.relative_to(root).as_posix() for p in actuals})
                actuals[-1].write_bytes(payload[:-4] + struct.pack("<f", .1))
                with self.assertRaises(ValueError):
                    scalar_oracle.recheck(record, root, run)
                with self.assertRaises(ValueError):
                    scalar_oracle.validate("fixture", actuals, run / "rejected", True)
                self.assertEqual(json.loads((run / "rejected/oracle.json").read_text())["status"], "rejected")


if __name__ == "__main__":
    unittest.main()
