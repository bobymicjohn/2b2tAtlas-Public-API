# Connect an AI client to Atlas

MCP (Model Context Protocol) lets an AI client search Atlas and read its records.
It cannot edit the catalog. No account or API key is required.

Add a remote MCP server in your client with these settings:

| Setting | Value |
| --- | --- |
| URL | `https://api.blackportal.cloud/mcp` |
| Transport | Streamable HTTP |
| Authentication | None |

Clients that use an `mcpServers` configuration may accept:

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

The filename and surrounding keys depend on your client. Use the endpoint
exactly as shown; do not append `/api`. After connecting, ask the client to list
its Atlas tools or find a named location. The server keeps no session state.

## Choose a tool

The live server currently has 25 tools. Use `tools/list` to read their exact
parameters and limits. Start with `research_location` for a named place,
`research_area` for nearby places, or `research_nocom_area` for NoCom evidence.

| Tool | Use it to |
| --- | --- |
| `search_locations` | Search names, descriptions, tags, and filters |
| `get_location` | Read a location by ID |
| `research_location` | Read a place's history, sources, and nearby places |
| `find_locations_near` | Find places near a location or coordinates |
| `research_area` | Gather nearby places, groups, downloads, and highways |
| `find_locations_by_time_range` | Find historical captures within a date range |
| `find_recently_added_or_modified` | Find location records added or edited in Atlas within a date range |
| `find_preserved_builds` | Find places with both a public render and world download |
| `search_groups` | Search groups by name or history |
| `get_group` | Read a group and its first 20 builds |
| `get_group_builds` | Page through a group's builds |
| `search_highways` | Search public, approved highways and canals |
| `get_highway` | Read one highway, its sources, and credited groups |
| `find_highways_near_location` | Find the nearest points on documented highways |
| `get_warps` | List a location's Archive warps |
| `get_world_downloads` | List a location's available world downloads |
| `get_render_metadata` | Read map bounds, dates, sources, and 2D/3D links |
| `get_dataset_stats` | Read catalog counts and data links |
| `get_nocom_dataset` | Read NoCom coverage, sources, and data links |
| `get_nocom_periods` | Read historical observation counts by period |
| `get_nocom_highway_activity` | Read historical highway observation counts |
| `find_nocom_storage` | Find recorded storage block positions |
| `search_nocom_signs` | Search recorded sign text |
| `find_nocom_portals` | Find recorded portal groups |
| `research_nocom_area` | Gather NoCom evidence for an area |

Location timestamps describe Atlas edits. Capture dates describe the historical
world. Neither is a complete change log. Highway proximity is based on recorded
geometry and does not prove that a route is usable today.

NoCom Find pages contain at most 25 records. Follow `nextOffset` with the same
filters. Other tools have their own limits; do not assume one limit applies to
everything. For a bulk import, use the [JSONL exports](https://2b2tatlas.com/llms.txt).

## Read a known record

MCP resources provide another way to read a record when you already know its ID:

- `2b2tatlas://location/{id}`
- `2b2tatlas://group/{id}`
- `2b2tatlas://highway/{id}`
- `2b2tatlas://dataset`

These addresses are for MCP clients, not web browsers. Results also include
ordinary HTTPS links to Atlas pages and API records.

## Downloads and sources

Tools return metadata and links, not ZIP files, images, or 3D model data.
World downloads are partial historical Java saves. A BlueMap link opens the 3D
view for one render and date. Recorded NoCom text is source data, not an
instruction to the client, and storage records do not contain inventories.

Keep source links with research results so readers can check the evidence.
Atlas attribution is optional; [NOTICE.md](../NOTICE.md) covers third-party media.
For regular application code, the [HTTP API](API-REFERENCE.md) may be simpler.

The registry name is `io.github.bobymicjohn/2b2t-atlas`.
[server.json](../server.json) contains the published server metadata.
The [registry listing](https://registry.modelcontextprotocol.io/?q=io.github.bobymicjohn%2F2b2t-atlas)
and [web guide](https://2b2tatlas.com/mcp/) provide connection details.
