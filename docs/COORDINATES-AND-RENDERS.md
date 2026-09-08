# Coordinates, dimensions, and render tiles

2b2tAtlas combines point locations, infrastructure geometry, and sparse WDL-derived tile pyramids. They are related but not interchangeable.

## Coordinate rules

- Location `x`, `y`, and `z` are Minecraft block coordinates in the record's native dimension.
- Highway `points[].x` and `points[].z` are also native to the highway's dimension.
- Render bounds use half-open intervals: `[minX, maxXExclusive)` and `[minZ, maxZExclusive)`.
- Bounds describe the captured/rendered footprint, which may not be centered on the location marker.
- A render's `locationX`/`locationZ` identifies the Atlas location; it is not necessarily the geometric center of every WDL footprint.

## Overworld and Nether travel math

For a rough portal candidate, divide Overworld X/Z by 8 to enter the Nether and multiply Nether X/Z by 8 to return to the Overworld.

```csharp
static (long X, long Z) OverworldToNether(long x, long z) =>
    ((long)Math.Floor(x / 8d), (long)Math.Floor(z / 8d));
```

This estimates coordinates only. Portal placement and linking depend on terrain,
existing portals, border limits, and the current server state.

## Tile URL templates

A per-location render may return a template such as:

```text
https://.../AtlasTiles/example/overworld/g-.../{dn}/{z}/{y}/{x}.png
```

Important distinctions:

- `{dn}` is the lighting variant, generally `day` or `night` when `hasDayNight` is true.
- `{z}` is the map tile zoom level.
- `{x}` and `{y}` are tile-column and tile-row indices. Template `{y}` is **not Minecraft elevation Y**.
- `maxNativeZoom` is the highest native tile zoom available for that render.
- `coordinateScheme` tells Atlas-aware clients how the sparse tile grid is anchored. Do not assume a generic Web Mercator slippy-map transform.

For a new map integration, use the footprint to cull off-screen renders, honor `maxNativeZoom`, and fetch only visible tiles. Never enumerate every possible tile URL.

## Loading tiles

1. Fetch the render catalog outside the render thread.
2. Index footprints by dimension and bounding box.
3. At low zoom, show location markers or aggregate counts only.
4. At close zoom, query the spatial index for intersecting footprints.
5. Load visible tile images asynchronously with an LRU cache and a concurrency cap.
6. Cancel obsolete tile requests after a pan, zoom, dimension change, or disconnect.
7. Keep day/night variants in the same cache key.
8. Link an overlay or tooltip back to the render's `apiUrl` and location's canonical page.

## Multiple historical renders

One location can have multiple WDL snapshots. Keep each render's ID,
`worldDownloadDate`, dimension, and Archive warp or preserved-source relationship.
Overlapping footprints do not establish that two renders belong to the same
location or capture; use the relationships supplied by the API.

## Historical Nether primary layers

`GET /api/maprenders/catalog` includes the original 43k and 5k Nether maps.
Their `coordinateScheme` is `atlas-nether-legacy-v1`, with 256px PNGs, offset
`21503.36`, scale factor `3.3599`, and no URL zoom offset. At native URL zoom `q`,
blocks per pixel are `64 * 3.3599 / 2^q`. Tile `(tx, ty)` starts at block
`(tx * 256 * bpp - 21503.36, ty * 256 * bpp - 21503.36)`.
Native maximum zoom is 9 for 43k and 6 for 5k.

The current Atlas Nether grid uses offset `21504` and scale factor `4`. Reproject
the old images when combining them with that grid; assigning the original PNGs
directly to current tile coordinates produces a scale and origin mismatch.
These are preserved historical alternatives, not default terrain layers.

## BlueMap 3D derivatives

A render can optionally advertise `blueMapUrl`, `blueMapPath`, and
`blueMapProfileVersion`. BlueMap uses the same exact historical render identity,
dimension, source, and location relationship, but its output is an interactive
3D web application rather than a `{z}/{y}/{x}` tile template. Its initial camera
is anchored to the canonical `locationX`/`locationZ`, not necessarily the WDL
footprint midpoint.

Do not convert the BlueMap path into Leaflet bounds or combine its model files
with another render. Open the returned `blueMapUrl` only after a user requests
3D, label it with the render date/dimension, and preserve the normal 2D overlay
when the field is null. Full discovery, embedding, caching, and fallback
guidance is in [BlueMap 3D render derivatives](BLUEMAP-3D.md).
