# Administrator guide

## Access model

Admin capabilities are permission-based. The local owner is account 1, `atlas-owner`.
Other roles receive permissions through Admin, with destructive operations still
restricted to the owner. Sessions reload current account and permission state on each
authenticated request; deactivation and password changes invalidate existing sessions.

## Admin tool status

| Area | Status | Purpose |
| --- | --- | --- |
| User Management | Operational | Create, edit, activate, deactivate, and delete users within role constraints |
| Location Management | Operational | Collector telemetry, manual location CRUD, warps, attachment links, WDL ingestion, render visibility |
| World Download Jobs | Operational | Queue progress, automatic existing/new decisions, warp evidence, manual exceptions, retries and failures |
| Archive collector | example host operational | Headless live `/warps` catalog, private WDL capture quarantine, explicit ready promotion |
| BlueMap 3D generation | Operational downstream service | Quality-gated derivative progress, dimension coverage, failures, storage, and stale-state monitoring |
| Moderation | Operational | Approve or reject pending highways and entity revisions |
| Audit Log | Operational | Review administrative and worker mutations |
| Groups | Operational | Maintain sourced histories, logos, classifications, bases, and highway attribution |
| Highways | Operational | View all statuses, edit metadata, moderate, and delete with permission |
| Roles & Permissions | Operational | Customize role permission sets and reset defaults |
| Operations | Operational status + guidance | Documents deployment contracts and performs an on-demand local-worker reachability check from the current browser |
| Binary attachment upload | Not available | Intentionally absent; publish reviewed files through the controlled media origin and store their HTTPS URLs |
| Waypoint bulk import | Not available | Planned only after duplicate/conflict review semantics exist |
| Render-only location import | Not available | WDL ingestion is the authoritative render path |

## Locations

### Manual creation

Use Location Management > Add Location > Manually for researched locations with known coordinates. Verify dimension and coordinates before saving. Prefer neutral historical descriptions; do not publish active private bases or stashes without an explicit disclosure policy.

### WDL creation

Use Location Management > Add Location > From World Download. Submission follows the deployment's owner/upload policy and requires a verified recovery checkpoint. The worker claims queued jobs independently of the browser.

For Archive/museum exports, historical storage eras, and dimension-attribution limits, follow `WDL_FORMATS_AND_PROVENANCE.md`. A custom museum dimension cannot prove the build's original 2b2t dimension and must not be guessed.

1. Drop one ZIP archive (or use Bulk Upload for many).
2. Set render name, date, source, and slug. Normally leave **Archive /warp** blank; the report or an Archive-attributed filename supplies it. An exact operator-entered value overrides those hints.
3. Normally leave location blank. Atlas evaluates the one Archive warp, historical warp identities, render
   overlap, names, provenance, and coordinates. Selecting a location is an explicit operator override.
4. Submit. The browser uploads the ZIP to the Atlas API, which stores it on the dedicated intake drive and queues the job.
5. Follow the job in World Download Jobs. Confident matches and confident-new Archive WDLs continue unattended;
   `needs-match` means identity, dimension, or warp ownership is genuinely ambiguous and requires Resolve.
6. Inspect exact bounds, center, source attribution, and public tile availability after completion.

Coordinates, bounds, and scale come from chunk storage and NBT. Do not enter or override them manually.

### Automated Archive batches

The example host collector is an alternate intake source, not an alternate matcher or publisher. It discovers exact live Archive warps, captures validated WDL ZIPs, and passes completed final-standard captures through the rolling handoff into the normal ingestion queue. Jobs appear in **World Download Jobs** and follow the same deterministic existing-location/confident-new/`needs-match` rules as browser uploads.

Operators with `renders.manage` can expand **Archive Collector** at the top of Location Management to see the current crawl from any browser. It shows queue completion, saved/unavailable/retryable counts, all six workers' current warp and live chunk/waypoint progress, the four-long/two-fast lane assignment, durable fast-to-long deferral count, production-handoff stage, and next scheduled check. The panel refreshes every 15 seconds. The browser receives a sanitized API snapshot; local paths, process IDs, command lines, logs, and account tokens are never returned.

