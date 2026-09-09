# Map Rendering, Coordinates, Layers, And Performance

> **AI-Generated documentation.**

## Scope

This file explains executable map math and layer behavior. End-user operation belongs in `docs/MAP_GUIDE.md`; WDL tile generation belongs in `WDL_INGESTION.md`.

The evaluated BlueMap 3D derivative architecture, measured storage benchmark,
operational phase-1 canary, and phased capacity plan are in
[`docs/BLUEMAP_PIPELINE.md`](../BLUEMAP_PIPELINE.md).
The canonical current operations and API contract is
[`docs/BLUEMAP_PIPELINE.md`](../BLUEMAP_PIPELINE.md).
BlueMap is a per-render derivative service, not a replacement for the global
2D layers described here. Explicit render-ID canaries write content-addressed
static generations to `F:\AtlasExample\AtlasBlueMap\location-renders`; they do not
modify the database, source WDLs, collector, uNmINeD tiles, or frontend.

The location-detail `3D` action snapshots the selected base-render layers that
intersect the current 2D viewport. Each available derivative remains a separate
tab because merging historical WDL snapshots would arbitrarily overwrite
overlapping chunks. Returning to 2D reveals the same live Leaflet instance, so
pan, zoom, waypoint selections, and checked renders are retained. Public render
JSON advertises `blueMapUrl`, `blueMapPath`, and `blueMapProfileVersion`; only a
completed highest-profile generation is exposed.
The control stays visible but disabled, with an explanatory tooltip, when none
of the selected renders in the active dimension advertises a validated
derivative. It never substitutes another date or dimension silently.

Operational commands:

```powershell
# Start the ten-render production-shaped canary in the background.
.\scripts\start-atlas-bluemap-canary.ps1

# Render an explicit set in the foreground. Completed exact-source generations
# are skipped, making this safe to resume.
.\scripts\invoke-atlas-bluemap-render.ps1 -RenderId 785,824,927

# Inspect durable progress without parsing renderer logs.
.\scripts\get-atlas-bluemap-status.ps1

# Refresh the canary index and start a localhost-only inspection route.
.\scripts\start-atlas-bluemap-preview.ps1
# Browse http://127.0.0.1:8770/
```

The preview exposes only currently validated generations. A direct URL for an
older experimental profile is intentionally unavailable even if its immutable
diagnostic files remain in the backing output directory.

BlueMap camera starts are anchored to each Atlas location's canonical X/Z. Do
not derive the initial camera from WDL bounds: sparse and multi-cluster WDLs can
have a midpoint in empty terrain. The profile-7 static gate verifies this as
`QualityGate.LocationStartExact`. Repair older profile-7 presentation metadata
without regenerating terrain models with:

```powershell
.\scripts\repair-atlas-bluemap-start-positions.ps1
```

The renderer verifies the immutable source hash before extraction, applies the
Atlas render footprint and vertical cutoff, records a generation manifest, and
promotes only validated static output. Active web/model output uses a per-job
Docker volume and the Minecraft resource cache uses
`atlas-bluemap-cache-v5-23`; this avoids unreliable small-file writes through a
Windows bind mount. Renderer profile 7 repairs lighting in a disposable Paper
derivative using SHA-pinned Light Cleaner/BKCommonLib artifacts. It inventories
the source dimension, allows temporary neighbor context while relighting,
prunes every generated Anvil record afterward, and fails unless the final chunk
set exactly equals the source. Version-keyed Paper runtime caches avoid repeated
downloads but never share per-render worlds. Block-region corruption remains a
hard failure; malformed entity/POI sidecars may be omitted from the disposable
render derivative because BlueMap does not model entities and the source ZIP is
never modified. BlueMap then uses its stock client with the
2b2t.info-style Overworld profile (sky 1, ambient 0.1, 500-block lowres tiles,
LOD factor 5/count 3); Nether and End use sky 0 and ambient 0.6. A static-output
quality gate verifies settings plus both 2D/3D payloads before promotion. The API
and local preview reject profiles below 7. The full-batch launcher selects
distinct render IDs rather than locations, so Overworld, Nether, and End renders
for the same location are processed independently. The Atlas watchdog resumes
one missing-generation batch after reboot and polls again after terminal runs;
BlueMap remains downstream and can never block normal ingestion.
Relight and BlueMap model containers encode their owning renderer PID; the
watchdog removes an orphaned disposable container only after proving that the
exact owner is no longer a matching renderer process.

