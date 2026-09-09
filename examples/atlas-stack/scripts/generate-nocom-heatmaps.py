#!/usr/bin/env python3
"""Generate Atlas-aligned Nocom World Pulse tiles from hits_grouped.sql.gz."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import math
import os
import shutil
import tempfile
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import DefaultDict, Dict, Iterable, Iterator, Tuple

from PIL import Image


TILE_SIZE = 256
VERSION = "nocom-world-pulse-v1"
SOURCE_FILE = "hits_grouped.sql.gz"
MAP_MIN_ZOOM = -10
DIMENSIONS = {
    -1: ("nether", 4, 1_344),
    0: ("overworld", 6, 8_000),
    1: ("end", 4, 1_312),
}
URL_ZOOM_OFFSETS = {"overworld": 1, "nether": 0, "end": 0}
PALETTE = ((34, 211, 238), (250, 204, 21), (239, 68, 68))
ALPHA_COVERAGE_LUT = tuple(min(255, value * 4) for value in range(256))


@dataclass(frozen=True)
class GroupedHit:
    dimension: int
    chunk_x: int
    chunk_z: int
    period_ms: int
    count: int


def floor_div(value: int, divisor: int) -> int:
    return value // divisor


def period_key(period_ms: int) -> str:
    return datetime.fromtimestamp(period_ms / 1000, timezone.utc).strftime("%Y-%m-%d")


def period_label(period_ms: int) -> str:
    start = datetime.fromtimestamp(period_ms / 1000, timezone.utc)
    return f"30 days from {start.strftime('%b %d, %Y')}"


def color_for_count(count: int) -> Tuple[int, int, int, int]:
    intensity = min(1.0, math.log10(max(1, count) + 1) / 5.0)
    if intensity <= 0.55:
        local = intensity / 0.55
        left, right = PALETTE[0], PALETTE[1]
    else:
        local = (intensity - 0.55) / 0.45
        left, right = PALETTE[1], PALETTE[2]
    rgb = tuple(round(a + (b - a) * local) for a, b in zip(left, right))
    return rgb[0], rgb[1], rgb[2], round(72 + 183 * intensity)


def iter_grouped_hits(source: Path) -> Iterator[GroupedHit]:
    in_copy = False
    previous = None
    with gzip.open(source, "rt", encoding="ascii", errors="strict", newline="") as stream:
        for line_number, raw_line in enumerate(stream, 1):
            line = raw_line.rstrip("\r\n")
            if not in_copy:
                if line.startswith("COPY public.hits_grouped "):
                    in_copy = True
                continue
            if line == r"\.":
                return
            parts = line.split("\t")
            if len(parts) != 5:
                raise ValueError(f"Unexpected grouped row at line {line_number}: {line[:120]}")
            row = GroupedHit(*(int(part) for part in parts))
            order = (row.dimension, row.chunk_x, row.chunk_z, row.period_ms)
            if previous is not None and order < previous:
                raise ValueError(
                    "hits_grouped rows are not ordered by dimension, x, z, and period; "
                    "the bounded streaming strategy cannot continue safely"
                )
            previous = order
            yield row
    raise ValueError("The hits_grouped COPY section did not terminate")


def iter_dimension_hits(source: Path, target_dimension: int) -> Iterator[GroupedHit]:
    in_copy = False
    scanned = 0
    accepted = 0
    with gzip.open(source, "rt", encoding="ascii", errors="strict", newline="") as stream:
        for line_number, raw_line in enumerate(stream, 1):
            line = raw_line.rstrip("\r\n")
            if not in_copy:
                if line.startswith("COPY public.hits_grouped "):
                    in_copy = True
                continue
            if line == r"\.":
                return
            scanned += 1
            if scanned % 50_000_000 == 0:
                print(f"Scanned {scanned:,} grouped rows; accepted {accepted:,} target rows", flush=True)
            dimension_text = line.split("\t", 1)[0]
            dimension = int(dimension_text)
            if dimension < target_dimension:
                continue
            if dimension > target_dimension:
                return
            parts = line.split("\t")
            if len(parts) != 5:
                raise ValueError(f"Unexpected grouped row at line {line_number}: {line[:120]}")
            row = GroupedHit(*(int(part) for part in parts))
            accepted += 1
            if accepted % 100_000 == 0:
                print(f"Accepted {accepted:,} target rows", flush=True)
            yield row
    raise ValueError("The hits_grouped COPY section did not terminate")


def replace_contents_in_place(path: Path, content: bytes) -> None:
    original = path.read_bytes()
    try:
        with path.open("r+b") as stream:
            stream.seek(0)
            stream.write(content)
            stream.truncate()
            stream.flush()
            os.fsync(stream.fileno())
    except BaseException:
        with path.open("r+b") as stream:
            stream.seek(0)
            stream.write(original)
            stream.truncate()
            stream.flush()
            os.fsync(stream.fileno())
        raise


def tile_position(chunk_x: int, chunk_z: int, dimension: int) -> Tuple[int, int, int, int]:
    _, _, chunk_offset = DIMENSIONS[dimension]
    shifted_x = chunk_x + chunk_offset
    shifted_z = chunk_z + chunk_offset
    tile_x = floor_div(shifted_x, TILE_SIZE)
    tile_y = floor_div(shifted_z, TILE_SIZE)
    pixel_x = shifted_x - tile_x * TILE_SIZE
    pixel_y = shifted_z - tile_y * TILE_SIZE
    return tile_x, tile_y, pixel_x, pixel_y


def tile_path(root: Path, zoom: int, tile_x: int, tile_y: int) -> Path:
    return root / str(zoom) / str(tile_y) / f"{tile_x}.png"


def write_sparse_tile(path: Path, pixels: Dict[int, int]) -> None:
    raw = bytearray(TILE_SIZE * TILE_SIZE * 4)
    for pixel_index, count in pixels.items():
        offset = pixel_index * 4
        raw[offset : offset + 4] = bytes(color_for_count(count))
    image = Image.frombytes("RGBA", (TILE_SIZE, TILE_SIZE), bytes(raw))
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG", optimize=False, compress_level=4)


def minimum_url_zoom(dimension_name: str) -> int:
    return MAP_MIN_ZOOM + URL_ZOOM_OFFSETS[dimension_name]


def resize_parent(mosaic: Image.Image) -> Image.Image:
    parent = mosaic.resize((TILE_SIZE, TILE_SIZE), Image.Resampling.BILINEAR)
    # Ordinary alpha averaging makes one-pixel highways disappear at every parent
    # boundary, which caused a visible contrast step between the old pyramid and
    # the negative-level backfill. Preserve the covered area of each 2x2 source
    # cell throughout the entire pyramid while retaining bilinear RGB interpolation.
    alpha = mosaic.getchannel("A").resize((TILE_SIZE, TILE_SIZE), Image.Resampling.BOX)
    alpha = alpha.point(ALPHA_COVERAGE_LUT)
    parent.putalpha(alpha)
    return parent


def build_parent_levels(
    source_root: Path,
    output_root: Path,
    start_zoom: int,
    min_zoom: int,
) -> Dict[str, int]:
    current = {}
    start_root = source_root / str(start_zoom)
    if not start_root.exists():
        return {}
    for path in start_root.glob("*/*.png"):
        current[(int(path.stem), int(path.parent.name))] = path

    counts = {str(start_zoom): len(current)}
    for zoom in range(start_zoom - 1, min_zoom - 1, -1):
        parents: DefaultDict[Tuple[int, int], Dict[Tuple[int, int], Path]] = defaultdict(dict)
        for (child_x, child_y), child_path in current.items():
            parent_x = floor_div(child_x, 2)
            parent_y = floor_div(child_y, 2)
            parents[(parent_x, parent_y)][(child_x - parent_x * 2, child_y - parent_y * 2)] = child_path

        next_level = {}
        for (parent_x, parent_y), children in parents.items():
            mosaic = Image.new("RGBA", (TILE_SIZE * 2, TILE_SIZE * 2), (0, 0, 0, 0))
            for (local_x, local_y), child_path in children.items():
                with Image.open(child_path) as child:
                    mosaic.paste(child, (local_x * TILE_SIZE, local_y * TILE_SIZE))
            parent = resize_parent(mosaic)
            target = tile_path(output_root, zoom, parent_x, parent_y)
            target.parent.mkdir(parents=True, exist_ok=True)
            parent.save(target, format="PNG", optimize=False, compress_level=4)
            next_level[(parent_x, parent_y)] = target
        current = next_level
        counts[str(zoom)] = len(current)
    return counts


def build_parent_pyramid(root: Path, max_zoom: int, min_zoom: int) -> Dict[str, int]:
    return build_parent_levels(root, root, max_zoom, min_zoom)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def update_stats(stats: dict, row: GroupedHit) -> None:
    key = (row.period_ms, row.dimension)
    item = stats.setdefault(
        key,
        {
            "rows": 0,
            "observations": 0,
            "minChunkX": row.chunk_x,
            "maxChunkX": row.chunk_x,
            "minChunkZ": row.chunk_z,
            "maxChunkZ": row.chunk_z,
        },
    )
    item["rows"] += 1
    item["observations"] += row.count
    item["minChunkX"] = min(item["minChunkX"], row.chunk_x)
    item["maxChunkX"] = max(item["maxChunkX"], row.chunk_x)
    item["minChunkZ"] = min(item["minChunkZ"], row.chunk_z)
    item["maxChunkZ"] = max(item["maxChunkZ"], row.chunk_z)


def flush_x_strip(
    output: Path,
    dimension: int,
    tile_x: int,
    monthly: Dict[Tuple[int, int], Dict[int, int]],
    totals: Dict[int, Dict[int, int]],
) -> None:
    dimension_name, max_zoom, _ = DIMENSIONS[dimension]
    for (period_ms, tile_y), pixels in monthly.items():
        target = tile_path(
            output / "monthly" / period_key(period_ms) / dimension_name,
            max_zoom,
            tile_x,
            tile_y,
        )
        write_sparse_tile(target, pixels)
    for tile_y, pixels in totals.items():
        target = tile_path(output / "total" / dimension_name, max_zoom, tile_x, tile_y)
        write_sparse_tile(target, pixels)


def generate(source: Path, output: Path) -> dict:
    if output.exists():
        raise FileExistsError(f"Output already exists: {output}")
    partial = output.with_name(f".{output.name}.partial")
    if partial.exists():
        raise FileExistsError(f"Partial output already exists: {partial}")
    partial.mkdir(parents=True)

    stats = {}
    row_count = 0
    current_dimension = None
    current_tile_x = None
    monthly: DefaultDict[Tuple[int, int], Dict[int, int]] = defaultdict(dict)
    totals: DefaultDict[int, Dict[int, int]] = defaultdict(dict)

    try:
        for row in iter_grouped_hits(source):
            if row.dimension not in DIMENSIONS:
                continue
            tile_x, tile_y, pixel_x, pixel_y = tile_position(row.chunk_x, row.chunk_z, row.dimension)
            if current_dimension is not None and (row.dimension, tile_x) != (current_dimension, current_tile_x):
                flush_x_strip(partial, current_dimension, current_tile_x, monthly, totals)
                monthly.clear()
                totals.clear()
            current_dimension, current_tile_x = row.dimension, tile_x
            pixel_index = pixel_y * TILE_SIZE + pixel_x
            monthly[(row.period_ms, tile_y)][pixel_index] = row.count
            totals[tile_y][pixel_index] = totals[tile_y].get(pixel_index, 0) + row.count
            update_stats(stats, row)
            row_count += 1
            if row_count % 5_000_000 == 0:
                print(f"Parsed {row_count:,} grouped rows")

        if current_dimension is not None:
            flush_x_strip(partial, current_dimension, current_tile_x, monthly, totals)

        frames = []
        for (period_ms, dimension), item in sorted(stats.items()):
            dimension_name, max_zoom, _ = DIMENSIONS[dimension]
            frame_root = partial / "monthly" / period_key(period_ms) / dimension_name
            min_zoom = minimum_url_zoom(dimension_name)
            tiles_per_zoom = build_parent_pyramid(frame_root, max_zoom, min_zoom)
            frames.append(
                {
                    "periodStartUtc": datetime.fromtimestamp(period_ms / 1000, timezone.utc).isoformat().replace("+00:00", "Z"),
                    "key": period_key(period_ms),
                    "label": period_label(period_ms),
                    "dimension": dimension_name,
                    "minNativeUrlZoom": min_zoom,
                    "maxNativeUrlZoom": max_zoom,
                    "urlTemplate": f"monthly/{period_key(period_ms)}/{dimension_name}/{{z}}/{{y}}/{{x}}.png",
                    "tilesPerZoom": tiles_per_zoom,
                    **item,
                }
            )

        totals_manifest = []
        for dimension, (dimension_name, max_zoom, _) in DIMENSIONS.items():
            total_root = partial / "total" / dimension_name
            min_zoom = minimum_url_zoom(dimension_name)
            tiles_per_zoom = build_parent_pyramid(total_root, max_zoom, min_zoom)
            if tiles_per_zoom:
                totals_manifest.append(
                    {
                        "dimension": dimension_name,
                        "minNativeUrlZoom": min_zoom,
                        "maxNativeUrlZoom": max_zoom,
                        "urlTemplate": f"total/{dimension_name}/{{z}}/{{y}}/{{x}}.png",
                        "tilesPerZoom": tiles_per_zoom,
                    }
                )

        manifest = {
            "version": VERSION,
            "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "source": {
                "file": SOURCE_FILE,
                "bytes": source.stat().st_size,
                "sha256": sha256_file(source),
                "schema": "COPY public.hits_grouped (dimension, x, z, month, cnt)",
                "serverScope": "The official grouped release has no server_id column; it is used as published by the Nocom authors.",
            },
            "metric": "Positive loaded-chunk observations",
            "period": "Fixed 30-day source buckets, not calendar months",
            "coordinateUnit": "One source x,z cell is one 16x16-block Minecraft chunk",
            "rowCount": row_count,
            "legend": [
                {"minimum": 1, "label": "1 observation", "color": "#22d3ee"},
                {"minimum": 100, "label": "100", "color": "#89d87b"},
                {"minimum": 10_000, "label": "10k", "color": "#facc15"},
                {"minimum": 100_000, "label": "100k+", "color": "#ef4444"},
            ],
            "caveats": [
                "A hit means a probed chunk was loaded by at least one player; it is not an exact player position.",
                "Density reflects scanner priorities and repeated checks as well as player activity.",
                "No observation does not prove that an area was empty.",
                "The historical record ends when Nocom was patched on July 15, 2021.",
            ],
            "frames": frames,
            "totals": totals_manifest,
        }
        (partial / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        partial.rename(output)
        return manifest
    except BaseException:
        shutil.rmtree(partial, ignore_errors=True)
        raise


def extend_dimension(source: Path, existing: Path, dimension: int) -> dict:
    dimension_name, max_zoom, _ = DIMENSIONS[dimension]
    manifest_path = existing / "manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"Existing World Pulse manifest was not found: {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if any(frame.get("dimension") == dimension_name for frame in manifest.get("frames", [])):
        raise ValueError(f"The existing manifest already contains {dimension_name} frames")

    staging = existing.with_name(f".{existing.name}.{dimension_name}.partial")
    if staging.exists():
        raise FileExistsError(f"Extension staging directory already exists: {staging}")
    staging.mkdir(parents=True)

    stats = {}
    row_count = 0
    current_tile_x = None
    monthly: DefaultDict[Tuple[int, int], Dict[int, int]] = defaultdict(dict)
    totals: DefaultDict[int, Dict[int, int]] = defaultdict(dict)
    moved = []
    try:
        for row in iter_dimension_hits(source, dimension):
            tile_x, tile_y, pixel_x, pixel_y = tile_position(row.chunk_x, row.chunk_z, dimension)
            if current_tile_x is not None and tile_x != current_tile_x:
                flush_x_strip(staging, dimension, current_tile_x, monthly, totals)
                monthly.clear()
                totals.clear()
            current_tile_x = tile_x
            pixel_index = pixel_y * TILE_SIZE + pixel_x
            monthly[(row.period_ms, tile_y)][pixel_index] = row.count
            totals[tile_y][pixel_index] = totals[tile_y].get(pixel_index, 0) + row.count
            update_stats(stats, row)
            row_count += 1

        if current_tile_x is not None:
            flush_x_strip(staging, dimension, current_tile_x, monthly, totals)
        if row_count == 0:
            raise ValueError(f"No {dimension_name} rows were found in {source}")

        frames = []
        for (period_ms, _), item in sorted(stats.items()):
            frame_root = staging / "monthly" / period_key(period_ms) / dimension_name
            min_zoom = minimum_url_zoom(dimension_name)
            frames.append(
                {
                    "periodStartUtc": datetime.fromtimestamp(period_ms / 1000, timezone.utc).isoformat().replace("+00:00", "Z"),
                    "key": period_key(period_ms),
                    "label": period_label(period_ms),
                    "dimension": dimension_name,
                    "minNativeUrlZoom": min_zoom,
                    "maxNativeUrlZoom": max_zoom,
                    "urlTemplate": f"monthly/{period_key(period_ms)}/{dimension_name}/{{z}}/{{y}}/{{x}}.png",
                    "tilesPerZoom": build_parent_pyramid(frame_root, max_zoom, min_zoom),
                    **item,
                }
            )

        total_root = staging / "total" / dimension_name
        total = {
            "dimension": dimension_name,
            "minNativeUrlZoom": minimum_url_zoom(dimension_name),
            "maxNativeUrlZoom": max_zoom,
            "urlTemplate": f"total/{dimension_name}/{{z}}/{{y}}/{{x}}.png",
            "tilesPerZoom": build_parent_pyramid(total_root, max_zoom, minimum_url_zoom(dimension_name)),
        }

        for frame in frames:
            source_root = staging / "monthly" / frame["key"] / dimension_name
            destination = existing / "monthly" / frame["key"] / dimension_name
            if destination.exists():
                raise FileExistsError(f"End extension destination already exists: {destination}")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(source_root), str(destination))
            moved.append((destination, source_root))
        destination_total = existing / "total" / dimension_name
        if destination_total.exists():
            raise FileExistsError(f"End extension destination already exists: {destination_total}")
        shutil.move(str(total_root), str(destination_total))
        moved.append((destination_total, total_root))

        manifest["frames"] = sorted(
            [*manifest.get("frames", []), *frames],
            key=lambda value: (value["periodStartUtc"], value["dimension"]),
        )
        manifest["totals"] = [*manifest.get("totals", []), total]
        manifest["rowCount"] = int(manifest.get("rowCount", 0)) + row_count
        manifest["extendedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        temporary_manifest = existing / ".manifest.json.partial"
        temporary_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        replace_contents_in_place(manifest_path, temporary_manifest.read_bytes())
        temporary_manifest.unlink()
        shutil.rmtree(staging, ignore_errors=True)
        return manifest
    except BaseException:
        for destination, source_root in reversed(moved):
            if destination.exists():
                source_root.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(destination), str(source_root))
        shutil.rmtree(staging, ignore_errors=True)
        raise


def extend_overview(existing: Path) -> dict:
    """Add only the missing far-zoom levels to an existing v1 product.

    Detailed and zoom-0 tiles are treated as immutable inputs. Every negative
    level is built in a sibling staging tree, moved into place only after all
    layer pyramids succeed, and advertised by replacing the manifest last.
    """
    manifest_path = existing / "manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"Existing World Pulse manifest was not found: {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    layers = [*manifest.get("frames", []), *manifest.get("totals", [])]
    if not layers:
        raise ValueError("The World Pulse manifest has no tile layers")

    layer_roots = []
    suffix = "/{z}/{y}/{x}.png"
    for layer in layers:
        dimension_name = layer.get("dimension")
        if dimension_name not in URL_ZOOM_OFFSETS:
            raise ValueError(f"Unsupported manifest dimension: {dimension_name!r}")
        template = str(layer.get("urlTemplate", "")).replace("\\", "/")
        if not template.endswith(suffix):
            raise ValueError(f"Unexpected World Pulse URL template: {template!r}")
        relative_text = template[: -len(suffix)]
        relative = Path(*relative_text.split("/"))
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"Unsafe World Pulse layer path: {relative_text!r}")
        root = existing / relative
        if not (root / "0").is_dir():
            raise FileNotFoundError(f"Zoom 0 input was not found: {root / '0'}")
        min_zoom = minimum_url_zoom(dimension_name)
        for zoom in range(-1, min_zoom - 1, -1):
            if (root / str(zoom)).exists():
                raise FileExistsError(f"Overview destination already exists: {root / str(zoom)}")
        layer_roots.append((layer, relative, root, min_zoom))

    staging = existing.with_name(f".{existing.name}.overview.partial")
    if staging.exists():
        raise FileExistsError(f"Overview staging directory already exists: {staging}")
    staging.mkdir(parents=True)
    moved = []
    try:
        generated = []
        for layer, relative, root, min_zoom in layer_roots:
            staging_root = staging / relative
            counts = build_parent_levels(root, staging_root, 0, min_zoom)
            expected_zooms = {str(zoom) for zoom in range(0, min_zoom - 1, -1)}
            if set(counts) != expected_zooms or any(count <= 0 for count in counts.values()):
                raise ValueError(f"Incomplete overview pyramid for {relative}: {counts}")
            generated.append((layer, relative, root, min_zoom, counts))

        for _, relative, root, min_zoom, _ in generated:
            for zoom in range(-1, min_zoom - 1, -1):
                source_zoom = staging / relative / str(zoom)
                destination_zoom = root / str(zoom)
                shutil.move(str(source_zoom), str(destination_zoom))
                moved.append(destination_zoom)

        for layer, _, _, min_zoom, counts in generated:
            layer["minNativeUrlZoom"] = min_zoom
            tiles_per_zoom = layer.setdefault("tilesPerZoom", {})
            for zoom in range(-1, min_zoom - 1, -1):
                tiles_per_zoom[str(zoom)] = counts[str(zoom)]
        manifest["overviewExtendedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        manifest["minMapZoom"] = MAP_MIN_ZOOM
        temporary_manifest = existing / ".manifest.json.overview.partial"
        temporary_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        replace_contents_in_place(manifest_path, temporary_manifest.read_bytes())
        temporary_manifest.unlink()
        shutil.rmtree(staging, ignore_errors=True)
        return manifest
    except BaseException:
        for destination in reversed(moved):
            if destination.exists():
                shutil.rmtree(destination)
        shutil.rmtree(staging, ignore_errors=True)
        raise


def rebuild_parent_pyramids(existing: Path, publish: bool = False) -> dict:
    """Rebuild every non-native level into review staging, optionally publishing it.

    Native maximum-zoom tiles are immutable inputs. Generation is resumable and
    never changes live tiles. Publication requires a separate explicit action: it
    swaps reviewed parent directories through a backup and writes the manifest
    last. A publication failure restores the old directories and keeps staging.
    """
    manifest_path = existing / "manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"Existing World Pulse manifest was not found: {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    layers = [*manifest.get("frames", []), *manifest.get("totals", [])]
    if not layers:
        raise ValueError("The World Pulse manifest has no tile layers")

    suffix = "/{z}/{y}/{x}.png"
    prepared = []
    seen_relatives = set()
    for layer in layers:
        dimension_name = layer.get("dimension")
        if dimension_name not in URL_ZOOM_OFFSETS:
            raise ValueError(f"Unsupported manifest dimension: {dimension_name!r}")
        template = str(layer.get("urlTemplate", "")).replace("\\", "/")
        if not template.endswith(suffix):
            raise ValueError(f"Unexpected World Pulse URL template: {template!r}")
        relative_text = template[: -len(suffix)]
        relative = Path(*relative_text.split("/"))
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"Unsafe World Pulse layer path: {relative_text!r}")
        if relative in seen_relatives:
            raise ValueError(f"Duplicate World Pulse layer path: {relative_text!r}")
        seen_relatives.add(relative)

        root = existing / relative
        max_zoom = int(layer.get("maxNativeUrlZoom"))
        min_zoom = minimum_url_zoom(dimension_name)
        native_root = root / str(max_zoom)
        if not native_root.is_dir():
            raise FileNotFoundError(f"Native input was not found: {native_root}")
        native_count = sum(1 for _ in native_root.glob("*/*.png"))
        advertised_native_count = int(layer.get("tilesPerZoom", {}).get(str(max_zoom), -1))
        if native_count <= 0 or native_count != advertised_native_count:
            raise ValueError(
                f"Native tile count mismatch for {relative}: disk={native_count}, "
                f"manifest={advertised_native_count}"
            )
        for zoom in range(max_zoom - 1, min_zoom - 1, -1):
            if not (root / str(zoom)).is_dir():
                raise FileNotFoundError(f"Existing parent level was not found: {root / str(zoom)}")
        prepared.append((layer, relative, root, max_zoom, min_zoom))

    staging = existing.with_name(f".{existing.name}.pyramid.partial")
    backup = existing.with_name(f".{existing.name}.pyramid.backup")
    if backup.exists():
        raise FileExistsError(f"Pyramid backup directory already exists: {backup}")
    staging.mkdir(parents=True, exist_ok=True)

    swapped = []
    try:
        generated = []
        for index, (layer, relative, root, max_zoom, min_zoom) in enumerate(prepared, 1):
            staging_root = staging / relative
            expected_zooms = {str(zoom) for zoom in range(max_zoom, min_zoom - 1, -1)}
            advertised = layer["tilesPerZoom"]
            staged_complete = staging_root.is_dir() and all(
                (staging_root / str(zoom)).is_dir()
                and sum(1 for _ in (staging_root / str(zoom)).glob("*/*.png")) == int(advertised[str(zoom)])
                for zoom in range(max_zoom - 1, min_zoom - 1, -1)
            )
            if staged_complete:
                print(f"Keeping complete staged pyramid {index}/{len(prepared)}: {relative}", flush=True)
                counts = {str(zoom): int(advertised[str(zoom)]) for zoom in range(max_zoom, min_zoom - 1, -1)}
            else:
                if staging_root.exists():
                    shutil.rmtree(staging_root)
                print(f"Rebuilding parent pyramid {index}/{len(prepared)}: {relative}", flush=True)
                counts = build_parent_levels(root, staging_root, max_zoom, min_zoom)
            if set(counts) != expected_zooms or any(count <= 0 for count in counts.values()):
                raise ValueError(f"Incomplete parent pyramid for {relative}: {counts}")
            if counts[str(max_zoom)] != int(layer["tilesPerZoom"][str(max_zoom)]):
                raise ValueError(f"Native tile count changed while rebuilding {relative}")
            generated.append((layer, relative, root, max_zoom, min_zoom, counts))

        for layer, _, _, _, min_zoom, counts in generated:
            layer["minNativeUrlZoom"] = min_zoom
            layer["tilesPerZoom"] = counts
        manifest["minMapZoom"] = MAP_MIN_ZOOM
        manifest["pyramidStagedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        manifest["alphaDownsampling"] = "Coverage-preserving at every generated parent level"
        (staging / "manifest.review.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        if not publish:
            print(f"Review staging is complete at {staging}; live tiles were not changed", flush=True)
            return manifest

        backup.mkdir(parents=True)
        for _, relative, root, max_zoom, min_zoom, _ in generated:
            for zoom in range(max_zoom - 1, min_zoom - 1, -1):
                destination = root / str(zoom)
                backup_destination = backup / relative / str(zoom)
                staged_source = staging / relative / str(zoom)
                backup_destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(destination), str(backup_destination))
                swapped.append((destination, backup_destination, staged_source))
                shutil.move(str(staged_source), str(destination))

        manifest["pyramidRebuiltAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        temporary_manifest = existing / ".manifest.json.pyramid.partial"
        temporary_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        replace_contents_in_place(manifest_path, temporary_manifest.read_bytes())
        temporary_manifest.unlink(missing_ok=True)
    except BaseException:
        for destination, backup_destination, staged_source in reversed(swapped):
            if destination.exists():
                staged_source.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(destination), str(staged_source))
            if backup_destination.exists():
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(backup_destination), str(destination))
        shutil.rmtree(backup, ignore_errors=True)
        raise

    shutil.rmtree(staging, ignore_errors=True)
    shutil.rmtree(backup, ignore_errors=True)
    return manifest


def run_self_test() -> None:
    period = 1_593_561_600_000
    with tempfile.TemporaryDirectory(prefix="nocom-heatmap-test-") as temp_value:
        temp = Path(temp_value)
        source = temp / SOURCE_FILE
        with gzip.open(source, "wt", encoding="ascii", newline="\n") as stream:
            stream.write("COPY public.hits_grouped (dimension, x, z, month, cnt) FROM stdin;\n")
            stream.write(f"-1\t0\t0\t{period}\t10\n")
            stream.write(f"0\t-1\t-1\t{period}\t1\n")
            stream.write(f"0\t0\t0\t{period}\t100000\n")
            stream.write(f"1\t0\t0\t{period}\t100\n")
            stream.write("\\.\n")
        output = temp / "v1"
        manifest = generate(source, output)
        overworld = output / "monthly" / "2020-07-01" / "overworld" / "6" / "31" / "31.png"
        nether = output / "monthly" / "2020-07-01" / "nether" / "4" / "5" / "5.png"
        end = output / "monthly" / "2020-07-01" / "end" / "4" / "5" / "5.png"
        assert overworld.exists() and nether.exists() and end.exists()
        assert (output / "monthly" / "2020-07-01" / "end" / "3" / "2" / "2.png").exists()
        with Image.open(overworld) as image:
            assert image.size == (256, 256)
            assert image.getpixel((64, 64))[3] > image.getpixel((63, 63))[3] > 0
        assert (output / "monthly" / "2020-07-01" / "overworld" / "0" / "0" / "0.png").exists()
        assert (output / "monthly" / "2020-07-01" / "overworld" / "-9" / "0" / "0.png").exists()
        assert (output / "monthly" / "2020-07-01" / "nether" / "-10" / "0" / "0.png").exists()
        with Image.open(output / "monthly" / "2020-07-01" / "overworld" / "-9" / "0" / "0.png") as image:
            assert image.getchannel("A").getextrema()[1] > 0
        assert next(frame for frame in manifest["frames"] if frame["dimension"] == "overworld")["minNativeUrlZoom"] == -9
        assert manifest["rowCount"] == 4

        extension_source = temp / "extension" / SOURCE_FILE
        extension_source.parent.mkdir()
        with gzip.open(extension_source, "wt", encoding="ascii", newline="\n") as stream:
            stream.write("COPY public.hits_grouped (dimension, x, z, month, cnt) FROM stdin;\n")
            stream.write(f"-1\t0\t0\t{period}\t10\n")
            stream.write(f"0\t0\t0\t{period}\t100000\n")
            stream.write(f"1\t1\t1\t{period}\t250\n")
            stream.write("\\.\n")
        extension_output = temp / "extension-v1"
        shutil.copytree(output, extension_output)
        for path in extension_output.glob("monthly/*/end"):
            shutil.rmtree(path)
        shutil.rmtree(extension_output / "total" / "end")
        extension_manifest_path = extension_output / "manifest.json"
        extension_manifest = json.loads(extension_manifest_path.read_text(encoding="utf-8"))
        extension_manifest["frames"] = [frame for frame in extension_manifest["frames"] if frame["dimension"] != "end"]
        extension_manifest["totals"] = [total for total in extension_manifest["totals"] if total["dimension"] != "end"]
        extension_manifest["rowCount"] = 3
        extension_manifest_path.write_text(json.dumps(extension_manifest, indent=2), encoding="utf-8")

        extended = extend_dimension(extension_source, extension_output, 1)
        assert extended["rowCount"] == 4
        assert len([frame for frame in extended["frames"] if frame["dimension"] == "end"]) == 1
        assert (extension_output / "monthly" / "2020-07-01" / "end" / "4" / "5" / "5.png").exists()
        assert not (extension_output / ".manifest.json.partial").exists()

        legacy_output = temp / "legacy-v1"
        shutil.copytree(output, legacy_output)
        legacy_manifest_path = legacy_output / "manifest.json"
        legacy_manifest = json.loads(legacy_manifest_path.read_text(encoding="utf-8"))
        for layer in [*legacy_manifest["frames"], *legacy_manifest["totals"]]:
            root = legacy_output / layer["urlTemplate"].removesuffix("/{z}/{y}/{x}.png")
            layer.pop("minNativeUrlZoom", None)
            for zoom in range(-1, -11, -1):
                shutil.rmtree(root / str(zoom), ignore_errors=True)
                layer["tilesPerZoom"].pop(str(zoom), None)
        legacy_manifest_path.write_text(json.dumps(legacy_manifest, indent=2), encoding="utf-8")
        overview_manifest = extend_overview(legacy_output)
        assert overview_manifest["minMapZoom"] == MAP_MIN_ZOOM
        assert (legacy_output / "total" / "overworld" / "-9" / "0" / "0.png").exists()
        assert (legacy_output / "total" / "end" / "-10" / "0" / "0.png").exists()
        assert not (legacy_output.parent / f".{legacy_output.name}.overview.partial").exists()

        rebuilt_tile = legacy_output / "monthly" / "2020-07-01" / "overworld" / "5" / "15" / "15.png"
        with Image.open(rebuilt_tile) as image:
            expected_alpha = image.getchannel("A").getextrema()[1]
            faded = image.copy()
        faded.putalpha(Image.new("L", faded.size, 0))
        faded.save(rebuilt_tile, format="PNG", optimize=False, compress_level=4)
        with Image.open(rebuilt_tile) as image:
            assert image.getchannel("A").getextrema()[1] == 0
        rebuilt_manifest = rebuild_parent_pyramids(legacy_output)
        with Image.open(rebuilt_tile) as image:
            assert image.getchannel("A").getextrema()[1] == 0
        staged_tile = legacy_output.parent / f".{legacy_output.name}.pyramid.partial" / "monthly" / "2020-07-01" / "overworld" / "5" / "15" / "15.png"
        with Image.open(staged_tile) as image:
            assert image.getchannel("A").getextrema()[1] == expected_alpha > 0
        assert rebuilt_manifest["alphaDownsampling"].startswith("Coverage-preserving")
        assert (legacy_output.parent / f".{legacy_output.name}.pyramid.partial" / "manifest.review.json").exists()
        published_manifest = rebuild_parent_pyramids(legacy_output, publish=True)
        with Image.open(rebuilt_tile) as image:
            assert image.getchannel("A").getextrema()[1] == expected_alpha > 0
        assert "pyramidRebuiltAtUtc" in published_manifest
        assert not (legacy_output.parent / f".{legacy_output.name}.pyramid.partial").exists()
        assert not (legacy_output.parent / f".{legacy_output.name}.pyramid.backup").exists()
    print("Nocom heatmap self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, help="Directory containing hits_grouped.sql.gz")
    parser.add_argument("--output", type=Path, help="New output directory for the immutable v1 tiles")
    parser.add_argument("--extend-dimension", type=int, choices=sorted(DIMENSIONS), help="Add one missing dimension to an existing output and publish its manifest last")
    parser.add_argument("--extend-overview", action="store_true", help="Add visibility-preserving levels through map zoom -10 to an existing v1 output")
    parser.add_argument("--rebuild-pyramid", action="store_true", help="Build or resume review-only parent staging; never changes live tiles")
    parser.add_argument("--publish-staged-pyramid", action="store_true", help="Explicitly publish the complete reviewed pyramid staging tree")
    parser.add_argument("--self-test", action="store_true", help="Run a small synthetic coordinate and pyramid test")
    args = parser.parse_args()
    if args.self_test:
        run_self_test()
        return 0
    if args.output is None:
        parser.error("--output is required unless --self-test is used")
    selected_actions = int(args.extend_overview) + int(args.rebuild_pyramid) + int(args.publish_staged_pyramid) + int(args.extend_dimension is not None)
    if selected_actions > 1:
        parser.error("--extend-overview, --rebuild-pyramid, --publish-staged-pyramid, and --extend-dimension cannot be combined")
    if args.publish_staged_pyramid:
        manifest = rebuild_parent_pyramids(args.output, publish=True)
        print(f"Published {len(manifest['frames']) + len(manifest['totals'])} reviewed parent pyramids in {args.output}")
    elif args.rebuild_pyramid:
        manifest = rebuild_parent_pyramids(args.output, publish=False)
        print(f"Staged {len(manifest['frames']) + len(manifest['totals'])} parent pyramids for review; live tiles were not changed")
    elif args.extend_overview:
        manifest = extend_overview(args.output)
        print(f"Extended {args.output} through map zoom {manifest['minMapZoom']}")
    elif args.source_root is None:
        parser.error("--source-root is required when generating or extending a dimension")
    elif args.extend_dimension is not None:
        manifest = extend_dimension(args.source_root / SOURCE_FILE, args.output, args.extend_dimension)
        print(f"Extended {args.output} to {manifest['rowCount']:,} grouped rows")
    else:
        manifest = generate(args.source_root / SOURCE_FILE, args.output)
        print(f"Generated {manifest['rowCount']:,} rows into {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
