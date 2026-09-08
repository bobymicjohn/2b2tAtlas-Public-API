# 2b2tAtlas Public API

Documentation and examples for the [2b2tAtlas](https://2b2tatlas.com) public API:
historical locations, groups, highways, Archive warps, world downloads, map renders,
and Nocom observation aggregates.

**Base URL:** `https://api.blackportal.cloud`. Public reads require no API key.
JSON responses support browser CORS.

- [API reference](docs/API-REFERENCE.md)
- [Live OpenAPI schema](https://api.blackportal.cloud/openapi/v1.json)
- [Coordinates and render tiles](docs/COORDINATES-AND-RENDERS.md)
- [BlueMap 3D views](docs/BLUEMAP-3D.md)
- [Nocom data](docs/NOCOM.md)
- [MCP setup and tools](docs/MCP.md)
- [Changelog](CHANGELOG.md)

## Requests

```sh
curl --fail "https://api.blackportal.cloud/api/locations"
curl --fail "https://api.blackportal.cloud/api/groups"
curl --fail "https://api.blackportal.cloud/api/renders?limit=10"
curl --fail "https://api.blackportal.cloud/api/nocom/highways?dimension=nether&direction=northeast"
```

List responses are JSON arrays. Warps, renders, and attachments accept `limit`
and `offset`; the default limit is 500 and the maximum is 1,000. Locations return
the complete catalog.

## Examples

Run from the repository root:

```sh
dotnet run --project examples/csharp -- "Mu Megabase"
node examples/javascript/atlas-examples.mjs "Mu Megabase"
python examples/python/atlas_examples.py "Mu Megabase"
python examples/python/nocom_activity.py --dimension nether --direction northeast
python examples/python/location_media.py 1254
```

The C# example uses .NET 8. The JavaScript and Python examples use their standard
libraries. Each defaults to the public API; set `ATLAS_API_BASE_URL` to use a
local fixture or development server.

[examples/requests.http](examples/requests.http) contains REST Client requests.
[examples/fabric](examples/fabric) contains an asynchronous Java HTTP client and
Minecraft client-thread handoff example; it is not a complete mod.

## Endpoints

All routes below use `GET`. See the [reference](docs/API-REFERENCE.md) for response
fields, filters, caching, and errors.

| Resource | Routes |
| --- | --- |
| Locations | `/api/locations`, `/api/locations/{id}` |
| Groups | `/api/groups`, `/api/groups/{id}` |
| Highways | `/api/highways`, `/api/highways/{id}` |
| Archive warps | `/api/warps`, `/api/warps/{id}` |
| Warp WDL metadata and ZIP | `/api/warps/{id}/world-download`, `/api/warps/{id}/world-download.zip` |
| Renders | `/api/renders`, `/api/renders/{id}`, `/api/locations/{id}/renders` |
| Preserved render-source WDL metadata and ZIP | `/api/renders/{id}/world-download`, `/api/renders/{id}/world-download.zip` |
| Historical media | `/api/attachments`, `/api/attachments/{id}` |
| Map layers | `/api/maprenders`, `/api/maprenders/catalog` |
| Nocom | `/api/nocom`, `/api/nocom/periods`, `/api/nocom/highways` |

## Client behavior

- Cache catalog responses and honor HTTP cache headers. In Minecraft clients,
  perform HTTP requests and image decoding outside the render/tick thread.
- Follow returned links such as `apiUrl`, `canonicalUrl`, `worldDownloadUrl`,
  and `blueMapUrl`. BlueMap links are optional and belong to a specific render.
- Handle null fields and ignore unknown JSON properties.
- Coordinates use the record's native dimension: `0` Overworld, `1` Nether,
  `2` End. Tile-template `{y}` is a tile row, not Minecraft elevation.
- WDLs are partial historical Java saves. Their metadata includes scope,
  provenance, size, and SHA-256; ZIP downloads support HTTP ranges.
- Historical coordinates and renders do not describe the current server state.
  Nocom observation counts are not unique-player counts.
- Retain source URLs, captions, attribution, and evidence fields when copying
  records so the original material can be checked.

## MCP

Endpoint: `https://api.blackportal.cloud/mcp`. Transport: Streamable HTTP.
The server is stateless and read-only, with no authentication required.

The [MCP guide](docs/MCP.md) includes client configuration, the 18 tools, and
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
| [media.jsonl](https://2b2tatlas.com/entities/media.jsonl) | Historical media records |
| [world-downloads.jsonl](https://2b2tatlas.com/entities/world-downloads.jsonl) | Available partial WDLs, checksums, and source links |
| [llms.txt](https://2b2tatlas.com/llms.txt) | Data and documentation index |

Use these exports for bulk imports. Keep entity IDs and canonical URLs with
imported records. The [WDL catalog](https://2b2tatlas.com/entities/world-downloads/)
also has an HTML view.

## Existing integration

[XaeroTools](https://github.com/dekrom/xaerotools) supports an optional Atlas
location overlay with source links and local copies of Atlas map imagery.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for example checks and pull requests.
Report documentation errors and broken examples through
[issues](https://github.com/bobymicjohn/2b2tAtlas-Public-API/issues).

## License

Examples and documentation are released under the [Unlicense](LICENSE).
Atlas does not require attribution for its factual API catalog.
[ATTRIBUTION.md](ATTRIBUTION.md) has optional credit formats;
[NOTICE.md](NOTICE.md) covers third-party media and source terms.

2b2tAtlas is not affiliated with Mojang Studios, Microsoft, or the operators of 2b2t.
