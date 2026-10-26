# Source versions

The root guides describe the current public HTTP API and MCP service. The live
contract was checked on October 26, 2026: 57 public GET routes and 38 read-only
MCP tools. Response examples in `reference/api-reference.json` were recorded on
October 19. Use the live schemas for current parameters and fetch records for
current values.

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

Link to a commit and file when discussing an implementation. Keep the snapshot
date with copied examples, and do not describe historical source as the current
service. For integrations, use the [API reference](API-REFERENCE.md),
[Find guide](FIND.md) and [MCP guide](MCP.md).

Atlas-authored material uses the [Unlicense](../LICENSE). Preserve the
[third-party notices](../NOTICE.md) for external software, images and sources.
