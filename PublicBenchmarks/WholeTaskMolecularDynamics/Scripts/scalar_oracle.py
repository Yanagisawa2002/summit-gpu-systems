"""Independent scalar float32 acceptance code. Real cases require admission.

Binary32 round-trip after each operation is independent of the extracted C++
kernels. Brute-force membership is capped at 4000 particles. Larger cases keep
the Serial ArborX/CPU-grid membership cross-check and receive full scalar state
validation using their complete CSR.
"""
import argparse
import json
import math
from pathlib import Path
import struct
import time
from input_contract import CASES, validate_case
from provenance import ROOT, RUN, capture, git_identity, inside, sha, verify_capture, write_new


def f32(value):
    try:
        result = struct.unpack("<f", struct.pack("<f", value))[0]
    except OverflowError as error:
        raise ValueError("Nonfinite scalar state") from error
    if not math.isfinite(result):
        raise ValueError("Nonfinite scalar state")
    return result


def squared(a, b):
    d = [f32(x-y) for x, y in zip(a, b)]
    squares = [f32(x*x) for x in d]
    return d, f32(f32(squares[0]+squares[1])+squares[2])


def members(points, radius=3.):
    if len(points) > 4000:
        raise ValueError("Independent scalar membership budget is 4000 particles")
    return [[j for j, p in enumerate(points) if i != j and f32(math.sqrt(squared(q, p)[1])) <= radius]
            for i, q in enumerate(points)]


def consume(points, velocities, rows):
    result_p, result_v, forces = [], [], []
    dt = f32(.005)
    for i, neighbours in enumerate(rows):
        force = [0., 0., 0.]
        for j in neighbours:
            delta, rsq = squared(points[i], points[j])
            if rsq == 0:
                raise ValueError("Coincident distinct particles are membership-only: original force is singular")
            inverse = f32(1. / rsq)
            sixth = f32(f32(inverse*inverse)*inverse)
            fij = f32(f32(sixth*f32(f32(1.*sixth)-1.))*inverse)
            force = [f32(total+f32(dx*fij)) for total, dx in zip(force, delta)]
        velocity = [f32(v+f32(dt*f)) for v, f in zip(velocities[i], force)]
        position = [f32(p+f32(dt*v)) for p, v in zip(points[i], velocity)]
        result_p.extend(position)
        result_v.extend(velocity)
        forces.extend(force)
    return [result_p, result_v, forces]


def compare(actual, expected):
    if len(actual) != 3 or len(expected) != 3:
        raise ValueError("Complete position/velocity/force fields are required")
    maxima = []
    for field, (values, golden) in enumerate(zip(actual, expected)):
        if len(values) != len(golden):
            raise ValueError("Full scalar state shape mismatch")
        absolute, relative = ((.002, .00002) if field == 2 else (.00002, .000002 if field == 0 else .00002))
        errors = [abs(float(x)-y) for x, y in zip(values, golden)]
        for index, (x, y, error) in enumerate(zip(values, golden, errors)):
            if not math.isfinite(x) or not math.isfinite(y) or error > absolute + relative*abs(y):
                raise ValueError(f"Independent scalar mismatch field={field} component={index}")
        maxima.append(max(errors, default=0.))
    return maxima


def reference(case, full_membership, run=None):
    from analyze import read_csr
    run = RUN if run is None else run
    n, only = CASES[case]
    validate_case(run / "inputs", case, n, only)
    if only:
        raise ValueError("Use complete CSR acceptance for the singular boundary fixture")
    source = run / "inputs" / (case + ".bin")
    data = source.read_bytes()
    points = [struct.unpack_from("<fff", data, 20 + i*16) for i in range(n)]
    velocities = [struct.unpack_from("<fff", data, 20 + n*16 + i*12) for i in range(n)]
    offsets, ids = read_csr(source.with_suffix(".csr"))
    rows = [list(ids[offsets[i]:offsets[i+1]]) for i in range(n)]
    if full_membership:
        brute = members(points)
        if any(sorted(row) != expected for row, expected in zip(rows, brute)):
            raise ValueError("Independent complete membership/multiplicity differs")
    return consume(points, velocities, rows)


