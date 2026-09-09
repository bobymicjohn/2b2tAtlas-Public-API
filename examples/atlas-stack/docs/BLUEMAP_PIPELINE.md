# BlueMap 3D derivative pipeline

This is the canonical implementation and operations guide for 2b2tAtlas's
location-level BlueMap output. The capacity study and the history behind the
current profile remain in
[`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md).

## Contract and ownership

### Embedded camera controls

The embedded viewer uses BlueMap's built-in camera controls. Atlas retains the
loaded-render tabs and 2D/3D toggle, but no longer duplicates Orbit/Fly buttons,
mode state, help overlays, or acknowledgement errors in its own toolbar. It also
does not force a camera mode when the iframe loads. The iframe stays mounted
when switching camera modes, preserving its current X/Z position. Fly enters at
BlueMap's terrain height + 3, then uses its unconstrained first-person controller:
WASD to move, Space/Shift to change height, and click inside for mouse-look.
Flight can pass through roofs/walls; it does not create interiors that were
omitted from the rendered mesh (including cave-removal settings).

The API injects `ClientAssets/atlas-controls-bridge-v1.js` only into quality-gated
viewer index requests with `?atlas-controls=1`. That shell is `no-store`; ordinary
BlueMap output and mesh caching remain immutable. No rerender or worker restart
is necessary. The compatibility bridge remains available for older frontends;
the current location page does not send navigation commands. Older buttons wait
for a correlated acknowledgement of the actual controller, not just a hash or
label change; failed/loading switches show a retryable message. Confirmation
checks native control state and camera distance instead of controller object
identity (which reactive wrappers can change). Focus denial is non-fatal.

BlueMap also requires `web/lang/settings.conf` and `web/lang/<locale>.conf`.
The API's `BlueMapContentTypeProvider` serves only these public language `.conf`
paths as UTF-8 plain text; other configuration and unknown extensions remain
blocked. Do not enable `ServeUnknownFileTypes`. The default ASP.NET provider
otherwise returns 404 for these existing files, breaking translation setup.
BlueMap can then switch the camera but throw in `updatePageAddress()` while
translating the page title, before updating its control-state acknowledgement.
In older Atlas clients this produced a working camera with a stale Orbit button
and a false failure; language loading is still required by the native controls.
After deploying the API fix, reload the page to reinitialize the viewer; no
Namecheap upload, rerender, or worker restart is needed.

Deployment checks must fetch language settings and at least `en.conf` through
the public API, not merely verify the viewer HTML and meshes. Expect HTTP 200,
`text/plain; charset=utf-8`, and real language content. Private configuration and
nonexistent locales must still return 404. `BlueMapContentTypeProviderTests`
exercises this contract through real ASP.NET static-file HTTP responses.

Run `node scripts/test-bluemap-controls.mjs <installed-index.js.map>` to exercise
the bridge against the installed BlueMap source-map's native controller setters
and page-title update. The regression reproduces the missing-locale failure,
then checks successful mode confirmation with initialized translations.
Also verify in a browser: enter Fly, descend through a roof, move inside, return
to Orbit, and switch between renders. Keyboard input belongs to the iframe;
pointer-lock requires a deliberate click. Protocol tests are not visual QA.

BlueMap is an optional, automatically discovered derivative of a completed
Atlas render. It is deliberately downstream from collection, matching, source
archival, uNmINeD rendering, sparse-tile adaptation, and database completion.

```text
Archive/browser WDL
        |
        v
normal Atlas ingestion --> immutable source ZIP on X:
        |                         |
        |                         v
        +--> verified 2D render   BlueMap polling consumer
                  |                    |
                  v                    v
             Render row       relight + exact audit + BlueMap
                                       |
                                       v
                             immutable profile generation on F:
                                       |
                                       v
                         quality-gated API fields and 3D UI
```

The normal ingestion job is complete whether or not its BlueMap derivative has
been generated. A BlueMap failure must never roll back a render, detach a warp,
block a WDL download, or stop Archive collection. BlueMap does not make location
matching decisions and never edits the preserved source ZIP.

## Eligibility and automatic discovery

The renderer selects distinct surviving `Render` rows whose newest related
`IngestionJob` is `completed` and has a non-null immutable `ArchiveSha256`.
Joining through `Renders` and `Locations` excludes deleted/orphaned provenance.
Selection is by render ID, not by location or warp, so separate Overworld,
Nether, and End renders all receive independent derivatives even when they came
from one multi-dimension source WDL.

`scripts/start-atlas-bluemap-coordinator.ps1` continuously refreshes the eligible
set every minute and assigns at most **two** single-render child processes.
Every fourth assignment selects the newest completed ingestion; the other
three select oldest render IDs. This gives new arrivals timely service without
starving the backfill. Failed renders enter a persisted 30-minute retry cooldown.
Already validated content-addressed generations are skipped. No additional
database queue row is required and normal ingestion does not call Docker.

The existing five-minute Atlas watchdog restarts a missing coordinator and
cleans up only positively identified orphan BlueMap children/containers and
unmounted per-job web volumes with dead/unrelated owners. It never prunes shared
caches or active jobs. A live worker with old activity is flagged in Admin, not
killed merely for a large job.
The legacy standalone `-All` or `-RenderId` commands use the same global lock
and cannot overlap the coordinator.

Each worker has its own mutable Paper runtime cache, BlueMap data/cache volume,
checkpoint, per-job logs, and exclusive slot lock. Per-render file claims prevent
duplicate work. Source ZIPs remain immutable. Output-space reservations under a
shared publication lock include both workers; final quota/free-space accounting
and promotion are serialized. Dead-owner reservations are reclaimed using PID
and process-start identity. Lock files are persistent: ownership is the open
exclusive handle, not whether a file exists.

## Generation stages

For each selected render, the profile-7 pipeline:

1. Resolves the latest completed ingestion provenance for the render.
2. Locates the content-addressed ZIP beneath the protected WDL object store and
   verifies its SHA-256 before extraction.
3. Extracts into a unique disposable directory on `D:` and requires exactly one
   safe Minecraft world root.
4. Selects the render's recorded dimension and preserves any `RenderTopY`
   cutaway. Nether output additionally removes the roof from Y 90 through 127.
5. Inventories the original Anvil block-chunk set.
6. Runs an isolated, version-keyed Paper derivative with pinned Light Cleaner
   and BKCommonLib components. Temporary neighbor chunks are permitted only to
   rebuild valid lighting at sparse edges.
7. Removes every generated neighbor record, audits the final Anvil inventory
   against the source inventory, and fails unless the sets are exactly equal.
   Malformed entity/POI sidecars may be omitted from this disposable block-model
   derivative; the downloadable ZIP remains unchanged.
8. Runs the pinned `ghcr.io/bluemap-minecraft/bluemap:v5.23` image with bounded
   CPU and memory. Active model data and the worker-local resource cache use Docker
   volumes; only the finished static webroot is exported to Windows storage.
9. Validates lighting, LOD settings, low-resolution tiles, 3D model payload,
   stock client assets, exact relight footprint, and a camera start anchored to
   the Atlas location's canonical X/Z.
10. Writes `manifest.json` and atomically promotes the immutable generation to
    `F:\AtlasExample\AtlasBlueMap\location-renders`.

The current display profile uses Overworld sky light `1` and ambient light
`0.1`; Nether and End use sky light `0` and ambient light `0.6`. It produces
500-block low-resolution tiles with LOD factor 5 and three LODs. Missing sparse
neighbors are allowed only after the retained source chunks have passed the
relight and exact-footprint audit.

## Immutable generation and quality gate

Generation directories are content-addressed by render ID, source SHA-256,
BlueMap version, and Atlas renderer-profile version:

```text
render-<render-id>-<source-sha256>-v5.23-p7/
  manifest.json
  web/
    index.html
    maps/atlas/settings.json
    ...BlueMap static assets and model data...
```

The API's `BlueMapCatalogService` scans only direct-child manifests and caches
the result. It advertises the newest generation per render only when all of the
following are true:

- `Status` is `complete`;
- the directory is not marked superseded;
- `web/index.html` exists;
- `RendererProfileVersion` meets `BlueMap:MinimumProfileVersion` (currently 7);
- `QualityGate.Passed` is true;
- `QualityGate.LocationStartExact` is true;
- `RenderingProfile.Relight.FootprintAuditExact` is true.

Partial, failed, superseded, and older experimental generations may remain for
diagnosis but are neither linked by API records nor served by the static-file
authorization middleware. This lets an unsafe profile be withdrawn by raising
the minimum version without deleting evidence.

## Public API and static delivery

The anonymous render APIs are the discovery surface:

```http
GET /api/renders
GET /api/renders/{id}
GET /api/locations/{id}
GET /api/locations/{id}/renders
```

When a validated derivative exists, the render record includes:

| Field | Meaning |
| --- | --- |
| `blueMapUrl` | Absolute HTTPS URL for the interactive static viewer |
| `blueMapPath` | Same-origin-relative viewer path, beginning with `/bluemap/` |
| `blueMapProfileVersion` | Atlas derivative profile that passed the public gate |

All three fields are nullable and additive. Their absence means no derivative
is currently advertised; it does not imply that the 2D render or source WDL is
missing. Clients should follow `blueMapUrl`, not construct a content-addressed
path. The static viewer and its assets are served by the Atlas API beneath
`/bluemap/` with immutable cache headers. Every requested generation directory
is rechecked against the current quality-gated catalog before file middleware
can serve it.

The Model Context Protocol `get_render_metadata` and location research results
project the same public render metadata. MCP returns links, never the BlueMap
asset bytes in model context.

## Location-page behavior

The location-detail map keeps the 2D Atlas as the default view. The bottom-left
`3D` control considers the selected render IDs and active dimension:

- it is disabled, with an explanatory tooltip, when none has an advertised
  BlueMap URL;
- it remains visible so visitors understand that 3D is a generated capability;
- entering 3D snapshots only selected render layers intersecting the current
  2D viewport;
- multiple historical derivatives appear as independent labeled tabs;
- returning to 2D preserves the Leaflet viewport and selected render state.

An unavailable derivative must not be replaced by another date or dimension
silently. The URL belongs to one exact Atlas render/source observation.

## Administrator monitoring

Administrators with `renders.manage` can read:

```http
GET /api/admin/bluemap
Authorization: Bearer <admin JWT>
```

The collapsible **BlueMap 3D Generation** panel under Admin > Location
Management polls this endpoint every 15 seconds. It reports:

- active/idle/stale/complete state and latest renderer activity;
- each worker's render, location, dimension, stage, elapsed time, and activity;
- refreshed discovered-catalog completed/total/percent (legacy pass totals remain supported);
- live eligible, validated, remaining, and post-snapshot counts;
- validated Overworld, Nether, and End totals;
- current-pass failures and retained diagnostic generations;
- manifest-reported output bytes, configured quota, and output-volume free
  bytes;
- the minimum publicly accepted profile.

The schema-2 checkpoint reports `coordinated: true` and `workers` (maximum two).
Its discovery totals refresh every minute; live eligible and validated totals
may briefly differ during ingestion. The response is sanitized and never exposes host paths, source
hashes, PIDs, command lines, account data, or log contents.

`Active` requires fresh checkpoint or configured renderer-log activity. A
nominally running checkpoint older than `StatusStaleAfterMinutes` is reported as
`Stale` instead of falsely healthy.

## Configuration and paths

API configuration:

| Setting | Production value/purpose |
| --- | --- |
| `BlueMap:OutputRoot` | `F:\AtlasExample\AtlasBlueMap\location-renders` |
| `BlueMap:RequestPath` | `/bluemap` |
| `BlueMap:PublicOrigin` | Public Atlas API origin |
| `BlueMap:CatalogCacheSeconds` | Manifest-catalog refresh interval |
| `BlueMap:MinimumProfileVersion` | Oldest generation safe to advertise |
| `BlueMap:StatusPath` | Atomic batch checkpoint used by Admin monitoring |
| `BlueMap:ActivityLogPaths` | Bounded list of log files used only for freshness |
| `BlueMap:StatusStaleAfterMinutes` | Running-state freshness threshold |

Operational storage:

| Path | Role |
| --- | --- |
| `E:\AtlasExample\WorldDownloads\objects` | Immutable source WDLs |
| `D:\AtlasExample\Ingest\bluemap-work` | Disposable extraction/export work |
| `D:\AtlasExample\Ingest\bluemap-tools\server-cache-worker-{1,2,3}` | Worker-isolated, version-keyed mutable Paper/Mojang caches |
| Docker `atlas-bluemap-cache-v5-23-worker-{1,2,3}` | Worker-isolated BlueMap data/resource caches |
| `C:\AtlasExample\Ingest\bluemap` | Coordinator lock/checkpoint, claims, reservations, cooldowns |
| `C:\AtlasExample\Ingest\bluemap\workers\{1,2,3}` | Worker checkpoint and per-job stdout/stderr |
| `C:\AtlasExample\Ingest\bluemap\stage-leases` | Private PID/start-time-pinned memory reservations |
| `F:\AtlasExample\AtlasBlueMap\location-renders` | Immutable public generations |

Defaults enforce an eight-CPU BlueMap stage, eight-CPU relight stage, 8/12 GiB
memory limits **per worker**, shared 500 GiB output quota, and 256 GiB free-space floor. At most
three worker slots can run stages, with a combined 32 GiB admission budget
(up to 24 CPU). Two relighters plus one renderer fit; three relighters do not.
`atlas-bluemap-resources.ps1` also reserves 4 GiB in Docker above observed other
container usage and requires requested stage memory plus 4 GiB available on the
Windows host. Resource inspection failure is fail-closed. Memory pressure queues
new stages; it never kills running work. Admin shows `waiting for ... resources`.
The global exclusive admission lock prevents simultaneous over-allocation.
Dead-owner reservations remain counted while an owned container survives, until
the watchdog cleans it up. Host Docker/WSL remains at its existing 48 GiB cap;
no Docker restart is necessary. Treat limit changes as reviewed capacity changes.

## Operator commands

```powershell
# Explicit canary or repair; validated matching generations are skipped.
.\scripts\invoke-atlas-bluemap-render.ps1 -RenderId 785,824,927

# Start/resume continuous three-worker discovery in a hidden process.
.\scripts\start-atlas-bluemap-full-batch.ps1

# Run the same coordinator in the foreground for diagnosis.
.\scripts\start-atlas-bluemap-full-batch.ps1 -Foreground

# Read-only selection check: never launches jobs or writes state.
.\scripts\start-atlas-bluemap-coordinator.ps1 -PlanOnly

# Graceful maintenance: stop dispatch, finish assigned jobs, then exit.
New-Item -ItemType File -Path C:\AtlasExample\Ingest\bluemap\pause-coordinator
# Resume: remove ONLY this marker; watchdog resumes within five minutes.
Remove-Item -LiteralPath C:\AtlasExample\Ingest\bluemap\pause-coordinator

# Summarize checkpoint, validated catalog, quota, and storage.
.\scripts\get-atlas-bluemap-status.ps1

# Serve only validated generations on localhost for visual review.
.\scripts\start-atlas-bluemap-preview.ps1
# Browse http://127.0.0.1:8770/

# Repair only canonical camera metadata for eligible profile-7 generations.
.\scripts\repair-atlas-bluemap-start-positions.ps1
```

Never start independent overlapping batches. Use one coordinator with three
managed workers. The exclusive lock is authoritative; do not remove it
while an owning process exists. Drain before standalone repairs. Do not use `-Force` across the catalog
without a specific profile migration and enough temporary/output capacity.

## Failure and recovery

- Preserve the failed result, per-render log, scratch evidence when needed, and
  any immutable manifest. Do not edit a manifest to pass a gate.
- A terminal per-render failure is recorded in `retry-cooldowns.json`; other
  eligible renders continue. The coordinator retries after 30 minutes.
  Retry comparisons explicitly use UTC (PowerShell 5.1 otherwise converts ISO
  timestamps to local time). Fresh caches use idempotent volume creation;
  an already auto-removed Docker container is a successful cleanup, not a failed
  render. Other cleanup errors are warnings and do not overwrite job results.
- If a process dies, confirm the owner PID is gone before removing its lock or
  container. The watchdog only reaps Atlas BlueMap containers whose encoded
  owner is absent or unrelated.
- A quota/free-space stop is an operational pause, not an ingestion failure.
  Add capacity or deliberately retire superseded diagnostics before resuming.
- If a public generation is bad, raise the minimum profile or make it fail the
  catalog gate first. Preserve the immutable directory for diagnosis and create
  a new content-addressed generation; never overwrite cached assets in place.
- A failed API catalog read removes 3D links but leaves 2D and WDL resources
  intact. Check the output-root ACL, manifest JSON, and `web/index.html`.

## Deployment and validation

BlueMap code changes require the normal Release build/test suite. Renderer or
profile changes additionally require real Overworld, Nether, and End canaries,
including at least one older save, one sparse edge, one large build, and any
vertical-cutaway profile affected by the change.

Before enabling automatic passes on a new host:

1. Verify immutable source/archive, scratch, output, and tool-cache boundaries.
2. Pin and verify every renderer, Paper, and plugin artifact.
3. Run explicit cross-dimension canaries and inspect several camera angles and
   zoom levels through the localhost preview.
4. Confirm source and post-relight block-chunk inventories are exactly equal.
5. Probe the public render JSON, `blueMapUrl`, nested static assets, immutable
   caching, and a rejected/superseded generation returning 404.
6. Authenticate as a `renders.manage` operator and verify the Admin panel's
   eligible/validated/pass counts against the CLI status summary.
7. Only then let the five-minute watchdog manage the three-worker coordinator.

Coordination-only regression checks (not benchmarks):
`powershell -File scripts/test-atlas-bluemap-coordination.ps1`, the control
protocol test above, and `dotnet test 2b2tAtlas.Ingestor.Tests`. Confirm three
distinct live jobs, independent caches/status, successful publication, and
continued queue advancement after rollout. The worker-count change does not
alter profile-7 geometry, lighting, dimension handling, or exact-footprint gates.

BlueMap output is reproducible derived data. Preserve source ZIPs, provenance,
tool hashes, profile code, and manifests independently; do not treat the 3D
webroot as a substitute for the historical WDL.
