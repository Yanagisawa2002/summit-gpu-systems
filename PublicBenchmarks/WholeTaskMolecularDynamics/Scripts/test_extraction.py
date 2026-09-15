"""Extraction/source-identity checks only; no C++ compilation or MD workload."""
from pathlib import Path
import tempfile
import unittest
from extract_upstream import ROOT, between, extract


class ExtractionTests(unittest.TestCase):
    def test_exact_force_and_update_blocks_and_no_overwrite(self):
        with tempfile.TemporaryDirectory(prefix="summit-md-extraction-") as temp:
            output = Path(temp) / "new"
            extract(output)
            original = (ROOT / "Docs/whole-task-md-20260915/sources/example_molecular_dynamics.cpp").read_text()
            header = (output / "UpstreamStep.hpp").read_text()
            force = between(original, "  Kokkos::View<float *[3], MemorySpace> forces(", "  float potential_energy;")
            update = original[original.index('  Kokkos::parallel_for(\n      "Example::update_particles_position_and_velocity"'):-2]
            self.assertIn(force, header)
            self.assertIn(update, header)
            pair = force[force.index("          auto const rsq"):force.index("        forces(i, 0)")][:-len("        }\n")]
            self.assertIn("KOKKOS_INLINE_FUNCTION void upstream_pair", header)
            self.assertIn(pair, header)
            self.assertIn("#if defined(SUMMIT_MD_SERIAL)\ninline Input initialize", header)
            with self.assertRaises(FileExistsError):
                extract(output)

    def test_ambiguous_anchor_rejected(self):
        with self.assertRaises(ValueError):
            between("start start end", "start", "end")


if __name__ == "__main__":
    unittest.main()
