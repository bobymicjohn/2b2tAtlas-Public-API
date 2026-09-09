# Deploying 2b2t Atlas in a new environment

This runbook rebuilds the complete v2 system from source. It covers the public Blazor application, ASP.NET API, SQLite data, tile and attachment storage, hostile-WDL ingestion worker, optional Archive acquisition clients, SEO/entity generation, backups, and validation.

For the shorter first-WDL route and a map of the relevant source files, start with
[From a ZIP to a point on the map](WDL_GETTING_STARTED.md). Minimal private-file
launchers are included under [deploy/examples](../deploy/examples/README.md).
The owner policy currently pins account 1 / `atlas-owner`; fresh seeding creates
that account first. Human uploads/global operations are owner-only. If adapting
the application for a different owner, update the seeder, owner validator and
security tests together. Role names alone do not override this check.

Human catalog writes also require a verified pre-edit SQLite snapshot. Configure
`Recovery__Root` with at least 10 GiB free and `Recovery__DatabasePath` to the live
database. An unavailable recovery directory intentionally returns 503 for writes.

The current production design targets Windows for the API, worker, renderer, and collector orchestration. The static client can be served by any HTTPS web host that supports SPA rewrites. Paths below are production defaults, not requirements; every stateful path can be moved if configuration and filesystem permissions are updated together.

## 1. Know the trust and storage boundaries

Keep these surfaces separate:

| Surface | Contains | Must not contain |
| --- | --- | --- |
| Static web host | Published Blazor WASM, generated entity pages, JSON-LD/JSONL, sitemap, public images | Database, secrets, raw WDLs, worker, renderer |
| API host | Self-contained server binary, SQLite database, auth and intake configuration | Static-host credentials, untrusted extraction work |
| Intake | Completed uploads waiting for the worker | Published tiles or application binaries |
| Work volume | Immutable input snapshots, extracted worlds, render staging, receipts and logs | Publicly served files |
| Tile origin | Immutable published tile generations | WDLs, secrets, database |
| WDL archive | Content-addressed raw/footprint objects and provenance | Mutable working copies |
| Collector profile | Minecraft/Fabric runtime and one account's authentication state | Another collector's auth, saves, logs, or locks |

WDLs are hostile input. The API only streams uploads into intake. The worker performs bounded ZIP inspection, immutable snapshotting, safe extraction, world-layout normalization, rendering, adaptation, verification, and atomic publication. The private Archive collector is an upstream producer and cannot bypass these gates.

## 2. Prerequisites

Install on the Windows application host:

- Git;
- the .NET SDK selected by `global.json` (currently .NET 10; `latestFeature` permits a newer compatible feature band);
- PowerShell 5.1 or PowerShell 7;
- enough separate storage for intake, extraction/render work, public tiles, database backups, and retained WDL objects;
- a pinned uNmINeD CLI build for rendering;
- Docker Desktop (Linux containers) for optional BlueMap 3D derivatives;
- NGINX or another static origin for published tiles and attachments;
- Cloudflare Tunnel, a reverse proxy, or equivalent HTTPS ingress for a loopback-bound API.

Optional Archive acquisition additionally requires Java 21, one legitimate Microsoft/Minecraft account per concurrent collector, network access to The Archive, and enough fast staging space for active saves. The installer downloads exact pinned HeadlessMc, Archive World Downloader, Fabric API, HMC-Specifics, and Baritone artifacts and verifies their SHA-256 digests.

Clone and validate the source:

```powershell
git clone https://github.com/bobymicjohn/2b2tAtlas-Public-API.git C:\Source\2b2tAtlas-Public-API
Set-Location C:\Source\2b2tAtlas-Public-API\examples\atlas-stack
git switch main

dotnet restore .\2b2tAtlas.sln
dotnet build .\2b2tAtlas.sln -c Release --no-restore --nologo
dotnet test .\2b2tAtlas.sln -c Release --no-build --nologo
dotnet list .\2b2tAtlas.sln package --vulnerable --include-transitive
```

Do not proceed if build/tests fail. Review the dependency audit; do not blindly update renderer, Minecraft, Fabric, SQLite, or archive tooling in production.

## 3. Create production directories and identities

A representative layout is:

