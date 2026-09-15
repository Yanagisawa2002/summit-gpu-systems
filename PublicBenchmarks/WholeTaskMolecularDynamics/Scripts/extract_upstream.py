"""Extract unchanged computation blocks from the pinned public application.

This is source preparation, not a benchmark. No build or hardware work occurs.
"""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PIN = "375875dfb6b2e7631b1ba599cd26ee5c1e68ab90"
SHA = "fc292b286270367980fbb1cc99a4944230138b064d4075c264472b7690828b16"


def between(text, start, end):
    if text.count(start) != 1 or text.count(end) != 1:
        raise ValueError("Pinned extraction anchor must occur exactly once")
    return text[text.index(start):text.index(end)]


def extract(destination):
    source = ROOT / "Docs/whole-task-md-20260915/sources/example_molecular_dynamics.cpp"
    if hashlib.sha256(source.read_bytes()).hexdigest() != SHA:
        raise ValueError("Pinned upstream source SHA256 changed")
    text = source.read_text()
    setup = between(text, "  // face-centered cubic lattice parameters", "  ArborX::BoundingVolumeHierarchy index(")
    for axis in ("x", "y", "z"):
        setup = setup.replace(f"float const d{axis} = 1.7f;", f"float const d{axis} = spacing;")
        setup = setup.replace(f"int const n{axis} = 10;", f"int const n{axis} = cells;")
    setup += "  execution_space.fence();\n  return Input{particles, velocities};\n"
    force = between(text, "  Kokkos::View<float *[3], MemorySpace> forces(", "  float potential_energy;")
    update = text[text.index('  Kokkos::parallel_for(\n      "Example::update_particles_position_and_velocity"'):]
    if not update.endswith("}\n"):
        raise ValueError("Unexpected upstream main ending")
    update = update[:-2]
    pair = force[force.index("          auto const rsq"):force.index("        forces(i, 0)")]
    if not pair.endswith("        }\n"):
        raise ValueError("Unexpected end of upstream force-neighbour loop")
    pair = pair[:-len("        }\n")]
    definitions = between(text, "template <class MemorySpace>\nstruct Neighbors", "int main(int argc")
    header = "// Extracted from the pinned BSD-3-Clause ArborX example; see source receipt.\n"
    header += "#include <ArborX.hpp>\n#include <Kokkos_Random.hpp>\n#include <type_traits>\n"
    header += definitions
    header += "#if defined(SUMMIT_MD_SERIAL)\nusing ExecutionSpace=Kokkos::Serial;\n"
    header += "#elif defined(SUMMIT_MD_OPENMP)\nusing ExecutionSpace=Kokkos::OpenMP;\n"
    header += "#elif defined(SUMMIT_MD_CUDA)\nusing ExecutionSpace=Kokkos::Cuda;\n"
    header += '#else\n#error "Select one explicit SUMMIT_MD execution space"\n#endif\n'
    header += "using MemorySpace=ExecutionSpace::memory_space;\n"
    header += "using Points=Kokkos::View<ArborX::Point<3>*,MemorySpace>;\nusing Vectors=Kokkos::View<float*[3],MemorySpace>;\n"
    header += "struct Input { Points particles; Vectors velocities; };\n"
    # Exporting RNG state on CUDA/OpenMP would create a different initial task.
    header += "#if defined(SUMMIT_MD_SERIAL)\n"
    header += "inline Input initialize(int cells, float spacing) { ExecutionSpace execution_space;\n" + setup + "}\n#endif\n"
    header += "KOKKOS_INLINE_FUNCTION void upstream_pair(float dx, float dy, float dz, float &fxi, float &fyi, float &fzi) {\n"
    header += pair + "}\n"
    header += "inline void advance(Points particles, Vectors velocities, Vectors forces) {\n"
    header += "  ExecutionSpace execution_space; int const n=particles.extent_int(0); auto const dt=5e-3f;\n"
    header += update + "  execution_space.fence();\n}\n"
    header += "inline Vectors consume(Points particles, Vectors velocities, Kokkos::View<int*,MemorySpace> indices, Kokkos::View<int*,MemorySpace> offsets) {\n"
    header += "  ExecutionSpace execution_space; int const n=particles.extent_int(0); auto const dt=5e-3f;\n"
    header += force + update + "  execution_space.fence(); return forces;\n}\n"
    destination.mkdir(parents=True, exist_ok=False)
    target = destination / "UpstreamStep.hpp"
    target.write_text(header, newline="\n")
    receipt = {"repository": "https://github.com/arborx/ArborX", "commit": PIN,
               "sourceSha256": SHA, "generatedSha256": hashlib.sha256(target.read_bytes()).hexdigest(),
               "changes": ["Parameterize cell count and spacing; primary defaults remain 10/1.7.",
                           "Return initialized views and computed forces to the validator.",
                           "Extract force/update dependency slice; exclude unused potential-energy diagnostic.",
                           "Canonical initialization is compiled only for the explicit Serial backend.",
                           "Force/update select Serial, OpenMP, or CUDA without changing computation blocks.",
                           "Expose the unchanged per-neighbour force block and update block for the ordinary tiled GPU control.",
                           "Explicit execution-space completion fence."]}
    (destination / "extraction.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("destination", type=Path)
    extract(parser.parse_args().destination)
