# 2b2tAtlas API Examples

[![API status](https://img.shields.io/website?url=https%3A%2F%2Fapi.blackportal.cloud%2Fapi&label=public%20API)](https://api.blackportal.cloud/api)
[![OpenAPI](https://img.shields.io/badge/OpenAPI-live-6BA539)](https://api.blackportal.cloud/openapi/v1.json)
[![Data provided by 2b2tAtlas](https://img.shields.io/badge/data-2b2tAtlas-b45309)](https://2b2tatlas.com)

Build Minecraft mods, map overlays, Discord bots, history tools, waypoint exporters, and research projects with the public [2b2tAtlas](https://2b2tatlas.com) data API.

This is a documentation and examples repository. It does **not** contain the private Atlas application, collector, credentials, moderation tools, or server infrastructure.

## Start here

- API base: [`https://api.blackportal.cloud`](https://api.blackportal.cloud/api)
- Interactive Atlas: [`https://2b2tatlas.com`](https://2b2tatlas.com)
- Live OpenAPI contract: [`/openapi/v1.json`](https://api.blackportal.cloud/openapi/v1.json)
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

// Find locations with WDL-derived map renders
var renders = await http.GetFromJsonAsync<List<Render>>("api/renders?limit=1000");
var renderedLocationIds = renders!.Select(x => x.LocationId).Distinct().ToHashSet();

// Query public, reviewed highways and canals
var highways = await http.GetFromJsonAsync<List<Highway>>("api/highways");
```

The complete, runnable version is in [`examples/csharp`](examples/csharp). Dependency-free [JavaScript](examples/javascript), [Python](examples/python), and a [Fabric-oriented Java pattern](examples/fabric) are included too.

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
| LLM/RAG history corpus | static JSONL entity feeds, canonical pages, cited media records |

See [2b2t-specific project ideas](docs/2B2T-IDEAS.md) for more—including safe client-thread patterns, route overlays, pilgrimage lists, historical diffing, and source-aware research tools.

## Public endpoints

| Resource | Routes | Useful relationships |
| --- | --- | --- |
| Locations | `GET /api/locations`, `GET /api/locations/{id}` | warps, attachments, renders, builder groups |
| Archive warps | `GET /api/warps`, `GET /api/warps/{id}` | owning location, WDL date/SHA when known |
| WDL renders | `GET /api/renders`, `GET /api/renders/{id}`, `GET /api/locations/{id}/renders` | location, Archive warp, tile template, footprint |
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
5. Preserve `sourceUrl`, `attribution`, and evidence fields when redistributing media or historical claims.
6. Tolerate additive JSON fields. Public `GET` contracts are stable, but the catalog continues to grow.

## Static and agent-friendly data

For crawlers, archives, bulk research, and language-model tools, 2b2tAtlas also publishes:

- [`llms.txt`](https://2b2tatlas.com/llms.txt)
- [`dataset.json`](https://2b2tatlas.com/dataset.json)
- [`locations.jsonl`](https://2b2tatlas.com/entities/locations.jsonl)
- [`groups.jsonl`](https://2b2tatlas.com/entities/groups.jsonl)
- [`media.jsonl`](https://2b2tatlas.com/entities/media.jsonl)
- canonical HTML/JSON-LD pages under `/entities/locations/{id}/` and `/entities/groups/{id}/`

Use the live API for interactive applications and the static feeds for deliberate bulk ingestion. See [LLM and bulk-data guidance](docs/2B2T-IDEAS.md#llm-search-and-research-tools).

## Credit the data

If Atlas data is visible or materially powers your project, this simple credit helps players find the historical source:

```markdown
Data provided by [2b2tAtlas](https://2b2tatlas.com).
```

For a mod About screen, README badge, website footer, or machine-readable notice, see [ATTRIBUTION.md](ATTRIBUTION.md). Preserve source-specific credits returned with attachments and evidence; an Atlas credit does not replace them.

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
  COORDINATES-AND-RENDERS.md
  2B2T-IDEAS.md
```

## Contributing and integrations

- Open an [integration showcase](https://github.com/jbrack14/2b2tAtlas-API-Examples/issues/new?template=integration-showcase.yml) when your tool uses the API.
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

Example code and repository-authored documentation are available under the [MIT License](LICENSE). API records can include third-party media and claims with their own source/attribution fields; consult [NOTICE.md](NOTICE.md) before redistributing those assets.
