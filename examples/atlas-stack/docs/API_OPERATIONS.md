# Admin and worker API

These routes are for operators of an Atlas installation. For public reads,
start with [the API guide](API.md).

`/openapi/internal.json` lists the current operations and request fields. It
requires a bearer token with `users.manage`. Access to the schema does not grant
permission to call its operations. Never put a worker key in browser code.

## Authentication

Send your login token as `Authorization: Bearer <JWT>`. Your account must also
have the permission listed for the route.

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

The Atlas client uses these routes:

- `/api/groups` with `groups.manage` for writes; group create/update accepts classification, history, color, founded/status labels, wiki/website/Discord URLs, and logo/source URLs;
- `/api/revisions` with authenticated submission and `submissions.moderate` review;
- `/api/roles` with Founder-only `roles.manage`;
- `/api/audit` with `audit.view`;
- `/api/admin/users` and `/api/admin/stats` with `users.manage`.

Canonical stored role IDs are `SuperAdmin`, `Admin`, `Cartographer`, `HighwayArchitect`, `Chronicler`, and `User`. The client displays these as Founder, Archivist, Cartographer, Highway Architect, Chronicler, and Member. Unknown role IDs are rejected; non-Founder callers cannot assign a role at or above their own rank.

## Map-render and ingestion operations

`/api/maprenders/all`, map-render writes, and ingestion job reads/cancellation require `renders.manage`.

`GET /api/admin/collector` requires `renders.manage`. It reports the configured collector workers, queue progress, capture counters, and recovery states. Worker count comes from the current configuration. It omits credentials, local paths, process IDs, and raw logs.

`GET /api/admin/bluemap` requires `renders.manage`. It reports each configured 3D worker, progress, retries, output size, quota, free space, and freshness. Read worker counts from the response rather than assuming a fixed count. It omits local paths, credentials, process IDs, and raw logs. See [the BlueMap guide](BLUEMAP_PIPELINE.md) for status fields and recovery.

BlueMap runs separately after 2D publication. A completed render is picked up
on a later pass. See [the BlueMap guide](BLUEMAP_PIPELINE.md) for checks,
monitoring, and recovery.

Operators holding `renders.manage` upload world downloads with resumable sessions:

1. `POST /api/ingestion-jobs/upload-sessions` creates an owner-bound session and returns its chunk size.
2. `PUT /api/ingestion-jobs/upload-sessions/{id}/chunks?offset=...` appends the next ordered chunk. The client uses 32 MiB chunks and the API rejects offset mismatches.
3. `POST /api/ingestion-jobs/upload-sessions/{id}/complete` verifies the declared length and ZIP signature, archives the source by SHA-256, and queues the job.

The maximum archive is 32 GiB. Incomplete sessions expire after 24 hours. Metadata may include an archive-relative `worldRoot` when multiple safe Minecraft world roots are present and an optional operator-confirmed `archiveWarpName`. Normally the server derives the warp from a recognized Archive downloader report or Archive-attributed filename. The legacy multipart `POST /api/ingestion-jobs/upload` remains available for compatible clients, but the bundled Admin client uses resumable sessions.

`POST /api/ingestion-jobs/local-intake` is the example host-only, worker-key-authenticated handoff for a complete ZIP already atomically placed in the configured intake directory. It accepts a plain ZIP basename plus normal bounded job metadata, then performs the same archive inspection, immutable SHA storage, dimension detection, matching, and queueing as a browser upload. An exact retry with the same immutable archive SHA-256, slug, dimension, and intake basename returns the already durable job; ordinary slug collisions still return `409`. It is not a public browser API and cannot read arbitrary host paths. The Archive collector's `captured` and `ready` directories are outside this route.

Recognized Archive jobs reuse an exact existing warp's owning location. Otherwise matching evaluates bounded historical-warp, name, footprint, coordinate, dimension, and provenance evidence. High-confidence existing/new decisions continue; ambiguous work parks in `needs-match`. An operator holding `renders.manage` resolves it with `POST /api/ingestion-jobs/{publicId}/match`, choosing an existing location or requesting creation at the inspected render center and reviewing the one canonical warp.

The worker supports Java Alpha chunks, McRegion, Anvil, gzip/zlib/raw/LZ4-Java region compression, and Overworld, Nether, and End. Every accepted dimension is rendered as matching day and night variants and registered with a generation-scoped `{dn}` tile URL only after both variants verify.

The worker routes under `/api/ingestion-jobs` (claim and status) use a separate worker API key and per-claim token; those are not browser APIs.

## Update behavior

Omitting `groups` from a location update preserves existing group links.
Supplying it replaces them. Warp updates retain matching IDs, source hashes,
coordinates, and render links. Admin request models may change with the bundled
client, so check the internal schema before writing an integration.
