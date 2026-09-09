# WDL worker deployment

> **AI-Generated documentation.**

## Deployment split

The production layout has three independent parts:

- Namecheap shared hosting serves the static Blazor WebAssembly files.
- The ASP.NET API and SQLite database run on the controlled backend host.
- The ingestion worker and uNmINeD run on the controlled Windows rendering host.
- A downstream BlueMap batch on the rendering host creates validated 3D static
  derivatives after primary ingestion completes.

Namecheap never runs .NET server code and never receives a WDL. World downloads are uploaded through the authenticated Atlas API: the owner account with `renders.manage` signs in to the Atlas site from any machine and uploads a WDL ZIP to `POST /api/ingestion-jobs/upload`. The API streams the bytes to the dedicated `D:` intake drive on example host and queues the job. The worker on example host claims the job, reads the ZIP from the shared intake drive, inspects and renders it locally, and reports bounded completion metadata back to the API. WDL bytes transit the Atlas API and stay on example host; they never touch Namecheap.

The headless Archive collector is upstream of this split. Its `captured` directory is private quarantine, and its `ready` directory is only a reviewed handoff inbox. `import-archive-inbox.ps1` reads the worker's real intake path from `C:\AtlasExample\Ingest\config\worker.json`, SHA-verifies the copy, sends explicit UTF-8 JSON, and calls the protected local-intake endpoint. Accepted hashes are checkpointed, while an exact immutable retry returns the original durable job ID. The normal worker remains the sole extraction/render/publication owner.

BlueMap is deliberately downstream of that ownership boundary. It reads only
completed, hash-addressed source records, writes to
`F:\AtlasExample\AtlasBlueMap\location-renders`, and must never claim jobs, mutate source
ZIPs, or decide location matches. The existing Atlas watchdog maintains one
coordinator with three memory-gated children after reboot or process loss. Its global
and per-render locks prevent duplicate work, and profile-7 manifests are advertised only after both the static-payload
quality gate and exact post-relight footprint audit pass.

