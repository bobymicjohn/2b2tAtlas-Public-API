# Atlas stack

The application behind 2b2tAtlas, with a fresh database and example host configuration.
Blazor UI, ASP.NET Core API, SQLite, WDL intake and rendering, Archive collectors,
BlueMap orchestration, historical research tools, and the tests live here together.

This is a source snapshot, not a connection to the live Atlas. Bring your own worlds,
Minecraft accounts, rendering tools, storage and HTTPS origins. The original project's
Git history, user database, credentials and operator incident reports are not included.

## Run the UI and backend

Use Windows and the .NET SDK selected by [global.json](global.json). From this repository:

```powershell
Set-Location examples/atlas-stack
dotnet build .\2b2tAtlas.sln -c Release
dotnet test .\2b2tAtlas.sln -c Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-example.ps1
```

Open **http://127.0.0.1:5297**. Sign in as **atlas-owner** using the generated password
in `.local/secrets.json`. The launcher restricts that file to the current Windows
account, does not print credentials, and keeps the database, backups and intake in
the git-ignored `.local` directory. It refuses an occupied port and starts only the
example API, in the foreground. Stop it with Ctrl+C; use `-SkipBuild` on later starts.

Only that owner is created. No production users or contributor grants are seeded.
Changing the bootstrap password later does not reset an existing account. Create
contributors through Admin and choose the smallest role they need. Owner identity
is deliberately reserved to account 1, `atlas-owner`; changing that name also requires
updating `AtlasSessionValidator`, rather than silently transferring ownership.

The initial location catalog is empty. Add a location in Admin, then attach a WDL or
register your own map layers. Optional historical group/highway seed code is retained;
enable `Bootstrap__SeedHistoricalCatalog=true` only on a deliberately configured instance.
Media seed manifests are empty. The included Nocom JSON contains publicly released
historical aggregate metadata, not raw observations or private player records; see
[data provenance](DATA_AND_LICENSES.md).

## What is included

| Folder | Purpose |
| --- | --- |
| `2b2tAtlas.Client` | Directory, maps, groups, locations, attachments, admin/editor UI |
| `2b2tAtlas.Server` | Public JSON/MCP, authenticated edits, moderation, job queue, audit and recovery |
| `2b2tAtlas.Shared` | API contracts, roles and permissions |
| `2b2tAtlas.Ingestor` | ZIP/NBT validation, chunk coverage, immutable sources, 2D rendering and publication |
| `tools/AtlasArchiveCoverage` | Fabric companion for adaptive surveys, saved terrain resume and missing-chunk repair |
| `scripts` | Collector lanes, handoff, BlueMap, backup/restore, research and static export tools |
| `2b2tAtlas.Ingestor.Tests` | Backend, ingestion, permissions, recovery and UI regressions |
| `e2e` | Browser test scenarios for a configured local instance |

## Process a WDL

Start with [the first-WDL guide](docs/WDL_GETTING_STARTED.md), then the
[ingestion guide](docs/INGESTION_PIPELINE.md) and [worker configuration](docs/WDL_WORKER_DEPLOYMENT.md).
The checked-in worker and renderer profiles are templates. Set paths, your tile origin,
the uNmINeD executable and its expected SHA-256 before running a job. Supply the worker
key through `ATLAS_INGEST_WORKER_KEY`; only its hash belongs in API configuration.

For automatic acquisition, use the [Archive collector guide](docs/ARCHIVE_SYNC_AUTOMATION.md).
It needs Java 21 and a legitimate Minecraft account per concurrent worker. Authentication
is performed locally. The installer downloads pinned third-party tools and verifies
their checksums; their executables and game sessions are not bundled here.

Collectors retain verified terrain across interruptions and lane changes, perform
missing-only repair, and hold incomplete or uncertain captures for review. A saved
partial survey is not a finished WDL. The final coverage and publication checks stay
enabled in this export.

[BlueMap](docs/BLUEMAP_PIPELINE.md) is a separate optional Docker/Java queue over completed
sources. [Media research](docs/MEDIA_RESEARCH.md) and [local AI enrichment](docs/GROUP_ENRICHMENT_RUNBOOK.md)
are optional too. They are not started by the example launcher.

## Configure your installation

The quick start runs the UI and API at one origin. To host the client separately, set
`2b2tAtlas.Client/wwwroot/appsettings.json` → `ApiBaseUrl`, publish the client, and configure
HTTPS and SPA rewrites at your host. Never put keys in browser configuration.

`atlas.example`, `api.atlas.example`, `tiles.atlas.example`, documentation-only IP addresses,
and `C:\AtlasExample` / `D:\AtlasExample` paths are intentional placeholders. Replace them
with your own domains and directories throughout the relevant subsystem. Some map-layer
definitions and canonical-link builders are code constants; search for `atlas.example`
when configuring the map and crawler outputs. The UI can run without those layers, but
world terrain, Nocom tiles and downloads need your own published artifacts.

The operations scripts are Windows reference implementations, not an automatic installer.
Read parameters and configure accounts, task names, paths, tool versions and disk reserves
before registering collectors, scheduled tasks, backup jobs or GPU work. On a small system,
start one collector and one renderer. The C/D/E/F/X layout in the guides describes storage
roles, not a requirement to own those drive letters.

See [deployment](docs/DEPLOY_FROM_SCRATCH.md), [security](SECURITY.md),
[source and data licensing](DATA_AND_LICENSES.md), and [validation](VALIDATION.md).
Report general source issues in this repository; keep credentials and unpublished saves out of issues.