```text
C:\AtlasExample\Api\
  app\
  data\
  logs\
  config\
C:\AtlasExample\Ingest\
  worker\
  config\
  tools\
  archive-sync\
D:\AtlasExample\Ingest\intake\
D:\AtlasExample\Ingest\archive-captures\
D:\AtlasExample\Ingest\work\
D:\AtlasExample\Ingest\DeferredCaptures\
F:\AtlasExample\AtlasTiles\
F:\AtlasExample\AtlasBlueMap\location-renders\
E:\AtlasExample\WorldDownloads\
E:\AtlasExample\HistoricalMedia\
B:\AtlasExample\Backups\
X:\AtlasExample\Backups\
```

Use dedicated, non-administrator service identities. Grant the API modify access only to `C:\AtlasExample\Api\data`, its logs, and intake; grant it read access to public WDL objects if download endpoints are enabled. Grant the worker modify access to intake, work, tiles, and the WDL archive. Grant NGINX read-only access to public tiles/attachments. Collector accounts need only their own isolated profile and staging roots.

Work and publication may be on different volumes. Cross-volume publication copies completed tiles into private destination staging, verifies the full byte inventory, then atomically renames it on the publication volume. Intake must not overlap work or publish. Keep the database outside the application directory so binary rollback cannot overwrite state.

## 4. Configure and start the API

Publish a self-contained Windows build:

```powershell
.\scripts\publish-atlas-api.ps1 -OutputDirectory C:\AtlasExample\Api\app-next-initial
```

Set secrets through the service launcher/task environment, never source `appsettings.json`:

```text
ASPNETCORE_URLS=http://127.0.0.1:5297
ASPNETCORE_ENVIRONMENT=Production
HostStaticClient=false
JwtSettings__SecretKey=<cryptographically-random-secret-at-least-32-characters>
Bootstrap__OwnerPassword=<independent-random-password-at-least-16-characters>
Database__Path=C:\AtlasExample\Api\data\atlas.db
Cors__AllowedOrigins__0=https://atlas.example
Cors__AllowedOrigins__1=https://atlas.example
IngestionWorker__ApiKeySha256=<lowercase-sha256-of-worker-key>
IngestionWorker__IntakeRoot=D:\AtlasExample\Ingest\intake
WdlArchive__Root=E:\AtlasExample\WorldDownloads
Recovery__Root=B:\AtlasExample\Backups\human-edits
Recovery__DatabasePath=C:\AtlasExample\Api\data\atlas.db
MapRenders__AllowedUrlPrefixes__0=https://tiles.example/AtlasTiles/
ArchiveCollectorStatus__RunRoot=C:\AtlasExample\Ingest\archive-sync\current-run
ArchiveCollectorStatus__ProfilesRoot=C:\AtlasExample\Ingest\archive-sync\collectors
BlueMap__OutputRoot=F:\AtlasExample\AtlasBlueMap\location-renders
BlueMap__RequestPath=/bluemap
BlueMap__PublicOrigin=https://api.example
BlueMap__MinimumProfileVersion=7
BlueMap__StatusPath=C:\AtlasExample\Ingest\bluemap\location-render-status.json
BlueMap__ActivityLogPaths__0=C:\AtlasExample\Ingest\bluemap\full-batch-stdout.log
BlueMap__ActivityLogPaths__1=C:\AtlasExample\Ingest\bluemap\full-batch-stderr.log
BlueMap__StatusStaleAfterMinutes=30
AiEnrichment__Enabled=false
```

Generate separate random JWT and worker secrets. The API receives only the worker key's lowercase SHA-256; the worker receives the plaintext key through `ATLAS_INGEST_WORKER_KEY`.

```powershell
$workerKey = '<random-worker-key>'
$bytes = [Text.Encoding]::UTF8.GetBytes($workerKey)
$sha = [Security.Cryptography.SHA256]::Create()
try { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
```

Launch `2b2tAtlas.Server.exe --contentRoot C:\AtlasExample\Api\app` with **working directory `C:\AtlasExample\Api\data`** and the explicit `Database__Path` above. The content root supplies published configuration/data files; `SchemaUpgrader` applies additive schema upgrades. A fresh database creates only `atlas-owner`, using `Bootstrap__OwnerPassword`. No automatic contributor accounts or password files are created by the API. For local development, `scripts/start-example.ps1` generates separate secrets in its protected `.local/secrets.json` file. Existing users are never promoted or reset on startup.

