# Public API reference

Use Atlas data in a map, mod, website, or research tool. Public reads need no
account or API key.

**Base URL:** `https://api.blackportal.cloud`

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
| Map layer | A shared map, such as a spawn map or highway survey | `/api/maprenders` |

Locations, groups, and highways return complete lists. Warps, renders, and
attachments return one page at a time. NoCom Find uses a separate page format
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
last page. A browser may not expose these headers, so array-based paging works too.

## Locations

```http
GET /api/locations
GET /api/locations/{id}
GET /api/locations/aliases
```

Location fields include `rowid`, `name`, `description`, `tags`, `dimension`,
`x`, `y`, `z`, `wiki`, and `videoUrl`. Related records appear in `warps`, `renders`,
`attachments`, and `groups`. `apiUrl` opens the JSON record, `canonicalUrl` opens
its reference page, and `interactiveUrl` opens it in Atlas.

Fetch the list once and search it locally. There is no text-search parameter on
`/api/locations`; MCP also provides search tools. Cache the list instead of
requesting it every frame. A merged location ID redirects to its replacement;
`/api/locations/aliases` lists those mappings.

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

When `blueMapUrl` is present, open it for an interactive 3D view. A missing link
means that 3D is unavailable for this render; its 2D map or world download may
still be available. See [BlueMap views](BLUEMAP-3D.md).

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
[world-download catalog](https://2b2tatlas.com/entities/world-downloads/) or
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
intact, including timestamps and case-sensitive IDs. The
[Python media example](../examples/python/location_media.py) lists these fields
and follows pagination without downloading the media.

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

## NoCom and popular records

```http
GET /api/nocom
GET /api/nocom/periods?dimension=nether
GET /api/nocom/highways?dimension=nether&direction=northeast
GET /api/nocom/find/storage?x=858407&z=1177384&radius=1024&limit=5
GET /api/nocom/find/signs?text=base&limit=5
GET /api/nocom/find/portals?x=0&z=0&radius=1024&limit=5
GET /api/popularity
```

NoCom data is historical. Observation counts are not player counts, and recorded
storage positions do not include inventories. See [NoCom](NOCOM.md) for filters,
coverage, and paging. `/api/popularity` lists popular public locations, groups,
and highways with daily totals and links.

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

For AI clients, use [MCP](MCP.md). For a complete application, start with the
[Atlas stack](../examples/atlas-stack/README.md).
