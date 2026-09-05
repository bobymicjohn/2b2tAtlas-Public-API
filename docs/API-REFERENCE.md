# Public API reference

Base URL: `https://api.blackportal.cloud`

The routes in this document are anonymous, read-only, JSON endpoints. The live [API discovery document](https://api.blackportal.cloud/api) and [OpenAPI contract](https://api.blackportal.cloud/openapi/v1.json) are authoritative.

## Common behavior

- Send `Accept: application/json`.
- Browser CORS is enabled for public reads.
- Location, group, highway, warp, and render reads are generally cached at the HTTP layer for 60 seconds.
- List responses are JSON arrays, not `{ data, page }` envelopes.
- `warps`, `renders`, and `attachments` use `limit`/`offset` paging. `limit` defaults to 500 and is clamped to 1–1000.
- Use returned link fields such as `apiUrl`, `canonicalUrl`, `interactiveUrl`, `locationApiUrl`, and `groupApiUrl`.
- Expect nullable fields: historical dates, source hashes, exact bounds, captions, and links are not known for every record.
- Ignore response properties your client does not recognize. New public metadata is additive.

Recommended request headers:

```http
Accept: application/json
User-Agent: YourProjectName/1.0 (+https://github.com/you/your-project)
```

## Dimensions

| Value | Dimension | Coordinate note |
| ---: | --- | --- |
| `0` | Overworld | Native Overworld X/Y/Z |
| `1` | Nether | Native Nether X/Y/Z; do not assume returned coordinates are already multiplied by eight |
| `2` | End | Native End X/Y/Z |

Use the record's `dimension`/`dimensionName`. Never infer a dimension from a name alone.

## Locations

### List locations

```http
GET /api/locations
```

Returns the complete location catalog. Each location can include:

- `rowid`, `name`, `description`, `tags`;
- `dimension`, `dimensionName`, `x`, `y`, `z`;
- `dateAddedUtc`, `modifiedUtc`;
- `wiki`, `videoUrl`;
- `warps[]`, `attachments[]`, `renders[]`, `groups[]`;
- counts and stable API/canonical/interactive links.

This endpoint is intentionally a rich bulk response. Fetch it on startup or refresh, cache it, and build local indexes for name, group, coordinate, and nearest-neighbor searches. Do not call it every tick or map frame.

### Get one location

```http
GET /api/locations/5
```

Returns `404` when the integer ID does not exist.

### Find by name client-side

There is no modern server-side text-search parameter. Normalize and index the cached location list:

```javascript
const normalize = value => value.normalize("NFKC").trim().toLocaleLowerCase("en-US");
const locations = await fetch(`${base}/api/locations`).then(r => r.json());
const exact = locations.find(x => normalize(x.name) === normalize("Mu Megabase"));
```

The legacy `/api/locations.php?search=...` remains available for old consumers, but new projects should prefer the richer modern model.

## Groups and their builds

### List groups

```http
GET /api/groups
```

Summary rows include identity, exact reviewed aliases, classification, history, public links, status/founding labels, and attributed build/highway counts.

### Get a group with reciprocal relationships

```http
GET /api/groups/7
```

The detail response adds:

- `locations[]`: build name, role, dimension, X/Z, render count, and links;
- `highways[]`: infrastructure name, role, dimension, map URL, and API URL.

Resolve a group name or alias from the list first, then follow its `apiUrl`. This avoids hard-coding database IDs.

## Archive warps

### List or filter

```http
GET /api/warps?limit=500&offset=0
GET /api/warps?locationId=5&limit=100
```

Warp records include their exact Archive identity, owning location, dimension, canonical links, and—when known—world-download date, Archive SHA-256, provenance, Archive coordinates, `worldDownloadMetadataUrl`, `worldDownloadUrl`, and `worldDownloadScope`.

An Archive warp identifies at most one WDL/render snapshot. One Atlas location may have multiple warps from different dates or variants.

### Get one warp

```http
GET /api/warps/145
```

### Download a bounded historical world

Eligible collector warps expose two stable links:

```http
GET /api/warps/8/world-download
GET /api/warps/8/world-download.zip
```

The first returns JSON metadata including the owning location, ZIP byte length, SHA-256, dimension, retained chunk count and half-open bounds when known, source date/provenance, and an explicit fidelity warning. The ZIP route supports HTTP byte ranges and uses the SHA-256 as its ETag, so large downloads can be resumed and immutable copies safely deduplicated.

These files are **bounded historical Minecraft Java saves**, not complete 2b2t worlds and not the collector's broader raw survey. They contain the chunks retained for one Archive warp/render footprint. Minecraft may generate new terrain outside the retained footprint if a save is opened normally; analyze a copy and prevent chunk generation when historical fidelity matters.

Use the returned URL rather than constructing it. Not every warp has a downloadable object. `404` means no public collector WDL is attached; `503` means the catalog record exists but its archived bytes are temporarily unavailable. Bulk tools should download serially or with very low concurrency and honor `429`/`Retry-After`.

Resume an interrupted download and then verify it:

```bash
curl --fail --location --continue-at - \
  https://api.blackportal.cloud/api/warps/8/world-download.zip \
  --output 2b2tAtlas-warp-8.zip
sha256sum 2b2tAtlas-warp-8.zip
```

## WDL-derived renders

### List and filter

```http
GET /api/renders?limit=1000&offset=0
GET /api/renders?locationId=5
GET /api/renders?dimension=1&limit=500
GET /api/renders?scale=256k&limit=500
```

Supported filters:

| Parameter | Meaning |
| --- | --- |
| `locationId` | Owning Atlas location ID |
| `dimension` | `0`, `1`, or `2` |
| `scale` | Case-insensitive stored scale label |
| `limit` | 1–1000 |
| `offset` | Non-negative rows to skip |

A render can expose:

- `locationId`, `locationName`, and location links;
- `archiveWarpId`, `archiveWarpName`, and warp API link;
- `worldDownloadDate`, `source`, and description;
- exact `minX`, `minZ`, `maxXExclusive`, `maxZExclusive` footprint;
- `tileUrlTemplate`, `hasDayNight`, `maxNativeZoom`, and `coordinateScheme`.

### Get one render or a location's renders

```http
GET /api/renders/38
GET /api/locations/5/renders
```

Read [coordinates and render tiles](COORDINATES-AND-RENDERS.md) before implementing an overlay.

## Attachments and historical media

```http
GET /api/attachments?limit=500&offset=0
GET /api/attachments?locationId=1030
GET /api/attachments?mediaType=Image&limit=100
GET /api/attachments/1
```

`mediaType` values currently used by the catalog include `Image`, `Video`, `Wiki`, and `Link`. Treat the vocabulary as extensible.

Useful fields include `path`, `thumbnailPath`, `sourceUrl`, `caption`, and `attribution`. A self-hosted path is a convenience copy; the original-source fields make optional credit and verification straightforward when presenting or redistributing it.

## Highways and canals

```http
GET /api/highways
GET /api/highways/1
```

Anonymous reads return only public, approved infrastructure. Records can describe:

- polyline `points[]` in native dimension coordinates;
- `width`, `height`, `yLevel`, and `ringRadius` when applicable;
- paving/enclosure/lighting metadata;
- `builderGroups[]` with reviewed roles and evidence links;
- a primary-builder compatibility view;
- `mapUrl` and `apiUrl`.

Disconnected construction is represented as separate records rather than a fictional line across an unbuilt gap. Do not automatically join same-named segments.

## Map render catalog

```http
GET /api/maprenders
GET /api/maprenders/catalog
```

`/api/maprenders` is the dimension-level primary-layer registry and may be empty. `/api/maprenders/catalog` combines primary layers with the per-location render catalog used by Atlas map overlays.

## Legacy compatibility

These read-only routes exist for consumers of the original API:

```http
GET /api/locations.php?search=spawn&rows=100&dimension=0&warps=true
GET /api/locationCount.php
```

Legacy dimension values differ: `0` Overworld, `1` End, and `-1` Nether. Do not mix legacy and modern dimension enums.

The former anonymous write route is gone. New projects must not depend on anonymous mutations.

## Errors and retries

Handle status codes conventionally:

- `200`: parse JSON;
- `404`: the requested integer entity does not exist or is not public;
- `429`: honor `Retry-After` when present and back off;
- `5xx`: retry a small number of times with exponential backoff and jitter;
- network/timeout: use cached data and retry later.

There is no reason for a client mod to fail the game because history data is temporarily unavailable. Keep the last known good snapshot and surface a quiet stale/offline state.

## OpenAPI and generated clients

Download the current contract when generating a client:

```bash
curl --fail --location \
  https://api.blackportal.cloud/openapi/v1.json \
  --output openapi.json
```

Example with OpenAPI Generator:

```bash
openapi-generator-cli generate \
  -i https://api.blackportal.cloud/openapi/v1.json \
  -g typescript-fetch \
  -o generated/atlas-client
```

Generated clients should still tolerate nullable historical fields and additive properties.
