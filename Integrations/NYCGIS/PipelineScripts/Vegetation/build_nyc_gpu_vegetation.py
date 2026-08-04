#!/usr/bin/env python3
"""Build one full-city, DEM-grounded GPU vegetation payload for SUMMIT."""

from __future__ import annotations

import argparse
import array
import csv
import datetime as dt
import hashlib
import json
import math
import pathlib
import statistics
import struct
from collections import Counter
from dataclasses import dataclass
from typing import Iterable, Sequence


TREE_STRUCT = struct.Struct("<7fI")
CLUSTER_STRUCT = struct.Struct("<II6f")
TREE_STRIDE = TREE_STRUCT.size
CLUSTER_STRIDE = CLUSTER_STRUCT.size
SCHEMA = "nycgis-gpu-vegetation-v1"


@dataclass(frozen=True)
class Tree:
    stable_id: int
    easting: float
    northing: float
    altitude: float
    height: float
    crown_radius: float
    trunk_radius: float
    rotation: float
    packed_color: int
    cluster_x: int
    cluster_y: int


class TerrainGrid:
    def __init__(self, manifest_path: pathlib.Path) -> None:
        self.manifest_path = manifest_path
        self.manifest_dir = manifest_path.parent
        self.manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.tile_size = float(self.manifest["tileSizeMeters"])
        self.bounds = self.manifest["bounds"]
        self.tiles: dict[tuple[int, int], dict[str, object]] = {}
        self._load_tiles()

    def _load_tiles(self) -> None:
        for tile in self.manifest.get("tiles", []):
            if tile.get("status") != "complete":
                continue
            path = self.manifest_dir / tile["path"]
            rows = int(tile.get("rows", self.manifest["rows"]))
            cols = int(tile.get("cols", self.manifest["cols"]))
            values = array.array("f")
            with path.open("rb") as handle:
                values.fromfile(handle, rows * cols)
            self.tiles[(int(tile["xIndex"]), int(tile["yIndex"]))] = {
                "minX": float(tile["minX"]),
                "minY": float(tile["minY"]),
                "maxX": float(tile["maxX"]),
                "maxY": float(tile["maxY"]),
                "rows": rows,
                "cols": cols,
                "values": values,
            }

    def _candidate_indices(self, x: float, y: float) -> Iterable[tuple[int, int]]:
        min_x = float(self.bounds["minX"])
        min_y = float(self.bounds["minY"])
        ix = math.floor((x - min_x) / self.tile_size)
        iy = math.floor((y - min_y) / self.tile_size)
        candidates = (
            (ix, iy),
            (ix - 1, iy),
            (ix, iy - 1),
            (ix - 1, iy - 1),
            (ix + 1, iy),
            (ix, iy + 1),
        )
        yield from dict.fromkeys(candidates)

    def sample(self, x: float, y: float) -> float | None:
        tile: dict[str, object] | None = None
        for key in self._candidate_indices(x, y):
            candidate = self.tiles.get(key)
            if candidate is None:
                continue
            if (
                x >= float(candidate["minX"]) - 1e-6
                and x <= float(candidate["maxX"]) + 1e-6
                and y >= float(candidate["minY"]) - 1e-6
                and y <= float(candidate["maxY"]) + 1e-6
            ):
                tile = candidate
                break
        if tile is None:
            return None

        rows = int(tile["rows"])
        cols = int(tile["cols"])
        min_x = float(tile["minX"])
        min_y = float(tile["minY"])
        max_x = float(tile["maxX"])
        max_y = float(tile["maxY"])
        column = max(0.0, min(cols - 1.0, (x - min_x) / (max_x - min_x) * (cols - 1)))
        row = max(0.0, min(rows - 1.0, (max_y - y) / (max_y - min_y) * (rows - 1)))
        c0 = int(math.floor(column))
        r0 = int(math.floor(row))
        c1 = min(cols - 1, c0 + 1)
        r1 = min(rows - 1, r0 + 1)
        tx = column - c0
        ty = row - r0
        values = tile["values"]
        assert isinstance(values, array.array)
        heights = (
            float(values[r0 * cols + c0]),
            float(values[r0 * cols + c1]),
            float(values[r1 * cols + c0]),
            float(values[r1 * cols + c1]),
        )
        finite = [height for height in heights if math.isfinite(height)]
        if len(finite) != 4:
            return statistics.median(finite) if finite else None
        north = heights[0] * (1.0 - tx) + heights[1] * tx
        south = heights[2] * (1.0 - tx) + heights[3] * tx
        return north * (1.0 - ty) + south * ty


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=pathlib.Path)
    parser.add_argument("--terrain-manifest", required=True, type=pathlib.Path)
    parser.add_argument("--output-dir", required=True, type=pathlib.Path)
    parser.add_argument("--cluster-size", type=float, default=256.0)
    parser.add_argument("--ground-offset", type=float, default=0.12)
    parser.add_argument("--fallback-ground-height", type=float, default=0.0)
    return parser.parse_args(argv)


