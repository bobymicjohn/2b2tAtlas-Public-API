# Changes

## Since 1.1.0 — 2026-09-08

Document the restored 43k Nether primary layer and its original coordinate
contract, including the reprojection needed on the current Nether map grid.
Withdraw the 5k layer from the catalog because its world transform is unverified.

Document Timeline and Article attachments, nullable legacy media types, event
versus chronology links, and timestamped YouTube references. Add a paginated
Python example that prints a location's media links and source credits. The
Atlas research runner and contributor workflow live in the main source repo;
this repository remains the public read-only API reference.

## 1.1.0 — 2026-09-07

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

## 1.0.0 — 2026-09-05

Initial public API documentation, C#/JavaScript/Python/Fabric examples, coordinate
and provenance guidance, offline contract checks, and MCP Registry discovery.
