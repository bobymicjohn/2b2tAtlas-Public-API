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

This is planning math, not a promise that a portal exists, links cleanly, or is safe. Portal search/linking, border clamping, terrain, obstructions, and the server's current world state still matter.

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

## A responsive overlay strategy

1. Fetch the render catalog outside the render thread.
2. Index footprints by dimension and bounding box.
3. At low zoom, show location markers or aggregate counts only.
4. At close zoom, query the spatial index for intersecting footprints.
5. Load visible tile images asynchronously with an LRU cache and a concurrency cap.
6. Cancel obsolete tile requests after a pan, zoom, dimension change, or disconnect.
7. Keep day/night variants in the same cache key.
8. Link an overlay or tooltip back to the render's `apiUrl` and location's canonical page.

This preserves close-up historical detail without turning hundreds of base renders into a permanent GPU, memory, or network cost.

## Multiple historical renders

One location can have multiple WDL snapshots. Useful UI patterns include:

- default to the newest dated render;
- allow the player to select or combine snapshots;
- label every snapshot with `worldDownloadDate` and Archive warp identity;
- provide a compare slider or blink/difference mode;
- do not merge footprints merely because they overlap.

Overlap is geographic evidence, not identity. Use the owning location and Archive warp relationships supplied by the API.
