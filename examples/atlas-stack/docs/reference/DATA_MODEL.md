# Data Model And Schema Lifecycle

> **AI-Generated documentation.**

## Scope

This file explains persisted concepts and schema evolution. HTTP representations belong in `API_COMPATIBILITY.md`; field validation remains authoritative in shared DTOs and controllers.

## Database Lifecycle

**Production:** Atlas uses SQLite through `2b2tAtlas.Server/Models/AtlasContext.cs` and `AtlasContext.Highways.cs`. `2b2tAtlas.Server/Program.cs` builds the path as `<current working directory>/atlas.db`.

Startup order is:

1. `AtlasContext.Database.EnsureCreatedAsync()` for a new database.
2. `SchemaUpgrader.UpgradeAsync()` for additive, idempotent changes.
3. user, highway, and group seeders.

This repository does not use EF migrations for operational upgrades. New tables, columns, indexes, or one-time data conversions for existing databases belong in `2b2tAtlas.Server/Services/SchemaUpgrader.cs`. Guarded columns use `PRAGMA table_info`; named one-time conversions are recorded in `SchemaMigrations`.

## Core Location Graph

`Locations.Rowid` is the integer primary key. `LocationUuid` is a persistent unique compatibility identifier; the upgrader recovers it from `Warps.LocationUuidFk` where possible and generates missing values. `ModifiedUtc` is the public entity-change clock: location edits, attachment replacement, accepted enrichment/revisions, and successful render registration or re-rendering touch it. Static entity pages, dataset metadata, and sitemap `lastmod` values derive from this field rather than the package build time. Existing rows receive the later of their add date and the SEO entity-page migration floor during the additive upgrade.

Related records use `LocationRowid`:

- `Warps`: Archive teleport commands for historical WDLs. A location may have many; `ArchiveSha256` uniquely
  binds an imported immutable WDL to at most one warp while legacy rows remain nullable.
- `Attachments`: public HTTPS media records with media type, optional thumbnail, original source, caption, and attribution; no binary payload is stored in SQLite. Reviewed copies live on the separately operated media origin.
- `Renders`: per-location sparse tile overlays.

The public DTO is `2b2tAtlas.Shared/Models/Location.cs`. Modern dimensions are `0=Overworld`, `1=Nether`, `2=End`, owned by `2b2tAtlas.Shared/Models/Dimension.cs`.

## Render Models

`Renders` and `MapRenders` are different contracts.

`Renders` belongs to one location. Exact placement uses `MinX`, `MinZ`, `MaxXExclusive`, `MaxZExclusive`, `MaxNativeZoom`, `CoordinateScheme`, `TilesPath`, and `HasDayNight`. New renders use immutable generation URLs containing `{dn}` (`day`/`night`). `IX_Renders_TilesPath` prevents duplicate URLs. `IsPublic` controls public map/catalog exposure without deleting an ingested capture; `EquivalentToRenderId` points a retained, rerenderable equivalent capture at its canonical public representative. That equivalence requires corroborated shared-location family semantics and is never inferred from footprint overlap alone.

`Source` is a stable machine-readable provenance class (`archive-collector`, `manual-upload`, `wdl-ingestion`, or `legacy`), not prose embedded in a public description. `ArchiveWarpId` is an optional unique one-to-one link to the exact Archive exhibit captured for that render. A location can still own many warps and renders, but one warp cannot silently label multiple render snapshots. Public render DTOs expose the linked warp so location and map cards can display and copy the corresponding `/warp` command.

BlueMap ownership is deliberately not stored as another mutable database row.
`BlueMapCatalogService` projects a validated immutable `manifest.json` onto the
existing render DTO as nullable `BlueMapUrl`, `BlueMapPath`, and
`BlueMapProfileVersion`. The generation is keyed by render ID, immutable source
SHA-256, BlueMap version, and Atlas profile version. If the manifest, exact
relight-footprint audit, canonical camera start, profile floor, or static
webroot fails validation, those fields are absent while the `Render`, source
WDL, and 2D tiles remain valid. See
[`../BLUEMAP_PIPELINE.md`](../BLUEMAP_PIPELINE.md).

