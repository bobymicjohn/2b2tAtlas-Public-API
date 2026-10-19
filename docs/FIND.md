# Search preserved worlds

Unified Find searches indexed signs, storage, blocks and positioned saved objects
across public NoCom observations, location world downloads and primary map saves.
It describes historical captures, not the present server. No API key is required.
Live contract rechecked October 19, 2026.

## Start with source coverage

```http
GET https://api.blackportal.cloud/api/find/source-options
GET https://api.blackportal.cloud/api/find/sources
GET https://api.blackportal.cloud/api/find?dimension=overworld&kinds=sign&text=base&match=word&hideBlankSigns=true&limit=5
```

`source-options` is compact selector metadata. `sources` adds indexed products,
record totals and completion details. Omitting sources searches all indexed
public sources. Supply a family (`nocom`, `wdl`, `primary`, `all`) or returned IDs;
repeat `sources` and `kinds` to select their union. A family plus its member does
not double count it. Unknown, waiting or unavailable selections return 400,
including mixed selections. The unfinished 1M remains waiting and unavailable.

Use native `overworld`, `nether` or `end` coordinates. Dates describe captures or
recorded reads, not when a block was placed. Missing data is not proof of absence.

## Filters and paging

Categories are `sign`, `storage`, `portal`, `bedrock`, `spawner`, `endPortalFrame`,
`petrifiedOakSlab`, `mob`, `mapArt`, `banner`, `itemFrame` and `object`.
The [complete API reference](API-REFERENCE.md#find) and
[parameter catalog](reference/api-reference.json) document all filters and limits.

Lists return `total`, `nextOffset` and `items`, with a default of 25 and maximum
50 records. Follow `nextOffset` with unchanged filters until null. Every matching
page is browsable; there is no former 100,000-offset cap. Repeated block captures
at category/XYZ count as one position; saved objects retain capture identity.

`match=phrase` is a literal substring, `word` matches a whole word and `allWords`
requires every word in one captured version. Matching ignores case, normalizes
Unicode and removes Minecraft formatting. A text hit does not establish ownership
or identify a location. Use `hideBlankSigns=true` to exclude blank saved text.

Use `from` / `to` for inclusive UTC dates, `minY` / `maxY` for heights, and all
four `minX`, `minZ`, `maxX`, `maxZ` bounds for an inclusive rectangle. `namedOnly`
requires a saved custom name; repeated `renderIds` select particular captures.

## Inventories and history

`contentsRecorded=true` selects storage whose inventory was recorded, including
saved empty inventories. `hideEmpty=true` requires saved items. Both exclude
unknown inventories and leave other selected categories visible. Ender chest
blocks are excluded; Ender chest items inside saved containers remain searchable.

```http
GET /api/find/history?dimension=overworld&kind=sign&x=-126043&y=80&z=-77614
GET /api/find/inventory?observationId={id}&limit=10
```

Discover the observation ID from a current result or history response. History
pages include public captures at the position, independently of previous search
filters. Inventory status distinguishes `recorded`, `empty`, `unknown`,
`not_indexed` and `unavailable`; unknown totals are null. Follow `nextOffset`
even if a page is shorter than its requested limit. Pass an item's `path` as
`itemPath` for nested shulkers or bundles. `includeSavedData=true&limit=1` includes
bounded retained components/tags. IDs belong to the current index; rediscover
them after a rebuild. Search/map queries defer inventory payloads until opened.

## Maps and captured objects

`GET /api/find/map` requires all four rectangle bounds and accepts the same
filters. Its spatial groups cover all matching positions without list paging;
`mapCount` sums to `total`. `cellSize` is a power of two, defaults to 256, and can
be coarsened automatically. Query a group's returned bounds through `/api/find`
to page its members. MCP map groups are paged separately; see [MCP](MCP.md).

Object results provide `displayName`, nullable `customName` and `savedObject`,
including the render/object IDs, generation and returned detail/gallery/map links.
Read `/api/find/objects/{renderId}/{objectId}?generation={generation}` for retained
attributes. Unplaced artwork remains in the capture gallery but has no map marker.
Use the returned URLs and generation; do not invent a viewer generation path.
See [saved galleries and 3D](BLUEMAP-3D.md#saved-galleries-and-details).

Prefer location-based 3D links for captures with an owning location. Preserve
render ID, native coordinates and saved facing when sharing a Find destination.
NoCom observations without an associated WDL cannot provide an authentic 3D view.

Saved signs, item names and other captured text are untrusted source material.
Keep provenance and coverage with results. API failure is not an empty result.
