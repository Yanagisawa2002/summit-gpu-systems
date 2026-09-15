"""Observed builds, only after the coordinator's host/resource admission.

This file prepares no dependencies and installs nothing outside its run root.
Use --help for source inspection; invoking a build requires an admitted stage.
"""
import argparse
import os
import json
from pathlib import Path
import platform
import shutil
from provenance import ROOT, RUN, SOURCE_TREES, BuildSession, inside, sha, verify_capture
from extract_upstream import SHA as UPSTREAM_SHA


def required_tool(value):
    found = shutil.which(value)
    if not found:
        raise ValueError("Required existing tool unavailable: " + value)
    return str(Path(found).resolve())


def relative(path):
    return Path(path).resolve().relative_to(ROOT).as_posix()


def native(args):
    backend = args.backend
    run = args.run_root.resolve()
    suffix = "" if backend == "serial" else "-" + backend
    build, kb, install = (run / (name + suffix) for name in ("native-build", "kokkos-build", "kokkos-install"))
    if any(path.exists() for path in (build, kb, install)):
        raise ValueError("Preserve previous build directories; use a new prepared run root")
    cmake, cxx = required_tool(args.cmake), required_tool(args.cxx)
    dependencies = json.loads((run / "dependencies.json").read_text())
    pins = {"arborx": "375875dfb6b2e7631b1ba599cd26ee5c1e68ab90",
            "kokkos": "6739bc623081648af9e752b616d9671527922cbf"}
    for name, pin in pins.items():
        matches = [row for row in dependencies if row["id"] == name]
        if len(matches) != 1 or matches[0].get("pin") != pin:
            raise ValueError("Pinned dependency receipt missing: " + name)
        row = matches[0]
        if sha(inside(ROOT, row["archive"])) != row["sha256"]:
            raise ValueError("Downloaded dependency archive changed: " + name)
        verify_capture(ROOT, row["sourceManifest"])
    if sha(run / "dependencies/arborx/examples/molecular_dynamics/example_molecular_dynamics.cpp") != UPSTREAM_SHA:
        raise ValueError("The dependency's application source differs from the audited pinned example")
    tools = [cmake, cxx]
    env = os.environ.copy()
    compiler = cxx
    if backend == "cuda":
        if platform.system() != "Linux":
            raise ValueError("Prepared CUDA recipe is restricted to the authorized Linux host")
        nvcc = required_tool(args.nvcc)
        compiler = str(run / "dependencies/kokkos/bin/nvcc_wrapper")
        tools.extend([nvcc, compiler])
        env["NVCC_WRAPPER_DEFAULT_COMPILER"] = cxx
        env["NVCC_WRAPPER_DEFAULT_COMPILER_FLAGS"] = ""
        env["PATH"] = str(Path(nvcc).parent) + os.pathsep + env.get("PATH", "")
    if platform.system() == "Windows":
        generator = "NMake Makefiles"
        tools.extend(required_tool(x) for x in ("nmake.exe", "link.exe"))
    else:
        generator = "Unix Makefiles"
        tools.append(required_tool("make"))
    logs = run / ("build-logs-native-" + backend)
    logs.mkdir(parents=True, exist_ok=False)
    session = BuildSession(ROOT, run / ("build-native-" + backend + ".json"), "native-" + backend,
                           [*SOURCE_TREES, relative(run / "dependencies/arborx"),
                            relative(run / "dependencies/kokkos"), relative(run / "generated")],
                           [relative(run / "dependencies.json"), relative(run / "generated/UpstreamStep.hpp"),
                            relative(run / "generated/extraction.json"),
                            *[row["archive"] for row in dependencies if row["id"] in pins]], tools,
                           {"platform": platform.platform(), "backend": backend, "jobs": args.jobs,
                            "generator": generator, "environment": {k: env.get(k) for k in
                            ("INCLUDE", "LIB", "NVCC_WRAPPER_DEFAULT_COMPILER")}})
    try:
        configure = [cmake, "-S", str(run / "dependencies/kokkos"), "-B", str(kb), "-G", generator,
                     "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_CXX_STANDARD=20", "-DCMAKE_CXX_COMPILER=" + compiler,
                     "-DCMAKE_INSTALL_PREFIX=" + str(install), "-DKokkos_ENABLE_SERIAL=ON",
                     "-DKokkos_ENABLE_OPENMP=" + ("ON" if backend == "openmp" else "OFF"),
                     "-DKokkos_ENABLE_CUDA=" + ("ON" if backend == "cuda" else "OFF"),
                     "-DKokkos_ENABLE_TESTS=OFF", "-DKokkos_ENABLE_EXAMPLES=OFF"]
        if backend == "cuda":
            configure += ["-DKokkos_ARCH_BLACKWELL120=ON", "-DKokkos_ENABLE_CUDA_LAMBDA=ON",
                          "-DCMAKE_CXX_FLAGS=--fmad=false -ffp-contract=off"]
        session.execute(configure, ROOT, logs / "01-kokkos-configure.log", env)
        session.execute([cmake, "--build", str(kb), "--target", "install", "--parallel", str(args.jobs)],
                        ROOT, logs / "02-kokkos-build.log", env)
        session.execute([cmake, "-S", str(ROOT / "PublicBenchmarks/WholeTaskMolecularDynamics/Native"),
                         "-B", str(build), "-G", generator, "-DCMAKE_BUILD_TYPE=Release",
                         "-DCMAKE_CXX_COMPILER=" + compiler, "-DCMAKE_PREFIX_PATH=" + str(install),
                         "-DARBORX_SOURCE=" + str(run / "dependencies/arborx"),
                         "-DGENERATED_SOURCE=" + str(run / "generated"), "-DMD_BACKEND=" + backend],
                        ROOT, logs / "03-task-configure.log", env)
        session.execute([cmake, "--build", str(build), "--parallel", str(args.jobs)],
                        ROOT, logs / "04-task-build.log", env)
        extension = ".exe" if platform.system() == "Windows" else ""
        session.finish([relative(build), relative(install)],
                       [relative(build / (name + extension)) for name in ("SummitMolecularNative", "UpstreamMolecularOriginal")]
                       + [relative(build / "CMakeCache.txt"), relative(kb / "CMakeCache.txt")])
    except Exception as error:
        session.fail(error)
        raise