The production batch runs only one job and one stage at a time. Both the Paper
relight and BlueMap render stages are capped at eight CPU cores; their memory
caps are 12 GiB and 8 GiB respectively. This uses no more CPU concurrently than
the already validated relight lane while avoiding an artificial four-core
bottleneck during large BlueMap model builds.

## Coordinate System

**Production:** `2b2tAtlas.Client/wwwroot/js/atlas-map.js` uses a fresh clone of Leaflet `L.CRS.Simple`, not a geographic projection. Transformation is `(1, 0, -1, 0)` and scale is `2^zoom / 64`.

For dimension configuration `offset` and `scaleFactor`:

$$
\mathrm{lat} = -\frac{z + offset}{scaleFactor}, \qquad
\mathrm{lng} = \frac{x + offset}{scaleFactor}
$$

The inverse rounds back to Minecraft integer $x,z$. Current values are:

| Dimension | Modern value | Offset | Scale factor | URL zoom behavior |
| --- | ---: | ---: | ---: | --- |
| Overworld | 0 | 128000 | 8 | `zoomOffset: 1` |
| Nether | 1 | 21504 | 4 | no offset |
| End | 2 | 20992 | 4 | no offset |

Map zoom bounds are `-10` through `10`; default zoom is `5`.

The v1 Nether 43k PNGs use `atlas-nether-legacy-v1`: offset `21503.36`,
scale factor `3.3599`, 256px tiles, and no URL zoom offset. Their native maximum
level is 9. `planLegacyTiles` maps the current output tile's
world bounds into that original grid; `makeLegacyLayer` composites the intersecting
source PNGs. Native levels are clamped, positive-index quadtree bounds keep
overview fetches bounded, and tile unload aborts outstanding requests. Do not
serve these PNGs as ordinary tiles on the new Nether CRS: the scale differs.

