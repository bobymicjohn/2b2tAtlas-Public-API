# Use the local API

Start the app with [the local setup guide](../README.md), then open
<http://127.0.0.1:5297/api/locations>. A new installation returns `[]`, an empty
JSON list, until you add a location in Admin.

```powershell
Invoke-RestMethod http://127.0.0.1:5297/api/locations
```

Public reads need no account or API key. The local
[OpenAPI schema](http://127.0.0.1:5297/openapi/v1.json) lists the routes and fields
implemented by this source snapshot. The hosted service may have newer routes.

## Common requests

| Route | Returns |
| --- | --- |
| `/api/locations` | All locations and their related records |
| `/api/locations/{id}` | One location |
| `/api/groups` | Groups |
| `/api/highways` | Public, approved highways |
| `/api/warps?limit=100&offset=0` | One page of linked Archive warps |
| `/api/renders?locationId=1&limit=100` | One page of a location's renders |
| `/api/attachments?locationId=1&limit=100` | One page of its source links and media |
| `/api/maprenders` | Published shared map layers |
| `/api/maprenders/catalog` | Combined map layers |

Replace `{id}` with an ID from the matching list. Warps, renders, and attachments
use `limit` and `offset`; the default limit is 500, with a maximum of 1,000.
Increase the offset by the number returned and stop after a short page.
Locations, groups, and highways return complete lists.

Dimensions are `0` Overworld, `1` Nether, and `2` End. Coordinates belong to that
dimension. Tile `{y}` is a tile row, not Minecraft elevation. Missing dates,
source information, or download links may be null.

When available, follow `worldDownloadMetadataUrl` to read a world download's
source, size, and checksum. Follow `worldDownloadUrl` for the ZIP and `blueMapUrl`
for a 3D view. A WDL is a partial historical Java save, so work on a copy and
expect missing terrain outside its saved area.

The [public API guide](../../../docs/API-REFERENCE.md) explains the common fields,
paging, coordinates, and downloads in more detail. Use your local schema to
check which routes this snapshot supports. Canonical-link builders and external
map URLs also need your own host configuration; see [setup](../README.md#configure-your-installation).

## MCP and administration

The local MCP endpoint is `http://127.0.0.1:5297/mcp`. Choose Streamable HTTP and
no authentication for its public reads. Use `tools/list` to discover the tools
in this snapshot. The [MCP guide](../../../docs/MCP.md) explains client setup
for the hosted service; replace its URL with your local endpoint.

For authenticated edits, uploads, and worker calls, see
[admin and worker operations](API_OPERATIONS.md). Public reads do not grant
permission to change data.
