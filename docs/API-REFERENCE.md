# Public API reference

Use Atlas data in a map, mod, website, or research tool. Public reads need no
account or API key.

**Base URL:** `https://api.blackportal.cloud`

Live contract rechecked October 19, 2026: 57 public GET routes and 38 read-only MCP
tools. The live OpenAPI schema and MCP discovery remain authoritative. Bundled
response examples were recorded on October 17; their values can change.

The current release's [website reference](https://2b2tatlas.com/api) documents the public GET routes
and MCP tools. Its checked date appears on the page. Its parameter
tables, response excerpts and MCP guide are also available without JavaScript.

The [complete route catalog](reference/api-reference.json) includes every
parameter, status and recorded response example used by the website reference.

## Make your first request

Open [the location list](https://api.blackportal.cloud/api/locations) in a browser.
The response is JSON: a list of records with named fields such as `name`, `x`, and `z`.
You can also request it from a terminal:

```sh
curl --fail "https://api.blackportal.cloud/api/locations"
```

On Windows PowerShell 5.1, use `curl.exe` instead of `curl`.

To try JavaScript, run this in a browser console or in Node.js 18 or newer:

```javascript
const response = await fetch("https://api.blackportal.cloud/api/locations");
if (!response.ok) throw new Error(`Atlas returned HTTP ${response.status}`);
const locations = await response.json();
console.table(locations.slice(0, 5).map(location => ({
  id: location.rowid,
  name: location.name,
  x: location.x,
  z: location.z
})));
```

`GET` means read a resource. In the routes below, replace `{id}` with an ID from
the matching list. A location ID, warp ID, and render ID are different things.
Use returned URLs where available. This website's `https://2b2tatlas.com/api`
page is documentation; send requests to the API base URL above.

## Choose a resource

| Resource | What it represents | List route |
| --- | --- | --- |
| Location | A named place, its history, coordinates, and related records | `/api/locations` |
| Group | A group and its credited builds or highways | `/api/groups` |
| Highway | A public, approved highway or canal | `/api/highways` |
| Warp | An Archive warp linked to a location | `/api/warps` |
| Render | A map of one historical world download | `/api/renders` |
| Attachment | A photo, video, article, or other source link | `/api/attachments` |
| Find | Captured signs, containers, blocks and saved objects | `/api/find` |
| Map layer | A shared map, such as a spawn map or highway survey | `/api/maprenders` |

Locations, groups, and highways return complete lists. Warps, renders, and
attachments return one page at a time. Unified Find returns `total`, `nextOffset`
and `items`; its map endpoint represents all matches without paging. Legacy NoCom Find uses a separate page format
described in [the NoCom guide](NOCOM.md).

## Read more than one page

`limit` is the number of records requested. `offset` is the number to skip.
For warps, renders, and attachments, the default limit is 500 and the maximum
is 1,000. Keep the same filters while paging.

```http
GET /api/renders?limit=100&offset=0
GET /api/renders?limit=100&offset=100
```

These routes return JSON arrays. Advance the offset by the number received and
stop when a page contains fewer records than requested. Renders sort by dimension
then ID; warps and attachments sort by ID. The catalog can change between requests,
so discard duplicate IDs during long imports. Warp responses also include
`X-Total-Count` and `X-Next-Offset` headers; an empty next-offset header means the
last page. Both headers are exposed through CORS for browser clients;
array-based paging works too.

## Locations

```http
GET /api/locations
GET /api/locations/directory
GET /api/locations/map
GET /api/locations/{id}
GET /api/locations/aliases
```

`/api/locations/directory` returns compact directory rows with descriptions,
groups, exact public warp/render/attachment counts and preferred actions. Use it
for a directory or location picker, then load `/api/locations/{id}` for full
history and media. Anonymous responses are cached on the server for 30 seconds.

`/api/locations/map` is the lightweight map catalog: coordinates, full search text,
group IDs/names/roles, warp names and capture dates, and public tile descriptors with bounds and dates. It omits
attachments and download/3D provenance; use the complete location or detail endpoint
for those. Anonymous map-catalog responses are cached on the server for 30 seconds.

Location fields include `rowid`, `name`, `description`, `tags`, `dimension`,
`x`, `y`, `z`, `wiki`, and `videoUrl`. Related records appear in `warps`, `renders`,
`attachments`, and `groups`. `apiUrl` opens the JSON record, `recordUrl` is a stable
provenance alias whose HTML permanently redirects to `interactiveUrl`, and
`interactiveUrl` opens the complete record and interactive Atlas page.
Older URL fields retain their existing destinations; prefer the explicit pair.

Fetch the list once and search it locally. There is no text-search parameter on
`/api/locations`; MCP also provides search tools. Cache the list instead of
requesting it every frame. A merged location ID redirects to its replacement;
`/api/locations/aliases` lists those mappings.

MCP `search_locations` and `find_preserved_builds` prioritize an exact location
name or saved warp name ahead of prefixes and incidental description mentions.
Ranking occurs before `limit`, so a known build is not hidden by a smaller result
limit. Preserved-build results still require a saved render or world download.
Names are matched case-insensitively; ambiguous-location research retains its
explicit choice behavior.

## Groups and highways

```http
GET /api/groups
GET /api/groups/{id}
GET /api/groups/{id}/attachments/{attachmentId}
GET /api/highways
GET /api/highways/{id}
GET /api/highways/{id}/attachments/{attachmentId}
```

Group details include history, aliases, source links, credited `locations`, and
`highways`. Resolve names from the list, then follow the record's `apiUrl`.

Highway details include `points`, construction information, and `builderGroups`
with their roles and evidence notes. Coordinates belong to the highway's own
dimension. Disconnected segments are separate records. Do not join them across
an unbuilt gap or treat historical geometry as a live route planner.

Group records include nullable `logoUrl` and `logoSourceUrl` fields. Keep logo
source attribution with reused images. A missing logo does not mean a group is absent.

The attachment routes above read a source linked to that group or highway.

## Warps and renders

```http
GET /api/warps?locationId=904&limit=100
GET /api/warps/{id}
GET /api/renders?locationId=904&limit=100
GET /api/renders?dimension=1&scale=256k&limit=100
GET /api/renders/{id}
GET /api/locations/{locationId}/renders
```

One location can have several dated warps and renders. A warp's `archiveX`,
`archiveY`, and `archiveZ` describe its saved arrival position, which may differ
from the location marker. `worldDownloadDate` describes the historical capture,
not when Atlas added the record.

Renders include the location and source links, bounds, `tileUrlTemplate`,
`coordinateScheme`, `maxNativeZoom`, and `hasDayNight`. Their optional filters are
`locationId`, `dimension`, and `scale` (a case-insensitive stored scale label).
Read [the coordinate guide](COORDINATES-AND-RENDERS.md) before placing tiles.

When `blueMapUrl` is present, a standalone 3D viewer is available. To keep the
location context, open the render's `locationInteractiveUrl` with `view=3d` and
`render={renderId}` query parameters. Find objects supply their own
`savedObject.locationLink`, which also preserves the selected object and camera.
A missing `blueMapUrl` means that 3D is unavailable for this render; its 2D map
or world download may still be available. See [BlueMap views](BLUEMAP-3D.md).

## Download a world

WDL means **world download**. These ZIPs are partial historical Minecraft Java
saves. They do not contain the whole server. Work on a copy: Minecraft may
generate new terrain outside the saved area when you open one.

Start with a record's `worldDownloadMetadataUrl`. The metadata gives you
`downloadUrl`, file size, SHA-256 checksum, source, capture date, and known bounds.
Not every warp or render has a download.

```http
GET /api/warps/{id}/world-download
GET /api/warps/{id}/world-download.zip
GET /api/renders/{id}/world-download
GET /api/renders/{id}/world-download.zip
GET /api/maprenders/{id}/world-download.zip
```

Use the full returned download URL, including its `sha256` query parameter.
It prevents a retry from silently downloading a different file. A `409` means
the checksum no longer matches; fetch fresh metadata before trying again.
ZIP routes support byte ranges for resuming downloads.

Warp downloads use `scope: "bounded-footprint"` for the retained capture area.
Verified community or older render sources use `scope: "preserved-render-source"`;
the original ZIP may cover more terrain than the render. Those sources can exist
even when a render also has an Archive warp link. Shared-layer ZIPs may contain
other dimensions as well as the displayed layer.

Download one file at a time, keep the returned filename, and compare its SHA-256
with the metadata. For example, after saving a file as `world.zip`:

```powershell
Get-FileHash .\world.zip -Algorithm SHA256
```

On Linux, use `sha256sum world.zip`. The checksum should match exactly, ignoring
letter case. For bulk discovery, use the
[world-download catalog](https://2b2tatlas.com/world-downloads/catalog/) or
[JSONL export](https://2b2tatlas.com/entities/world-downloads.jsonl). JSONL stores
one JSON record per line. Static exports are snapshots; check the live metadata
before downloading.

## Attachments

```http
GET /api/attachments?locationId=904&limit=100
GET /api/attachments?mediaType=Image&limit=100
GET /api/attachments/{id}
```

`mediaType` can be `Image`, `Video`, `Wiki`, `Link`, `Timeline`, or `Article`.
Older records may use null, and new types may be added.

Open `path` for the item and `sourceUrl` for its source. Keep `caption` and
`attribution` when reusing media. `thumbnailPath` is optional. Keep video URLs
intact, including timestamps and case-sensitive IDs. Follow the response's
pagination headers to read further pages; listing metadata does not download the media.

## Shared map layers

```http
GET /api/maprenders
GET /api/maprenders/catalog
```

The first lists published shared layers. The catalog combines shared and
location layers, including built-in maps. Bounds use native block coordinates;
`coordinateScheme` describes tile placement. `defaultEnabled` controls the
initial display, not a user's saved layer choice. Follow `worldDownloadUrl`
when a source ZIP is available.

## Find

```http
GET /api/find/sources
GET /api/find/source-options
GET /api/find?dimension=overworld&kinds=sign&kinds=petrifiedOakSlab&sources=nocom&sources=primary&limit=25
GET /api/find?kind=sign&text=base&match=word&changedSigns=true
GET /api/find?kind=sign&hideBlankSigns=true
GET /api/find?kind=storage&sources=wdl&contentsRecorded=true
GET /api/find?kind=mob&sources=wdl&namedOnly=true&renderIds=19
GET /api/find/map?dimension=overworld&kinds=sign&minX=-200000&minZ=-200000&maxX=200000&maxZ=200000&cellSize=16384
GET /api/find/history?dimension=overworld&kind=sign&x=-126043&y=80&z=-77614
GET /api/find/inventory?observationId=2925260&limit=10
```

Find combines NoCom, public location WDLs and registered primary-map WDLs.
Use `/api/find/source-options` for compact picker metadata; use `/api/find/sources`
for record totals, indexed products and completion/coverage details.
Repeat `kinds` and `sources` to select their union. `sources` accepts `all`,
`nocom`, `wdl`, `primary`, or IDs from the sources endpoint. Repeated block captures
at the same category/XYZ count as one position; saved objects retain their capture
identity. Dimensions are the strings
`overworld`, `nether` and `end`, with native coordinates.
Requests that omit sources search all indexed public sources. Individual captures are selectable
by ID. Selecting both a family and one of its members does not double count it.

Search returns `total`, `nextOffset` and `items` (default 25, maximum 50).
Follow `nextOffset` with unchanged filters until it is null. Every matching result
can be browsed, including deep pages. `total` counts all matches. Every item retains a representative
observation and matching capture counts. History returns up to 50 observations
at the exact position across all visible sources, independent of search filters.

For map markers, use `/api/find/map` with all four inclusive rectangle bounds.
It applies the same category, source, text, height, date and optional radius
filters. `cellSize` is a power of two in blocks (default 256); large requests are
coarsened automatically. The response gives the actual `cellSize`, `total`, and
`items` with `mapCount` and native `mapBounds`. The sum of `mapCount` equals
`total`; list limits never hide map positions. Zoom for smaller groups, or use a
group's bounds and category with `/api/find` to page through its matches.
Preserve source, text, date, height and recorded-contents filters while paging.
`mapSummary` provides a position-weighted center, Y range and block-type counts.
It is safe to merge nearby groups visually, provided every original group remains
available for browsing. Increasing marker density must not truncate the results.

Categories are `sign`, `storage`, `portal`, `bedrock`, `spawner`, `endPortalFrame`,
`petrifiedOakSlab`, `mob`, `mapArt`, `banner`, `itemFrame` and `object`.
Rare blocks follow the documented classification policy. Storage excludes ender chest blocks, including in map counts and
capture history. Ender chest items saved inside other containers remain searchable.

`hideBlankSigns=true` excludes signs without saved text, including whitespace or
formatting alone. It applies to lists and maps and leaves other categories visible.
Positions with a written capture remain; history still includes blank versions.

The last five categories use positioned objects from location captures.
`namedOnly=true` requires a saved custom name. Repeat `renderIds` to restrict the
search to selected captures. Read attributes with
`GET /api/find/objects/{renderId}/{objectId}?generation={generation}` using the
returned capture and object identifiers. Unplaced map art remains available in
the capture gallery; it has no map position.

Set `hideEmpty=true` on either endpoint to require saved items in storage results,
excluding empty and unknown inventories. Other categories remain visible. It works
alone or with `contentsRecorded=true`; both filters apply to map groups and lists.

Set `contentsRecorded=true` on the search or map endpoint to show only storage
whose inventory was recorded, including saved empty containers. Position-only
NoCom observations and unknown inventories are excluded; other selected types
remain visible. Matching capture counts include only qualifying observations.
Filtered map overviews use precomputed summaries and keep inventory payloads
deferred until a container opens. The default is false.
Storage `detailsJson` is a JSON-encoded string. Parse it to read
`inventoryDeferred`, `inventoryListSaved`, block `properties` and, when available,
`savedDetailUrl`. If deferred data has no detail URL, use capture history to find
the observation with the same `id`. A recorded empty list is different from an
absent or unindexed inventory. Saved detail files may contain combined double
chests, item stacks, custom names, enchantments and nested shulker contents.
Use `/api/find/inventory?observationId={id}` with an observation ID from search
or history to read saved items directly, including deferred saved exports. It
returns `status`, nullable `total`, `nextOffset` and paged `items` (default 25,
maximum 50), plus container `metadata`, `sourceUrl` and `itemIconsUrl` when
available. Status is `recorded`, `empty`, `unknown`, `not_indexed` or `unavailable`;
unknown/unavailable totals are null. An absent or withdrawn observation returns
404. Observation IDs belong to the current index; discover fresh IDs after a rebuild.

Each item has `path`, nullable `contentsCount`, `savedDataOmitted` and `data`
(item ID, count, slot, custom name, enchantments and other retained fields).
Pass its `path` as `itemPath` to page a nested shulker or bundle. Set
`includeSavedData=true&limit=1` to include retained raw components/tags.
Follow `nextOffset` even when a response contains fewer items than requested;
large item payloads also bound a page. Inventory reads open only the requested
saved derivative. Normal map requests never fetch contents or item icons.

Equivalent MCP tools are `list_world_find_sources`, `search_world_find`,
`get_world_find_map`, `get_world_find_history` and `get_world_find_inventory`.
The search and map tools take the same filters in a `query` object. MCP map
groups are paged while `total` covers the full viewport. See [MCP](MCP.md) for examples.

To open a result in 3D, use `locationId` and the render ID from the
validated `captureUrl`: `/location/{locationId}?view=3d&render={renderId}&findX={x}&findY={y}&findZ={z}`.
Add `findFacing` from saved cardinal facing or sign rotation (0–15) when available.
Coordinates remain native to the capture's dimension. Captures without an owning
location retain their direct viewer link. NoCom records have no associated 3D WDL.

Importing a saved-object export does not establish complete raw-region coverage.
Source `status`, `products`, `completedParts` and `totalParts`
describe indexing coverage. The 1M source remains waiting until supported world
data is complete and verified. A zero count in a partial source does not prove
absence. Verified 2b2t.place ZVCR archives contribute their latest saved block
and block-entity state; older reverse-delta snapshots are not separate captures
in Find. Missing container NBT means contents are unknown, not an empty container.
Dates describe saved captures or recorded reads, not placement dates.
See [World Find](FIND.md) for exact filters, formats and indexing setup.

## NoCom and popular records

```http
GET /api/nocom
GET /api/nocom/periods?dimension=nether
GET /api/nocom/highways?dimension=nether&direction=northeast
GET /api/nocom/find/storage?x=858407&z=1177384&radius=1024&limit=5
GET /api/nocom/find/signs?text=base&limit=5
GET /api/nocom/find/portals?x=0&z=0&radius=1024&limit=5
GET /api/nocom/find/clusters?x=0&z=0&radius=8192&limit=5
GET /api/nocom/find/rare?kind=bedrock&limit=5
GET /api/nocom/find/sign-history?text=base&match=word&changedOnly=true
GET /api/nocom/find/special?kind=illegalSpawner
GET /api/nocom/hits
GET /api/nocom/hits/daily?from=2020-04-01&to=2020-04-07&dimension=overworld
GET /api/nocom/hits/chunks?date=2020-04-01&minX=-512&minZ=-512&maxX=511&maxZ=511
GET /api/nocom/hits/tiles/manifest
GET /api/popularity
```

NoCom data is historical. Observation counts are not player counts, and recorded
storage positions do not include inventories. See [NoCom](NOCOM.md) for filters,
coverage, and paging. `/api/popularity` lists popular public locations, groups,
and highways with daily totals and links.

Daily UTC hits expose full-source counts, paginated chunk records, complete daily
CSV exports and classic/contrast map tiles. These include legacy and final-period
observations absent from the older grouped product. Missing days mean no records
in the release, not proof of inactivity. See the [daily-data release](NOCOM.md#daily-world-pulse)
for coverage, query limits and all-source verification.

The interactive map offers Daily, Monthly and All time. Monthly uses the original
fixed 30-day buckets, not calendar months. Link humans to
`/map?dimension=overworld&nocom=pulse&day=2020-04-01`; use
`resolution=monthly&period=2020-04-08` for a grouped bucket or `resolution=all`.
The map preserves the selected UTC date across dimensions and navigation, and its
Copy link action retains the time scale. The independent highway-direction
source remains in 30-day buckets; daily chunk observations are not daily traveler counts.

The Home/About scale claim counts about 10.12 billion primary block rows plus
3.17 billion server-1 raw hit observations, over 13 billion records in total.
Repeated observations are included; derived monthly totals are not added again.
The packaged dataset's `nocomProcessedSourceRecords` gives exact components and
`nocomDaily` provides coverage and export links. These are source-record counts,
not claims of unique blocks, players, or a general query for every block type.

Sign-history search matches individual captured versions and actual read dates;
detail responses retain every timestamp. Special blocks expose one independently
corroborated illegal pig spawner and explicit zero-result End gateway scan coverage.
Ordinary and unverified spawners are excluded. See [sign and special-block evidence](NOCOM.md#sign-history-and-special-blocks).

The map's bedrock and End portal-frame layers have a separate static release.
Follow `rareBlocks.manifestUrl` in
[NoCom Find discovery JSON](https://2b2tatlas.com/nocom/find/dataset.json).
The storage, sign and portal REST/MCP queries do not return those rare blocks.
Bedrock excludes natural Y 0-4; frame totals count individual blocks, not complete
portals. The manifest supplies coverage, tiled files and integrity information.

Popularity accepts `days=7`, `30` (default), or `90` and caches results for 60
seconds. Counts approximate browser page views, not unique visitors. The website
shows ranked cards while keeping counts and daily charts hidden.

## Coordinates and missing fields

Modern dimension values are `0` Overworld, `1` Nether, and `2` End. Coordinates
use the record's own dimension. Do not multiply Nether values by eight unless
you deliberately want an Overworld projection.

Bounds include their minimum and exclude their maximum. For example, X bounds
`0` to `16` cover blocks 0 through 15. A bounding rectangle may contain missing
chunks. Tile `{y}` is a row index, not Minecraft elevation.

Dates, bounds, captions, hashes, and links can be null when unknown. Ignore JSON
fields your client does not use so newly added fields do not break it.

## Errors and retries

| Status | Meaning and action |
| --- | --- |
| `200` | Success. Read JSON, or ZIP bytes for a download route. |
| `206` | A requested byte range was returned. |
| `301` | A merged record redirects to its replacement. |
| `400` | Invalid parameters. Check the names, types, and ranges. |
| `404` | The record or public download is unavailable. |
| `409` | Download checksum changed. Refresh its metadata. |
| `429` | Too many requests. Wait for `Retry-After` when provided. |
| `503` | Data or a service is temporarily unavailable. Retry later. |

Honor cache headers and keep the last good catalog during an outage. For server
errors or timeouts, wait longer between each retry and stop after a few attempts.
A temporary Atlas failure should not stop a game or map from working.

## Older clients and the full schema

```http
GET /api/locations.php?search=spawn&rows=100&dimension=0&warps=true
GET /api/locationCount.php
GET /api
```

The PHP-compatible routes remain for older clients. Their dimension values are
different: `0` Overworld, `1` End, and `-1` Nether. New projects should use the
modern routes. The retired anonymous `newWarp.php` write returns `410 Gone`.

[`/api`](https://api.blackportal.cloud/api) lists API resources.
[OpenAPI](https://api.blackportal.cloud/openapi/v1.json) is the machine-readable
reference for current public routes, parameters, and response fields. It can be
used to generate a client library. Admin and worker operations require separate
credentials and are excluded from that public schema. Browser access to public
reads is allowed through CORS; no login is needed.

For AI clients, use [MCP](MCP.md). For your own installation, use
[the setup guide](../examples/atlas-stack/docs/DEPLOY_FROM_SCRATCH.md). That application is a historical source snapshot. For a running instance's
admin and worker contract, consult its permission-protected internal schema.

## Location URL roles

Location responses expose `recordUrl` (`/entities/locations/{id}/`) as a stable identity and provenance alias, and `interactiveUrl` (`/location/{id}`) for human links and citations. The legacy HTML URL permanently redirects (HTTP 301) to the interactive page, which includes the complete readable record and JSON-LD before JavaScript runs. Follow redirects for HTML; use `apiUrl`, MCP or JSONL for structured data. Existing URL fields and entity identifiers retain their values.

WDL, warp, render, media and REST group-build relationships use `locationRecordUrl` and `locationInteractiveUrl`; MCP group builds use `recordUrl` and `interactiveUrl`. JSONL location relationship objects carry the same explicit pair. NoCom research exposes a nullable `recordUrl`: coordinate-only requests do not invent an Atlas entity. Preserve original source URLs independently.

### Group and highway URL roles

Groups and highways follow the location model. `canonicalUrl` and `interactiveUrl`
point to `/group/{id}` or `/highway/{id}`; `recordUrl` retains the stable
`/entities/groups/{id}/` or `/entities/highways/{id}/` provenance alias; `apiUrl`
points to the public JSON endpoint. Legacy HTML URLs return permanent HTTP 301
redirects to the complete interactive records. The primary sitemap and ordinary
HTML navigation link to interactive pages. JSONL catalogs retain stable provenance
fields and remain available at their existing URLs.

This is additive: `pageUrl`, `groupUrl`, `groupApiUrl`, `highwayApiUrl` and
dimension-wide `mapUrl` retain their existing meanings. REST group highway
relationships expose the four standard fields alongside `highwayApiUrl`;
REST group attributions on locations/highways use `groupRecordUrl` and
`groupInteractiveUrl`. MCP group/highway detail, search and nested relationships
include `recordUrl` and `interactiveUrl`, including nearby-highway and NoCom area
research results. Prefer `interactiveUrl` when presenting an Atlas link to a
human; retain `recordUrl` for machine identity and provenance. Follow its redirect
for HTML, or use `apiUrl`, MCP or JSONL for structured data.

Published highway pages also include nearby catalog locations within 2,048
native-dimension blocks of the recorded polyline (up to 12, ordered by distance
then name/ID). No dimension conversion or portal connection is inferred.
NoCom counts are historical dimension/direction aggregates, shown only for
origin-based axis/diagonal geometry that matches the released corridor. They
are not route-specific traveler counts or evidence of present passability.
The optional interactive context mirrors the published snapshot; live API
records remain the source for current catalog metadata.

### Combined daily map ranges

The tile manifest now includes `range.urlTemplate`. Fill its `releaseId`,
`from`, `to`, `dimension`, `style`, `z`, `y`, and `x` placeholders. Dates are
inclusive UTC calendar dates, ordered, with a 4,096-day maximum covering the
entire current daily release. Use each dimension's existing signed tile grid
and native zoom bounds. One PNG combines every observed daily pixel, preserving
its strongest daily rendered intensity without stacking opacity. This is a
coverage union, **not a sum of per-chunk hit counts**; overview pixels remain
qualitative. Sum the manifest frames' observations for exact dimension-wide
range totals. Never add unique-chunk counts across days.

Human links use `/map?dimension=overworld&nocom=pulse&resolution=daily&from=2020-04-01&to=2020-04-07`.
The daily menu offers a two-handle range slider and exact start/end dates.
Responses are immutable PNGs; 404 means unknown release or absent tile, while
503 means unavailable data/capacity and includes Retry-After. Do not interpret
503 as no historical observations. Rendering uses two slots and a bounded 64 MiB
compressed-tile cache; it does not rescan the raw NoCom database or create new
permanent tile pyramids.

## Source validation and classification

Unknown or unavailable source IDs return HTTP 400, including mixed selections
with a valid source or `all`. MCP returns a tool error. Discover public IDs with
`/api/find/sources` or `list_world_find_sources`. A valid source in another
dimension can still return no matches. Explicit waiting, failed or empty partial
sources return HTTP 400 with their coverage status, including mixed selections.
Ready sources with zero matching records remain valid. Waiting sources remain
listed in coverage metadata and are excluded from the map picker.

Illegal bedrock, spawner and End portal observations include `classification.policyVersion`, `reasonCode` and
`reason`. Other categories return null. These fields describe the current
`illegal-v1` selection rule, not proof of placement or origin. Existing bedrock
bands and indexed records are unchanged. A map group's classification describes
its representative observation, not every block in that group.

A terrain render is not necessarily a separate captured world. Use source coverage,
not the total number of renders, to assess indexing.