The ingestion worker does not enqueue or wait for BlueMap. The coordinator
discovers each distinct completed source-backed render from SQLite, including
separate dimension renders created from the same WDL. Newly completed renders
after the current discovery are picked up within a minute; failures retry after
30 minutes without blocking other jobs. Shared disk reservations and serialized
publication protect the output quota. Use
`scripts\get-atlas-bluemap-status.ps1` or the protected Admin panel to observe
the derivative without parsing logs. The canonical renderer and recovery
runbook is [`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md).

For the automated Archive catalog pass, `invoke-archive-rolling-handoff.ps1` is the review policy and runs continuously beside capture. It drains already completed final-standard captures first, then handles each new checkpoint as it appears: D-drive capture staging -> hash-verified E-drive archive -> strict promotion -> production API submission -> worker render/publication/registration. The worker prefers verified local intake and materializes a missing E source into D before processing. Heartbeats cover long verification and render stages. If registration fails after a new tile generation was rolled back, a retry detects the missing immutable generation and rebuilds it from authenticated native renderer output (or performs a full render when those artifacts are unavailable).

## Worker credential

Generate a random worker key once. Store the raw key only on the worker and configure only its SHA-256 hash on the API host:

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
$hashBytes = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($key))
$hash = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
[Environment]::SetEnvironmentVariable('ATLAS_INGEST_WORKER_KEY', $key, 'User')
$hash
```

Set `IngestionWorker__ApiKeySha256` on the API process to the printed hash, then restart the API. Never put the raw key or hash in source control. The API fails closed when no valid hash is configured.

Run the key-generation command while signed in as the dedicated worker service account. A key stored for another Windows user is not visible to that account.

## API host

Set production values through environment variables:

```text
JwtSettings__SecretKey=<random production JWT signing key>
IngestionWorker__ApiKeySha256=<printed lowercase hash>
IngestionWorker__IntakeRoot=D:\AtlasExample\Ingest\intake
Cors__AllowedOrigins__0=https://atlas.example
Cors__AllowedOrigins__1=https://atlas.example
```

Expose the API through HTTPS, such as an authenticated Cloudflare Tunnel origin. The SQLite database remains on this host. Back it up before deploying a build that adds the `IngestionJobs` table.

## Windows worker

Publish the worker:

```powershell
.\scripts\publish-ingestion-worker.ps1 -OutputDirectory C:\AtlasExample\Ingest\worker
```

The package is self-contained for Windows x64 and does not require a separately installed .NET runtime.

Copy `worker.example.json` to a local configuration file and set:

- `apiBase`: public HTTPS API origin.
- `intakeRoot`: the dedicated `D:` intake drive shared with the Atlas API (`D:\AtlasExample\Ingest\intake`).
- `workRoot`: private extraction/render staging.
- `rendererProfile`: the SHA-pinned local uNmINeD profile.
- `publishRoot`: immutable backend-served tile web root.
- `publicTileRoot`: final public HTTPS tile prefix.

Use `D:\AtlasExample\Ingest\work` for extraction/render/adaptation and `F:\AtlasExample\AtlasTiles` for published tiles. Cross-volume publication copies completed output to private staging on F, verifies its byte inventory, then atomically promotes it. NGINX already mounts `F:\AtlasExample\AtlasTiles` at `/AtlasTiles`. Worker completion immediately links the render, so do not configure a directory that still requires a later manual upload. Namecheap serves the WASM frontend; example host serves tiles. WDL intake is on D, preservation/serving objects are on E, and X holds backups. The legacy `F:\AtlasIngest\work` path redirects to D solely to preserve absolute paths in existing authenticated job receipts.

Intake, work, and publication roots must not overlap. Work and publication may use different volumes; completed output is verified in private staging on the destination before its atomic rename. Run one queued job without the UI for validation:

```powershell
C:\AtlasExample\Ingest\worker\2b2tAtlas.Ingestor.exe worker `
  --config C:\AtlasExample\Ingest\config\worker.json --once true
```

Remove `--once true` for continuous polling. The worker exposes no network listener; it only polls the API for claims and reads the ZIP from the shared `D:` intake drive. In Atlas Admin, use **Location Management > Add Location > From World Download**, drop the ZIP, and select an existing location or create one from the detected bounds center. Run the worker under a dedicated, non-administrator Windows account with read access to the `D:` intake drive, write access to work/publication, and no interactive secrets beyond the worker key.

For collector-sourced work, do not preselect a location merely because its coordinates are nearby. The job matcher first reuses an exact existing Archive warp, then evaluates historical warp identity, footprint overlap, names, coordinates, dimension, and provenance. High-confidence existing and new decisions continue automatically; only `needs-match` exceptions require an Admin decision. One WDL carries at most one Archive warp, while one location may accumulate many dated WDL warps.

## Namecheap package

Build the static package with the backend API origin injected at runtime:

```powershell
.\scripts\build-namecheap-package.ps1 `
  -ApiBaseUrl http://127.0.0.1:5297
```

On example host this automatically requires the revision-pinned group evidence index at
`C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json`. Do not bypass it for a production package: the
index supplies reviewed group/build citation links, and a missing index must fail the build instead of
publishing a citation-regressed corpus.

Upload the generated ZIP in cPanel and extract its contents into `public_html`. The package includes the Apache SPA fallback and WebAssembly MIME types. It contains no API, renderer, SQLite database, WDL, or credential.

The target frontend origin is `https://atlas.example` (and optional `www`). `tiles.atlas.example` remains the example host NGINX file/tile origin; it is not the v2 frontend origin. See `DEPLOYMENT_TOPOLOGY.md`.

Probe `publicTileRoot` directly after deployment. Location renders are linked when worker completion commits; there is no later Namecheap upload or publication toggle in this workflow.

## First TGG job

In Atlas Admin, choose **From World Download** and drop `TGG_wdl.zip`:

```text
Render name: TGG (June 2021 Test)
Slug: tgg-2021-06-29-ui-test
World download date: 2021-06-29
Source: TGG world download
Location: Create from detected center
```

The detected bounds should be `X [-192, 7488)`, `Z [-3248, 208)` with center `(3648, -1520)`. Use the known landmark near `(4014, -2822)` to verify alignment, then remove retained duplicate output deliberately.