def first(row: dict[str, str], *names: str, default: str = "") -> str:
    lower = {key.strip().lower(): value for key, value in row.items()}
    for name in names:
        value = lower.get(name.lower())
        if value is not None and value.strip():
            return value.strip()
    return default


def fnv1a32(text: str) -> int:
    value = 2166136261
    for byte in text.encode("utf-8", errors="replace"):
        value ^= byte
        value = (value * 16777619) & 0xFFFFFFFF
    return value or 1


def stable_id(row: dict[str, str], row_index: int) -> int:
    value = first(row, "tree_id", "id", "stable_id")
    if value:
        try:
            parsed = int(float(value))
            if 0 < parsed <= 0xFFFFFFFF:
                return parsed
        except ValueError:
            return fnv1a32(value)
    return (row_index + 1) & 0xFFFFFFFF or 1


def estimate_dimensions(dbh_inches: float, seed: int) -> tuple[float, float, float]:
    dbh_metres = max(0.05, dbh_inches * 0.0254)
    sqrt_dbh = math.sqrt(dbh_metres)
    height = 4.0 + 18.0 * sqrt_dbh
    crown_radius = 1.0 + 5.8 * sqrt_dbh
    variation = (((seed >> 8) & 0xFFFF) / 65535.0 - 0.5) * 0.10
    height *= 1.0 + variation
    crown_radius *= 1.0 - variation * 0.5
    return (
        max(3.0, min(30.0, height)),
        max(1.0, min(9.0, crown_radius)),
        max(0.05, min(0.8, dbh_metres * 0.5)),
    )


