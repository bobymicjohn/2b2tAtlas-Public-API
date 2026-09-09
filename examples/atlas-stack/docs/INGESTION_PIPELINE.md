# WDL ingestion pipeline

The original ZIP is the source of truth. Every render is a reproducible derivative with a receipt back to that source. For installation, start with [the WDL quick start](WDL_GETTING_STARTED.md).

The public [About explanation](https://atlas.example/about#ingestion-pipeline)
summarizes this workflow for visitors. Its numbered cards describe collection,
preservation, matching, 2D publication, downstream 3D, independent historical
research, and reuse. They are not seven blocking worker stages. Archive coverage
checks apply to the surveyed capture area; a successful audit does not establish
the full historical extent of a build or recover absent upstream terrain.

When changing that explanation, also update the homepage JSON-LD in
`2b2tAtlas.Client/wwwroot/index.html` and the dataset/llms metadata in
`scripts/generate-location-entities.ps1`. The Namecheap package gate checks that
the visible, dataset and homepage stage names and positions agree. Static exports
describe the catalog at site publication; they can lag behind current API records.

## Purpose and trust boundary

`2b2tAtlas.Ingestor` is an offline .NET 10 CLI and polling worker for turning untrusted Minecraft Java WDL ZIPs into reviewed web-map tiles. The owner uploads through authenticated API sessions (`renders.manage` plus the non-delegable owner check) in ordered 32 MiB chunks. Completion verifies length and ZIP signature, writes to `D:` intake, and SHA-verifies into the deduplicated `E:\AtlasExample\WorldDownloads` archive before queueing. The API never extracts or renders; there is no anonymous upload endpoint.

The example host Archive collector is a separate upstream producer. It can catalog and capture, but it cannot bypass intake: Minecraft saves are transient on `C:`, verified raw/manifest/footprint artifacts are assembled in `D:\AtlasExample\Ingest\archive-captures`, and `archive-collector-captures.ps1` hash-verifies them into the durable `E:\AtlasExample\WorldDownloads\collector` archive before promotion. A reviewed batch moves through `ready`, and `import-archive-inbox.ps1` submits each hash through the protected local-intake endpoint. Downloader reports, the exact canonical warp, catalog path, and capture mode remain provenance evidence; they do not relax any archive, dimension, matching, rendering, or publication gate. The handoff is idempotent: accepted SHA-256 values are checkpointed locally, and an exact immutable hash/slug/intake retry returns the existing durable job rather than creating another render.

The collector journals each adaptive capture before downloading. Interrupted saves resume from verified private terrain on D; failed coverage audits repair only the missing chunks and rerun the original full audit. See [capture recovery and sparse repair](COLLECTOR_RECOVERY.md).

The pipeline is deliberately staged:

1. `inspect`: read-only ZIP central-directory validation and SHA-256 inventory.
2. `prepare`: re-inspect, copy an immutable snapshot, verify its hash, safely extract, inspect `level.dat`, discover dimensions, and write a render plan.
3. `render`: launch the hash-pinned uNmINeD profile twice: daytime, then `--night=true`. When the profile enables `nightColorGrade`, Atlas keeps uNmINeD's native night/block-light luminance but restores terrain chroma from the pixel-identical day tile inventory. The production starting point is saturation `1.20` and lightness `1.15`; both are previewable and adjustable in **Admin > Render Settings**. The grade is recorded in night provenance and a mismatched day/night inventory fails closed.
4. `adapt`: independently convert day and night native tiles into identical Atlas coordinate schemes and verify both layouts.
5. `verify`: independently inspect generated `z/y/x` image paths, coordinate ranges, tile count, and byte budget.
6. `publish`: write both variants into a new immutable `g-<UTC>-<nonce>/{day|night}` generation and verify both receipts.
7. Complete: submit exact bounds plus a `{dn}` URL template. The API switches the Render row transactionally; only then does the worker remove superseded generations.

A failure in one stage does not make a later stage valid. Completion requires a matching publication receipt and derives maximum zoom from the measured published inventory.

BlueMap is an independent downstream derivative, not an eighth blocking stage.
The five-minute Atlas watchdog maintains one coordinated three-worker, memory-gated service
(`scripts\start-atlas-bluemap-coordinator.ps1`) that discovers newly completed
source-backed render IDs every minute. Profile 7 copies the
immutable WDL to disposable work storage, repairs saved lighting with pinned
Paper/Light Cleaner components, prunes every generated neighbor record, requires
an exact original/final Anvil chunk-set match, and only then runs pinned BlueMap
5.23. Overworld, Nether, and End render IDs are independent, so a WDL containing
multiple dimensions can publish one 2D and 3D derivative for each applicable
dimension. A BlueMap failure or storage pause leaves the primary ingestion job
complete and is retried after a 30-minute per-render cooldown.

The downstream pass takes a database snapshot rather than accepting a new
untrusted queue message. It selects distinct surviving render IDs backed by a
completed ingestion record and immutable archive SHA-256. New renders that
complete after discovery are exposed as pending discovery and enter the refreshed
queue within one minute. All three workers have isolated mutable caches, exclusive
render claims, shared disk reservations, and serialized publication; Admin shows
their independent stages and activity. Public 3D URLs appear only after the generation manifest passes the same
profile-7 quality gate enforced by static-file serving. The complete implemented
contract, commands, paths, and incident procedure are in
[`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md).

## Supported input eras

World discovery understands nested world roots and the conventional Java dimensions:

| Era | Chunk storage | Detection |
| --- | --- | --- |
| Alpha | individual `c.*.dat` chunks | two-level legacy chunk directories |
| Beta 1.3 through 1.1 | McRegion | `region/r.*.*.mcr` |
| 1.2 and later | Anvil | `region/r.*.*.mca` |
| Mixed/upgraded worlds | multiple formats by dimension | reported as `mixed` |

Region chunks support standard gzip, zlib, raw, and Minecraft's lz4-java block compression introduced in the 1.20.5 development cycle. Inline and external `.mcc` LZ4 chunks are checksum-verified with bounded allocation. Namespaced custom compression ID `127` is rejected because a WDL cannot safely supply executable codec implementations.

Dimension roots are `.` (Overworld), `DIM-1` (Nether), and `DIM1` (End), plus canonical vanilla namespaced paths `dimensions/minecraft/overworld`, `dimensions/minecraft/the_nether`, and `dimensions/minecraft/the_end`. If both legacy and namespaced roots contain occupied chunks for the same dimension, preparation fails closed. Unknown custom namespaces are not guessed. `level.dat` is parsed with a bounded NBT reader for `LevelName`, `DataVersion`, `Version.Name`, and `LastPlayed`. Storage recognition is independent of version-name metadata, so missing old metadata is not fatal. `LastPlayed` supplies a convenient UTC date suggestion but is editable metadata, not proof of capture time or server origin.

This detection does not promise that every uNmINeD release renders every Minecraft version. Renderer support is an independent, pinned profile decision. A missing dimension command fails closed.

The Admin upload has an optional **World root** field. Ambiguous archives fail closed and report relative candidates; retry with one exact archive-relative path. Rooted paths, traversal, and dot segments are rejected.

Structural validation does not prove that a WDL came from 2b2t or used the 2b2t seed. Multiplayer WDL metadata is not cryptographically authoritative. A museum/archive may store an imported 2b2t End or Nether build in its own Overworld or custom dimension, so the storage path also does not prove original dimension. Public promotion requires reviewed source provenance, including the archive catalog/warp identity and original-dimension attribution; overlap and terrain fingerprints are supporting evidence rather than an absolute seed or dimension claim.

For recognized Archive input, one canonical warp identifies one WDL and one location may own many dated warps/WDLs. An already-known warp resolves to its existing location. Otherwise the matcher combines date-insensitive warp identity, location-name similarity, prior render-footprint overlap, coordinates, dimension, and Archive provenance. An unsuffixed location name and an explicit first iteration (`1`, `I`, or `one`) share one canonical identity; second and later iterations remain distinct bases. A unique high-confidence match attaches automatically; a trusted warp with no plausible existing candidate can create a location after rendering succeeds. Conflicts and low confidence stop at `needs-match`.

Render-footprint overlap is corroboration, not identity. A very large historical WDL can contain the complete footprint of many unrelated later captures; outside the normal coordinate radius, overlap cannot even nominate that location unless compatible Archive-warp identity or location-name evidence also agrees. This specifically prevents continent-scale renders such as Mu's original WDL from becoming a false match merely because a smaller base lies inside their bounds.

New jobs remain in the unclaimable `matching` preflight state until the upload-time matcher has saved its result; an interrupted preflight is recovered after five minutes. Prepare-time review revokes any preliminary destination, and completion independently refuses to register a render, warp, or location while `MatchResolved=0` or the decision is still `review`. This prevents a fast worker or stale location ID from turning shared museum-world footprint overlap into a destructive attachment.

Collector registration keeps identity and history separate: an auto-created location and its render use the Archive display identity with recognized date and vanilla-dimension suffixes removed, while `WorldDownloadDate` remains the historical snapshot date. No collector boilerplate is written into `Location.Description` or `Render.Description`. Instead, the render stores `Source=archive-collector` and a unique `ArchiveWarpId`. Recapturing the same exact warp requests replacement of that render's immutable tile generation rather than creating a duplicate card.

## Default resource limits

| Limit | Default | Reason |
| --- | ---: | --- |
| Input ZIP | 32 GiB | practical intake cap for known multi-GB WDLs |
| Declared expanded data | 256 GiB | permits large region sets while bounding disk use |
| Generated output | 512 GiB | tile pyramids can exceed world size substantially |
| Generated entries | 5,000,000 | bounds tiny-file/inode exhaustion and verification work |
| Free-space reserve | 20 GiB | avoids filling the host volume |
| ZIP entries | 500,000 | bounds metadata, path, and extraction work |
| One expanded entry | 8 GiB | region files should not require unbounded entries |
| Per-entry compression ratio | 250:1 | rejects common ZIP-bomb behavior above 1 MiB |
| Path length/depth | 240 chars / 32 parts | portable Windows-safe extraction |
| Candidate worlds | 16 | rejects accidental packs and discovery abuse |
| Expanded `level.dat` | 16 MiB | normal NBT is far smaller |
| Renderer runtime | 24 hours | bounds hung processes |
| Renderer log | 64 MiB | prevents redirected-output disk exhaustion |

Limits are code-owned in `IngestLimits`. Raise them only for a measured archive, with enough storage for the immutable ZIP, expanded world, generated tiles, and reserve simultaneously.

## Manifest

Start from `2b2tAtlas.Ingestor/examples/ingest.example.json`.

- `schemaVersion`: exactly `1`.
- `slug`: stable lowercase identifier, 1-54 characters (the dimension suffix must fit the 64-character API field).
- `name`: public picker label, 1-90 characters (the dimension label must fit the 100-character API field).
- `worldDownloadDate`: `YYYY-MM-DD`, not in the future.
- `source`: provenance, 1-200 characters.
- `worldRoot`: optional safe relative path. Required when an archive contains multiple worlds.
- `dimensions`: `"auto"` or an array containing `overworld`, `nether`, and/or `end`.
- `dayNight`: compatibility metadata; location-render workers always produce both day and night variants.
- `scale`: compact block radius/extent label such as `5k`, `256k`, or `1m`.
- `publish`: retained in the plan. The worker's location-render workflow links on successful completion; the standalone legacy global-render registration command remains unpublished until reviewed.

Unknown keys and malformed types are rejected. The manifest is metadata, not a command source.

## Renderer profiles

uNmINeD CLI behavior is version-specific. Do not copy flags from another release or assume that GUI settings map to CLI switches.

1. Download a specific CLI release from the publisher.
2. Verify its provenance and record `Get-FileHash -Algorithm SHA256`.
3. Capture `unmined-cli --help` and each relevant subcommand's help.
4. Test each dimension against a small known world.
5. Inspect the emitted layout and confirm global coordinate alignment in Atlas.
6. Create a local profile with fixed argument arrays for only the verified dimensions.

The profile allows only `{world}` and `{output}` substitutions. It is local trusted configuration, not accepted from a WDL. Arguments are passed through `ProcessStartInfo.ArgumentList`; no shell parses them. The executable is checked by name and SHA-256 before every run. The child receives a minimal environment, redirected bounded logs, a timeout, and process-tree termination.

The example profile contains only the officially documented Overworld command:

```text
unmined-cli web render --world=<world> --output=<empty-folder>
```

Nether, End, settings-file, and output-layout entries must not be added until verified against the exact pinned binary. This is an intentional safety stop, not missing auto-detection.

## Commands

```powershell
dotnet run --project .\2b2tAtlas.Ingestor -- inspect `
  --archive 'F:\Inbox\world.zip'

dotnet run --project .\2b2tAtlas.Ingestor -- prepare `
  --archive 'F:\Inbox\world.zip' `
  --manifest '.\2b2tAtlas.Ingestor\examples\ingest.example.json' `
  --work 'F:\AtlasWork'
```

`prepare` prints the SHA-256 job ID. The job directory is `work/<sha256>` and is never silently reused.

If `prepare` was interrupted or failed, retry only that stage with the same archive:

```powershell
dotnet run --project .\2b2tAtlas.Ingestor -- prepare `
  --archive 'F:\Inbox\world.zip' `
  --manifest '.\2b2tAtlas.Ingestor\examples\ingest.example.json' `
  --work 'F:\AtlasWork' --resume true
```

Resume is accepted only from `snapshotted` or `failed`-during-prepare state. It rehashes the source and immutable snapshot, compares the complete archive inventory, and moves any prior `worlds/` or render plan into `attempts/` before fresh extraction. A render failure cannot be resumed as prepare.

```powershell
dotnet run --project .\2b2tAtlas.Ingestor -- render `
  --job '<sha256>' --work 'F:\AtlasWork' `
  --profile 'C:\AtlasConfig\renderer.json'

# Only after an interrupted render, with matching recorded provenance:
dotnet run --project .\2b2tAtlas.Ingestor -- render `
  --job '<sha256>' --work 'F:\AtlasWork' `
  --profile 'C:\AtlasConfig\renderer.json' --resume true
```

Native uNmINeD web tiles use signed coordinates and negative zoom names. Convert them into an explicitly certified Atlas scheme before publication:

```powershell
dotnet run --project .\2b2tAtlas.Ingestor -- adapt `
  --job '<sha256>' --work 'F:\AtlasWork' `
  --dimension overworld --scheme atlas-overworld-sparse-v1
```

`atlas-overworld-sparse-v1` retains global Atlas coordinates on the infinite `CRS.Simple` plane. uNmINeD zoom 0 uses 256 blocks per tile; Atlas URL zoom 10 uses the same resolution with `(tileX + 500, tileZ + 500)`. Signed and out-of-standard-XYZ coordinates are valid. The adapter requires every chunk-derived native tile, rejects visible renderer output outside that inventory, prunes fully transparent renderer padding, copies maximum-detail tiles without re-encoding, and generates floor-aligned parents through zoom 0.

Manually reviewed captures use dimension-gated schemes: `atlas-overworld-sparse-v1`, `atlas-nether-sparse-v1`, and `atlas-end-sparse-v1`. End has no Leaflet zoom offset: uNmINeD's 256-block tiles map to URL zoom 8 with an 82-tile origin offset. Auto-detect queues every recognized dimension present so an End capture is never silently discarded because a small Overworld or Nether fragment shares the ZIP.

Verify each final tile-only subtree. The sparse verifier accepts canonical signed coordinates but still requires exact `z/y/x.ext` paths, one image extension, zoom 0, and contiguous zoom levels. It rejects links, extra files, unexpected directories, duplicate coordinates, and output beyond byte or entry-count limits. Its inventory SHA-256 binds every canonical tile path, length, and byte.

```powershell
dotnet run --project .\2b2tAtlas.Ingestor -- verify `
  --tiles 'F:\AtlasWork\<sha256>\render\overworld\atlas-tiles' `
  --coordinate-scheme atlas-sparse-v1

dotnet run --project .\2b2tAtlas.Ingestor -- publish `
  --job '<sha256>' --work 'F:\AtlasWork' `
  --dimension overworld --tile-root 'atlas-tiles' `
  --destination 'F:\AtlasPublished\spawn-2026-01\overworld'
```

`--tile-root` is relative to `render/<dimension>` and must match the adaptation receipt. Staging and destination must share a volume so publication is an atomic directory rename. Existing destinations are never overwritten. Publication writes `publication-<dimension>.json` only after destination reverification.

The normal Admin/worker workflow completes automatically after publication: the worker submits exact bounds and the server transactionally creates or links the per-location render.

The following standalone command exists only for intentionally creating a legacy global map-render layer after serving and probing its public tile URL. Put the JWT only in an environment variable:

```powershell
$env:ATLAS_RENDER_TOKEN = '<short-lived-token>'
dotnet run --project .\2b2tAtlas.Ingestor -- register `
  --job '<sha256>' --work 'F:\AtlasWork' `
  --api 'http://127.0.0.1:5297/' `
  --token-env 'ATLAS_RENDER_TOKEN' `
  --dimension overworld `
  --url-template 'https://tiles.atlas.example/AtlasTiles/spawn-2026-01/overworld/{z}/{y}/{x}.png'
Remove-Item Env:\ATLAS_RENDER_TOKEN
```

The server accepts only configured tile prefixes and exact `{z}`, `{y}`, `{x}`, and conditional `{dn}` placeholders. Legacy global registration rejects a missing, mismatched, or tampered publication receipt and uses its measured maximum zoom. It sets `IsPublished=false`; promote only after visual coordinate checks.

## Job layout and recovery

```text
<work>/<archive-sha256>/
  input.zip                 immutable snapshot
  archive-report.json       sizes, count, hash, level.dat candidates
  worlds/                   extracted world(s)
  render-plan.json          selected world and dimensions
  render/                   renderer staging output and bounded logs
    .atlas-render-provenance-<dimension>.json
    <dimension>/atlas-tiles/ adapted exact z/y/x pyramid
  attempts/                 preserved artifacts from prepare retries
  adaptation-<dimension>.json
  publication-<dimension>.json
  job-state.json            stage, timestamp, detail, and failed stage
```

- `inspect` is always safe to repeat.
- A failed `prepare` leaves a job and failure state for forensics. Explicit resume preserves old artifacts and performs fresh extraction from a revalidated snapshot.
- `render` rejects existing dimension output unless `--resume true` is explicit and matching provenance exists.
- Render provenance binds the plan hash, dimension, world root, renderer version/hash, and normalized profile arguments. Missing or stale provenance fails closed.
- A completed provenance record is written only after the renderer exits successfully; interrupted `started` provenance remains resumable when every bound field still matches.
- State transitions fail closed: render starts only from prepared state (or an explicit render retry), adaptation requires rendered state, and publish requires adapted state plus matching render/adaptation provenance.
- `publish` never overwrites. For location renders, unlink the render metadata before retiring tiles. For legacy global layers, remove or unpublish the registration first. Delete immutable trees only after cache expiry.
- Retain source ZIP, reports, profile, binary hash, and plan until the render is independently reproducible.

## Coordinate acceptance

A syntactically valid pyramid can still be geographically wrong. Before promotion:

1. Verify adjacent-region continuity and no duplicated/missing boundary columns.
2. Compare at least three known Minecraft coordinates against visible landmarks.
3. Test positive and negative X/Z quadrants.
4. Confirm the Atlas `CRS.Simple` origin, axis direction, tile size, and deepest zoom against emitted HTML/config.
5. Confirm Nether scaling is represented intentionally; do not infer it from the database dimension number.
6. Compare an overlapping prior render at several zooms.

The TGG fixture certifies the Overworld transform, exact chunk bounds, and native tile inventory automatically. Landmark inspection remains a release check for renderer/profile changes and for newly certified dimension schemes.


## Group attribution at publication

Completed ingestion applies `ArchiveGroupAttributionService` inside the location/render/warp publication transaction, before optional AI enrichment. Complete Archive owner tags and reviewed build prefixes assign additive, audited group credits even when the display name omits the group. This also runs for new warps on existing locations and rerenders. Startup backfill uses the same rules; AI review-only mode preserves its no-apply behavior. See [the September 7 repair and verification](GROUP_ENRICHMENT_RUNBOOK.md) for rule boundaries, measured coverage and rollback details.
