# 2b2tAtlas MCP server

The public 2b2tAtlas Model Context Protocol server lets compatible AI assistants, research agents, IDEs, bots, and automation tools query the Atlas as a connected historical graph.

- Endpoint: `https://api.blackportal.cloud/mcp`
- Transport: Streamable HTTP
- Session model: stateless
- Authentication: none for public reads
- Mutations: none
- Human guide: `https://2b2tatlas.com/mcp/`

## Client configuration

Many MCP clients accept this generic shape, although the surrounding settings filename and keys vary by client:

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

The endpoint negotiates the MCP protocol itself. Do not append `/api`, and do not configure it as an SSE-only legacy server.

## Tools

| Tool | Purpose |
| --- | --- |
| `search_locations` | Search canonical names, descriptions, tags, warps, and reviewed group relationships with optional dimension/group/type filters |
| `get_location` | Fetch one canonical location and its public relationships |
| `find_locations_near` | Find documented places around Minecraft X/Z coordinates in one dimension |
| `find_locations_by_time_range` | Search locations with dated renders or Archive warps in an inclusive historical range |
| `find_preserved_builds` | Find locations satisfying render, WDL, attachment, group, dimension, and date constraints |
| `research_location` | Gather one compact research bundle across a location, its groups, warps, renders, WDLs, and nearby sites |
| `search_groups` | Search canonical group names, reviewed aliases, classifications, and histories |
| `get_group` | Fetch one group and its reciprocal public build/highway relationships |
| `get_group_builds` | List public locations attributed to a reviewed group |
| `search_highways` | Search approved public highways/canals and optional builder-group attribution |
| `get_highway` | Fetch one approved public highway and builder relationships |
| `get_warps` | List Archive warp identities belonging to a location |
| `get_world_downloads` | List publicly downloadable, provenance-validated partial WDLs for a location |
| `get_render_metadata` | List public render footprints, dates, tile/preview URLs, and provenance |
| `get_dataset_stats` | Return synchronized catalog and relationship counts |

Tool inputs are server-bounded. Search result limits cannot be raised above 100, and nearby searches cannot exceed the server's coordinate-radius ceiling. Clients should make focused calls instead of attempting to reproduce a bulk export through repeated MCP requests.

## Resources

Stable resources provide direct retrieval when an entity ID is already known:

- `2b2tatlas://location/{id}`
- `2b2tatlas://group/{id}`
- `2b2tatlas://highway/{id}`
- `2b2tatlas://dataset`

These are MCP resource URIs, not browser URLs. Returned records also include canonical `https://2b2tatlas.com/entities/...` pages and live `https://api.blackportal.cloud/api/...` URLs.

## Useful 2b2t prompts

Once the server is connected, an agent can handle requests such as:

- “Find preserved DonFuer builds with downloadable WDLs, then cite each canonical Atlas page.”
- “What documented Overworld locations are within 25,000 blocks of X -50,000, Z 56,000?”
- “Research Mu Megabase, distinguish its historical renders, and list related groups and nearby sites.”
- “Compare the known builds and highways attributed to the Highway Workers Union.”
- “Find locations with render or warp evidence between 2016 and 2018.”
- “List downloadable partial worlds for this location and include their checksums and scope warnings.”

MCP tool results are deterministic projections of current Atlas records. The calling model may summarize or infer from those records, so important claims should still cite the returned canonical entity page and preserve any original evidence/source links.

## WDL and media safety

The MCP server never returns ZIP or image bytes. It returns metadata and HTTPS URLs for resources that Atlas already exposes publicly. Downloadable worlds are partial historical Minecraft Java saves, either an exact retained Archive collector footprint or a verified preserved render source; they are not complete copies of 2b2t.

For bulk analysis, use the static [JSONL catalogs](https://2b2tatlas.com/llms.txt) rather than treating MCP as a bulk-transfer protocol. For interactive mods and deterministic application code, the [REST/OpenAPI surface](API-REFERENCE.md) may be more direct.

## Attribution and reuse

Use Atlas-authored factual records and these examples however you want. Attribution is optional, but linking the canonical entity page helps other players inspect the same historical record. When practical, retain original media/evidence provenance too.