def recheck(record, root=ROOT, run=RUN):
    """Recompute full scalar state; retain the costly original membership audit."""
    from analyze import read_state
    if (record.get("schemaVersion") != 2 or record.get("status") != "validated"
            or not record.get("completeStatePassed") or record.get("case") not in CASES
            or record.get("oracleSourceSha256") != sha(Path(__file__))):
        raise ValueError("A current complete independent scalar receipt is required")
    case = record["case"]
    n, only = CASES[case]
    if only or record.get("components") != n*9 or record.get("fullMembershipPassed") != (n <= 4000):
        raise ValueError("Scalar acceptance is missing its required membership/state scope")
    verify_capture(root, record["evidence"])
    source = run / "inputs" / (case + ".bin")
    needed = [source, source.with_suffix(".csr"), source.with_suffix(".state"),
              inside(root, record["scalarState"]), *[inside(root, p) for p in record["actualStates"]]]
    covered = {row["path"] for row in record["evidence"]["files"]}
    if not record["actualStates"] or not {p.relative_to(root).as_posix() for p in needed}.issubset(covered):
        raise ValueError("Scalar inputs or complete actual state files are not identity-bound")
    scalar = reference(case, False, run)
    compare(read_state(inside(root, record["scalarState"])), scalar)
    compare(read_state(source.with_suffix(".state")), scalar)
    for actual in record["actualStates"]:
        compare(read_state(inside(root, actual)), scalar)
    return set(record["actualStates"])


def validate(case, actual_paths, output, full_membership):
    from analyze import read_state
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    record = {"schemaVersion": 2, **git_identity(ROOT), "case": case, "status": "started",
              "oracleSourceSha256": sha(Path(__file__)),
              "actualStates": [p.relative_to(ROOT).as_posix() for p in actual_paths]}
    write_new(output / "started.json", record)
    try:
        n, only = CASES[case]
        if only or full_membership != (n <= 4000):
            raise ValueError("Require full independent membership at <=4000 points; boundary remains CSR-only")
        source = RUN / "inputs" / (case + ".bin")
        paths = [source, source.with_suffix(".csr"), source.with_suffix(".state"), *actual_paths]
        inputs = capture(ROOT, [], [p.relative_to(ROOT).as_posix() for p in paths])
        scalar = reference(case, full_membership)
        golden_error = compare(read_state(source.with_suffix(".state")), scalar)
        actual_errors = {p.relative_to(ROOT).as_posix(): compare(read_state(p), scalar) for p in actual_paths}
        verify_capture(ROOT, inputs)
        payload = b"SUMDSTA1" + struct.pack("<I", n)
        payload += b"".join(struct.pack("<f", value) for field in scalar for value in field)
        (output / "scalar.state").write_bytes(payload)
        record.update(status="validated", completeStatePassed=True, fullMembershipPassed=full_membership,
                      goldenMaxAbs=golden_error, actualMaxAbs=actual_errors, components=n*9,
                      scalarState=(output / "scalar.state").relative_to(ROOT).as_posix(),
                      durationSeconds=time.monotonic()-started,
                      membershipMethod="independent scalar all-pairs" if full_membership else "canonical complete CSR; cross-checked by Serial ArborX and CPU grid",
                      evidence=capture(ROOT, [], [p.relative_to(ROOT).as_posix() for p in [*paths, output / "scalar.state"]]))
    except Exception as error:
        record.update(status="rejected", error=str(error), durationSeconds=time.monotonic()-started)
        write_new(output / "oracle.json", record)
        raise
    write_new(output / "oracle.json", record)
    print(json.dumps({k: record[k] for k in ("case", "components", "goldenMaxAbs", "actualMaxAbs")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("case", choices=CASES)
    parser.add_argument("--actual", type=Path, action="append", required=True,
                        help="Repeat for complete first/last states from both GPU tiles; compute each oracle once")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--full-membership", action="store_true")
    args = parser.parse_args()
    validate(args.case, [p.resolve() for p in args.actual], args.output.resolve(), args.full_membership)
