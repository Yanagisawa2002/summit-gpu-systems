"""Fail-closed, platform-independent source/build/product identity checks.

Receipts describe locally observed builds, not a signed/reproducible-build claim.
No compiler, dependency fetch, or experiment is launched by this module.
"""
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[3]
RUN = ROOT / "Artifacts/whole-task-md-20260915"
SOURCE_TREES = ("PublicBenchmarks/WholeTaskMolecularDynamics",
                "PublicBenchmarks/External/Adapters", "Packages",
                "Docs/whole-task-md-20260915/sources")
IGNORED_PARTS = {".git", "__pycache__"}
STAGED_CODE = {".cs", ".compute", ".hlsl", ".shader", ".dll", ".asmdef", ".asmref", ".rsp"}


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def inside(root, relative):
    root = Path(root).resolve()
    path = root / relative
    if Path(relative).is_absolute() or ".." in Path(relative).parts:
        raise ValueError("Expected contained relative path: " + str(relative))
    if not path.resolve().is_relative_to(root):
        raise ValueError("Path escapes identity root: " + str(relative))
    return path


def file_record(root, relative):
    path = inside(root, relative)
    if not path.is_file() or path.is_symlink():
        raise ValueError("Required regular file missing: " + str(path))
    return {"path": Path(relative).as_posix(), "sha256": sha(path), "bytes": path.stat().st_size}


def tree_records(root, relative):
    base = inside(root, relative)
    if not base.is_dir() or base.is_symlink():
        raise ValueError("Required directory missing: " + str(base))
    records = []
    for path in sorted(base.rglob("*")):
        if IGNORED_PARTS.intersection(path.relative_to(base).parts):
            continue
        if path.is_symlink():
            raise ValueError("Uncaptured symlink in identity tree: " + str(path))
        if path.is_file():
            records.append(file_record(root, path.relative_to(root)))
    if not records:
        raise ValueError("Required directory is empty: " + str(base))
    return records


def capture(root, trees, files=()):
    entries = {}
    for relative in trees:
        for record in tree_records(root, relative):
            entries[record["path"]] = record
    for relative in files:
        record = file_record(root, relative)
        entries[record["path"]] = record
    if not entries:
        raise ValueError("An empty identity manifest is invalid")
    return {"trees": list(trees), "requiredFiles": list(files),
            "files": [entries[key] for key in sorted(entries)]}


def verify_capture(root, manifest):
    if not manifest.get("files"):
        raise ValueError("An empty identity manifest is invalid")
    actual = capture(root, manifest["trees"], manifest["requiredFiles"])
    if actual != manifest:
        raise ValueError("File identity or directory membership changed")


