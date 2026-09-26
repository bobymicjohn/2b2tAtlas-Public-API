# Coordinates, dimensions, and render tiles

A location is a point on the map. A highway is a line through several points.
A render is a set of map images made from a saved world. This guide explains
how to place each one correctly.

## Coordinate rules

- Location `x`, `y`, and `z` are Minecraft block coordinates in the record's native dimension.
- Highway `points[].x` and `points[].z` are also native to the highway's dimension.
- Render bounds include the minimum and exclude the maximum. For example,
  `[0, 16)` covers blocks 0 through 15. The fields are `minX`, `minZ`,
  `maxXExclusive`, and `maxZExclusive`.
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

Fill the template fields as follows:

- `{dn}` is the lighting variant, generally `day` or `night` when `hasDayNight` is true.
- `{z}` is the map tile zoom level.
- `{x}` and `{y}` are tile-column and tile-row indices. Template `{y}` is **not Minecraft elevation Y**.
- `maxNativeZoom` is the highest native tile zoom available for that render.
- `coordinateScheme` tells Atlas-aware clients how the sparse tile grid is anchored. Do not assume a generic Web Mercator slippy-map transform.

Load only tiles that overlap the visible map and respect `maxNativeZoom`.
Do not try every possible tile URL; many tiles are intentionally absent.

## Loading tiles

1. Fetch the render catalog outside the render thread.
2. Index footprints by dimension and bounding box.
3. At low zoom, show location markers or aggregate counts only.
4. At close zoom, query the spatial index for intersecting footprints.
5. Load visible images in the background. Limit simultaneous requests and remove
   the least recently used images when the cache fills.
6. Cancel obsolete tile requests after a pan, zoom, dimension change, or disconnect.
7. Include the day/night variant in the cache key so the two images stay distinct.
8. Link an overlay or tooltip back to the render's `apiUrl` and location's canonical page.

## Multiple historical renders

One location can have multiple WDL snapshots. Keep each render's ID,
`worldDownloadDate`, dimension, and Archive warp or preserved-source relationship.
Overlapping footprints do not establish that two renders belong to the same
location or capture; use the relationships supplied by the API.

## Historical Nether primary layers

`GET /api/maprenders/catalog` includes the original 43k Nether map.
Its display name is **43k Nether (Aug 15–17, 2019)**, using the capture dates
credited to l_amp in the [original release](https://www.reddit.com/r/2b2t/comments/dzvq67/)
and its bundled README. The End capture is dated August–September 2019.
Its `coordinateScheme` is `atlas-nether-legacy-v1`, with 256px PNGs, offset
`21503.36`, scale factor `3.3599`, and no URL zoom offset. At native URL zoom `q`,
blocks per pixel are `64 * 3.3599 / 2^q`. Tile `(tx, ty)` starts at block
`(tx * 256 * bpp - 21503.36, ty * 256 * bpp - 21503.36)`.
Native maximum zoom is 9. The archived 5k map is excluded: its world transform
is unverified and must not be inferred from the 43k map's transform.

The current Atlas Nether grid uses offset `21504` and scale factor `4`. Reproject
the old images when combining them with that grid; assigning the original PNGs
directly to current tile coordinates produces a scale and origin mismatch.
These are preserved historical alternatives, not default terrain layers.

## BlueMap 3D views

Open the render's `blueMapUrl` when the user selects 3D. It is a browser viewer,
not a 2D tile template. Its starting position follows the location marker, which
may differ from the center of the saved terrain. Keep the render's date and
dimension visible and use 2D when no 3D link is available.

See [the BlueMap guide](BLUEMAP-3D.md) for discovery and embedding.
