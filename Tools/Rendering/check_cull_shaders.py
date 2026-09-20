"""Check shared predicate wiring; optionally compile actual entry points with DXC.

Compilation is not Unity import, runtime validation or a GPU measurement.
"""
from pathlib import Path
import argparse
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SHADERS = ROOT / "Integrations/NYCGIS/Assets/Shaders/NYCGISDemo"


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--dxc", help="Explicit existing DXC executable; never downloads one")
    args = p.parse_args()
    count = 0
    with tempfile.TemporaryDirectory(prefix="summit-cull-compile-") as tmp:
        for name, expected in (("Bfp2GpuClusterCull.compute", 2),
                               ("Bfp2GpuClusterCullWave.compute", 1)):
            path = SHADERS / name
            text = path.read_text(encoding="utf-8")
            if text.count('#include "Bfp2NormalConeVisibility.hlsl"') != 1:
                raise SystemExit(f"Missing shared predicate include: {name}")
            if text.count("Bfp2NormalConeRejectsAllCameras(") != expected:
                raise SystemExit(f"Not all scalar/compact/wave cone sites use union semantics: {name}")
            if "float3 viewDirection = toCamera / distanceToCamera" in text:
                raise SystemExit(f"Legacy nearest-camera cone still present: {name}")
            if args.dxc:
                for entry in re.findall(r"^#pragma kernel (\w+)", text, re.M):
                    subprocess.run([args.dxc, "-T", "cs_6_0", "-E", entry,
                                    "-I", str(SHADERS), "-Fo", str(Path(tmp)/(entry+".dxil")),
                                    str(path)], check=True)
                    count += 1
    print(f"PASS shared predicate wiring; {count} actual HLSL entry points compiled")


if __name__ == "__main__":
    main()