def write_new(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def git_identity(root):
    return {"sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
            "sourceClean": not subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all"], cwd=root, text=True).strip()}


def tool_records(paths):
    result = []
    for item in paths:
        path = Path(item).resolve()
        if not path.is_file():
            raise ValueError("Required tool missing: " + str(path))
        result.append({"path": path.as_posix(), "sha256": sha(path), "bytes": path.stat().st_size})
    if not result:
        raise ValueError("Missing toolchain identity")
    return result


def verify_staging(root, staging_relative, host_relative):
    staging = inside(root, staging_relative)
    host = inside(root, host_relative)
    rows = json.loads(staging.read_text(encoding="utf-8-sig"))
    if not rows or not isinstance(rows, list):
        raise ValueError("Missing staged-source mapping")
    targets = set()
    for row in rows:
        target = row["target"]
        if target in targets:
            raise ValueError("Duplicate staged target: " + target)
        targets.add(target)
        if sha(inside(root, row["source"])) != row["sha256"] or sha(inside(host, target)) != row["sha256"]:
            raise ValueError("Staged source differs from original: " + target)
    for base in (host / "Assets", host / "Packages"):
        if not base.is_dir():
            raise ValueError("Missing staged project tree: " + str(base))
        for path in base.rglob("*"):
            if path.is_file() and path.suffix.lower() in STAGED_CODE and path.relative_to(host).as_posix() not in targets:
                raise ValueError("Unmapped staged compilation input: " + str(path))
    return {"receipt": staging_relative, "host": host_relative,
            "files": [file_record(root, str(Path(host_relative) / row["target"])) for row in rows]}


class BuildSession:
    """The caller must execute every build command through execute()."""

    def __init__(self, root, receipt, kind, trees, files, tools, config, staging=None):
        self.root, self.receipt = Path(root), Path(receipt)
        if self.receipt.exists() or self.receipt.with_suffix(".started.json").exists():
            raise ValueError("Preserve prior build attempt; choose a new run root")
        self.value = {"schemaVersion": 2, "kind": kind, "startedUtc": utc(),
                      **git_identity(root), "inputs": capture(root, trees, files),
                      "tools": tool_records(tools), "config": config, "commands": []}
        if staging:
            self.value["staging"] = verify_staging(root, *staging)
        write_new(self.receipt.with_suffix(".started.json"), self.value)

    def execute(self, argv, cwd, log, env=None):
        command = {"argv": [str(x) for x in argv], "cwd": str(Path(cwd).resolve()),
                   "startedUtc": utc(), "log": str(Path(log).relative_to(self.root))}
        self.value["commands"].append(command)
        with Path(log).open("x", encoding="utf-8") as output:
            process = subprocess.run(command["argv"], cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT,
                                     creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0, check=False)
        command.update(exitCode=process.returncode, finishedUtc=utc(), logSha256=sha(log))
        if process.returncode:
            self.fail("Build command failed; see " + str(log))
            raise RuntimeError("Build command exit " + str(process.returncode))

    def fail(self, reason):
        if not self.receipt.exists():
            self.value.update(completed=False, failure=str(reason), finishedUtc=utc())
            write_new(self.receipt, self.value)

    def finish(self, product_trees, required_products):
        try:
            verify_capture(self.root, self.value["inputs"])
            if git_identity(self.root) != {k: self.value[k] for k in ("sourceCommit", "sourceClean")}:
                raise ValueError("Git identity changed during build")
            if tool_records([x["path"] for x in self.value["tools"]]) != self.value["tools"]:
                raise ValueError("Toolchain changed during build")
            if "staging" in self.value:
                stage = self.value["staging"]
                if verify_staging(self.root, stage["receipt"], stage["host"]) != stage:
                    raise ValueError("Staged compilation inputs changed during build")
            if not self.value["commands"] or any(x.get("exitCode") != 0 for x in self.value["commands"]):
                raise ValueError("No successful observed build commands")
            self.value.update(products=capture(self.root, product_trees, required_products),
                              completed=True, finishedUtc=utc())
            write_new(self.receipt, self.value)
        except Exception as error:
            self.fail(error)
            raise


def verify_build(root, receipt, expected_commit, require_clean=True):
    value = json.loads(Path(receipt).read_text(encoding="utf-8-sig"))
    if value.get("schemaVersion") != 2 or not value.get("completed"):
        raise ValueError("Missing successful build receipt: " + str(receipt))
    if value["sourceCommit"] != expected_commit or (require_clean and not value["sourceClean"]):
        raise ValueError("Build is not from the clean frozen candidate commit")
    if not value.get("commands") or any(x.get("exitCode") != 0 for x in value["commands"]):
        raise ValueError("Unsuccessful or unobserved build commands")
    for command in value["commands"]:
        if sha(inside(root, command["log"])) != command["logSha256"]:
            raise ValueError("Build command log changed")
    verify_capture(root, value["inputs"])
    verify_capture(root, value["products"])
    if tool_records([x["path"] for x in value["tools"]]) != value["tools"]:
        raise ValueError("Built toolchain identity changed")
    if "staging" in value:
        stage = value["staging"]
        if verify_staging(root, stage["receipt"], stage["host"]) != stage:
            raise ValueError("Staged build identity changed")
    return value