For the first batch, select a mix of: an exact existing warp; a different dated warp for an existing location; an apparent Archive-only location; an `@Nether` or `@End` identity when available; and one awkward name containing punctuation. Verify each location, warp ownership, dimension, render footprint, day/night output, and audit row before increasing batch size. Collection success alone is not publication approval. See `ARCHIVE_SYNC_AUTOMATION.md`.

### BlueMap 3D generation

BlueMap is a separate downstream consumer of completed source-backed renders.
Its coordinator polls durable Atlas records every minute and assigns three isolated
workers. Each relights a disposable copy, audits the exact source chunk
footprint, and publishes an immutable static viewer only after the current
quality gate passes. It never blocks WDL completion, collection, matching, 2D
publication, or public WDL downloads.

Operators with `renders.manage` can expand **BlueMap 3D Generation** in Location
Management. The panel polls every 15 seconds and shows each worker's stage,
elapsed time, activity, and running/idle/stale state alongside discovered-catalog
progress and the live eligible and validated totals. It shows current render,
location, dimension, Overworld/Nether/End coverage, post-snapshot work,
failures, diagnostic generations, output quota/free space, latest activity, and
the minimum accepted profile. A checkpoint that claims to be running without
fresh activity is reported as stale. The sanitized response contains no host
paths, source hashes, PIDs, command lines, or log contents.