For an existing installation, restore `atlas.db` only while the API is stopped. Keep `atlas.db`, `atlas.db-wal`, and `atlas.db-shm` together if copying a non-checkpointed database. Prefer the repository backup script, which uses SQLite's online backup mechanism.

Local checks:

```powershell
Invoke-RestMethod http://127.0.0.1:5297/api
Invoke-RestMethod http://127.0.0.1:5297/api/locations
Invoke-RestMethod http://127.0.0.1:5297/openapi/v1.json
.\scripts\test-atlas-mcp.ps1 -Endpoint http://127.0.0.1:5297/mcp
```

Expose only the loopback listener through a reverse proxy or Cloudflare Tunnel. Public read routes and `/mcp` remain anonymous; write routes retain JWT/permission or worker-key enforcement. Confirm TLS, CORS for both site origins, Streamable HTTP POST/SSE forwarding on `/mcp`, request-size/time limits appropriate for chunked WDL uploads, range requests on WDL downloads, and that no filesystem path appears in public metadata.

## 5. Publish tiles and attachments

Serve the immutable tile root at the same HTTPS prefix configured in `MapRenders__AllowedUrlPrefixes`. Preserve signed path segments, disable directory listing, and send long immutable cache headers for generation-addressed content. Keep CORS compatible with the Atlas frontend and any intended public API consumers.

Restore separately from source control:

- `F:\AtlasExample\AtlasTiles` or equivalent published render generations;
- public attachment derivatives;
- content-addressed WDL objects and their manifests;
- historical-media archives and evidence indexes.

These datasets are intentionally not committed to Git. A source checkout alone creates a functional empty Atlas, not a copy of the production historical corpus.

## 6. Build and configure the ingestion worker

Publish the worker:

```powershell
.\scripts\publish-ingestion-worker.ps1 -OutputDirectory C:\AtlasExample\Ingest\worker
```

Copy `worker.example.json` to a protected operational file and adjust all paths and origins:

```json
{
  "schemaVersion": 1,
  "apiBase": "https://api.example/",
  "apiKeyEnvironment": "ATLAS_INGEST_WORKER_KEY",
  "intakeRoot": "D:\\AtlasExample\\Ingest\\intake",
  "archiveRoot": "E:\\AtlasExample\\WorldDownloads",
  "workRoot": "D:\\AtlasExample\\Ingest\\work",
  "rendererProfile": "C:\\AtlasExample\\Ingest\\config\\renderer.json",
  "publishRoot": "F:\\AtlasExample\\AtlasTiles",
  "publicTileRoot": "https://tiles.example/AtlasTiles",
  "tileScheme": "atlas-overworld-sparse-v1",
  "pollSeconds": 15
}
```

Create `renderer.json` from `renderer-profile.example.json`. Point it to the exact uNmINeD executable, calculate its SHA-256, record the version, and validate every dimension argument against that precise binary. Do not reuse a hash or CLI flags from another release.

```powershell
Get-FileHash C:\AtlasExample\Ingest\tools\unmined-cli.exe -Algorithm SHA256
$env:ATLAS_INGEST_WORKER_KEY = '<same-plaintext-worker-key>'
Set-Location C:\AtlasExample\Ingest\worker
.\2b2tAtlas.Ingestor.exe worker --config C:\AtlasExample\Ingest\config\worker.json
```

Run exactly one ingestion worker per work root. Its filesystem lock prevents common duplicates, but service supervision must not repeatedly launch competing instances. Validate with a small known WDL before admitting large or unknown archives. See `INGESTION_SECURITY.md`, `INGESTION_PIPELINE.md`, and `WDL_WORKER_DEPLOYMENT.md` for limits, claims, recovery, renderer receipts, and publication rules.

### Optional location-level BlueMap service

The checked-in profile-7 renderer uses pinned BlueMap 5.23, a pinned Paper
container/image and SHA-verified Light Cleaner/BKCommonLib artifacts. Run one
explicit three-dimension canary before starting the catalog backfill:

```powershell
.\scripts\invoke-atlas-bluemap-render.ps1 -RenderId <overworld-id>,<nether-id>,<end-id>
.\scripts\start-atlas-bluemap-preview.ps1
# Inspect http://127.0.0.1:8770/

# Start three coordinated workers; eligible records refresh every minute.
.\scripts\start-atlas-bluemap-full-batch.ps1
```

