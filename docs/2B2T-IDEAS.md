# 2b2t-specific integration ideas

2b2t tools live in a different environment from ordinary survival-server plugins: travel is measured in millions of blocks, the Nether network matters, history spans many Minecraft eras, landmarks may be destroyed, and public archives contain multiple snapshots of the same place. The strongest integrations account for that context.

## Client-side mods

### Offline landmark index

Cache `/api/locations` at startup or on explicit refresh, partition by dimension, and build a k-d tree or grid index locally. A HUD can then answer “what historical place is near these coordinates?” without polling Atlas while the player moves.

Good filters:

- dimension;
- distance band;
- has render / has Archive warp / has historical media;
- build group;
- added or WDL date;
- tags and normalized name.

Always label results historical. “Nearest catalogued location” must not become “active base nearby.”

### JourneyMap, Xaero, or neutral map layer

Render simple markers at medium zoom and WDL footprints/tiles only up close. Clicking a marker can open the canonical Atlas page, copy coordinates, list dated Archive warps, or toggle individual render snapshots.

Keep the integration map-neutral where possible: translate Atlas records into your own internal marker/overlay model, then write thin adapters for specific map mods.

### Portal and highway travel assistant

Combine native-dimension location coordinates with `/api/highways` geometry to:

- estimate an Overworld-to-Nether portal target;
- identify the nearest principal or diagonal route;
- distinguish highways, ring roads, grids, and canals;
- show which groups built, extended, or currently stewarded a route;
- generate a staged route without claiming the historical geometry is presently unobstructed.

### Historical arrival card

When the player opts in at a coordinate, query the local index and display:

- location and known aliases;
- build groups and roles;
- historical description and sources;
- Archive warp names and WDL dates;
- screenshots/video/wiki attachments;
- a button to view the Atlas render or canonical page.

Avoid automatic chat spam and avoid sending the player's live coordinates to third parties; local nearest-neighbor matching is enough.

### Base time machine

For locations with multiple renders, add a date selector, swipe comparison, day/night toggle, or user-initiated BlueMap 3D tab. Use exact render identities and footprints so each snapshot remains independently selectable even when its coverage differs. Enable 3D only when that render's `blueMapUrl` is present; do not substitute another date or dimension.

## Bots and community services

### Discord slash commands

Useful commands include:

```text
/atlas location <name>
/atlas near <dimension> <x> <z>
/atlas group <name>
/atlas warp <archive-name>
/atlas renders <location>
/atlas highway <name>
```

Cache the catalogs, use autocomplete from the cache, and put `Data: 2b2tAtlas` in the embed footer. Link titles to `interactiveUrl` and preserve source attribution for media.

### Group/build graph

`GET /api/groups/{id}` already returns reciprocal build and highway edges. A graph browser can visualize:

- group succession and shared projects;
- builds by era;
- infrastructure contributors versus current stewards;
- alias-aware group search;
- source-backed relationship evidence.

Do not infer membership or ownership merely from coordinate proximity or overlapping renders.

### Archive warp assistant

Index `/api/warps` by exact and normalized name. Given a copied `/warp ...` command, return its canonical location, dimension, render availability, date, and entity links. Exact warp identity should outrank fuzzy place-name guesses.

### Pilgrimage or history itinerary builder

Let users select public historical locations, cluster them by dimension and geography, estimate Nether-equivalent distances, and export a personal waypoint list. Mark all routing as advisory and let users omit sensitive/current destinations.

## Map and preservation projects

### Render availability/coverage map

Use render footprints to show which historical locations have WDL-derived coverage and which have only point records. Coverage is not a current chunk-activity map.

### WDL snapshot diff viewer

Group renders by `locationId`, order by `worldDownloadDate`, and compare matching geographic regions. This can document construction, griefing, terrain changes, and incomplete captures while keeping each source snapshot distinct.

### Historical 3D viewer

Use the render catalog to add an **Open in 3D** action for validated BlueMap
derivatives. A desktop tool can use a browser tab; a mod or launcher companion
can use an optional webview. Keep the canonical location, render date,
dimension, warp/source provenance, and WDL link beside the viewer so users do
not confuse a historical model with the live server. See
[the BlueMap developer guide](BLUEMAP-3D.md).

### Highway history visualization

Draw reviewed route geometry with widths and group roles. Allow layers for primary highways, diagonals, rings, grids, canals, historical builders, and current maintainers. Preserve disconnected segments rather than drawing unverified links.

### Source-aware media gallery

Build a gallery from `/api/attachments`, but treat the API's self-hosted thumbnail/path and original `sourceUrl` as separate concepts. Show captions and credits; link back to both the Atlas entity and the original source.

## LLM, search, and research tools

For bulk retrieval, use the static corpus instead of repeatedly downloading the interactive location endpoint:

- `/entities/locations.jsonl` for location entities;
- `/entities/groups.jsonl` for groups and relationships;
- `/entities/media.jsonl` for sourced attachments;
- `/dataset.json` for synchronized counts/catalog discovery;
- `/llms.txt` for corpus guidance;
- canonical HTML/JSON-LD entity pages for human-readable citations.

Recommended RAG behavior:

1. Chunk by canonical entity rather than arbitrary page length.
2. Store the canonical URL and entity ID with every chunk.
3. Keep relation roles and evidence/source URLs.
4. Distinguish an Atlas-authored summary from quoted or linked third-party material.
5. Refresh incrementally using the static catalog metadata.
6. Cite the entity page in generated answers.
7. Never convert missing dates, attribution, or coordinates into invented certainty.

## Analytics ideas

With careful historical caveats, developers can explore:

- build distribution by era/dimension/distance;
- groups with the most catalogued builds or infrastructure relationships;
- Archive warps with and without rendered WDLs;
- render footprint size and temporal coverage;
- attachment/source completeness;
- growth of the public historical catalog over time.

These are catalog statistics, not measurements of the live server population or every build ever created.

## Integration anti-patterns

- Polling `/api/locations` every tick, frame, or coordinate change.
- Blocking the Minecraft client/render thread on HTTP or image decoding.
- Treating all overlapping WDLs as one base.
- Dropping numbered base/event identities during fuzzy matching.
- Drawing a line between disconnected same-name highway/canal segments.
- Assuming the newest WDL is the current live state.
- Republishing media without its source and attribution.
- Hard-coding entity IDs when a name/alias discovery step is available.
- Scraping rendered website HTML when the API, JSONL, or JSON-LD already exposes the relationship.
- Preloading a BlueMap WebGL viewer for every row or trying to merge model data from unrelated historical renders.
