#!/usr/bin/env python3
"""Build an Atlas XYZ pyramid from a georeferenced, one-pixel-per-block image."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import sys
import uuid
from pathlib import Path

from PIL import Image


TILE_SIZE = 256


def tile_path(root: Path, zoom: int, x: int, y: int) -> Path:
    return root / str(zoom) / str(y) / f"{x}.png"


def save_png(image: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG", optimize=False, compress_level=4)


def source_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def build_maximum_zoom(
    source: Image.Image,
    root: Path,
    max_zoom: int,
    origin_offset: int,
    min_x: int,
    min_z: int,
) -> set[tuple[int, int]]:
    max_x = min_x + source.width
    max_z = min_z + source.height
    min_tile_x = math.floor(min_x / TILE_SIZE)
    max_tile_x = math.floor((max_x - 1) / TILE_SIZE)
    min_tile_z = math.floor(min_z / TILE_SIZE)
    max_tile_z = math.floor((max_z - 1) / TILE_SIZE)
    written: set[tuple[int, int]] = set()

    for native_z in range(min_tile_z, max_tile_z + 1):
        for native_x in range(min_tile_x, max_tile_x + 1):
            world_left = native_x * TILE_SIZE
            world_top = native_z * TILE_SIZE
            intersect_left = max(world_left, min_x)
            intersect_top = max(world_top, min_z)
            intersect_right = min(world_left + TILE_SIZE, max_x)
            intersect_bottom = min(world_top + TILE_SIZE, max_z)
            if intersect_left >= intersect_right or intersect_top >= intersect_bottom:
                continue

            crop = source.crop((
                intersect_left - min_x,
                intersect_top - min_z,
                intersect_right - min_x,
                intersect_bottom - min_z,
            )).convert("RGBA")
            tile = Image.new("RGBA", (TILE_SIZE, TILE_SIZE), (0, 0, 0, 0))
            tile.alpha_composite(crop, (intersect_left - world_left, intersect_top - world_top))
            target_x = native_x + origin_offset
            target_y = native_z + origin_offset
            coordinate_limit = 1 << max_zoom
            if not (0 <= target_x < coordinate_limit and 0 <= target_y < coordinate_limit):
                raise ValueError(f"source exceeds Atlas XYZ bounds at tile {target_x},{target_y}")
            save_png(tile, tile_path(root, max_zoom, target_x, target_y))
            written.add((target_x, target_y))
    return written


def build_parents(root: Path, max_zoom: int, children: set[tuple[int, int]]) -> int:
    count = len(children)
    for zoom in range(max_zoom - 1, -1, -1):
        parents = {(x // 2, y // 2) for x, y in children}
        for parent_x, parent_y in sorted(parents, key=lambda value: (value[1], value[0])):
            mosaic = Image.new("RGBA", (TILE_SIZE * 2, TILE_SIZE * 2), (0, 0, 0, 0))
            for child_y in range(2):
                for child_x in range(2):
                    coordinate = (parent_x * 2 + child_x, parent_y * 2 + child_y)
                    if coordinate not in children:
                        continue
                    with Image.open(tile_path(root, zoom + 1, *coordinate)) as image:
                        mosaic.alpha_composite(image.convert("RGBA"), (child_x * TILE_SIZE, child_y * TILE_SIZE))
            parent = mosaic.resize((TILE_SIZE, TILE_SIZE), Image.Resampling.BILINEAR)
            save_png(parent, tile_path(root, zoom, parent_x, parent_y))
        count += len(parents)
        children = parents
    return count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--min-x", required=True, type=int)
    parser.add_argument("--min-z", required=True, type=int)
    parser.add_argument("--max-zoom", type=int, default=10)
    parser.add_argument("--origin-offset", type=int, default=500)
    args = parser.parse_args()

    source_path = args.source.resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError(f"output already exists: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.parent / f".{output.name}.{uuid.uuid4().hex}.partial"
    temporary.mkdir()
    try:
        Image.MAX_IMAGE_PIXELS = None
        with Image.open(source_path) as source:
            source.load()
            children = build_maximum_zoom(
                source, temporary, args.max_zoom, args.origin_offset, args.min_x, args.min_z
            )
            width, height = source.size
        tile_count = build_parents(temporary, args.max_zoom, children)
        report = {
            "schemaVersion": 1,
            "source": str(source_path),
            "sourceSha256": source_sha256(source_path),
            "sourceWidth": width,
            "sourceHeight": height,
            "minX": args.min_x,
            "minZ": args.min_z,
            "maxXExclusive": args.min_x + width,
            "maxZExclusive": args.min_z + height,
            "maxZoom": args.max_zoom,
            "originOffset": args.origin_offset,
            "tileCount": tile_count,
        }
        os.replace(temporary, output)
        output.with_name(output.name + ".pyramid-report.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
        print(json.dumps(report, indent=2))
        return 0
    except BaseException:
        shutil.rmtree(temporary, ignore_errors=True)
        raise


if __name__ == "__main__":
    sys.exit(main())
