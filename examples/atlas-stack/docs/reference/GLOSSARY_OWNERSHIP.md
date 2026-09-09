# Glossary And Code Ownership Map

> **AI-Generated documentation.**

## Scope

This file defines overloaded terms and points retrieval to the code that decides behavior. Paths are repository-relative to `2b2tAtlas.New`.

## Glossary

### Atlas API

The ASP.NET Core process in `2b2tAtlas.Server`. It owns public/admin JSON, auth, RBAC, SQLite, job coordination, audit, and the 2b2t.place proxy. In production it binds loopback and is exposed through Cloudflare.

### Base Render Or Location Render

A sparse `Renders` row linked to one `Location`, placed by exact chunk bounds where available. It is not a global render picker row.

### Global Render Or Map Render

A `MapRenders` row for a dimension-level tile layer. Public `GET /api/maprenders` returns only published rows.

### BlueMap Derivative

An optional immutable 3D webroot generated downstream from one completed,
source-backed `Render`. It is discovered from durable database provenance,
relit and exact-footprint-audited in a disposable world, and projected onto the
render DTO only while its manifest passes the current quality gate. It is not a
database identity, global map, playable WDL, ingestion stage, or completion
requirement.

### WDL

A Minecraft Java world download treated as hostile input. Structural validity, renderer compatibility, geographic alignment, and 2b2t provenance are separate gates.

### Worker Key

A machine credential sent as `X-Atlas-Worker-Key`. The API config stores only its SHA-256. It is distinct from user JWTs, upload CSRF tokens, and per-job claim tokens.

### Claim Token

A random per-attempt job secret returned only when the worker claims work. Its hash is stored in `IngestionJobs`; it scopes status/completion to the active lease.

### WDL Upload

An authenticated, owner-bound resumable session from an operator holding `renders.manage`. The client sends ordered 32 MiB chunks to the API; completion verifies and archives the ZIP before queueing. The API does not extract or render.

### Archive Warp

The single canonical `/warp` command that identifies one downloadable exhibit snapshot on The Archive. One Archive WDL has at most one warp identity; one Atlas location can own many dated warps and therefore many historical WDL renders. Matching a previously known warp reuses its owning location even when the new capture filename or terrain center differs.

### Captured And Ready

Two intentionally separate collector states. `captured` is a private, hash-verified quarantine under `C:\AtlasExample\Ingest\archive-sync`; it is not an Atlas job. `ready` is an explicit, reviewed handoff inbox. `import-archive-inbox.ps1` is the only collector helper that crosses from `ready` into the normal hostile-input API/worker pipeline.

### Needs Match

An ingestion state for genuine identity, dimension, or warp-ownership ambiguity. It is not a renderer failure. An operator can review the bounded evidence and either attach the one WDL/warp to an existing location or create a new location before rendering continues.

### Native Coordinates

Minecraft block coordinates in the record's own dimension. Overworld/Nether display projection does not rewrite stored coordinates. End does not portal-project.

### Sparse Coordinate Scheme

A named, certified transform from chunk-backed renderer output into Atlas `z/y/x` URLs. `atlas-sparse-v1` is the API completion contract; dimension-specific adapter profiles define the actual zoom/origin mapping.

### Production, Operational, Optional, Future

Status terms defined in `docs/RAG_INDEX.md`. In particular, optional BlackBrain behavior is never an Atlas availability dependency.

## Server Ownership

| Concern | Owning source |
| --- | --- |
| Middleware, JWT validation, CORS, rate limits, startup | `2b2tAtlas.Server/Program.cs` |
| Existing DB upgrades | `2b2tAtlas.Server/Services/SchemaUpgrader.cs` |
| Login, JWT claims, effective permissions | `2b2tAtlas.Server/Services/AuthService.cs` |
| Location CRUD and attachment replacement | `2b2tAtlas.Server/Controllers/LocationsController.cs` |
| Highway public/filter/write/moderation | `2b2tAtlas.Server/Controllers/HighwaysController.cs` |
| Revisions | `2b2tAtlas.Server/Controllers/RevisionsController.cs` |
| Roles and DB overrides | `2b2tAtlas.Server/Controllers/RolesController.cs` |
| WDL upload intake, worker queue, leases, completion | `2b2tAtlas.Server/Controllers/IngestionJobsController.cs` |
| Legacy PHP compatibility | `2b2tAtlas.Server/Controllers/LegacyApiController.cs` |
| 2b2t.place proxy/cache behavior | `2b2tAtlas.Server/Controllers/PlaceTilesController.cs` |
| BlueMap public catalog/static gate | `2b2tAtlas.Server/Services/BlueMapCatalogService.cs`, `2b2tAtlas.Server/Program.cs` |
| BlueMap Admin monitoring projection | `2b2tAtlas.Server/Controllers/AdminController.cs` and shared BlueMap status DTOs |

