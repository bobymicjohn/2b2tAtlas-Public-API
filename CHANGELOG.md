# Changes

## Public reference check - 2026-10-19

- Recheck all 57 public GET route contracts and the 38-tool MCP catalog against
  the live service. Retain the recorded response examples' original date.
- Document the compact directory endpoint and location-based 3D links.
- Update the dated catalog counts and explain which code belongs to the
  historical source snapshot versus the maintained .NET 10 application.

## Registry discovery refresh - 2026-10-17

- Refresh registry metadata to version 1.1.0, covering captured signs, saved
  inventories, rare blocks, mobs, map art and historical NoCom queries.
- Add a dated fact sheet with canonical guide, entity-directory and machine
  discovery links for integrations and directory maintainers.
- Keep the existing public MCP endpoint and 38-tool contract unchanged.
- Document local registry publication; GitHub Actions remains disabled.

## Public documentation refresh - 2026-10-17

- Verify 57 public GET routes and 38 read-only MCP tools against the live service.
- Document Unified Find, all result pages, source selection, inventories, saved
  object galleries and previews, daily NoCom data and date-range maps.
- Add complete parameter/response and tool-example catalogs, refresh human versus
  machine URLs, and correct retrieval metadata and bulk-data links.
- Label the bundled September 26 application as a historical source snapshot;
  its code and historical validation results are unchanged.
- Run local documentation, public-export and private-code checks before publishing.


## Code and documentation cleanup - 2026-09-26

Simplified the API examples, setup guides and stack documentation. Expanded
compressed code and replaced temporary maintenance scripts with reusable tools.
Added local checks for BlueMap controls, starting positions and terrain rendering.
BlueMap now checks the configured database before advertising a public render.

## Reuse policy - 2026-09-08

Atlas-authored stack code now uses the Unlicense alongside the API examples.
Aligned the application license, coverage-mod metadata and contributor guidance.
Credit remains optional; source and hosting courtesies are requests, not added
license conditions. Third-party notices and terms remain in place.

## Full-stack source example - 2026-09-08

Added `examples/atlas-stack`: the Blazor frontend, ASP.NET Core API, SQLite models,
WDL ingestion and publication, Archive collectors and Fabric coverage companion,
BlueMap orchestration, recovery and research tools. Includes local bootstrap,
generalized host configuration, setup guides and local checks. The initial MIT
license was replaced by the Unlicense, as noted above. Production
databases, credentials, game state, private history and operator incident reports
are excluded. Existing public API clients remain standalone.

## Since 1.1.0 - 2026-09-08

Use the documented August 15–17, 2019 capture dates in the 43k Nether layer name.

Document the restored 43k Nether primary layer and its original coordinate
contract, including the reprojection needed on the current Nether map grid.
Withdraw the 5k layer from the catalog because its world transform is unverified.

Document Timeline and Article attachments, nullable legacy media types, event
versus chronology links, and timestamped YouTube references. Add a paginated
Python example that prints a location's media links and source credits. The
Atlas research runner and contributor workflow live in the main source repo;
this repository remains the public read-only API reference.

## 1.1.0 - 2026-09-07

Documents the historical aggregates Atlas serves from the public Nerds Inc release: fixed
30-day buckets, dimension totals and compass-direction highway series.

- Document `/api/nocom`, `/api/nocom/periods` and `/api/nocom/highways`, including
  dimension ordinals, overlapping whole-bucket date filters and sparse tile paths.
- Document the three Nocom MCP tools. The public server now offers 18 tools.
- Add a small Python activity example and an offline response-shape fixture.
- Keep BlueMap and preserved-source WDL guidance alongside the ordinary REST
  examples. Follow the returned source/viewer links instead of guessing storage paths.
- Explain the public-only OpenAPI surface. Admin routes are not a supported
  consumer interface.

Positive loaded-chunk observations are not unique players, exact positions or a
live radar. The last bucket boundary also does not mean the exploit worked until
that date.

API/MCP data is live independently of the static site's deployment. The Nocom
HTML/JSONL exports are included in the pending frontend package. This repository
release does not deploy that package or change the wire protocol; the running
MCP server/registry still reports its existing 1.0.0 implementation version.

## 1.0.0 - 2026-09-05

Initial public API documentation, C#/JavaScript/Python/Fabric examples, coordinate
and provenance guidance, offline contract checks, and MCP Registry discovery.