Configure the API's `BlueMap__OutputRoot`, `BlueMap__RequestPath`, and
`BlueMap__PublicOrigin`, and keep `BlueMap__MinimumProfileVersion=7`. Serve the
output root at the request path with immutable caching. Install the repository's
`atlas-ensure-running.ps1` as the five-minute watchdog only after confirming
Docker and the F-drive quota/free-space floor. The relight copy uses `D:` scratch;
the content-addressed ZIP on `E:` is never mounted into the Paper server.

Deploy `start-atlas-bluemap-coordinator.ps1` with the renderer and watchdog as a
unit. Allow up to 16 CPUs/24 GiB combined container limits for the two lanes.
Mutable Paper caches and BlueMap volumes are isolated per worker; the coordinator
shares exclusive render claims, output reservations and serialized publication.
Do not launch independent `-All` batches. Create
`C:\AtlasExample\Ingest\bluemap\pause-coordinator` for graceful drain/maintenance and
remove that marker to resume. Worker checkpoints/logs are under
`C:\AtlasExample\Ingest\bluemap\workers\{1,2}`. See the canonical pipeline guide for
the fresh/backfill selection policy, retry cooldowns and recovery.

Also configure `BlueMap__StatusPath`, `BlueMap__ActivityLogPaths__*`, and
`BlueMap__StatusStaleAfterMinutes` so the authenticated Admin monitor can
distinguish active work from an abandoned `running` checkpoint. The API must
have read access to the output manifests/checkpoint and static webroots, but it
does not need write access to source ZIPs or renderer scratch. The renderer needs
read access to the database and WDL object store plus write access to its state,
scratch, Docker volumes, and output root.

