# Troubleshooting And Runbooks

> **AI-Generated documentation.**

## Scope

Use this file to route symptoms to the owning boundary. Preserve evidence before repair. Detailed WDL incident handling is in `docs/INGESTION_CHECKLIST.md`; deployment recovery is in `docs/DEPLOYMENT_TOPOLOGY.md`.

## Client Loads But Data Is Empty

Check the packaged `ApiBaseUrl`, then request `GET http://127.0.0.1:5297/api/locations` directly. If direct API works but browser calls fail, inspect the exact `Access-Control-Allow-Origin`; API CORS comes from `Cors__AllowedOrigins__*` in the API process environment.

If the API runs from `C:\AtlasExample\Api\data`, do not assume an `appsettings.json` beside the binary is the active content-root configuration. Confirm process environment and working directory.

## API Opens The Wrong Or Empty Database

`2b2tAtlas.Server/Program.cs` resolves `atlas.db` from `Directory.GetCurrentDirectory()`. Stop the process, inspect its launcher/task CWD, preserve any unexpected DB, and restart from the intended data directory. Do not copy over a live WAL database.

## API Upgrade Fails

Stop writes and preserve DB/WAL/SHM plus startup logs. Restore a verified backup only if damage is confirmed. Inspect `SchemaUpgrader` for the failing guarded operation and `SchemaMigrations` for one-time conversions. Changing entity classes alone does not upgrade an existing DB.

## WDL Upload Fails Or Does Not Queue

The bundled client creates a session below `http://127.0.0.1:5297/api/ingestion-jobs/upload-sessions`, sends ordered chunks, then completes the session with a bearer JWT. A `401` means the login expired, the caller does not own the session, or the account lacks `renders.manage`. A `409` during chunk transfer usually means the supplied offset does not match the server's current length; restart the upload. A `503` means the intake root is unavailable. Confirm API CORS allows the site origin for `POST` and `PUT` requests.

## Upload Is Rejected Before Queueing

Session creation requires bounded metadata and declared archive length no greater than 32 GiB. Chunks must be ordered, no larger than the server-provided chunk size, and complete to the declared length; finalization verifies the ZIP signature. The requested dimension may be `overworld`, `nether`, `end`, or `auto`. With `auto`, the API scans Alpha, McRegion, and Anvil names and queues every recognized canonical dimension present. Unknown custom namespaces are rejected rather than guessed. Jobs are unique per `(slug, dimension)`. If preparation reports multiple safe world roots, retry from Admin with one exact archive-relative **World root**; host paths, traversal, and dot segments are rejected.

## Archive Catalog Crawl Stops

Inspect `C:\AtlasExample\Ingest\archive-sync\logs` and the current HeadlessMc game log. A missing or late inventory GUI is retryable; changed menu semantics, authentication failure, permission denial, or an unexpected dimension transition are stop conditions. The crawler checkpoints every accepted leaf in `archive-warp-catalog.json`, so resume the same bounded command rather than deleting state or starting a second client. Catalog and capture share one lock and must not overlap.

## Archive Capture Exists But No Job Appears

That is the expected quarantine boundary. `captured` means the downloader ZIP, completion report, region inventory, and copied SHA-256 passed validation; it does not mean approved for Atlas. Promote only a reviewed batch with `-MoveToReady`, confirm the ZIPs in `ready`, run `import-archive-inbox.ps1 -DryRun`, and then run the importer without `-DryRun`. The importer reads the actual worker intake root from `C:\AtlasExample\Ingest\config\worker.json`.

If a capture is already recorded as `captured`, the controller will not recapture or implicitly promote it. Use a new uncaptured queue item for a test batch or perform a separately reviewed hash-preserving promotion; never edit `collector-state.json` to force a transition.

## Archive Job Enters Needs-Match

Review the exact warp, downloader/report provenance, raw/custom dimension evidence, footprint overlap, name candidates, and coordinate disagreement in Admin. One WDL owns at most one Archive warp; a location may own several dated warps. Attach to an existing location only when the evidence supports the same place, or choose create-new when the warp is confidently a new location. Do not approve an arbitrary museum dimension or allow one warp to resolve to multiple locations.

