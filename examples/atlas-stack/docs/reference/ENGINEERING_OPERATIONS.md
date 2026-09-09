# Development, Build, Test, Release, And Operations

> **AI-Generated documentation.**

## Scope

This file routes engineering and release work. Host-specific deployment steps are in `docs/DEPLOYMENT_TOPOLOGY.md`; per-WDL review is in `docs/INGESTION_CHECKLIST.md`.

## Development Requirements

**Production source baseline:** .NET 10 solution `2b2tAtlas.sln`. SQLite is supplied through NuGet. The Blazor client does not require Node.js for normal build. Local combined hosting is supported by `2b2tAtlas.Server/Program.cs` when `HostStaticClient` is true.

Use a development JWT key of at least the configured application minimum and a disposable working directory when the repository database must remain untouched. Because `atlas.db` is relative to process CWD, launching from the wrong directory changes which database is opened.

## Standard Validation

```powershell
dotnet restore .\2b2tAtlas.sln
dotnet build .\2b2tAtlas.sln -c Release --nologo
dotnet test .\2b2tAtlas.sln -c Release --nologo
dotnet list .\2b2tAtlas.sln package --vulnerable --include-transitive
```

Focused ingestion/security validation:

```powershell
dotnet test .\2b2tAtlas.Ingestor.Tests\2b2tAtlas.Ingestor.Tests.csproj -c Release --nologo
```

Archive acquisition validation is deliberately separate from .NET build validation:

- historical 2026-08-24 acceptance-fixture baseline: 1,594 clean live-catalog warps, 2,114 merged unique identities, 2,104 eligible captures after excluding ten captured/ready identities, zero ownership conflicts;
- current 2026-09-05 operational checkpoint: 1,603 discovered records; 1,598 applicable queue entries; 798 final-standard WDLs saved; 81 applicable warps unavailable; 719 remaining. The parallel shard `completed` sum is 828 because it also treats 27 retryables and 3 footprint reviews as pass-terminal; do not present that number as saved WDLs;
- production representative batch: four automatic completions and one correct `needs-match`, with exact retry idempotency and Unicode request encoding exercised.

```powershell
# Narrow catalog probe, then merge without capture.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\get-archive-warp-catalog.ps1 -RootSlots 38 -MaxNewWarps 3 -MaxPages 10
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-archive-collector-queue.ps1

# Private canary; omit -MoveToReady until the ZIP and catalog evidence are reviewed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-archive-collector.ps1 -MaxWarps 1 -CaptureSeconds 35
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\import-archive-inbox.ps1 -DryRun
```

The crawler and capture controller share one lock and checkpoint every accepted result. Never run them concurrently, copy account tokens into repository files, or treat `captured` as an ingestion inbox. Full procedures are in `docs/ARCHIVE_SYNC_AUTOMATION.md`.

Test ownership includes `SecureZipArchiveTests`, `ChunkBoundsInspectorTests`, `AtlasTileAdapterTests`, `TilePublisherTests`, `IngestionJobsControllerTests`, `LegacyApiCompatibilityTests`, `RbacProfileTests`, and `PlaceTilesControllerTests`.

## Build Artifacts

| Artifact | Command owner | Deployment target |
| --- | --- | --- |
| Static Blazor + location entity package | `scripts/build-namecheap-package.ps1` | Namecheap document root |
| Self-contained API | `scripts/publish-atlas-api.ps1` | `C:\AtlasExample\Api\app-next` then controlled cutover |
| Self-contained worker | `scripts/publish-ingestion-worker.ps1` | `C:\AtlasExample\Ingest\worker` |
| Verified DB backup | `scripts/backup-atlas-db.ps1` | `B:\AtlasExample\Backups` |
| BlueMap 3D derivatives | `scripts/invoke-atlas-bluemap-render.ps1` | `F:\AtlasExample\AtlasBlueMap\location-renders` |

BlueMap profile 7 is the only currently advertised 3D profile. It follows the
official BlueMap lighting-repair guidance: a disposable Paper copy is relit with
pinned Light Cleaner/BKCommonLib, generated context chunks are removed, and an
exact Anvil inventory plus static-payload quality gate must pass. The API and
localhost preview both enforce that provenance rather than trusting a directory
name or `index.html`. The five-minute watchdog maintains one three-worker, memory-gated
coordinator after 2D ingestion, with minute-by-minute discovery, isolated mutable
caches, per-render claims, shared space reservations, and serialized publication;
check `C:\AtlasExample\Ingest\bluemap\location-render-status.json`
and `scripts\get-atlas-bluemap-status.ps1` before intervening.
The complete current selection, generation, quality-gate, API, storage,
monitoring, and recovery contract is in
[`../BLUEMAP_PIPELINE.md`](../BLUEMAP_PIPELINE.md).