After the canary, verify one public render response includes `blueMapUrl`,
`blueMapPath`, and `blueMapProfileVersion`; fetch the returned viewer plus a
nested asset; confirm an old/rejected generation returns 404; then authenticate
with `renders.manage` and compare `GET /api/admin/bluemap` with
`scripts\get-atlas-bluemap-status.ps1`. Full details are in
[`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md).

## 7. Optional Archive acquisition

Archive acquisition is not required for a normal Atlas deployment. If enabled, it must remain a private, paced, resumable producer whose output enters the same ingestion boundary.

Install the version-pinned client template and build the Atlas coverage companion:

```powershell
.\scripts\install-archive-collector-toolchain.ps1 -InstallRoot C:\AtlasExample\Ingest\archive-sync\collector
.\scripts\build-archive-coverage-mod.ps1 -InstallRoot C:\AtlasExample\Ingest\archive-sync\collector -Install
```

The coverage-mod build downloads a SHA-pinned Gradle wrapper at build time. Java 21 is mandatory. Gradle/Loom caches and built JARs are generated artifacts and are excluded from Git; the complete mod source lives under `tools/AtlasArchiveCoverage`.

Create one isolated instance per account:

```powershell
.\scripts\initialize-archive-collector-instance.ps1 -InstanceName collector-1
```

Complete Microsoft device authentication interactively for each instance. Never copy `.accounts.json`, session tokens, saves, logs, or locks between instances or into Git. Start with one catalog/capture canary, verify its raw and footprint hashes, promote only reviewed high-confidence captures, and then use the sharded supervisor. The canonical state machine, rate limits, quarantine rules, compatibility profiles, recovery commands, and weekly scheduling gate are in `ARCHIVE_SYNC_AUTOMATION.md`.

## 8. Build the static frontend and SEO graph

The basic static package needs only the public API:

```powershell
.\scripts\build-namecheap-package.ps1 `
  -ApiBaseUrl https://api.example `
  -SiteBaseUrl https://atlas.example
```

The production wrapper additionally fingerprints public locations, group records/relationships, evidence, and package-producing source. It generates a pending upload record and optional Pushover notification:

```powershell
.\scripts\invoke-atlas-seo-package.ps1 `
  -ApiBaseUrl https://api.example `
  -SiteBaseUrl https://atlas.example `
  -StateRoot C:\AtlasExample\Seo `
  -GroupEvidenceIndexPath C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json `
  -Force -SkipNotification
```

The ZIP root is the web document root: extract its contents directly into `public_html` (or equivalent), including `.htaccess`. Do not add an extra enclosing folder. The generated release includes the WASM client, crawler-facing location/group/media/WDL entities, JSON-LD and JSONL catalogs, `llms.txt`, sitemap, robots rules, redirects, and package evidence.

If using Apache, enable `mod_rewrite` and the MIME types required by Blazor WebAssembly. For another host, translate `.htaccess` behavior: real static files and entity pages win, known interactive client routes fall back to `index.html`, and unknown asset/entity URLs return real 404/410 responses rather than the SPA shell.

Validate production after extraction:

```powershell
.\scripts\test-production.ps1
.\scripts\test-seo-production.ps1 -SampleCount 25
```

The daily package task is optional and never uploads by itself:

```powershell
.\scripts\register-atlas-seo-package-task.ps1 -RepositoryRoot C:\Source\2b2tAtlas
```

## 9. Enrichment and historical media

AI enrichment is disabled by default. Enable it only when a reviewed local model endpoint and evidence index are available. Model output is a suggestion, never authority: edits still pass authorization, review, evidence, and audit logging.

Group evidence, wiki relationships, YouTube transcripts, historical-media originals, and public derivatives have separate scripts and runbooks. Preserve source URL, revision/video identity, retrieval timestamp, hash, creator, license/permission context, and Atlas relationship. Do not let a scrape directly overwrite reviewed descriptions or group relationships.

Relevant entry points:

- `GROUP_ENRICHMENT_RUNBOOK.md`;
- `YOUTUBE_RESEARCH_PIPELINE.md`;
- `sync-historical-location-media.ps1`;
- `archive-wiki-location-media.py`;
- `invoke-atlas-group-evidence-refresh.ps1`.

## 10. Backups, upgrades, rollback, and monitoring

Create and verify an online database backup before every API/schema deployment:

```powershell
.\scripts\backup-atlas-db.ps1 `
  -DatabasePath C:\AtlasExample\Api\data\atlas.db `
  -BackupRoot B:\AtlasExample\Backups `
  -RetentionCount 30
```

Deploy binaries through a staged `app-next-*` directory. The example host helper `deploy-atlas-api-local.ps1` verifies paths, swaps directories, probes `/api/locations`, and rolls back automatically on failure. Adapt or replace its example host-specific launcher when deploying elsewhere.

Never roll back the database by merely swapping application directories. Schema upgrades are additive, but a binary rollback must still be checked against the upgraded schema. Keep database backups, prior application directories, renderer profiles/binary hashes, tile generations, WDL objects, and evidence indexes under independent retention policies.

Monitor at minimum:

- API process/listener and `/api/locations` health;
- free space on database, intake, work, tile, and archive volumes;
- ingestion claim age, stage, progress, retries, and `needs-match` backlog;
- collector heartbeat/status age and per-lane account identity;
- tile and attachment origin 200/404 behavior;
- WDL metadata plus one-byte range download;
- last verified SQLite backup;
- last SEO fingerprint, pending package, and public `seo-release.json`.
- BlueMap eligible/validated/remaining counts, current-pass freshness, failures,
  output quota, and output-volume free space through `GET /api/admin/bluemap`.

## 11. Release checklist

Before promoting a commit or package:

1. Confirm the Git worktree contains no secrets, databases, WDLs, auth state, build caches, binaries, or production logs.
2. Restore, build, and run the full test suite from the committed tree.
3. Run the NuGet vulnerability audit and review results.
4. Build the API and worker packages from the same commit.
5. Back up and integrity-check SQLite.
6. Validate a staged API with a disposable working directory/database.
7. Validate public locations, groups, highways, renders, attachments, warps, and WDL download metadata.
8. Generate the SEO/static package and require its relationship/evidence gate to pass.
9. Deploy API, tiles/attachments, and static frontend as separate operations with separate rollback points.
10. Run production and SEO probes, verify collector/worker health, and record commit/package hashes.

For component internals and incident recovery, continue with `DEPLOYMENT_TOPOLOGY.md`, `ADMIN_GUIDE.md`, `API.md`, `INGESTION_PIPELINE.md`, `INGESTION_SECURITY.md`, `WDL_WORKER_DEPLOYMENT.md`, and the reference documents under `docs/reference`.