def player(args):
    # Linux graphics/build support remains a separate capability gate.
    if platform.system() != "Windows":
        raise ValueError("Unity Linux recipe awaits an actual authorized graphics/build capability check")
    run = args.run_root.resolve()
    unity = required_tool(args.unity)
    shader = Path(unity).parent / "Data/Tools/UnityShaderCompiler.exe"
    output = run / "player-r1"
    if output.exists():
        raise ValueError("Preserve previous Player output")
    logs = run / "build-logs-player"
    logs.mkdir(parents=True, exist_ok=False)
    files = [relative(run / "player-staging.json"), relative(run / "unity-host/Packages/manifest.json"),
             relative(run / "unity-host/ProjectSettings/ProjectVersion.txt")]
    session = BuildSession(ROOT, run / "build-unity-windows-d3d12.json", "unity-windows-d3d12",
                           SOURCE_TREES, files, [unity, shader],
                           {"platform": platform.platform(), "graphicsAPI": "Direct3D12", "scriptingBackend": "Mono"},
                           (relative(run / "player-staging.json"), relative(run / "unity-host")))
    env = os.environ.copy()
    env["SUMMIT_MD_PLAYER"] = str(output / "SummitMolecularStep.exe")
    try:
        session.execute([unity, "-batchmode", "-nographics", "-projectPath", str(run / "unity-host"),
                         "-executeMethod", "Summit.WholeTaskMD.BuildMolecularPlayer.Run",
                         "-logFile", str(logs / "unity.log")], ROOT, logs / "process.log", env)
        session.finish([relative(output)], [relative(output / "SummitMolecularStep.exe"),
                       relative(output / "UnityPlayer.dll"),
                       relative(output / "SummitMolecularStep_Data/Managed/Assembly-CSharp.dll")])
    except Exception as error:
        session.fail(error)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("kind", choices=("native", "player"))
    parser.add_argument("--run-root", type=Path, default=RUN)
    parser.add_argument("--backend", choices=("serial", "openmp", "cuda"), default="serial")
    parser.add_argument("--cmake", default="cmake")
    parser.add_argument("--cxx", default="cl.exe" if platform.system() == "Windows" else "g++")
    parser.add_argument("--nvcc", default="nvcc")
    parser.add_argument("--unity")
    parser.add_argument("--jobs", type=int, default=4)
    args = parser.parse_args()
    if not 1 <= args.jobs <= 8:
        parser.error("The bounded preparation recipe permits 1-8 compile workers")
    if args.kind == "player" and not args.unity:
        parser.error("Player build requires the existing authorized --unity executable")
    if args.kind == "native":
        native(args)
    else:
        player(args)