def parse_color(value: str, seed: int) -> int:
    text = value.strip().lstrip("#")
    if len(text) == 6:
        try:
            rgb = int(text, 16)
            red = (rgb >> 16) & 0xFF
            green = (rgb >> 8) & 0xFF
            blue = rgb & 0xFF
            return red | (green << 8) | (blue << 16) | (0xFF << 24)
        except ValueError:
            pass
    jitter = ((seed >> 12) & 0xFF) / 255.0
    red = int(47 + jitter * 30)
    green = int(112 + jitter * 55)
    blue = int(43 + jitter * 24)
    return red | (green << 8) | (blue << 16) | (0xFF << 24)


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_trees(
    csv_path: pathlib.Path,
    terrain: TerrainGrid,
    cluster_size: float,
    ground_offset: float,
    fallback_ground_height: float,
) -> tuple[list[Tree], Counter[str]]:
    trees: list[Tree] = []
    stats: Counter[str] = Counter()
    seen_ids: set[int] = set()
    with csv_path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row_index, row in enumerate(reader):
            stats["inputRows"] += 1
            if first(row, "status", default="Alive").lower() != "alive":
                stats["skippedDead"] += 1
                continue
            try:
                easting = float(first(row, "easting", "epsg32118_easting"))
                northing = float(first(row, "northing", "epsg32118_northing"))
                dbh_inches = max(0.0, float(first(row, "tree_dbh", "dbh", default="6")))
            except (TypeError, ValueError):
                stats["skippedInvalid"] += 1
                continue
            tree_id = stable_id(row, row_index)
            if tree_id in seen_ids:
                stats["skippedDuplicate"] += 1
                continue
            seen_ids.add(tree_id)
            seed = fnv1a32(f"nyc-tree:{tree_id}")
            height, crown_radius, trunk_radius = estimate_dimensions(dbh_inches, seed)
            altitude = terrain.sample(easting, northing)
            if altitude is None:
                altitude = fallback_ground_height
                stats["fallbackGround"] += 1
            else:
                stats["demGrounded"] += 1
            altitude += ground_offset
            trees.append(
                Tree(
                    stable_id=tree_id,
                    easting=easting,
                    northing=northing,
                    altitude=altitude,
                    height=height,
                    crown_radius=crown_radius,
                    trunk_radius=trunk_radius,
                    rotation=(seed / 4294967296.0) * math.tau,
                    packed_color=parse_color(first(row, "tree_map_color"), seed),
                    cluster_x=math.floor(easting / cluster_size),
                    cluster_y=math.floor(northing / cluster_size),
                )
            )
    trees.sort(key=lambda tree: (tree.cluster_y, tree.cluster_x, tree.stable_id))
    stats["recordCount"] = len(trees)
    return trees, stats


