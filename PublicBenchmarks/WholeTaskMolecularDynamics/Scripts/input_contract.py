"""Structural, finite-value validation of complete canonical input/golden files."""
import math
from pathlib import Path
import struct

CASES = {"discovery-8": (2048, False), "small-6": (864, False),
         "original-10": (4000, False), "large-20": (32000, False),
         "perturbed-10": (4000, False), "boundary-membership": (8, True),
         "isolated-3": (3, False), "tail-129": (129, False)}


def validate_case(directory, name, count, membership_only):
    base = Path(directory) / name
    raw = base.with_suffix(".bin").read_bytes()
    if len(raw) != 20 + count * 28 or raw[:8] != b"SUMD0001":
        raise ValueError("Input ABI/size mismatch: " + name)
    n, radius, flag = struct.unpack_from("<IfI", raw, 8)
    if n != count or radius != 3 or flag != int(membership_only):
        raise ValueError("Input task identity mismatch: " + name)
    for i in range(n):
        x, y, z, identity = struct.unpack_from("<fffI", raw, 20 + i * 16)
        if identity != i or not all(math.isfinite(v) for v in (x, y, z)):
            raise ValueError("Input position/identity invalid: " + name)
    if not all(math.isfinite(x[0]) for x in struct.iter_unpack("<f", raw[20 + n * 16:])):
        raise ValueError("Input velocity is nonfinite: " + name)
    csr = base.with_suffix(".csr").read_bytes()
    if len(csr) < 16 or csr[:8] != b"SUMDCSR1":
        raise ValueError("Golden CSR ABI: " + name)
    rows, members = struct.unpack_from("<II", csr, 8)
    if rows != n or len(csr) != 16 + 4 * (n + 1 + members):
        raise ValueError("Golden CSR size: " + name)
    offsets = struct.unpack_from(f"<{n + 1}I", csr, 16)
    ids = struct.unpack_from(f"<{members}I", csr, 16 + 4 * (n + 1))
    if offsets[0] != 0 or offsets[-1] != members or any(a > b for a, b in zip(offsets, offsets[1:])):
        raise ValueError("Golden CSR offsets: " + name)
    for i in range(n):
        row = ids[offsets[i]:offsets[i + 1]]
        if any(j >= n or j == i for j in row) or len(set(row)) != len(row):
            raise ValueError("Golden CSR ID/self/multiplicity invalid: " + name)
    state_path = base.with_suffix(".state")
    if membership_only:
        if state_path.exists():
            raise ValueError("Singular membership-only fixture must not have a state golden")
    else:
        state = state_path.read_bytes()
        if len(state) != 12 + n * 36 or state[:8] != b"SUMDSTA1" or struct.unpack_from("<I", state, 8)[0] != n:
            raise ValueError("Golden state ABI/size: " + name)
        if not all(math.isfinite(x[0]) for x in struct.iter_unpack("<f", state[12:])):
            raise ValueError("Golden state is nonfinite: " + name)
    return {"case": name, "particles": n, "ids": members, "membershipOnly": membership_only}


def validate_inputs(directory):
    if not Path(directory).is_dir():
        raise ValueError("Canonical inputs directory is required")
    result = [validate_case(directory, name, *shape) for name, shape in CASES.items()]
    expected = {name + suffix for name, (_, only) in CASES.items()
                for suffix in ((".bin", ".csr") if only else (".bin", ".csr", ".state"))}
    actual = {path.name for path in Path(directory).iterdir() if path.suffix in (".bin", ".csr", ".state")}
    if actual != expected:
        raise ValueError("Canonical input/golden file set differs from the declared cases")
    return result