The picker labels this WDL **43k Nether (Aug 15–17, 2019)**. Capture dates and
l_amp's download credit come from the original `2b2t_100k_final_v1.3/README.txt`,
corroborated by the [project release announcement](https://www.reddit.com/r/2b2t/comments/dzvq67/).
The same release dates the End capture to August–September 2019. The old Vue
picker's 2023 description and local render-file timestamps are not capture dates.

The 5k Nether layer is excluded from both the picker and public primary catalog.
Its original 10272px image and tiles are preserved, but their world origin and
scale are unverified. The old Vue map applied the 43k transform to both layers;
that is not evidence that the 5k image is calibrated. Coordinate-math tests alone
cannot validate source georeferencing; restoration needs independently known
world landmarks and distances.

Default stacks use 2b2t.place alone in Nether/End and its terrain plus obsidian in
Overworld. Historical layers are opt-in. Selection is validated and deduplicated
at the JS boundary and again before layer construction. Run
`node scripts/test-map-primary-layers.mjs` for layer and coordinate regressions.

The client build hashes `atlas-map.js` into `MapModuleAsset.Url`. All four map
hosts import that URL so a frontend release loads the matching module even when
the browser still has a fresh cached copy of an earlier script.

World Pulse uses direct sparse Nocom URL levels across that complete range. Its generated parents stop at URL zoom `-9` for Overworld (map zoom `-10` plus `zoomOffset: 1`) and `-10` for Nether/End. Do not replace these levels with `minNativeZoom`: clamping a world-scale viewport to zoom 0 would fan out into a very large number of sparse requests. The current public pyramid remains the review baseline. Original heat renders its cyan-yellow-red palette directly; Custom color applies a client-side SVG color matrix to the complete tile-layer container, preserving source alpha while replacing RGB with the chosen color. Picker input updates the matrix in place without recreating the Leaflet tile layer or issuing new tile requests. Both styles expose the full 10%-100% opacity range.

Pane order is base tiles/renders, ordinary highways (`350`), World Pulse (`375`), transient highway hover/selection emphasis (`385`), then normal Leaflet overlays/markers (`400+`). The World Pulse and emphasis panes use `pointer-events: none`; the wide invisible highway hit paths remain in `hwPane`, so the heatmap can visually cover highways without blocking their tooltips or clicks. Highway base opacity is independently adjustable from 10%-100%; hover and selected emphasis remain fully visible.

## Location Projection

Native location coordinates are authoritative. Overworld locations may be displayed in Nether at rounded $x/8,z/8$; Nether locations may be displayed in Overworld at $8x,8z$. These markers are visually subdued and retain native detail values. End locations do not project to other dimensions.

The location detail page exposes an Overworld/Nether map switch for paired dimensions. It filters the base-render cards to the selected map dimension, rebuilds primary layers and overlays, refocuses the projected location, and projects the full-map link by $/8$ or $\times8$. A location with no render in its home dimension defaults to the newest available render dimension; this is how an Overworld-owned record such as Wässrige Hölle Zwei can immediately display its attached Nether WDL. End remains isolated and does not show a paired switch.

The owner is `locDisplayCoords` in `atlas-map.js`. Modern dimension mapping is `DIM_KEY = {0: overworld, 1: nether, 2: end}`.

## Render Layer Types

Global/spawn layers come from hard-coded descriptors plus published `GET /api/maprenders` rows registered by `registerWorldRenders`. Multiple layers may be stacked, with the first selected row visually on top. `maxNativeZoom` upscales a shallow pyramid instead of requesting nonexistent deeper tiles.

Per-location base layers come from `Location.Renders`. Exact chunk bounds define Leaflet bounds; old rows without exact bounds fall back to scale-centered placement. Both the main-map location popup and Location Detail keep a set of selected render IDs per location. The newest applicable capture is the sole initial selection, while every render card independently toggles membership; selected captures are sent oldest-first so the newest is added last and stays visually on top. They render in `baseRenderPane` at z-index 250, above primary tiles and below roads/markers.

Per-location tile layers are viewport-virtualized. `loadBaseRenders` retains lightweight descriptors and precomputes bounds, but it does not instantiate a `L.TileLayer` for every selected render. On `moveend`, `zoomend`, and map resize, `refreshBaseRenderLayers` reconciles an active set against a viewport padded by 40 percent. A candidate must intersect that prefetch area and project to at least 24 pixels on its longest axis; this naturally makes large WDLs visible at wider zooms while delaying small captures until close-up. Active layers use idle-only updates and a one-tile buffer. A 96-layer dense-overlap guard prefers renders intersecting the true viewport and then nearest to its center. `getBaseRenderStats` exposes available, nearby, mounted, and capped counts for staging diagnostics. Do not regress this to a single layer group containing the full catalog: even bounds-constrained off-screen Leaflet layers subscribe to every map movement and make interaction cost grow with total render count.

`focusBaseRender` backs the per-card **Zoom** action. The UI first adds the capture to the current multi-selection when necessary, then fits exact chunk-derived bounds with a legacy scale-and-location fallback. The action changes the viewport and never clears other selected captures.

## Highway And Marker Layers

API-loaded highways are preferred. Overworld/Nether views select native Nether highway rows; End selects native End rows. If no API highway data exists, the built-in canonical Nether set is a fallback; End has no built-in fallback.

Highways use a dedicated SVG renderer and pane at z-index 350. A visible path is paired with a transparent 16-pixel hit path. Detailed location markers occupy normal overlay/marker panes; world-overview markers use a shared Canvas pane at z-index 340 so highways remain interactive.

## Location Performance Modes

At zoom `>=1`, locations use detailed circle/image markers. Below zoom `1`, they switch to interactive Canvas dots with shared spatial hit testing. This preserves global visibility without thousands of DOM/SVG nodes. Selection is carried across layer rebuilds when the location remains visible.

Base-render performance is independent from marker mode. Zoomed-out maps normally have zero per-location tile layers mounted; close-up maps carry only the padded local working set. Day/night, dimension, and selected-render changes rebuild that small working set without changing user selection.

Do not move `locOverviewPane` above `hwPane`: it would mask highway hover/click. Do not globally enable Leaflet `preferCanvas`; the full-screen overlay previously intercepted panning and highway interactions.

## 2b2t.place Resampling

The 2b2t.place grid does not align with Atlas CRS at low zoom. `makePlaceLayer` composites upstream 512-pixel quadtree images onto Atlas canvas tiles through `GET /tiles/place/...`.

The client chooses LOD from blocks per pixel, caps one output tile at 16 source images, leaves over-broad overview tiles transparent, uses `updateWhenIdle`, keeps one tile buffer, and aborts fetches on `tileunload`. Fetches use `credentials: omit` and `cache: no-store`; the API/Cloudflare layer owns cache and parent-LOD fallback behavior.

## Coordinate Certification

`docs/TGG_COORDINATE_FIXTURE.md` is the retained Overworld proof. WDL sparse schemes are dimension-specific: the Overworld adapter maps uNmINeD native 256-block tiles to Atlas URL zoom 10 with tile offset 500; End uses URL zoom 8 and offset 82. Never reuse one scheme's offset or Leaflet zoom assumptions for another dimension.

**Future:** Nether WDL adaptation remains uncertified. Existing Nether display layers do not prove that arbitrary Nether WDL publication is safe.
