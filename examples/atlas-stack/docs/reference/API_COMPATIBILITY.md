# API And Legacy Compatibility

> **AI-Generated documentation.**

## Scope

This file routes modern, administrative, worker, proxy, and legacy HTTP contracts. Exact DTO validation is code-owned; concise public usage remains in `docs/API.md`.

## Modern Public Reads

**Production:** public JSON endpoints are:

- `GET /api/locations` and `GET /api/locations/{id}`.
- `GET /api/highways` and `GET /api/highways/{id}`.
- `GET /api/groups` and `GET /api/groups/{id}`.
- `GET /api/maprenders` for published global layers.
- `GET /api/renders`, `GET /api/renders/{id}`, and
  `GET /api/locations/{id}/renders` for source-backed historical render
  metadata. Render records may add nullable `blueMapUrl`, `blueMapPath`, and
  `blueMapProfileVersion` when a validated 3D derivative exists.

Controllers are `LocationsController.cs`, `HighwaysController.cs`, `GroupsController.cs`, and `MapRendersController.cs` under `2b2tAtlas.Server/Controllers/`. Public highway collection results include only approved public rows. Clients should tolerate additive JSON fields.

## Authentication Endpoints

- `POST /api/auth/register`: rate-limited registration.
- `POST /api/auth/login`: rate-limited login returning JWT/user data.
- `GET /api/auth/profile`: authenticated, server-resolved current profile and permissions.
- `POST /api/auth/update-last-login`: authenticated timestamp update.
- `GET /api/auth/validate`: authenticated token check.

The authorization model is described in `AUTH_SECURITY.md`.

## Permission-Gated Writes

| Contract | Required permission |
| --- | --- |
| `POST /api/locations` | `locations.create` |
| `PUT /api/locations/{id}` | `locations.edit` |
| `PUT /api/locations/{id}/attachments` | `attachments.manage` |
| `DELETE /api/locations/{id}` | `locations.delete` |
| `POST /api/highways` | `highways.create` |
| `PUT /api/highways/{id}` | `highways.edit` |
| `DELETE /api/highways/{id}` | `highways.delete` |
| writes under `/api/groups` | `groups.manage` |
| `/api/maprenders/all`, PUT, DELETE | `renders.manage` |
| `/api/audit` | `audit.view` |
| `/api/roles` | `roles.manage` |
| `/api/admin/users`, `/api/admin/stats` | `users.manage` |
| `GET /api/admin/bluemap` | `renders.manage` |

Moderation uses `GET /api/highways/pending`, `POST /api/highways/{id}/approve`, `POST /api/highways/{id}/reject`, and the corresponding `/api/revisions` queue/review routes with `submissions.moderate`.

## Ingestion Worker Contract

Administrative job reads, queue creation, and queued cancellation under `/api/ingestion-jobs` require `renders.manage`.

Worker-only calls are `POST /api/ingestion-jobs/local`, `POST /api/ingestion-jobs/claim`, and `PUT /api/ingestion-jobs/{publicId}/status`. They are marked anonymous only because they use `X-Atlas-Worker-Key`; status also requires the active per-job claim token in the JSON body. They are not public browser APIs.

## Tile Proxy Contract

`GET /tiles/place/{layer}/{lod}/{dim}/{sx}/{sy}/{file}` is owned by `2b2tAtlas.Server/Controllers/PlaceTilesController.cs`. The client uses it to fetch/cache 2b2t.place source tiles without exposing upstream CORS and availability behavior directly to the canvas layer.

This route is not an Atlas metadata API and does not use the `/api` prefix.

Validated BlueMap static viewers and their nested assets are served beneath
`/bluemap/`. The path is an immutable resource surface, not a JSON API. Static
middleware rechecks the generation against the current manifest catalog, so a
partial, superseded, failed, or below-minimum profile cannot be fetched even if
its directory name is known. Public clients discover the exact URL from the
render DTO and must not construct it. See
[`../BLUEMAP_PIPELINE.md`](../BLUEMAP_PIPELINE.md).

## Legacy Read Compatibility

`2b2tAtlas.Server/Controllers/LegacyApiController.cs` preserves:

- `GET /api/locations.php` with `x`, `z`, `rows`, `dimension`, `warps`, and `search`.
- `GET /api/locationCount.php` returning `{ "locationCount": number }`.

Responses send `Access-Control-Allow-Origin: *` and `X-Atlas-Compatibility: legacy-read-v1`. Location fields retain snake_case; coordinates are strings; `warps` is omitted when null.

Legacy dimensions are `0=Overworld`, `1=End`, and `-1=Nether`. Never apply those values to modern DTOs, where `1=Nether` and `2=End`.

## Retired Legacy Write

`GET` or `POST /api/newWarp.php` returns `410 Gone`. Anonymous writes were intentionally not restored. The response points to `/api/locations/{id}`, whose actual update method is authenticated `PUT` with `locations.edit`.

## Browser-Generated Exports

**Production:** map and directory exports are generated in the client. There is no supported server JourneyMap or Xaero export endpoint. Files under `2b2tAtlas.Server/Controllers/V1/` are not listed in `docs/API.md` as the supported integration surface; use current public GET contracts unless source and tests establish a required legacy contract.