## Worker Cannot Claim Or Update A Job

`401` on `/api/ingestion-jobs/claim` means the raw worker key does not hash to `IngestionWorker__ApiKeySha256`. `401` on status can also mean a bad claim token. `409` means lease/state/concurrent completion conflict. Claims last 15 minutes; progress updates renew them. After three expired attempts the job fails at `lease`.

Rotate a compromised raw key, update only its hash on the API host, restart both boundaries, and never place the raw key in browser configuration.

## Render Or Adaptation Fails

Preserve the entire job directory, bounded logs, profile, executable hash, and receipts. Do not delete `.atlas-render-provenance-*` or edit state. A resume is valid only when the plan, world root, dimension, renderer version/hash, and arguments still match.

Unknown codec 127, unsupported dimension profile, stale provenance, unexpected visible tiles, missing chunk-derived tiles, and output grammar errors intentionally fail closed.

## Published Tiles Are Missing Or Misaligned

Probe the exact public URL and verify image content type, cache headers, and signed path handling. Compare local published inventory to the publication receipt. Check at least three landmarks including negative coordinates and adjacent render overlap.

Overworld, Nether, and End use different sparse zoom/origin transforms. A valid-looking pyramid can be geographically wrong. Unlink bad render metadata first; preserve the immutable tree for diagnosis and publish a new immutable generation rather than overwriting cached content.

## BlueMap 3D Is Missing, Stale, Or Visually Wrong

First confirm the 2D render and source WDL are healthy; BlueMap is downstream
and must not be repaired by replaying collection or changing ingestion rows.
Run `scripts\get-atlas-bluemap-status.ps1`, compare live eligible/validated and
post-snapshot totals, and inspect the **BlueMap 3D Generation** Admin panel. A
`running` checkpoint without fresh checkpoint or renderer-log activity is
reported as stale. Confirm that one renderer owns the lock before starting a
foreground canary; never run concurrent full batches.

If `blueMapUrl` is null, inspect the generation manifest, minimum profile,
`web/index.html`, `QualityGate.Passed`, `QualityGate.LocationStartExact`, and
`RenderingProfile.Relight.FootprintAuditExact`. A known directory is not enough:
partial, failed, superseded, and below-minimum generations are intentionally
404. If the viewer is patchy, washed out, darkens with zoom, starts in empty
terrain, or loses chunks, reject that immutable generation and reproduce with
an explicit render-ID canary. Never edit a manifest to pass. Preserve the
source ZIP, compare source/final Anvil inventories, review the Paper relight
log and BlueMap settings, then publish a new profile generation. Use the
localhost preview at several angles/zooms and verify Overworld, Nether, and End
separately. See [`../BLUEMAP_PIPELINE.md`](../BLUEMAP_PIPELINE.md).

## Map Freezes At World Overview

Inspect `atlas-map.js` behavior before changing Leaflet defaults. 2b2t.place output tiles cap source fan-out at 16 and skip over-broad composites. Location rendering switches to shared Canvas below zoom 1. Keep overview markers at z-index 340 and highways at 350; reversing them masks highway interaction.

Do not re-enable global `preferCanvas` without interaction tests. Verify pan, location hover/click, highway hover/click, and transitions across zoom 1.

## Legacy Client Breaks

Confirm it calls `/api/locations.php`, not modern `/api/locations`, and uses legacy dimension values `0`, `1`, `-1`. Check `X-Atlas-Compatibility: legacy-read-v1`. Anonymous `/api/newWarp.php` is deliberately retired and cannot be fixed by CORS; migrate writes to authenticated modern APIs.

## BlackBrain Is Offline Or Held

**Optional behavior:** Atlas should continue serving and accepting authorized writes. Disable enrichment schedules/calls and retain cached source-attributed artifacts. Do not route Atlas through BlackBrain's generic tool endpoint or weaken Atlas auth to restore suggestions.
