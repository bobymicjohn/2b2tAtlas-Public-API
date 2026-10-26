# 2b2tAtlas Public API

Use [Atlas](https://2b2tatlas.com) data in your own map, mod or research tool.
Look up places, groups and highways, find a saved world, or search captured
signs, inventories, mobs and map art. Public reads need no account or API key.

This repo has the API reference, HTTP examples and a
[dated copy of the application source](examples/atlas-stack/README.md).

Checked October 26, 2026: **57 public GET routes and 38 read-only MCP tools**.
Recorded response examples are from October 19; values and counts can change.
Use [the live website reference](https://2b2tatlas.com/api/) or the
[complete route catalog](docs/reference/api-reference.json) for exact parameters.

**Base URL:** `https://api.blackportal.cloud`.
You can make requests from a browser, a command line, or your own code.

- [API reference](docs/API-REFERENCE.md)
- [Live OpenAPI schema](https://api.blackportal.cloud/openapi/v1.json)
- [Coordinates and render tiles](docs/COORDINATES-AND-RENDERS.md)
- [BlueMap 3D views](docs/BLUEMAP-3D.md)
- [Unified Find, inventories and saved objects](docs/FIND.md)
- [Nocom data](docs/NOCOM.md)
- [MCP setup and tools](docs/MCP.md)
- [Current facts and canonical links for integrations](docs/DISCOVERY.md)
- [Changelog](CHANGELOG.md)
- [Source versions and the current application stack](docs/SOURCE-STATUS.md)
- [Application source snapshot](examples/atlas-stack/README.md) - September 26, 2026 source and setup instructions.

## Try it

Open [the location list](https://api.blackportal.cloud/api/locations) in a browser,
or run one of these commands. On Windows PowerShell 5.1, use `curl.exe`.

```sh
curl --fail "https://api.blackportal.cloud/api/locations"
curl --fail "https://api.blackportal.cloud/api/groups"
curl --fail "https://api.blackportal.cloud/api/renders?limit=10"
curl --fail "https://api.blackportal.cloud/api/nocom/highways?dimension=nether&direction=northeast"
```

List responses are JSON arrays. Warps, renders, and attachments accept `limit`
and `offset`; the default limit is 500 and the maximum is 1,000. Locations return
the complete catalog.

For a location picker or directory, use `/api/locations/directory`. It returns
compact rows with names, groups, counts and preferred actions. Fetch
`/api/locations/{id}` when someone opens a record for its full history and media.

## Request examples

[examples/requests.http](examples/requests.http) contains ordinary HTTP requests
for catalog records, Find, inventories and historical observations. Use the API
from whatever language or HTTP tool you already use. No Atlas client package is required.

The [application snapshot](examples/atlas-stack/README.md) is separate from the
current API reference. Its setup guides describe the September 26, 2026 source;
they do not describe later production features or runtime changes.

## Endpoints

All routes below use `GET`. See the [reference](docs/API-REFERENCE.md) for response
fields, filters, caching, and errors.

| Resource | Routes |
| --- | --- |
| Locations | `/api/locations`, `/api/locations/directory`, `/api/locations/map`, `/api/locations/{id}`, `/api/locations/aliases` |
| Groups | `/api/groups`, `/api/groups/{id}` |
| Highways | `/api/highways`, `/api/highways/{id}` |
| Archive warps | `/api/warps`, `/api/warps/{id}` |
| Warp WDL metadata and ZIP | `/api/warps/{id}/world-download`, `/api/warps/{id}/world-download.zip` |
| Renders | `/api/renders`, `/api/renders/{id}`, `/api/locations/{id}/renders` |
| Preserved render-source WDL metadata and ZIP | `/api/renders/{id}/world-download`, `/api/renders/{id}/world-download.zip` |
| Historical media | `/api/attachments`, `/api/attachments/{id}` |
| Map layers | `/api/maprenders`, `/api/maprenders/catalog` |
| Unified Find | `/api/find`, `/api/find/map`, `/api/find/sources`, `/api/find/source-options`, `/api/find/history`, `/api/find/inventory`, `/api/find/objects/{renderId}/{objectId}` |
| Saved galleries | `/api/renders/{renderId}/explore/objects`, `/api/renders/{renderId}/explore/objects/{id}` and its `/art.png` preview |
| NoCom | `/api/nocom`, `/api/nocom/periods`, `/api/nocom/highways`, `/api/nocom/hits` and its daily/chunk/tile routes, `/api/nocom/find/*` |
| Popularity | `/api/popularity?days=7`, `30` or `90` |

## Client behavior

- Cache catalog responses and honor HTTP cache headers. In Minecraft clients,
  perform HTTP requests and image decoding outside the render/tick thread.
- Use `interactiveUrl` for a location page, `apiUrl` for JSON, and
  `worldDownloadUrl` for a saved world. For a Find object's 3D destination,
  prefer `savedObject.locationLink`, resolved against `https://2b2tatlas.com`.
  `blueMapUrl` opens the standalone viewer and is optional and render-specific.
- Handle null fields and ignore unknown JSON properties.
- Coordinates use the record's native dimension: `0` Overworld, `1` Nether,
  `2` End. Tile-template `{y}` is a tile row, not Minecraft elevation.
- WDLs are partial historical Java saves. Their metadata includes scope,
  provenance, size, and SHA-256; ZIP downloads support HTTP ranges.
- Historical coordinates and renders do not describe the current server state.
  Nocom observation counts are not unique-player counts.
- Saved inventories come from WDL captures. NoCom records container positions,
  not their contents. Neither an old capture nor a missing result tells you that
  a chest was later emptied or looted.
- Retain source URLs, captions, attribution, and evidence fields when copying
  records so the original material can be checked.

## MCP

Endpoint: `https://api.blackportal.cloud/mcp`. Transport: Streamable HTTP.
The server is stateless and read-only, with no authentication required.

The [MCP guide](docs/MCP.md) includes client configuration, the current tools, and
resource URIs. The registry name is
[`io.github.bobymicjohn/2b2t-atlas`](https://registry.modelcontextprotocol.io/?q=io.github.bobymicjohn%2F2b2t-atlas);
its manifest is [server.json](server.json).

## Bulk data

Static exports are hosted on `2b2tatlas.com`:

| File | Content |
| --- | --- |
| [dataset.json](https://2b2tatlas.com/dataset.json) | Dataset metadata and catalog links |
| [locations.jsonl](https://2b2tatlas.com/entities/locations.jsonl) | Location records |
| [groups.jsonl](https://2b2tatlas.com/entities/groups.jsonl) | Groups and relationships |
| [highways.jsonl](https://2b2tatlas.com/entities/highways.jsonl) | Approved highways and relationships |
| [media.jsonl](https://2b2tatlas.com/entities/media.jsonl) | Historical media records |
| [world-downloads.jsonl](https://2b2tatlas.com/entities/world-downloads.jsonl) | Available partial WDLs, checksums, and source links |
| [llms.txt](https://2b2tatlas.com/llms.txt) | Data and documentation index |

Use these exports for bulk imports. Keep entity IDs and canonical URLs with
imported records. The [WDL catalog](https://2b2tatlas.com/world-downloads/catalog/)
also has an HTML view.

## Existing integration

[XaeroTools](https://github.com/dekrom/xaerotools) supports an optional Atlas
location overlay with source links and local copies of Atlas map imagery.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for local checks and pull requests.
Report documentation errors and incorrect request examples through
[issues](https://github.com/bobymicjohn/2b2tAtlas-Public-API/issues).

## License

Atlas-authored code, documentation and the [full stack](examples/atlas-stack/README.md)
are released under the [Unlicense](LICENSE). Use, modify and share them however you like;
no permission or Atlas attribution is required. The same goes for Atlas's factual catalog.
[ATTRIBUTION.md](ATTRIBUTION.md) has optional credit formats;
[NOTICE.md](NOTICE.md) covers third-party media and source terms.

2b2tAtlas is not affiliated with Mojang Studios, Microsoft, or the operators of 2b2t.
