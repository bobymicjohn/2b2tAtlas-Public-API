# 2b2tAtlas Public API

[![API status](https://img.shields.io/website?url=https%3A%2F%2Fapi.blackportal.cloud%2Fapi&label=public%20API)](https://api.blackportal.cloud/api)
[![OpenAPI](https://img.shields.io/badge/OpenAPI-live-6BA539)](https://api.blackportal.cloud/openapi/v1.json)
[![MCP](https://img.shields.io/badge/MCP-Streamable_HTTP-8b5cf6)](https://2b2tatlas.com/mcp/)
[![Official MCP Registry](https://img.shields.io/badge/MCP_Registry-io.github.bobymicjohn%2F2b2t--atlas-5b5fc7)](https://registry.modelcontextprotocol.io/?q=io.github.bobymicjohn%2F2b2t-atlas)
[![Data provided by 2b2tAtlas](https://img.shields.io/badge/data-2b2tAtlas-b45309)](https://2b2tatlas.com)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](LICENSE)

Build Minecraft mods, map overlays, Discord bots, history tools, waypoint exporters, and research projects with the public [2b2tAtlas](https://2b2tatlas.com) data API.

This is a documentation and examples repository. It does **not** contain the private Atlas application, collector, credentials, moderation tools, or server infrastructure.

## Start here

- API base: [`https://api.blackportal.cloud`](https://api.blackportal.cloud/api)
- Interactive Atlas: [`https://2b2tatlas.com`](https://2b2tatlas.com)
- Live OpenAPI contract: [`/openapi/v1.json`](https://api.blackportal.cloud/openapi/v1.json)
- MCP endpoint: [`https://api.blackportal.cloud/mcp`](https://2b2tatlas.com/mcp/)
- Official MCP Registry name: [`io.github.bobymicjohn/2b2t-atlas`](https://registry.modelcontextprotocol.io/?q=io.github.bobymicjohn%2F2b2t-atlas)
- Authentication: none for the public `GET` routes documented here
- Format: JSON over HTTPS; public reads allow browser CORS

```csharp
// Find a location
var locations = await http.GetFromJsonAsync<List<Location>>("api/locations");
var mu = locations!.First(x => x.Name.Equals("Mu Megabase", StringComparison.OrdinalIgnoreCase));

// Find bases by group
var groups = await http.GetFromJsonAsync<List<Group>>("api/groups");
var group = groups!.First(x => x.Name.Contains("DonFuer", StringComparison.OrdinalIgnoreCase));
var groupWithBuilds = await http.GetFromJsonAsync<Group>($"api/groups/{group.Id}");

// Query Archive warps for a location
var warps = await http.GetFromJsonAsync<List<Warp>>($"api/warps?locationId={mu.Rowid}&limit=100");

// Follow an eligible warp's worldDownloadMetadataUrl or worldDownloadUrl.
// These are bounded historical Java saves, not complete copies of 2b2t.

// Find locations with WDL-derived map renders
var renders = await http.GetFromJsonAsync<List<Render>>("api/renders?limit=1000");
var renderedLocationIds = renders!.Select(x => x.LocationId).Distinct().ToHashSet();

// Query public, reviewed highways and canals
var highways = await http.GetFromJsonAsync<List<Highway>>("api/highways");
```

The complete, runnable version is in [`examples/csharp`](examples/csharp). Dependency-free [JavaScript](examples/javascript), [Python](examples/python), and a [Fabric-oriented Java pattern](examples/fabric) are included too.

## Connect an AI assistant with MCP

2b2tAtlas exposes a public, stateless, read-only Model Context Protocol server. MCP clients can search and traverse the Atlas knowledge graph without downloading the entire catalog or teaching a model every REST relationship.

```json
{
  "mcpServers": {
    "2b2t-atlas": {
      "type": "http",
      "url": "https://api.blackportal.cloud/mcp"
    }
  }
}
```

The server offers 15 bounded tools for locations, nearby and historical searches, groups and their builds, highways, Archive warps, render provenance, WDL metadata, preserved builds, and dataset statistics. It also exposes stable resources such as `2b2tatlas://location/{id}`. See the complete [MCP client and tool guide](docs/MCP.md).

The canonical discovery record is published as [`io.github.bobymicjohn/2b2t-atlas`](https://registry.modelcontextprotocol.io/?q=io.github.bobymicjohn%2F2b2t-atlas) in the official MCP Registry. Its checked-in [`server.json`](server.json) and [OIDC publishing workflow](.github/workflows/publish-mcp-registry.yml) make the remote endpoint independently discoverable and every registry release reproducible.

MCP is an agent interface over the same reviewed Atlas records, not a second AI-generated database. It returns metadata and public HTTPS links rather than putting WDL ZIPs or render images into model context.

All runnable examples default to production. Set `ATLAS_API_BASE_URL` to point them at a mock or development server; the repository's CI uses this seam to test every example without generating bursts against the public service.

## What can I build?

| Project idea | Atlas data to use |
| --- | --- |
| JourneyMap/Xaero-style landmark layer | locations, dimensions, coordinates, canonical URLs |
| Historical base time machine | render footprints, dates, day/night tile templates |
| Highway and canal route planner | reviewed geometry, dimensions, widths, builder groups |
| Nether portal travel helper | Overworld/Nether coordinates plus local 8:1 conversion |
| `/whereis`, `/history`, or `/group` Discord bot | locations, warps, groups, builds, source links |
| Archive warp resolver | exact Archive warp identities and owning locations |
| Group lineage/build explorer | reciprocal group-to-build and group-to-highway records |
| Offline nearest-landmark search | cache `/api/locations` and build a local spatial index |
| WDL coverage dashboard | locations with renders, render dates, footprints, warp provenance |
| Offline archaeology / block analysis | immutable bounded-world ZIP, SHA-256, chunk count, exact bounds |
| LLM/RAG history corpus | static JSONL entity feeds, canonical pages, cited media records |
| MCP research assistant | bounded semantic tools, canonical resource URIs, reciprocal entity relationships |
| World-download browser or mirroring tool | WDL JSONL catalog, resumable ZIP links, checksums, scope warnings |

See [2b2t-specific project ideas](docs/2B2T-IDEAS.md) for more—including safe client-thread patterns, route overlays, pilgrimage lists, historical diffing, and source-aware research tools.

## Projects using 2b2tAtlas

- [XaeroTools](https://github.com/dekrom/xaerotools) is an open-source browser, merger, backup, and live-sharing toolkit for Xaero's World Map and XaeroPlus data. Its optional 2b2tAtlas overlay loads community-documented locations with Atlas source links, and it can mirror Atlas map imagery for local use.

Built something with the API? Open an [integration showcase](https://github.com/bobymicjohn/2b2tAtlas-Public-API/issues/new?template=integration-showcase.yml) so other players and tool authors can find it.

## Public endpoints

| Resource | Routes | Useful relationships |
| --- | --- | --- |
| Locations | `GET /api/locations`, `GET /api/locations/{id}` | warps, attachments, renders, builder groups |
| Archive warps | `GET /api/warps`, `GET /api/warps/{id}` | owning location, WDL date/SHA and bounded-world links when available |
| Archive world ZIPs | `GET /api/warps/{id}/world-download`, `GET /api/warps/{id}/world-download.zip` | size, digest, bounds, resumable immutable Java-save download |
| WDL renders | `GET /api/renders`, `GET /api/renders/{id}`, `GET /api/locations/{id}/renders` | location, Archive warp or preserved source, tile template, footprint |
| Legacy render-source ZIPs | `GET /api/renders/{id}/world-download`, `GET /api/renders/{id}/world-download.zip` | verified pre-Archive/community source, digest, provenance, resumable download |
| Historical media | `GET /api/attachments`, `GET /api/attachments/{id}` | location, source, caption, attribution |
| Groups | `GET /api/groups`, `GET /api/groups/{id}` | aliases, attributed builds and highways |
| Highways | `GET /api/highways`, `GET /api/highways/{id}` | geometry and reviewed group roles |
| Map layers | `GET /api/maprenders`, `GET /api/maprenders/catalog` | primary layers plus per-location renders |

The [API reference](docs/API-REFERENCE.md) explains filters, paging, dimensions, stable links, errors, and caching. The live OpenAPI document is the machine-readable source of truth.

## 2b2t-aware client guidance

1. Fetch in a background thread. Never block Minecraft's render or client tick thread on HTTP.
2. Cache responses. Location/group/highway reads are cacheable for at least 60 seconds; a mod should usually cache much longer or keep an offline snapshot.
3. Follow `apiUrl`, `canonicalUrl`, `interactiveUrl`, `locationApiUrl`, and similar link fields instead of rebuilding URLs.
4. Treat coordinates as historical public records—not proof that a base is active, intact, safe, or loaded on the live server.
5. When practical, keep `sourceUrl`, `attribution`, and evidence fields with redistributed media or historical claims so their history remains traceable.
6. Tolerate additive JSON fields. Public `GET` contracts are stable, but the catalog continues to grow.

## Static and agent-friendly data

For crawlers, archives, bulk research, and language-model tools, 2b2tAtlas also publishes:

- [`llms.txt`](https://2b2tatlas.com/llms.txt)
- [`dataset.json`](https://2b2tatlas.com/dataset.json)
- [`locations.jsonl`](https://2b2tatlas.com/entities/locations.jsonl)
- [`groups.jsonl`](https://2b2tatlas.com/entities/groups.jsonl)
- [`media.jsonl`](https://2b2tatlas.com/entities/media.jsonl)
- [`world-downloads.jsonl`](https://2b2tatlas.com/entities/world-downloads.jsonl)
- [crawlable world-download catalog](https://2b2tatlas.com/entities/world-downloads/)
- [MCP server guide](https://2b2tatlas.com/mcp/) and remote endpoint at `https://api.blackportal.cloud/mcp`
- [official MCP Registry record](https://registry.modelcontextprotocol.io/?q=io.github.bobymicjohn%2F2b2t-atlas) under `io.github.bobymicjohn/2b2t-atlas`
- canonical HTML/JSON-LD pages under `/entities/locations/{id}/` and `/entities/groups/{id}/`

Use the live API for interactive applications and the static feeds for deliberate bulk ingestion. The WDL feed identifies every available ZIP as a partial Java save and links it to its canonical location and render, plus its exact Archive warp when one exists. `sourceType` and `scope` distinguish collector-bounded snapshots from verified preserved sources behind older/community renders. See [LLM and bulk-data guidance](docs/2B2T-IDEAS.md#llm-search-and-research-tools).

## Credit the data

Use Atlas data however you want. No Atlas credit or permission is required. If Atlas data is visible or materially powers your project, this simple optional credit helps players find the historical source:

```markdown
Data provided by [2b2tAtlas](https://2b2tatlas.com).
```

For a mod About screen, README badge, website footer, or machine-readable notice, see [ATTRIBUTION.md](ATTRIBUTION.md). Original-source fields returned with attachments and evidence are kept so downstream projects can credit and verify them too.

## Repository map

```text
examples/
  csharp/       complete .NET console example
  fabric/       async Java/Fabric integration pattern
  javascript/   dependency-free Node example
  python/       dependency-free Python example
  requests.http copy-ready REST Client requests
docs/
  API-REFERENCE.md
  MCP.md
  COORDINATES-AND-RENDERS.md
  2B2T-IDEAS.md
```

## Contributing and integrations

- Open an [integration showcase](https://github.com/bobymicjohn/2b2tAtlas-Public-API/issues/new?template=integration-showcase.yml) when your tool uses the API.
- Report unclear or stale documentation through the issue templates.
- Add examples in another language or a small integration recipe through a pull request.
- Ask for a new **public read** projection by describing the player/developer use case; never post credentials or non-public coordinates.

Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting code. Questions and project demos are welcome in GitHub Discussions.

## Stability and affiliation

Public read models may gain fields as the historical graph grows. Clients should ignore unknown JSON properties and use nullable handling for incomplete historical metadata. Write/admin endpoints are intentionally outside this repository.

2b2tAtlas is a community historical project and is not affiliated with Mojang Studios, Microsoft, or the operators of 2b2t. Minecraft names and assets belong to their respective owners.

## Design references

The repository's task-first examples and integration guidance borrow useful documentation patterns from [HypixelDev/PublicAPI](https://github.com/HypixelDev/PublicAPI), [GTNewHorizons/Navigator](https://github.com/GTNewHorizons/Navigator), and [odds-api/odds-api](https://github.com/odds-api/odds-api), adapted to the very different needs of a historical 2b2t map and entity graph.

## License

Repository-authored examples and documentation are released under the [Unlicense](LICENSE): copy, modify, publish, commercialize, or remix them for any purpose without permission or required attribution. Atlas likewise places no attribution condition on reuse of its factual API catalog; a link back is simply appreciated. Some records reference third-party media whose original source terms remain separate, as explained in [NOTICE.md](NOTICE.md).