Legacy descriptions may retain import-era newline spellings such as escaped CRLF or literal `rnrn` in the authoritative source field. Interactive views and generated SEO assets pass descriptions through `DisplayText`: full views restore paragraph breaks and compact cards/metadata collapse them to spaces. This is deliberately presentation-only so provenance-bearing source text is not silently rewritten; package verification rejects leaked newline artifacts in public entity pages and catalogs.

`MapRenders` is a dimension-level layer shown in the render picker. It has unique `Slug`, `UrlTemplate`, `HasDayNight`, sort order, provenance, and `IsPublished`. DTO: `2b2tAtlas.Shared/Models/Maps/MapRender.cs`.

## Highways, Groups, And Review

`Highways` stores dimension-aware ordered geometry as `PointsJson`, construction metadata, visibility, review status, attribution, and audit user IDs. `BuilderGroupId` remains the primary-builder compatibility field. `HighwayGroups` is the explicit many-to-many history table: one route can credit a current steward, predecessor builders, and contributors without erasing any of them; each link carries a role and concise evidence note. Public reads filter to `Visibility=Public` and `ReviewStatus=Approved` and expose the full attribution array.

`Groups` stores the canonical display name, Build/Highway/Mixed/Other classification, concise reviewed history, color, founded/status labels, public website/Discord/wiki links, and separately credited logo URL/source URL. `ModifiedUtc` drives group entity-page and sitemap freshness. `LocationGroups` is the explicit many-to-many build-attribution table; its composite key prevents a location/group duplicate and `Role` records the reviewed relationship (currently `Builder`). A location may have several credited groups, and one group may link to many bases. Group attribution is independent of Archive warp/render identity and must never rewrite location coordinates, warps, or render provenance.

The canonical seeder adds exact reviewed location-name associations and narrowly defined provenance rules. The current provenance rule recognizes Archive warp suffixes that explicitly identify SpawnMason/SpawnMason Lodge ownership; it does not infer ownership from the word `Sky`, coordinate overlap, fuzzy names, membership, raids, griefs, or mere residency. Ambiguous attribution requires manual review. Public location and group detail responses expose the relationship in both directions, while public counts include only public renders/highways where those filters apply.

`Revisions` stores proposed JSON, submitter/reviewer identity, notes, status, and timestamps. `AuditLogs` stores action, entity, user, summary, optional details JSON, and UTC creation time. They support moderation and forensics; audit is not a database rollback mechanism.

Owners: `2b2tAtlas.Server/Models/Highway.cs`, `HighwayGroup.cs`, `Group.cs`, `LocationGroup.cs`, `Revision.cs`, `AuditLog.cs`, plus shared DTOs under `2b2tAtlas.Shared/Models/`.

## Users And Permission Overrides

`Users` stores account/authentication fields plus canonical `Role`, `IsSuperAdmin`, profile fields, failed-login state, trust metadata, and activation state. Passwords are BCrypt hashes; generated seed credentials are operational files, not schema documentation.

`RolePermissions` is a replacement override for one role's code-defined default. If rows exist for a role, `2b2tAtlas.Server/Services/AuthService.cs` uses those rows; otherwise it uses `Atlas.Auth.RolePermissions.ForRole`. SuperAdmin always resolves every permission.

## Ingestion Jobs

`IngestionJobs` coordinates queue state without storing WDL bytes. It records public ID, intake filename, slug, target dimension, optional `WorldRoot`, optional reviewed `RenderTopY`, status/stage/progress/ETA, archive SHA-256, the inferred single Archive warp and evidence source, automatic/manual match decision/confidence/reason, warp/location/render linkage, re-render state, lease/claim data, inspection JSON, and timestamps. Source bytes live in the external X: content-addressed archive. A completed job with a render but no warp is also the durable provenance edge for a verified pre-Archive/community render source; the public API exposes it through render-native WDL routes instead of fabricating an Archive warp. `RenderTopY` is durable render provenance: retries use the same vertical cutaway instead of relying on mutable worker configuration.

Unique indexes protect `PublicId` and the `(Slug, Dimension)` pair (one base slug hosts a job per dimension); `IX_IngestionJobs_Status_Id` supports claim ordering. Completion transactionally creates or links a location render in `IngestionJobsController`.

## Change Rules

Back up the live database with SQLite online backup before deployment. Additive changes must be rerunnable. Uniqueness and data backfills must be explicit. Never assume changing a C# model updates an existing database, and never raw-copy an active WAL database as the backup strategy.
