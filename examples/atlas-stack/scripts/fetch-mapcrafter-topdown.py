#!/usr/bin/env python3
"""Recover a georeferenced one-pixel-per-block image from a Mapcrafter top-down map."""

from __future__ import annotations

import argparse
import concurrent.futures
import io
import math
import sys
import urllib.error
import urllib.request
from pathlib import Path

from PIL import Image


def quad_path(x: int, y: int, zoom: int) -> str:
    digits = []
    for bit in range(zoom - 1, -1, -1):
        digits.append(str(((x >> bit) & 1) + 2 * ((y >> bit) & 1) + 1))
    return "/".join(digits)


def fetch_tile(base_url: str, x: int, y: int, zoom: int) -> tuple[int, int, bytes | None]:
    url = f"{base_url.rstrip('/')}/{quad_path(x, y, zoom)}.png"
    request = urllib.request.Request(url, headers={"User-Agent": "2b2tAtlas historical-map preservation/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=45) as response:
            return x, y, response.read()
    except urllib.error.HTTPError as exception:
        if exception.code == 404:
            return x, y, None
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--min-x", required=True, type=int)
    parser.add_argument("--min-z", required=True, type=int)
    parser.add_argument("--max-x-exclusive", required=True, type=int)
    parser.add_argument("--max-z-exclusive", required=True, type=int)
    parser.add_argument("--max-zoom", required=True, type=int)
    parser.add_argument("--tile-size", required=True, type=int)
    parser.add_argument("--texture-size", required=True, type=int)
    parser.add_argument("--tile-offset-x", required=True, type=int)
    parser.add_argument("--tile-offset-z", required=True, type=int)
    parser.add_argument("--workers", type=int, default=16)
    args = parser.parse_args()

    if args.output.exists():
        raise FileExistsError(f"output already exists: {args.output}")
    if args.max_x_exclusive <= args.min_x or args.max_z_exclusive <= args.min_z:
        raise ValueError("invalid Minecraft bounds")

    plane_pixels = args.tile_size * (1 << args.max_zoom)
    center = plane_pixels // 2
    scale = args.texture_size
    pixel_left = center + (args.min_x - args.tile_offset_x * 16) * scale
    pixel_top = center + (args.min_z - args.tile_offset_z * 16) * scale
    pixel_right = center + (args.max_x_exclusive - args.tile_offset_x * 16) * scale
    pixel_bottom = center + (args.max_z_exclusive - args.tile_offset_z * 16) * scale
    if min(pixel_left, pixel_top) < 0 or max(pixel_right, pixel_bottom) > plane_pixels:
        raise ValueError("requested Minecraft bounds exceed the Mapcrafter pixel plane")

    min_tile_x = pixel_left // args.tile_size
    max_tile_x = (pixel_right - 1) // args.tile_size
    min_tile_y = pixel_top // args.tile_size
    max_tile_y = (pixel_bottom - 1) // args.tile_size
    coordinates = [
        (x, y)
        for y in range(min_tile_y, max_tile_y + 1)
        for x in range(min_tile_x, max_tile_x + 1)
    ]
    canvas = Image.new("RGBA", (pixel_right - pixel_left, pixel_bottom - pixel_top), (0, 0, 0, 0))
    present = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as executor:
        futures = [
            executor.submit(fetch_tile, args.base_url, x, y, args.max_zoom)
            for x, y in coordinates
        ]
        for completed, future in enumerate(concurrent.futures.as_completed(futures), 1):
            x, y, content = future.result()
            if content is not None:
                with Image.open(io.BytesIO(content)) as tile:
                    if tile.size != (args.tile_size, args.tile_size):
                        raise ValueError(f"unexpected tile size at {x},{y}: {tile.size}")
                    canvas.alpha_composite(
                        tile.convert("RGBA"),
                        (x * args.tile_size - pixel_left, y * args.tile_size - pixel_top),
                    )
                present += 1
            if completed % 256 == 0:
                print(f"checked {completed}/{len(coordinates)} tiles; {present} present", flush=True)

    width = args.max_x_exclusive - args.min_x
    height = args.max_z_exclusive - args.min_z
    output = canvas.resize((width, height), Image.Resampling.BOX)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    output.save(args.output, format="PNG", optimize=False, compress_level=4)
    print(f"wrote {args.output}: {width}x{height}, {present}/{len(coordinates)} source tiles present")
    return 0


if __name__ == "__main__":
    sys.exit(main())