There is no manual approval button in this panel. A missing derivative is
retried after a 30-minute cooldown; the watchdog restores a missing coordinator.
A bad public derivative is withdrawn through
the quality/minimum-profile gate and replaced by a new immutable generation.
On Location Detail, the 3D control remains disabled with a tooltip until at
least one selected render in the active dimension has a validated
`blueMapUrl`. See [`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md).

### Wiki metadata review

The deterministic wiki matcher can prepare exact-title suggestions and source-derived intro descriptions, but it never writes production data. Review its JSON/Markdown report, verify page identity and disclosure safety, then apply approved fields through normal authenticated location edits so authorization and audit history are preserved. See `WIKI_ENRICHMENT.md`.

### AI enrichment and group evidence

The AI Enrichment tab combines coordinate-aware location/wiki matching, model-drafted descriptions, and a
revision-pinned group/build evidence index. Refresh that index with
`scripts\invoke-atlas-group-evidence-refresh.ps1` after large Archive import batches. Exact infobox base
declarations may be auto-applied but remain reviewable; group-title identity and base-section evidence always
queue for review. **Discover new groups** creates Pending proposals only and atomically adds an approved group
with its explicitly sourced build links. See `GROUP_ENRICHMENT_RUNBOOK.md` for the full evidence policy and
repeatable workflow.

A complete location pass is a server-owned background run. The start request returns immediately, and the
run continues when the initiating browser refreshes, navigates away, disconnects, or crosses an upstream HTTP
timeout. Every admin browser polls the same status, including eligible/scanned counts, percent complete,
current location, matches, applied changes, review-queued suggestions, skips, per-location errors, elapsed
time, and the last terminal result. The API accepts only one catalog pass at a time and returns the existing
run on a duplicate start attempt; group discovery is also unavailable while that pass is active. An API
process restart cancels the in-memory run and requires an operator to start a fresh pass.

### Attachments

Attachments are reviewed HTTPS media records; Atlas does not accept binary attachment uploads through the public application. For durable historical media, download the original into the operator-controlled media archive, generate a web-sized thumbnail, and record both the original publication URL and attribution. Each record can distinguish image, video, wiki, or generic link media and can carry a caption, thumbnail, source URL, and credit. Give links human-readable names and avoid sources that disclose sensitive active locations. The API rejects HTTP, FTP, javascript, data, file, embedded credentials, malformed URLs, duplicates, and lists over 50 items before replacing any stored links.

`scripts/sync-historical-location-media.ps1` is the reviewed seed workflow for the initial historical collection. Its manifest is `2b2tAtlas.Server/Data/historical-location-media.json`; add only media whose location identity, source, reuse terms, and attribution have been checked. The startup seeder adds missing canonical URLs but never replaces an operator-edited attachment.

The 2b2t Wiki's administrator, Joey_Coconut, is an Atlas team member and directly authorized the Atlas to reuse material from the wiki on September 4, 2026. This project-specific permission removes a licensing blocker; it does not remove the relevance and provenance checks. Every copied file must retain its wiki file-page URL, a useful caption, and creator/credit metadata when the file page provides it. Do not publish logos, interface graphics, or merely adjacent article images as location media.

`scripts/sync-reviewed-wiki-location-media.py` turns the review inventory into a bounded, self-hosted collection. Its default policy accepts only high-confidence filename matches, counts existing Atlas images, and caps each location at two images total. It writes the generated seed catalog to `2b2tAtlas.Server/Data/wiki-location-media.json`; that catalog is loaded alongside the hand-reviewed historical manifest. Run without `--apply` to inspect the plan, then use `--apply` only after reviewing the selected source file pages.

The production web derivatives are a deliberately small serving cache at `E:\2b2t\MiscRenders\LocationAttachments`; do not treat that copy as the archive. `scripts/archive-wiki-location-media.py` downloads every original discovered from Atlas-linked wiki pages through `D:\AtlasExample\Ingest\media-staging\2b2t-wiki`, hashes it, verifies the durable copy, and removes staging. The preservation archive is `E:\AtlasExample\HistoricalMedia\2b2t-wiki`, with content-addressed objects, source/relationship JSONL, an inventory hash, and reuse authorization. Archive runs merge atomically with the existing object, relationship, and source-inventory manifests, so a targeted review run cannot discard the rest of the preservation catalog. Wiki file-page identities are canonicalized across the Miraheze-to-Wikioasis migration, and a higher human-review priority is retained when the same relationship appears in a later bulk inventory. After any archive run, verify that `archive.json` counts match `objects.jsonl`, `relationships.jsonl`, `source-inventory.json`, and the content-addressed files, that `failures` is empty, and that D staging contains no `.part` files. `E:\AtlasExample\HistoricalMedia\public` is a second copy of the curated web set. Docker cannot bind the mapped NAS drive reliably, so NGINX continues to serve the small E cache; originals and preservation metadata belong on X.

`scripts/sync-historical-location-media.ps1` also performs an incremental, non-destructive preservation copy after generating curated thumbnails: the complete E public tree is copied to `E:\AtlasExample\HistoricalMedia\public`, while full-resolution operator-owned +Z border sources are copied separately to `E:\AtlasExample\HistoricalMedia\operator\+Z Border`. A successful run reports both archive destinations. Verify matching file counts and byte totals before treating a newly published historical-media batch as complete.

Generated wiki manifests carry both `locationRowid` and `locationName`. The startup seeder uses the stable ID and verifies the name before inserting; legacy name-only rows must resolve to exactly one location. This prevents same-name locations from silently receiving one another's media. The reviewed wiki sync also merges with the existing generated manifest and canonicalizes legacy wiki file-page hosts; confirm its final `preserved + published = manifestTotal` report before packaging or deployment.

### Media and history research queues

`scripts/audit-wiki-location-media.py` inventories images only from wiki pages
already linked to Atlas locations. It records the exact wiki revision, original
file page, dimensions, artist/credit fields, and declared reuse terms in
`C:\AtlasExample\Research\2b2t-wiki-media`. It does not download or publish files.
Run its built-in guardrail checks with `--self-test` before changing its matching
or prioritization rules.

`scripts/audit-wiki-location-links.py` compares locations without a wiki field to
the complete main-namespace article index. It accepts exact, punctuation-only,
and explicit Roman/Arabic iteration equivalence; it deliberately has no fuzzy
matching mode. Each candidate retains the latest MediaWiki revision ID, timestamp,
and immutable `oldid` URL; a revision-query API error fails the audit instead of
emitting unpinned evidence. Review its candidates first, then rerun the media audit
after approved wiki links have reached the live API.

`scripts/extract-youtube-location-candidates.py` compares normalized, timestamped
research captions with the live location catalog. Its JSON and Markdown results
under `C:\AtlasExample\Research\2b2t-youtube` are also review-only. Exact titles and
repeated claim context receive higher priority; duplicate and generic names are
suppressed.

For an accepted media candidate:

1. Confirm that the image or video actually depicts the selected location.
2. Retain the original wiki file-page URL and a useful public caption.
3. Record creator/credit metadata when available and the direct reuse authorization.
4. Self-host approved images and thumbnails under the configured attachment
   storage root rather than hotlinking the wiki or social host.
5. Add the reviewed record to `historical-location-media.json` (or use the admin
   attachment editor), then verify the location page and public API response.

The admin attachment editor exposes media type, public file/thumbnail URL,
original source URL, caption, and attribution. The original source is evidence;
the public file URL is the durable Atlas-hosted resource.

## Highways

Cartographers and Highway Architects can draw or update geometry directly on the map. Chroniclers cannot edit highways. Cartographers and Archivists review pending highways and revisions.

Before approval, check:

- dimension and coordinate units;
- path continuity and direction;
- category, width, height, Y level, paving material, walls, roof, lighting, and condition;
- builder-group attribution and source links;
- whether the record duplicates an axis, diagonal, ring, or existing spur.

Use rejected status for invalid submissions rather than deleting evidence needed for moderation history.

## Groups

Group records are public attribution metadata used by both locations and highways. The public group directory shows logos, classification, status, era, and attribution counts; each group detail page links back to its reviewed bases/builds and highways.

- Use the commonly recognized display name; mention important historical aliases in the prose rather than creating duplicate records.
- Classify by the group's primary documented function: Build, Highway, Mixed, or Other. Do not force PvP factions or loose coalitions into Build merely because members also built bases.
- Store the concise sourced history in Description. Store the emblem in Logo URL and its credit/page in Logo source URL; never use an unrelated search result as a placeholder.
- Location Management > Edit > Built By is a multi-select. Link only a documented builder/maintainer relationship, not a raid, grief, residency, alliance, or nearby footprint.
- Exact canonical associations may be seeded automatically. Archive warp suffixes such as `@Spawnmason`, `@Spawnmasons`, and `@SpawnMason_Lodge` are also accepted as explicit collection provenance for SpawnMason build attribution. Ambiguous attribution is deliberately manual and must not be inferred from similar names or coordinates.
- Editing Built By must not alter the location's Archive warps, render-to-warp links, or coordinates. If those change, stop and treat it as a regression.

The Admin layout collapses cards, controls, tabs, and dialogs for phone-width use. Wide data grids remain horizontally scrollable so operational columns are not silently removed.

## Users and roles

| Display name | Stored role | Default scope |
| --- | --- | --- |
| Founder | `SuperAdmin` | Every permission, system settings, and role-profile definitions |
| Archivist | `Admin` | Content administration, moderation, renders/WDLs, groups, audit, and lower-role assignment |
| Cartographer | `Cartographer` | Locations, attachments, highways, renders, groups, and mapping moderation; no deletes or users |
| Highway Architect | `HighwayArchitect` | Create and edit highways only |
| Chronicler | `Chronicler` | Create historical locations only |
| Member | `User` | Public/read-only access |

- Give the lowest role that supports the user's work.
- Archivists may assign lower roles but cannot create another Archivist or Founder.
- Only the Founder can modify role-profile permission bundles (`roles.manage`).
- Routine specialist roles never receive delete or user-management permissions.
- Review custom role profiles marked with `*` and use Audit Log after sensitive changes.

## Incident handling

- WDL failure: preserve job directory and logs; do not edit receipts.
- Bad render placement: unlink metadata, preserve immutable output for diagnosis, and rerun from the original archive.
- Compromised worker key: stop worker, rotate raw key, update only its SHA-256 on API host.
- Suspicious account: deactivate first, then review audit and authentication logs.
- Database issue: stop writes and make a copy before repair.
