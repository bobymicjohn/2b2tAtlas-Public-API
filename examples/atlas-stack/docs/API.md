# API reference

> **AI-Generated documentation.**

Base path: `/api`. Public responses use JSON unless noted.

`/openapi/v1.json` documents only anonymous public GET endpoints. The complete
schema is `/openapi/internal.json`, requiring an Atlas bearer JWT with
`users.manage`. It declares Bearer and `X-Atlas-Worker-Key` authentication,
per-operation security, permission requirements, and 401/403 responses. Schema
visibility does not replace endpoint authorization. Login and registration remain
available at their existing routes but are omitted from the public data schema.

Public integration guide and runnable examples: [atlas-owner/2b2tAtlas-Public-API](https://github.com/bobymicjohn/2b2tAtlas-Public-API). The public repository documents only anonymous read contracts and contains no Atlas application source, credentials, collector internals, or moderation operations.

## Model Context Protocol (MCP)

Historical Nocom data is public at `/api/nocom` (provenance and coverage),
`/api/nocom/periods` (39 fixed 30-day dimension/period aggregates), and
`/api/nocom/highways` (136 rows per dimension or 17 per compass direction).
Periods accept `dimension=overworld|nether|end` and optional overlapping `from`/`to`
dates. Highway queries accept a dimension and optional full compass direction.
These are observations, not unique players, visits or ownership. MCP tools are
`get_nocom_dataset`, `get_nocom_periods` and `get_nocom_highway_activity`.
The crawlable `/nocom/` dataset and JSONL exports ship in the static-site package.

The production API also exposes a public MCP server at `http://127.0.0.1:5297/mcp`. It uses stateless Streamable HTTP and is intentionally read-only. The server runs inside the Atlas API process and queries the EF Core data layer directly; it does not call the public HTTP API or maintain a second copy of Atlas data.

The server is published in the official MCP Registry as [`io.github.example/2b2t-atlas`](https://registry.modelcontextprotocol.io/?q=io.github.example%2F2b2t-atlas). The versioned registry manifest and GitHub OIDC publication workflow live in the public API examples repository.

Generic MCP client configuration:

```json
{
  "mcpServers": {
    "2b2t-atlas": {
      "type": "http",
      "url": "http://127.0.0.1:5297/mcp"
    }
  }
}
```

The 15 bounded tools cover location/group/highway search and detail, nearby and date-range discovery, group builds, warps, render provenance, WDL metadata, preserved-build queries, synthesized location research context, and dataset statistics. Stable MCP resources are available at `2b2tatlas://location/{id}`, `2b2tatlas://group/{id}`, `2b2tatlas://highway/{id}`, and `2b2tatlas://dataset`.

MCP returns structured metadata and HTTPS download links; it never places WDL ZIP or render-image bytes into model context. Results retain canonical entity/API URLs so agents can cite the exact Atlas record and preserve original-source provenance. Anonymous MCP requests have a separate per-client rate limit, and every query limit/radius is server-bounded.

Run `scripts/test-atlas-mcp.ps1` after deployment to validate protocol negotiation, the complete tool surface, read-only annotations, normalized search, dataset access, and resource discovery.

## Public endpoints

### `GET /api/locations`

Returns every location with warps, attachment links, location renders, and reviewed builder-group attributions. Render rows may include exact `minX`, `minZ`, `maxXExclusive`, `maxZExclusive`, `maxNativeZoom`, and `coordinateScheme`.

Every location has `canonicalUrl`, `interactiveUrl`, and `apiUrl`. Nested group relationships, warps, attachments, and renders carry their own canonical/interactive/API links where applicable; clients should prefer those values over constructing URLs.

Location research metadata includes `description`, bounded `tags`, `wiki`, and `videoUrl`. Wiki/video writes require valid HTTP or HTTPS URLs.

### `GET /api/locations/{id}`

Returns one location by integer row ID, or `404`.

### `GET /api/attachments`

Returns sourced location media and reference links with owning-location context. Optional filters are `locationId` and `mediaType` (`Image`, `Video`, `Wiki`, or `Link`); `limit` and `offset` provide paging. Curated media records may include a self-hosted `path`, lightweight `thumbnailPath`, original `sourceUrl`, `caption`, and `attribution`.

### `GET /api/attachments/{id}`

Returns one attachment with provenance and owning-location context, or `404`.

### `GET /api/warps`

Returns Archive warp identities with owning-location context. Optional filters are `locationId`, `limit`, and `offset`. One Archive warp identifies at most one WDL/render snapshot, while one Atlas location may own many historical warps.

### `GET /api/warps/{id}`

Returns one warp with its canonical owning-location and API links, or `404`. Collector-backed records also expose `worldDownloadUrl`, `worldDownloadMetadataUrl`, and `worldDownloadScope: "bounded-footprint"`.

### `GET /api/warps/{id}/world-download`

Returns metadata for the immutable world ZIP linked to a successfully ingested Archive collector warp. The response includes the SHA-256 digest, byte length, native dimension, retained chunk count and block bounds when available, source/date attribution, download URL, a location-aware filename such as `2b2tAtlas-La-Rosa-warp-123.zip`, and an explicit partial-world warning. Host paths are never returned.

### `GET /api/warps/{id}/world-download.zip`

Streams the collector's exact-footprint Minecraft Java world ZIP as `application/zip`. The `Content-Disposition` filename includes the canonical location name plus the unique warp ID. Responses support HTTP byte ranges and use the immutable archive digest as an ETag, allowing resumable downloads and safe intermediary caching. Origin reads are globally concurrency-limited so public traffic cannot starve collector archival or rendering.

These are structurally playable Java saves, but they are bounded historical snapshots rather than complete copies of the 2b2t world or the original source WDL. Chunks outside the retained footprint are absent and may generate as new terrain if opened normally. Consumers should work from a copy, prevent new chunk generation when fidelity matters, and verify the published SHA-256.

### `GET /api/renders`

Returns public per-location renders with owning-location context, render metadata URL, linked Archive warp metadata when known, exact footprint bounds, preview URL, and tile template. Optional filters are `locationId`, `dimension`, `scale`, `limit`, and `offset`.

When a validated 3D derivative exists, a render also exposes `blueMapUrl`,
`blueMapPath`, and `blueMapProfileVersion`. Atlas currently advertises profile 7
or newer only after its manifest proves both the static BlueMap payload quality
gate and an exact post-relight/source chunk-footprint match. Absence of these
fields means the downstream derivative is pending, unsupported, or failed; it
does not make the normal 2D render or downloadable WDL unavailable.

`blueMapUrl` is the canonical absolute viewer URL and should be preferred by
external clients. `blueMapPath` is the equivalent API-origin-relative path for
the Atlas frontend. Both identify one immutable render/source/profile generation;
clients must not construct a generation path from the render ID or source hash.
The `/bluemap/` static-file middleware serves a generation only while it is the
newest quality-gated generation advertised for that render. BlueMap HTML, model,
texture, and settings requests are static assets rather than JSON endpoints.

### `GET /api/renders/{id}`

Returns one public render with linked location and Archive warp context, or `404`.

Pre-Archive and community-source renders may instead expose `worldDownloadUrl`,
`worldDownloadMetadataUrl`, `worldDownloadScope: "preserved-render-source"`,
`worldDownloadSha256`, and `worldDownloadSource` directly on the render. Atlas only
publishes these fields when a completed ingestion record cryptographically identifies
the preserved source ZIP; it does not fabricate an Archive warp for legacy material.

### `GET /api/renders/{id}/world-download`

Returns metadata for a verified preserved source WDL used to produce a public legacy
render. The response identifies the render and canonical location, content digest,
byte length, source/date provenance, known chunks and bounds, and a descriptive filename
such as `2b2tAtlas-Mu-Megabase-render-38.zip`.

### `GET /api/renders/{id}/world-download.zip`

Streams that immutable source ZIP with range support, digest ETag, bounded concurrency,
and `X-Atlas-World-Scope: preserved-render-source`. These saves are generally playable
partial Java worlds, but their extent follows the original community/archive source and
is not necessarily the same as a later Archive collector footprint. A `404` means Atlas
cannot prove an independent source relationship for that render; a `503` means the
verified catalog record exists but its bytes are temporarily unavailable.

### `GET /api/highways`

Returns only highways where visibility is Public and review status is Approved. `builderGroupId`/`builderGroupName` retain the primary-builder compatibility view; `builderGroups[]` contains every reviewed builder, predecessor, contributor, and maintainer attribution with its role and evidence note.

Coordinates are in the highway's native dimension. Physically disconnected infrastructure, such as the two documented heads of the Southern Canal, is represented as separate records rather than a line across an unbuilt gap.

### `GET /api/highways/{id}`

Returns one highway by integer ID, or `404`. Public highway records include their API URL, dimension-map URL, and linked group attribution records.

### `GET /api/groups`

Returns public builder/faction/group metadata, canonical name plus exact reviewed `aliases`, classification, founding/status labels, verified wiki/website/Discord/logo source links, modification timestamps, and attributed location/highway counts. Aliases are a derived read-only projection keyed by canonical identity; group write endpoints do not accept them as editable history.

### `GET /api/groups/{id}`

Returns one group by integer ID with its attributed public bases/builds and public reviewed highways, or `404`. Each build relation includes role, dimension, Minecraft X/Z coordinates, public render count, canonical entity URL, interactive URL, and API URL. Each highway relation includes role, dimension, map URL, and API URL.

Static consumers can discover the same reviewed corpus at `/entities/groups/`, one canonical HTML/JSON-LD entity at `/entities/groups/{id}/`, and the normalized JSONL feed at `/entities/groups.jsonl`. Reviewed group aliases appear in JSONL `aliases`, visible static-page text, and Schema.org `Organization.alternateName`. Location entities live under `/entities/locations/{id}/` and `/entities/locations.jsonl`. Sourced attachments are normalized at `/entities/media.jsonl`; downloadable Archive and preserved-render source worlds are normalized at `/entities/world-downloads.jsonl`. Both have human-readable Dataset/DataDownload landing pages. Root `dataset.json` schema v5 advertises all four catalogs plus synchronized group/build, group/highway, warp, world-download, render, and attachment totals. The live API remains authoritative; the static corpus is regenerated for search engines, archival clients, LLM retrieval, and non-JavaScript tools.

Static location and group HTML, JSONL, and JSON-LD carry reciprocal group/build relationships. Warps, renders, and attachments are individually addressable API resources and are linked from their owning location records. Image attachments appear as semantic HTML images, `ImageObject` nodes with content URL, MIME type, source provenance, caption/credit when known, and image-sitemap entries; this keeps the visible page, machine graph, and media catalog synchronized.

The world-download catalog makes each available source WDL a first-class record linked to its canonical Atlas location and render, plus its Archive warp when one exists. `sourceType` distinguishes `archive-warp` from `render`; the latter preserves verified pre-Archive/community sources without inventing a warp identity. Every record includes its ZIP, metadata endpoint, SHA-256 digest, and explicit `isCompleteWorld: false` / `playability: "partial-java-save"` semantics. `scope` is either `bounded-footprint` or `preserved-render-source`, so search engines and automated consumers do not mistake a partial historical save for a complete 2b2t world.

### `GET /api/maprenders`

Returns published global map-render descriptors used by the layer picker.

## Authentication

Write endpoints accept `Authorization: Bearer <JWT>` and enforce named permission policies. Authentication alone is not sufficient where a permission is listed.

## Location writes

| Method and route | Permission | Behavior |
| --- | --- | --- |
| `POST /api/locations` | `locations.create` | Create a location, research metadata, and optional warps |
| `PUT /api/locations/{id}` | `locations.edit` | Update location/research fields and synchronize warps and builder groups by identity |
| `PUT /api/locations/{id}/attachments` | `attachments.manage` | Atomically replace validated HTTPS attachment metadata; media URLs, previews, and source URLs are independently validated |
| `DELETE /api/locations/{id}` | `locations.delete` | Delete location and related warps, attachments, and renders |

Location dimension values are `0 Overworld`, `1 Nether`, and `2 End`.

## Highway writes

| Method and route | Permission | Behavior |
| --- | --- | --- |
| `POST /api/highways` | `highways.create` | Create; users without edit permission submit Pending |
| `PUT /api/highways/{id}` | `highways.edit` | Update a highway and, when supplied, atomically synchronize its `builderGroups[]` attributions |
| `DELETE /api/highways/{id}` | `highways.delete` | Delete a highway |
| `GET /api/highways/pending` | `submissions.moderate` | Pending moderation queue |
| `GET /api/highways/all` | `highways.edit` | All statuses and visibility values |
| `POST /api/highways/{id}/approve` | `submissions.moderate` | Approve pending highway |
| `POST /api/highways/{id}/reject` | `submissions.moderate` | Reject pending highway |

## Groups, revisions, roles, audit, and users

These are administrative contracts used by the bundled client. Controller routes are:

- `/api/groups` with `groups.manage` for writes; group create/update accepts classification, history, color, founded/status labels, wiki/website/Discord URLs, and logo/source URLs;
- `/api/revisions` with authenticated submission and `submissions.moderate` review;
- `/api/roles` with Founder-only `roles.manage`;
- `/api/audit` with `audit.view`;
- `/api/admin/users` and `/api/admin/stats` with `users.manage`.

Canonical stored role IDs are `SuperAdmin`, `Admin`, `Cartographer`, `HighwayArchitect`, `Chronicler`, and `User`. The client displays these as Founder, Archivist, Cartographer, Highway Architect, Chronicler, and Member. Unknown role IDs are rejected; non-Founder callers cannot assign a role at or above their own rank.

## Map-render and ingestion operations

`/api/maprenders/all`, map-render writes, and ingestion job reads/cancellation require `renders.manage`.

`GET /api/admin/collector` also requires `renders.manage`. It returns a read-only, sanitized snapshot of the example host Archive collector: aggregate queue progress, five worker states, current warp names, chunk/waypoint counters, rolling-handoff state, and the next configured weekly run. Each worker includes a stable `outcome` (`live`, `archive-backend-rejected`, `server-disconnected`, `connecting`, `backoff`, `complete`, or `idle`) so the UI can distinguish a real capture from an Archive-side rejection without exposing diagnostic logs. Aggregate `activeWorkers` counts only running workers with current operational coverage telemetry; a fresh supervisor that is retrying but not capturing reports `Recovering` rather than `Active`. Host paths, PIDs, command lines, credentials, and logs are deliberately omitted. The static Namecheap client calls this endpoint through `api.atlas.example`, so the administrator's browser does not need filesystem or LAN access to example host.

`GET /api/admin/bluemap` requires `renders.manage` and returns the downstream 3D-generation snapshot used by the Admin Location Management panel. `coordinated: true` identifies the two-worker service; `workers` contains at most two records with `workerId`, `state`, `stage`, `current` (render/location/name/dimension), `startedUtc`, and `updatedUtc`. Each worker has independent activity/staleness. `batchCompleted`/`batchTotal` describe the discovered catalog, refreshed every minute (legacy single-pass checkpoints retain snapshot semantics); `eligibleRenderCount` is the live database inventory; `validatedRenderCount` counts only the newest publicly advertised derivative per render that passes the configured profile, static-output, exact-lighting-footprint, and canonical-location-start gates. `pendingAfterSnapshot` counts newly ingested work awaiting discovery. The response also includes per-dimension validated counts, retry failures, retained diagnostic-generation count, manifest-reported output bytes, quota, output-volume free bytes, and renderer/checkpoint freshness. It never returns local paths, source SHA-256 values, process identifiers, command lines, or log contents. Coordinator failures retry after a 30-minute per-render cooldown; a missing coordinator is restored by the five-minute watchdog.

BlueMap is not part of the worker claim/completion contract. A completed source-
backed render becomes eligible through the renderer's next database snapshot;
the watchdog resumes that idempotent downstream pass independently. See
[`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md) for eligibility, quality gates,
storage, monitoring semantics, and recovery.

Operators holding `renders.manage` upload world downloads with resumable sessions:

1. `POST /api/ingestion-jobs/upload-sessions` creates an owner-bound session and returns its chunk size.
2. `PUT /api/ingestion-jobs/upload-sessions/{id}/chunks?offset=...` appends the next ordered chunk. The client uses 32 MiB chunks and the API rejects offset mismatches.
3. `POST /api/ingestion-jobs/upload-sessions/{id}/complete` verifies the declared length and ZIP signature, archives the source by SHA-256, and queues the job.

The maximum archive is 32 GiB. Incomplete sessions expire after 24 hours. Metadata may include an archive-relative `worldRoot` when multiple safe Minecraft world roots are present and an optional operator-confirmed `archiveWarpName`. Normally the server derives the warp from a recognized Archive downloader report or Archive-attributed filename. The legacy multipart `POST /api/ingestion-jobs/upload` remains available for compatible clients, but the bundled Admin client uses resumable sessions.

`POST /api/ingestion-jobs/local-intake` is the example host-only, worker-key-authenticated handoff for a complete ZIP already atomically placed in the configured intake directory. It accepts a plain ZIP basename plus normal bounded job metadata, then performs the same archive inspection, immutable SHA storage, dimension detection, matching, and queueing as a browser upload. An exact retry with the same immutable archive SHA-256, slug, dimension, and intake basename returns the already durable job; ordinary slug collisions still return `409`. It is not a public browser API and cannot read arbitrary host paths. The Archive collector's `captured` and `ready` directories are outside this route.

Recognized Archive jobs reuse an exact existing warp's owning location. Otherwise matching evaluates bounded historical-warp, name, footprint, coordinate, dimension, and provenance evidence. High-confidence existing/new decisions continue; ambiguous work parks in `needs-match`. An operator holding `renders.manage` resolves it with `POST /api/ingestion-jobs/{publicId}/match`, choosing an existing location or requesting creation at the inspected render center and reviewing the one canonical warp.

The worker supports Java Alpha chunks, McRegion, Anvil, gzip/zlib/raw/LZ4-Java region compression, and Overworld, Nether, and End. Every accepted dimension is rendered as matching day and night variants and registered with a generation-scoped `{dn}` tile URL only after both variants verify.

The worker routes under `/api/ingestion-jobs` (claim and status) use a separate worker API key and per-claim token; those are not browser APIs.

## Exports

Waypoint exports are generated in the browser map UI. The server does not expose JourneyMap or Xaero export routes.

## Stability

Public GET contracts are the supported integration surface. Administrative DTOs may evolve with the bundled client; integrations should tolerate additive JSON fields.

Every mutation route is excluded from anonymous use even though public reads allow wildcard CORS. A bearer token alone is not sufficient: the account must also hold the route's named permission.

Omitting `groups` from a location update preserves its current group links for backward compatibility. Supplying the field replaces the reviewed associations. Warp updates are ID-aware: retained warps preserve Archive SHA, source coordinates, and render links rather than being deleted and recreated.

## Legacy compatibility

The original public read URLs remain supported for community tools:

- `GET /api/locations.php` returns the original snake_case location shape and accepts `x`, `z`, `rows`, `dimension`, `warps`, and `search` query parameters. Legacy dimensions are `0` Overworld and `1` End; `-1` is also accepted for Nether.
- `GET /api/locationCount.php` returns `{ "locationCount": number }`.
- Browser access to these read-only routes permits wildcard CORS, matching the original PHP API.

The former anonymous `newWarp.php` mutation is intentionally not restored. It returns `410 Gone`; modern writes require authenticated permission policies.
