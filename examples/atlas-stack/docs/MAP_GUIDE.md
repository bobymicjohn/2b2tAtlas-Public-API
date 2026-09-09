# Map guide

## Core navigation

- Choose Overworld, Nether, or End from navigation.
- Pan and zoom with standard mouse, touch, and keyboard map controls.
- The lower coordinate readout shows current Minecraft coordinates and the paired Overworld/Nether travel coordinate when applicable.
- Copy coordinates or copy a share link from the coordinate bar.

## Search and coordinates

Open Search to find locations by name, tags, description, or coordinates. Accepted coordinate examples include:

- `4014 -2822`
- `4014, -2822`
- `/tp 4014 64 -2822`

A coordinate query produces a direct Go to coordinates result in the current dimension. A location result switches dimensions when necessary and selects the marker.

## Layers

The default is the latest 2b2t.place terrain: 100k in Nether, 256k in End,
and the 1M terrain plus obsidian overlay in Overworld. Older maps remain under
**Layers → Spawn renders → Available**. Add one to compare it, remove it to return
to the newer terrain, or drag it above/below another active map.

Nether includes the original **43k Nether (Aug 15–17, 2019)** render. The archived 5k
image is excluded because its world-coordinate transform is not verified.
End's **42k End (Aug–Sep 2019)** remains available but is off by default.
Overworld retains its v1 256k, 100k, 7k and named map layers. Legacy Nether tiles
are corrected to the current coordinate grid when displayed; their archived PNGs
are unchanged. They upscale their deepest native tiles at high zoom.

Spawn renders are historical overlapping base maps. Multiple active renders can be reordered; the first row is visually in front. Base renders are location-specific sparse WDL overlays. Each location starts with only its newest applicable render selected; click additional render cards to compare or combine historical WDL footprints, and click a selected card again to disable it. Every card has a distinct checkbox showing whether that capture is currently displayed. Newer selected captures remain visually above older ones. Every render card also has a **Zoom** action that enables that capture if necessary and fits its full footprint without turning off other selected renders. Base renders are a close-up layer: the map mounts only renders near the viewport once their footprint is large enough to be useful on screen. Large WDLs therefore appear sooner than small ones, while zoomed-out navigation remains responsive.

Additional overlays:

- locations;
- End portals;
- highways and coordinate axes;
- world border and spawn radius;
- crosshairs;
- marker style controls.

On Location Detail, the bottom-left **3D** action opens quality-gated BlueMap
views for the selected render snapshots that intersect the current viewport.
Each historical render remains a separate labeled view; returning to 2D keeps
the current map position and selection. The action remains disabled with a
tooltip when no selected render in the active dimension has completed 3D output.
The 2D map and WDL download remain available while 3D generation is pending.

## World Pulse history

Open the History control to explore the public Nocom loaded-chunk record from 2018 through July 2021.

- **Period** shows one fixed 30-day source bucket. Use the scrubber or playback controls to move through the record.
- **All time** combines every released period.
- **Original heat** uses the fixed cyan-yellow-red intensity ramp. **Custom color** opens a color picker that recolors the same observations instantly; Overworld, Nether, and End begin with yellow, cyan, and magenta defaults and remember separate choices during the session.
- World Pulse opacity can be adjusted from 10% through 100% in either color style.
- World Pulse remains visible through the map's full zoom range. Every generated parent level uses the same coverage-preserving downsampling, avoiding a visibility step where older parents previously faded thin routes; zoom in for chunk-level shape.
- World Pulse draws above the ordinary highway lines without blocking them. Highway tooltips and clicks remain active underneath, hover/selection feedback is repeated above the heatmap, and the Layers panel has a separate highway-opacity slider.
- Overworld, Nether, and End use their native coordinate spaces. End coverage is narrower: five released 30-day periods from February through June 2021.

A Nocom observation means the server reported that a remotely probed chunk was loaded by at least one player. It is not an exact player position, visit count, ownership record, or proof that an unobserved area was empty. Density also reflects the Nocom system's scanning priorities and repeated checks.

World Pulse tiles are generated offline from the official `hits_grouped.sql.gz` release. The source stores chunk coordinates and fixed 30-day buckets rather than calendar months. The immutable public manifest records the source SHA-256, row counts, coordinate scheme, frame bounds, and tile inventory.

Overworld locations can appear in Nether travel coordinates, and Nether locations can appear in Overworld-equivalent coordinates. Cross-dimension markers are visually subdued; location details always show authoritative native coordinates.

Group detail pages use the same interactive map surface as location details. The default marker set contains only locations with reviewed attribution to that group and fits their full extent; the other-locations control can add the rest of the Atlas without changing the viewport. Overworld and Nether tabs project compatible locations into the selected travel coordinate space, while End remains separate. The standard primary-render, base-render, highway, axis, world-border, End-portal, and day/night controls are available. Selecting any marker opens its full location popup with coordinates, description, tags, Archive warps, dated multi-select render choices, render-footprint zoom, source links, and navigation to the location record.

In Admin -> Render Settings, **Render preview** opens the same Leaflet primary-layer stack used by the main map. Toggle 1m, Obsidian, 256k, 100k, and other primary WDL layers independently; switch day/night and adjust preview opacity to compare the generated sample against overlapping historical terrain.

## Player tools

- Measure routes in blocks, chunks, kilometers, or Nether-equivalent distance.
- Place temporary custom markers for planning.
- Export current/all dimensions from the map to Xaero, CSV, or JSON.
- Select locations in Directory to create JourneyMap or Xaero waypoint ZIP bundles.
- Switch day/night when the active render supports both.
- Open fullscreen for route planning or screenshots.

## Highway maintainer workflow

Users with highway edit permission receive the road-edit control.

1. Select an existing highway or start a new one.
2. Draw vertices in order.
3. Enter geometry and construction metadata.
4. Attribute a builder group where known.
5. Save. Contributor submissions may enter moderation instead of becoming public immediately.

Keep geometry in the selected dimension's coordinate system. Check endpoints at high zoom and ensure the path does not accidentally jump across wrap or dimension boundaries.

## Sharing

Shared map links contain dimension, center X/Z, and zoom. Location Detail's **Open in map** action uses the same contract and centers the selected location. Malformed or duplicate query values are ignored safely. Shared links do not encode private markers or authentication state.

## Privacy and 2b2t safety

The Atlas is a historical research tool. Do not publish coordinates for active private bases, stashes, or other sensitive locations without a clear community policy and source review. Historical/griefed locations should include date and provenance when known.