## Client Ownership

| Concern | Owning source |
| --- | --- |
| App bootstrap and API base | `2b2tAtlas.Client/Program.cs` |
| Navigation and auth-aware shell | `2b2tAtlas.Client/Layout/MainLayout.razor` |
| Map state orchestration | `2b2tAtlas.Client/Pages/Map.razor` |
| CRS, tiles, layers, markers, performance | `2b2tAtlas.Client/wwwroot/js/atlas-map.js` |
| Admin tabs and operations UI | `2b2tAtlas.Client/Pages/Admin.razor` |
| WDL upload dialogs and chunk service | `2b2tAtlas.Client/Pages/IngestionJobDialog.razor`, `BulkIngestionDialog.razor`, and `Services/IngestionUploadService.cs` |
| Typed API calls | `2b2tAtlas.Client/Services/` |

## Shared Contract Ownership

| Contract | Owning source |
| --- | --- |
| Modern dimensions | `2b2tAtlas.Shared/Models/Dimension.cs` |
| Roles and permissions | `2b2tAtlas.Shared/Models/Auth/Rbac.cs` |
| Location graph DTO | `2b2tAtlas.Shared/Models/Location.cs` |
| Per-location render DTO | `2b2tAtlas.Shared/Models/Locations/Render.cs` |
| Global map render DTO | `2b2tAtlas.Shared/Models/Maps/MapRender.cs` |
| Highway DTO/enums | `2b2tAtlas.Shared/Models/Highways/Highway.cs` |
| Job/update/completion validation | `2b2tAtlas.Shared/Models/Ingestion/IngestionJob.cs` |

## Ingestor Ownership

| Concern | Owning source |
| --- | --- |
| CLI and command routing | `2b2tAtlas.Ingestor/Program.cs` |
| Resource limits | `2b2tAtlas.Ingestor/Configuration/IngestLimits.cs` |
| ZIP and path security | `2b2tAtlas.Ingestor/Security/` |
| World/NBT/chunk inspection | `2b2tAtlas.Ingestor/Minecraft/` |
| Job state and render plans | `2b2tAtlas.Ingestor/Pipeline/` |
| Renderer process boundary | `2b2tAtlas.Ingestor/Rendering/RendererRunner.cs` |
| Sparse adaptation | `2b2tAtlas.Ingestor/Publishing/AtlasTileAdapter.cs` |
| Verification/publication | `2b2tAtlas.Ingestor/Publishing/TilePublisher.cs` and `PublicationService.cs` |
| Queue polling/stage reporting | `2b2tAtlas.Ingestor/Worker/IngestionWorker.cs` |

## Archive Acquisition Ownership

| Concern | Owning source |
| --- | --- |
| Headless toolchain installation and pinned artifacts | `scripts/install-archive-collector-toolchain.ps1` |
| Live `/warps` traversal and durable catalog | `scripts/get-archive-warp-catalog.ps1` |
| Atlas/catalog queue merge | `scripts/new-archive-collector-queue.ps1` |
| One-warp capture, ZIP validation, and quarantine | `scripts/invoke-archive-collector.ps1` |
| Explicit ready-to-Atlas handoff | `scripts/import-archive-inbox.ps1` |
| Operator runbook and fidelity boundary | `docs/ARCHIVE_SYNC_AUTOMATION.md` |

## BlueMap Derivative Ownership

| Concern | Owning source |
| --- | --- |
| Selection, source verification, relight, footprint audit, render, manifest, promotion | `scripts/invoke-atlas-bluemap-render.ps1` |
| Complete resumable pass | `scripts/start-atlas-bluemap-full-batch.ps1` |
| Watchdog discovery/restart | `scripts/atlas-ensure-running.ps1` |
| Sanitized status summary | `scripts/get-atlas-bluemap-status.ps1`, `GET /api/admin/bluemap` |
| Canonical operational contract | `docs/BLUEMAP_PIPELINE.md` |

## Documentation Ownership

Use `docs/API.md` for concise API reference, `docs/DEPLOYMENT_TOPOLOGY.md` for host operations, `docs/INGESTION_PIPELINE.md` for commands/limits, `docs/BLUEMAP_PIPELINE.md` for the downstream 3D derivative, `docs/ARCHIVE_SYNC_AUTOMATION.md` for the headless Archive acquisition boundary, `docs/INGESTION_SECURITY.md` for threat review, `docs/INGESTION_CHECKLIST.md` for per-archive gates, `docs/MAP_GUIDE.md` for user workflow, and `docs/BLACKBRAIN_INTEGRATION.md` for optional enrichment design.

`docs/ROADMAP.md` owns future intent and current corpus maturity statements. Dated `docs/RELEASE_READINESS.md` evidence must not override newer source or production baselines.
