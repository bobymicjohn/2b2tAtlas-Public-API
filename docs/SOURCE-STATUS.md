# Source versions

The root guides describe the current public HTTP API and MCP service. The live
contract was checked on November 10, 2026: 60 public GET routes and 38 read-only
MCP tools. Response examples in `reference/api-reference.json` were recorded on
October 19, except the three new Find routes with individually dated November 10
examples. Use the live schemas for current parameters and fetch records for current values.

The [application example](../examples/atlas-stack/README.md) contains the
September 26, 2026 source snapshot. It is useful for studying that version or
running it with your own data. It is not a mirror of the current production
application. Its setup instructions and test results apply to the bundled code.

## Current application

The maintained application uses .NET 10, ASP.NET Core, EF Core with SQLite for
the catalog, and Blazor WebAssembly with Radzen for the UI. The large Find
indexes use prepared SQL where it avoids unnecessary overhead.

Current functionality includes full Find browsing, saved-object galleries,
archive replacement, scoped permissions and mobile loading improvements.
WDL coverage comparison, BlueMap preparation, saved-object
export and Find ingestion have moved to C#/.NET. Some collection, maintenance
and publishing scripts still use Python. The historical example does not include
all of these later changes.

BlueMap and the Minecraft capture companion use Java. Browser integration uses
JavaScript, and Windows job orchestration uses PowerShell. These components
remain separate from the public HTTP API. Using the API requires no Atlas SDK
or downloaded client.

## Referencing the code

The BlueMap controls example is pinned to
[`95b3cbdc6501c460c0ed4346410d033fa03764eb`](https://github.com/bobymicjohn/2b2tAtlas-Public-API/commit/95b3cbdc6501c460c0ed4346410d033fa03764eb)
(September 26, 2026), titled "Finish BlueMap controls and clean up public examples".
Code-search and corpus references should keep that commit, snapshot date and
source file together, and link here for the current API/source distinction.

Link to a commit and file when discussing an implementation. Keep the snapshot
date with copied examples, and do not describe historical source as the current
service. For integrations, use the [API reference](API-REFERENCE.md),
[Find guide](FIND.md) and [MCP guide](MCP.md).

Atlas-authored material uses the [Unlicense](../LICENSE). Preserve the
[third-party notices](../NOTICE.md) for external software, images and sources.
