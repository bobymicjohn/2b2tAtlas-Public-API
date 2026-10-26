# Atlas facts and canonical links

Checked October 26, 2026. This reference is for integrations and directory
maintainers describing the current hosted Atlas. Counts are a dated snapshot;
use the discovery endpoints for a current inventory.

## Short description

2b2t Atlas is a historical map and preservation catalog. Find searches captured
signs, saved WDL inventories, rare blocks, mobs and map art alongside documented
bases, groups, highways and preserved world downloads. NoCom provides historical
block observations, including container positions, but no inventory contents.

Registry description, within the 100-character limit:

> Search 2b2t bases, WDLs, signs, saved inventories, rare blocks, mobs, map art and NoCom history.

## Useful links

| Purpose | Canonical destination |
| --- | --- |
| Interactive atlas | [2b2t Atlas](https://2b2tatlas.com/) |
| Find guide and source caveats | [Find](https://2b2tatlas.com/find/) |
| Signs, inventory and saved-object examples | [Find reference](FIND.md) |
| Group directory | [Groups](https://2b2tatlas.com/groups) |
| Highway directory | [Highways](https://2b2tatlas.com/highways) |
| World downloads and capture provenance | [World downloads](https://2b2tatlas.com/world-downloads/) |
| API and MCP documentation | [API](https://2b2tatlas.com/api) and [MCP guide](https://2b2tatlas.com/mcp/) |
| Machine discovery | [Dataset](https://2b2tatlas.com/dataset.json) and [OpenAPI](https://api.blackportal.cloud/openapi/v1.json) |
| MCP connection | `https://api.blackportal.cloud/mcp` (Streamable HTTP) |

The live catalog contained 2,253 locations, 209 groups, 107 highways and
2,467 location render records. Render totals are not a count of independently
verified downloadable worlds. The public API advertised 57 GET
operations; MCP `tools/list` advertised 38 tools. Third-party counts reflect
their retrieval dates and may lag.

Preserved inventories and mobs describe the selected capture. They do not establish
current contents, survival, later visits or looting. Sign text does not prove
authorship. Capture dates differ by source and do not
establish construction dates. Coverage varies by source and product; the
separate 1M (2026) source is not yet available to Find. Follow source status and
provenance returned by the API rather than implying complete world coverage.

Link to the guide for explanations, and to a specific canonical entity for a
specific place, group or highway. Interactive filter URLs are for opening a task;
there is no need to index every filter combination. Registry updates do not
guarantee when independent directories refresh their descriptions or tool counts.

The [source status](SOURCE-STATUS.md) distinguishes the maintained .NET 10
application from the September 26 source example. External code indexes should
keep the example's date and commit with their references.