The static package injects public client settings and generates one canonical semantic HTML + JSON-LD record per public location and group, reciprocal related-location and reviewed group/build links, dimension collection pages, `sitemap.xml`, `robots.txt`, `llms.txt`, Dataset/DataDownload metadata, and schema-v2 location/group JSONL catalogs. Group entity graphs use `Organization`, `WebPage`, `BreadcrumbList`, and separate build/highway `ItemList` nodes; the group directory is a `CollectionPage`/`Dataset`. Attachments are also emitted as schema-v1 `entities/media.jsonl`, a human-readable media Dataset landing page, semantic image HTML, sourced `ImageObject`/`MediaObject` nodes, and image-sitemap children. Schema-v5 `dataset.json` advertises the location, group, media, and revision-pinned group/build evidence catalogs. Both location and group semantic descriptions normalize repeated legacy literal `rnrn` separators and escaped CR/LF text before whitespace compaction, and the build scans every generated semantic artifact for either regression. Each live location UUID receives an exact root-path 301 from either host directly to its apex canonical numeric entity; unknown UUID-shaped paths return 410. Private utility routes receive `noindex,nofollow`, while interactive Blazor location and group routes receive `noindex,follow` so the static entity URL remains the sole indexable record. Missing static assets return 404 rather than false-200 SPA HTML, while lowercase JourneyMap/Xaero icon requests from cached clients are internally mapped to canonical `/Images` files. Entity and sitemap modification dates come from `Locations.ModifiedUtc` and `Groups.ModifiedUtc`. The package build fails closed when its revision-pinned group evidence index is absent, entity/redirect counts differ, UUIDs are invalid, entity/sitemap modification dates diverge, evidence citations or media/resource counts disagree across HTML, JSONL, JSON-LD, and the image sitemap, icon signatures are invalid, normalized output still contains a legacy newline artifact, or crawler-visible failure copy remains in the homepage. `scripts/tests/test-seo-package-evidence-gate.ps1` verifies that both direct and scheduled package paths reject a missing evidence index before creating output. The package must not contain secrets, SQLite, WDLs, or renderer work. Rebuild all artifacts from the same validated source revision, verify generated entity/media/evidence counts match the public API and pinned evidence index, and run `scripts/test-seo-production.ps1` after deployment.

`scripts/invoke-atlas-seo-package.ps1` is the daily change-detected wrapper. It hashes the exact public location catalog, the group list plus every group detail/relationship payload, the revision-pinned group/build evidence index, and package-producing Client/Shared source files plus both entity generators. It builds only when that fingerprint changes, writes catalog/evidence hashes and synchronized relationship/citation counts under `C:\AtlasExample\Seo`, retains the newest 14 automatic packages, and sends one Pushover notification for each new pending ZIP. Evidence is published only where an exact candidate's group ID intersects an already-reviewed Atlas location/group relationship; the package gate independently rejects missing, extra, or duplicate citations. Every automatic package includes uncached `seo-release.json`; after cPanel extraction, the next daily run observes the public fingerprint and clears the pending-upload state automatically. It never uploads to or mutates Namecheap. `scripts/register-atlas-seo-package-task.ps1` registers the limited-user 06:30 daily task. Use `-DryRun -SkipNotification` to inspect a decision without building, writing state, or sending a push.

After a validated package build, `scripts/audit-seo-enrichment-gaps.ps1` reads its location/group JSONL catalogs and emits a schema-versioned, read-only quality report. Supplying the current revision-pinned group evidence index also emits exact group/location candidates for thin pages while retaining evidence labels and revision IDs. The report rejects unknown relationship group IDs and distinguishes absent narrative/source/render resources; it does not mutate Atlas, infer ownership, or replace the package validator. Its deterministic fixture is `scripts/tests/test-audit-seo-enrichment-gaps.ps1`, and the interpretation policy plus current baseline live in `docs/research/SEO_ENRICHMENT_GAPS.md`.

## Database Release Rule

Before an API build that changes models or `SchemaUpgrader`, run SQLite online backup, `PRAGMA integrity_check`, and record SHA-256/length/UTC. A raw file copy of an active WAL database is not the release backup.

Start the staged API against the intended CWD and reviewed DB copy. Confirm `SchemaUpgrader` is idempotent, then probe core reads and authenticated operations. Keep data outside versioned application directories so binary rollback cannot overwrite it.

## Release Order

1. Pause writes and create a verified production DB backup.
2. Run Release build, full tests, and transitive vulnerability audit.
3. Build API, static client, and worker from the same source state.
4. Deploy/probe API and exact CORS before static client cutover.
5. Verify tile origin, MIME types, immutable caching, and signed paths.
6. Deploy static package and purge/update edge caches as required.
7. Smoke Directory, map, search, auth, Admin permissions, legacy reads, and mobile layout.
8. Start/check the worker and run a known valid reviewed WDL before declaring ingestion operational.

Use `scripts/test-production.ps1` for the repository's production acceptance surface. Do not infer release success from a frontend-only deployment when API/schema behavior also changed.

## Rollback Ownership

Frontend rollback restores the previous Namecheap document-root package. API rollback switches to the previous app directory while preserving data. Worker rollback restores the previous package and leaves receipts untouched. Restore SQLite only for confirmed data/schema damage, using a verified backup. Unlink render metadata before retiring immutable tiles.

## BlackBrain Operations

**Optional:** BlackBrain indexing should run only when source artifacts change and must pause for configured game/render contention. Atlas release gates do not depend on it.

**Future:** enable model-assisted enrichment only after dependency security, authentication, resource routing, source attribution, and reviewed deterministic precision gates in `docs/BLACKBRAIN_INTEGRATION.md` pass.