def write_payload(
    trees: list[Tree],
    output_dir: pathlib.Path,
    cluster_size: float,
    source_path: pathlib.Path,
    terrain_manifest: pathlib.Path,
    stats: Counter[str],
) -> pathlib.Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    tree_path = output_dir / "trees.tvgpu"
    cluster_path = output_dir / "clusters.tvgpu"
    manifest_path = output_dir / "manifest.json"

    origin_easting = sum(tree.easting for tree in trees) / len(trees)
    origin_northing = sum(tree.northing for tree in trees) / len(trees)
    min_easting = min(tree.easting for tree in trees)
    min_northing = min(tree.northing for tree in trees)
    max_easting = max(tree.easting for tree in trees)
    max_northing = max(tree.northing for tree in trees)
    min_altitude = min(tree.altitude for tree in trees)
    max_altitude = max(tree.altitude + tree.height for tree in trees)

    clusters: list[dict[str, float | int]] = []
    with tree_path.open("wb") as tree_handle:
        cluster_key: tuple[int, int] | None = None
        cluster_first = 0
        cluster_min = [math.inf, math.inf, math.inf]
        cluster_max = [-math.inf, -math.inf, -math.inf]

        def finish_cluster(end_index: int) -> None:
            if cluster_key is None:
                return
            center = [(cluster_min[i] + cluster_max[i]) * 0.5 for i in range(3)]
            extents = [
                max(0.5, (cluster_max[i] - cluster_min[i]) * 0.5)
                for i in range(3)
            ]
            clusters.append(
                {
                    "first": cluster_first,
                    "count": end_index - cluster_first,
                    "centerX": center[0],
                    "centerY": center[1],
                    "centerZ": center[2],
                    "extentX": extents[0],
                    "extentY": extents[1],
                    "extentZ": extents[2],
                }
            )

        for tree_index, tree in enumerate(trees):
            key = (tree.cluster_x, tree.cluster_y)
            if cluster_key is not None and key != cluster_key:
                finish_cluster(tree_index)
                cluster_first = tree_index
                cluster_min = [math.inf, math.inf, math.inf]
                cluster_max = [-math.inf, -math.inf, -math.inf]
            cluster_key = key
            local_x = tree.easting - origin_easting
            local_z = tree.northing - origin_northing
            tree_handle.write(
                TREE_STRUCT.pack(
                    local_x,
                    tree.altitude,
                    local_z,
                    tree.height,
                    tree.crown_radius,
                    tree.trunk_radius,
                    tree.rotation,
                    tree.packed_color,
                )
            )
            cluster_min[0] = min(cluster_min[0], local_x - tree.crown_radius)
            cluster_min[1] = min(cluster_min[1], tree.altitude)
            cluster_min[2] = min(cluster_min[2], local_z - tree.crown_radius)
            cluster_max[0] = max(cluster_max[0], local_x + tree.crown_radius)
            cluster_max[1] = max(cluster_max[1], tree.altitude + tree.height)
            cluster_max[2] = max(cluster_max[2], local_z + tree.crown_radius)
        finish_cluster(len(trees))

    with cluster_path.open("wb") as cluster_handle:
        for cluster in clusters:
            cluster_handle.write(
                CLUSTER_STRUCT.pack(
                    int(cluster["first"]),
                    int(cluster["count"]),
                    float(cluster["centerX"]),
                    float(cluster["centerY"]),
                    float(cluster["centerZ"]),
                    float(cluster["extentX"]),
                    float(cluster["extentY"]),
                    float(cluster["extentZ"]),
                )
            )

    manifest = {
        "schema": SCHEMA,
        "generatedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "source": "NYC Parks NYC Tree Map trees_landscaped vector tiles",
        "horizontalCrs": "EPSG:32118",
        "verticalDatum": "NAVD88 / Geoid 12B",
        "recordCount": len(trees),
        "recordStrideBytes": TREE_STRIDE,
        "clusterCount": len(clusters),
        "clusterStrideBytes": CLUSTER_STRIDE,
        "clusterSizeMeters": cluster_size,
        "originEasting": origin_easting,
        "originNorthing": origin_northing,
        "treeFile": tree_path.name,
        "clusterFile": cluster_path.name,
        "bounds": {
            "minEasting": min_easting,
            "minNorthing": min_northing,
            "maxEasting": max_easting,
            "maxNorthing": max_northing,
            "minAltitudeMeters": min_altitude,
            "maxAltitudeMeters": max_altitude,
        },
        "statistics": dict(sorted(stats.items())),
        "input": {
            "file": str(source_path),
            "sha256": sha256_file(source_path),
        },
        "terrain": {
            "manifest": str(terrain_manifest),
            "sha256": sha256_file(terrain_manifest),
        },
        "payloads": {
            "trees": {
                "bytes": tree_path.stat().st_size,
                "sha256": sha256_file(tree_path),
            },
            "clusters": {
                "bytes": cluster_path.stat().st_size,
                "sha256": sha256_file(cluster_path),
            },
        },
    }
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return manifest_path


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    if not args.input.is_file():
        raise SystemExit(f"Input CSV does not exist: {args.input}")
    if not args.terrain_manifest.is_file():
        raise SystemExit(f"Terrain manifest does not exist: {args.terrain_manifest}")
    if args.cluster_size < 32.0:
        raise SystemExit("Cluster size must be at least 32 metres.")
    terrain = TerrainGrid(args.terrain_manifest)
    trees, stats = load_trees(
        args.input,
        terrain,
        args.cluster_size,
        args.ground_offset,
        args.fallback_ground_height,
    )
    if not trees:
        raise SystemExit("No live valid trees were accepted.")
    manifest_path = write_payload(
        trees,
        args.output_dir,
        args.cluster_size,
        args.input,
        args.terrain_manifest,
        stats,
    )
    print(
        f"Built {len(trees):,} DEM-grounded trees in "
        f"{json.loads(manifest_path.read_text(encoding='utf-8'))['clusterCount']:,} "
        f"GPU clusters -> {manifest_path}"
    )
    print(
        f"Grounding: DEM={stats['demGrounded']:,}, "
        f"fallback={stats['fallbackGround']:,}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
