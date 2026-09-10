# The Archive automated WDL sync

## Operational state

Fast workers 5 and 6 automatically refill in batches of up to 24 fresh jobs.
When fresh work is unavailable, they can temporarily run one long capture with
a 12-hour budget and return to 30-minute mode after its WDL completes and fresh
work is queued. Durable refill intents and safe dispatch boundaries preserve
exclusive queue ownership. See [the deployed policy and verification](COLLECTOR_RECOVERY.md).

Coverage companion 0.9.1 uses a warp-anchored survey grid, coverage-aware waypoint
selection, and incremental missing-chunk counters. It skips a window only when
every chunk in that window has already been observed; the five-second minimum
settle and final ZIP/coverage gates remain intact. Pending upgrades install only
after the current WDL and client session end, with SHA-256 validation and old-JAR
rollback. See [coverage implementation and regression tests](../tools/AtlasArchiveCoverage/README.md).

Fast-lane timeouts preserve a flushed, validated partial ZIP and coverage checkpoint
privately in `D:\AtlasExample\Ingest\DeferredCaptures`. The long worker restores terrain
evidence from saved NBT, plus known void cells and survey progress from new
checkpoints. It skips fully covered windows and merges the continuation by Anvil
chunk slot before the independent final footprint audit. Old partial ZIPs also
recover their terrain; old hints did not record void cells, so those cells need
another observation. Exact server, dated warp, dimension, landing, policy and
SHA-256 checks remain mandatory. Waiting more than 24 hours no longer discards
progress. Invalid checkpoints stop for review instead of silently starting over;
failed handoff writes do not transfer ownership. Partials remain private.
See [the resume implementation and measured test](COLLECTOR_RECOVERY.md).

The collector is authenticated and operational on example host without a visible Minecraft or browser window. Each headless client uses one real Microsoft/Minecraft account and an isolated install/game/auth root. The initial catalog pass runs under a six-worker supervisor with staggered joins; strict pacing, disjoint queue shards, per-worker locks, bounded restarts, and resumable JSON checkpoints remain mandatory.

Archive acquisition ends at normal Atlas ingestion. Completed source-backed
renders are later discovered by the independent BlueMap polling derivative;
collector workers neither enqueue nor wait for 3D work. BlueMap failure cannot
change capture, promotion, WDL, matching, or 2D completion state. See
[`BLUEMAP_PIPELINE.md`](BLUEMAP_PIPELINE.md).

The end-to-end path is:

1. `get-archive-warp-catalog.ps1` walks the live `/warps` inventory GUI, including nested pages, and clicks leaves to recover the exact canonical Archive warp. Results are checkpointed after every discovery.
2. `new-archive-collector-queue.ps1` merges live catalog entries with Atlas's warp inventory. Existing Atlas warps retain location UUID, dimension, and coordinates; Archive-only warps retain their display/category provenance for downstream matching or confident-new creation.
3. `invoke-archive-collector.ps1` teleports to one exact warp at a time, runs Archive World Downloader, waits for a complete save, validates the ZIP structure and report, verifies SHA-256 after copying, and writes only to the private D-drive `captured` staging quarantine by default. Only after the D artifact and JSON checkpoint are durable does it remove the matching transient ZIP/extracted save from that client's C-drive `game\saves`; a cleanup failure is warning-only and never invalidates a good capture. A hash-bound `.metadata.json` sidecar preserves the exact warp, catalog display/category path, timestamps, source, capture mode, and chunk count. `archive-collector-captures.ps1` later copies raw ZIP, non-void manifest, exact footprint, and sidecars to the X-drive NAS with independent destination hashes before state paths are committed to the archive tier.
4. A reviewed batch is explicitly promoted with `-MoveToReady`; collection alone cannot cross this boundary.
5. `import-archive-inbox.ps1` validates any capture sidecar against the ZIP SHA-256, passes its exact warp as operator-confirmed acquisition metadata, content-addresses ready ZIPs, copies them atomically into Atlas intake, and calls the worker-key-protected `POST /api/ingestion-jobs/local-intake` route.
6. The normal pipeline performs report extraction, dimension detection, deterministic location/warp matching, optional bounded local-model second opinion, rendering, tile publication, and atomic location/warp/render registration. Confident existing-location and confident-new-location decisions continue; genuine ambiguity becomes `needs-match`.

Published collector renders carry first-class provenance (`Source=archive-collector`) and a unique link to the exact Archive warp. The public render card exposes that warp as a copyable `/warp` command. Auto-created public names omit recognized catalog dates and vanilla-dimension suffixes; the date remains on the render/warp records. Collector attribution is never injected into a location or render description. A higher-quality recapture of an already-linked warp replaces that render's tile generation in place.

The Archive also retains a small `@concepts`/explicit `(concept)` corpus of single-player creative builds. Atlas preserves these useful design-history artifacts, but `ArchiveWarpResolver.IsSinglePlayerConcept` classifies the explicit token at ingestion. Render names and descriptions say `Singleplayer Concept`, download metadata and headers distinguish them from live-server snapshots, and public API/MCP/SEO records expose `isSinglePlayerConcept=true`. The exact `/warp` text remains unchanged for archival fidelity. Never infer concept status from coordinates, visual similarity, or words such as `Conceptual`; the explicit Archive token is required.

The 2026-08-24 acceptance crawl checkpoint contains 1,594 live-confirmed, normalized warps with zero duplicate identities, mojibake markers, known Constantiam/3b3t leaves, or Atlas ownership conflicts. Before the representative batch, the merged inventory contained 2,114 unique warps. Excluding five earlier captures left 2,109 eligible entries: 1,055 Archive-only, 538 present in both sources, and 516 Atlas-only. After the batch registered the reviewed results and added five `ready` captures to the exclusion, the current checkpoint queue excludes 10 identities and contains 2,104 eligible entries. Regenerate the merged queue before relying on its Archive-only/both/Atlas-only breakdown because the manually resolved Space Valkyria warp is now registered in Atlas. The final `Kinograd 2016` display leaf could not be live-click-confirmed after repeated Archive resets; its exact `Kinograd_2016` identity already exists in Atlas and remains explicitly Atlas-only rather than being guessed into the live catalog.

The first five supervised captures passed archive/hash/region/`level.dat`/downloader-completion validation. The representative ingestion batch then exercised exact-warp reuse, existing-location/new-warp matching, confident-new creation, Unicode, Nether, End, reconnect recovery, and manual-review fallback. Four jobs completed automatically; the Space Valkyria End rebuild stopped at `needs-match` with a 0.768 review score, was correctly matched by the owner to location 1204, and then completed after the resume/date defect below was repaired. One deliberately stale Atlas warp was classified `missing`. No capture enters `ready`, Atlas ingestion, or publication unless the explicit promotion switch is used.

Archive catalog dates are authoritative for collector imports. A museum capture touches `level.dat`, so its `LastPlayed` value records the capture session, not the historical WDL date. The importer therefore disables `LastPlayed` substitution for recognized Archive captures, and the API independently preserves the catalog date when downloader evidence identifies The Archive. The acceptance repair restored the Dust Falls Manor, Fusionia I, Hurtville, Wässrige Hölle Zwei, and Space Valkyria dates across jobs, renders, and warps. Worker resume still permits a non-Archive job to adopt only the inspected immutable snapshot's own valid `LastPlayed` date; arbitrary metadata drift remains rejected.

The importer is resumable by SHA-256, rate-limited, and single-instance. It sends request JSON as explicit UTF-8 bytes under Windows PowerShell 5.1 and preserves API response bodies in failure state. A failed item remains retryable. Accepted hashes are skipped, and the local-intake API treats an exact archive-hash/slug/intake retry as the same durable job, covering a lost HTTP response without creating a duplicate.

The scheduled task `2b2t Atlas Archive Weekly Sync` runs Sundays at 06:00 local time through `scripts/invoke-archive-weekly-sync.ps1`. It is inert until both the initial continuation reaches `ready-for-ingestion` and its production handoff reaches `complete` or `submitted-to-production`. A premature run records `deferred-initial-pass` and exits successfully without connecting to The Archive. Once enabled by that gate, each run refreshes the live catalog, selects only identities absent from the terminal initial/weekly states, performs adaptive capture, applies the strict high-confidence promoter, and submits accepted artifacts through the normal production importer. Distance from origin is not an eligibility boundary. Low-confidence, capped, incomplete, neighboring-warp, foreign-server, or otherwise ineligible captures remain quarantined. Task Scheduler suppresses overlaps, the wrapper owns a stale-aware lock, and it additionally refuses to start while another catalog/capture process exists. Persistent weekly state lives under `C:\AtlasExample\Ingest\archive-sync\weekly`; `weekly-status.json` is the monitoring contract.

Dry-run example:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\import-archive-inbox.ps1 -DryRun
```

## Commands and checkpoints

```powershell
# Resume the complete live catalog crawl. Existing entries are updated, not duplicated.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\get-archive-warp-catalog.ps1 `
  -MaxNewWarps 10000 -MaxPages 1000 -MaxDepth 12

# Repair a catalog written/read through a legacy Windows PowerShell code page.
# This creates a timestamped source backup and machine-readable audit first.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\repair-archive-catalog-utf8.ps1

# Resume only an explicitly reviewed root branch after a durable full-crawl failure.
# Root labels are preferred because inventory slot numbers can change by session.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\get-archive-warp-catalog.ps1 `
  -RootLabels 'established 2015' -MaxNewWarps 10000 -MaxPages 100

# Merge the live catalog with Atlas's current warp/location inventory.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-archive-collector-queue.ps1

# Private one-item capture canary. This cannot enter ingestion.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-archive-collector.ps1 `
  -MaxWarps 1 -CaptureSeconds 35

# Build/install the pinned pathfinder and Atlas coverage companion.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-archive-baritone.ps1 -Install
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-archive-coverage-mod.ps1 -Install

# Private exact-within-known-bounds capture. Bounds are blocks, max values exclusive.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-archive-collector.ps1 `
  -Warp @('reviewed_exact_warp') -CoverageBounds @(3424,-23184,4224,-22384) `
  -CoverageDimension Overworld -CoverageTerrainRadiusChunks 10 -CoverageStepChunks 16 `
  -CoverageTimeoutSeconds 4800

# Private adaptive-footprint canary. It can never be combined with -MoveToReady.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-archive-collector.ps1 `
  -Warp @('reviewed_exact_warp') -AdaptiveScan `
  -AdaptiveCoreRadiusBlocks 512 -AdaptiveExpansionBlocks 256 -AdaptiveMaxRadiusBlocks 4096 `
  -AdaptiveDimension Overworld -AdaptiveTerrainRadiusChunks 10 -AdaptiveStepChunks 16 `
  -AdaptiveTimeoutSeconds 7200

# Independently audit the saved ZIP's Anvil region headers against those bounds.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-wdl-chunk-coverage.ps1 `
  -Path 'C:\path\capture.zip' -Dimension Overworld `
  -Bounds @(3424,-23184,4224,-22384) -RequireExact

# Explicitly promote only a reviewed, bounded batch into the ready inbox.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-archive-collector.ps1 `
  -Warp @('exact_warp_1','dated_warp_2','archive_only_warp_3') -CaptureSeconds 35 -MoveToReady

# Inspect submissions without mutating Atlas, then perform the reviewed import.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\import-archive-inbox.ps1 -DryRun
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\import-archive-inbox.ps1
```

Durable state lives under `C:\AtlasExample\Ingest\archive-sync`:

- `archive-warp-catalog.json`: exact live Archive warps and category provenance;
- `collector-queue.json`: merged Atlas plus Archive capture queue;
- `collector-state.json`: `captured`, `ready`, `missing`, or `retryable` outcome per normalized warp;
- `collector\`: isolated HeadlessMc/Fabric game, mods, logs, saves, and build provenance;
- `D:\AtlasExample\Ingest\archive-captures\...\captured`: fast local staging for validated private captures and sidecars;
- `E:\AtlasExample\WorldDownloads\collector\...\captured`: durable NAS archive after source/destination hash verification;
- `ready\`: explicit handoff inbox for Atlas ingestion;
- `failed\`: recoverable failed/probe artifacts retained for diagnosis.

Catalog GUI misses, disconnects, changed screens, save failures, malformed ZIPs, hash mismatches, low disk, and client exits fail closed. The catalog saves each leaf immediately, so rerunning after a transient server/UI failure resumes from durable results. A repeated TCP-successful join stall is treated as Archive throttling/availability: stop reconnecting, retain the checkpoint, and retry after a cooldown. The capture loop never retries a completed normalized warp and never accepts a partial ZIP. A positively observed disconnect during capture marks only that WDL retryable, clears the active-download state, performs one bounded reconnect/lobby settle, and continues the selected batch; an isolated retry can later recover that exact warp without recapturing successful entries.

## Remaining acquisition boundary

The Archive does not expose a documented public warp catalog or WDL download API in its public GitHub organization. The live authenticated Minecraft GUI is therefore the authoritative catalog surface. The implemented collector covers catalog discovery, landing-view capture, exact traversal of an operator-supplied rectangle, and adaptive build-evidence traversal, but full-fidelity exhibit coverage still needs:

- confirmation from The Archive that recurring automated traversal is acceptable;
- an authoritative original chunk inventory or exhibit bounds for captures larger than the server landing view (an existing Atlas render footprint can seed a canary, but proves only that known footprint);
- a reviewed schedule and alert policy after the bounded ingestion batch proves matching, rendering, and publication;
- preservation of the official downloader ZIP/report as immutable source evidence.

The deployed architecture is a real headless modded Minecraft client running the official Archive World Downloader plus HeadlessMc-specifics. HeadlessMc is only the control surface; Archive World Downloader remains the archival capture engine.

### Shared-location catalog families

Archive collection names and shared terrain are not location identities. In particular, each named leaf in the Spawnmasons `Sky:` catalog is an independently addressable build and must remain its own Atlas location even though all captures share the larger Sky Masons terrain. The `Sky Masons` parent retains a ceiling-visible overview and a Y254 cutaway; each named child uses its exact Archive landing coordinate and a Y254 cutaway render.

Collector captures remain stored, linked to their immutable jobs, and eligible for whole-set rerendering. Every named Sky leaf is public after its Y254 replacement succeeds; equal dates and rectangular bounds do not make two exhibits equivalent. `Renders.IsPublic` and `EquivalentToRenderId` remain available only for separately corroborated true duplicates. Geometric overlap alone never activates equivalence.

### Controller state machine

The capture controller should be deliberately boring and resumable:

1. `catalogued` - retain the exact warp, GUI path/title, discovered dimension key/type, and catalog timestamp.
2. `teleporting` - send one `/warp`; reject unexpected chat errors and wait for a new world/dimension binding.
3. `settling` - wait until the player position and received-chunk set stop changing for a bounded quiet period. A fixed sleep alone is not sufficient.
4. `capturing` - start `/wdl start <safe unique name>` before traversal when the server flow permits it; record the mod's session identifier.
5. `covering` - traverse an explicit bounded route, server-provided exhibit extent, or a quarantined adaptive scan. Stationary capture only saves the server send distance around the landing point.
6. `stopping` - issue `/wdl stop`, wait for `wdl/download.pending` to disappear and a finished `download.jsonl` record to appear, then wait for the output ZIP size to stabilize.
7. `ready` - atomically move the ZIP into the Atlas ready inbox and let `import-archive-inbox.ps1` take over.
8. `accepted`, `needs-review`, or `retryable` - checkpoint the Atlas job IDs and never recapture accepted content by warp plus SHA.

Every transition needs a timeout, bounded retries, a screenshot/log tail, and exponential reconnect backoff. Stop the weekly run on authentication failure, permission denial, a changed GUI contract, repeated teleport errors, low disk reserve, or any unexpected dimension transition. Those are operator alerts, not reasons to guess and continue.

Atlas's existing warp table seeds the first capture queue. `scripts\get-archive-warp-catalog.ps1` now performs the missing permitted server catalog traversal: it opens `/warps`, walks nested inventory pages, selects leaves to obtain the server's exact canonical warp string, checkpoints every result, excludes non-2b2t lobby/survival/spawn/Constantiam/3b3t entries at any nesting depth, and fails closed on changed GUI behavior. Canonical `@Constantiam_server` and `@3b3t_server` leaves are also rejected even if a menu label changes. 2b2t test/rollback worlds and Archive project namespaces are retained; they are provenance requiring classification, not foreign-server evidence. `scripts\new-archive-collector-queue.ps1` merges that catalog with Atlas, preserving Archive-only 2b2t entries for the normal confident-match/confident-new/manual-review ingestion flow. No public GitHub repository supplies this authoritative live list.

Do not copy launcher tokens, login aliases, or email addresses into scripts, logs, or repository documentation. Device-code authentication is persisted only inside each isolated collector profile. Cataloging, capture, ready promotion, ingestion, and public publication remain separate observable boundaries.

### Collector identity registry (non-secret)

Minecraft's profile returned after a successful device-code login is authoritative; Microsoft/Xbox labels and dotted login aliases are not assumed to be Minecraft profile names. As of 2026-08-31:

- `atlas-owner` — confirmed primary collector profile under `C:\AtlasExample\Ingest\archive-sync\collector`;
- `collector-two` — confirmed secondary profile under `C:\AtlasExample\Ingest\archive-sync\collectors\collector-two`;
- `collector-three` — confirmed collector profile under `C:\AtlasExample\Ingest\archive-sync\collectors\collector-three`;
- `collector-four` — confirmed collector profile under `C:\AtlasExample\Ingest\archive-sync\collectors\collector-four`;
- `collector-five` — confirmed collector profile under `C:\AtlasExample\Ingest\archive-sync\collectors\collector-five`;
- `collector-six` — confirmed reserve collector profile under `C:\AtlasExample\Ingest\archive-sync\collectors\collector-six`.

All six operator-supplied identities are mapped to their authoritative Minecraft profiles. The original Microsoft/Xbox labels are retained only in the operator's private credential record because they are not reliable Minecraft-name evidence. The checkpoint-safe September 5 reshard promoted `collector-six` into worker 6 without changing the 798 completed-capture checkpoint.

The first supervised concurrent canary (`atlas-owner` plus `collector-two`) completed on 2026-08-31. The secondary independently recovered Blackhaven's exact 239-chunk manifest with zero survey/capture misses while the primary remained connected. A subsequent three-client canary split Blackhaven and the x25m Nether sample across isolated shards and again completed with exact manifests and zero misses. All five independently authenticated profiles were then enabled behind the same staggered supervisor and isolated mutable roots. This does not authorize unbounded concurrency or shared mutable game/state paths.

Record newly confirmed Minecraft profile names here, but keep their Microsoft login aliases and all token material only in the operator's private credential system. `scripts\initialize-archive-collector-instance.ps1` seeds a new isolated runtime without copying saves, Baritone cache, logs, or authentication.

### Throughput baseline

Coverage mod 0.6.0 raised the default waypoint stride from 16 to 20 chunks, reduced the stable load-quiet window from three seconds to two, started WDL during discovery, and accepted that save only when the independent manifest/header audit proves it exact. Blackhaven's isolated regression fell from 574 seconds to 397.3 seconds (about 31 percent) while preserving the same 239 non-void coordinates and zero misses. Coverage mod 0.7.0 adds the conservative dominant-component fallback described below. Five workers improve wall-clock throughput without weakening any per-WDL proof; actual catalog completion time remains dominated by unusually large exhibits.

## Verified collector toolchain

The verified stack is Minecraft 1.21.11, Fabric Loader 0.19.5, HeadlessMc 2.10.0, HeadlessMc-specifics 2.4.0, Fabric API 0.141.6, Archive World Downloader 1.2.0, official Baritone 1.21.11/Fabric 1.17.0, and the private Atlas Archive Coverage companion. The 1.21.11 client/mod stack and telemetry entrypoint have been exercised together. A Sep 4 controlled compatibility canary also launched the preserved complete Minecraft 1.21.10 stack with the official `archive-wdl-fabric-1.2.0+1.21.10.jar` artifact and an isolated state copy. It authenticated successfully but received the same server-side `Unable to connect to archive` rejection, ruling out both the Minecraft protocol selection and WDL 1.2.0 artifact as the cause. Terbin restored backend admission on Sep 5; six unique 1.21.11 identities then joined and emitted live operational telemetry. The Sep 5 upstream source audit independently confirmed that the official WDL 1.2.0 release matrix includes Fabric builds for both Minecraft versions. Do not use the public server-list MOTD as a recovery or maintenance signal. Only a current authenticated join followed by operational coverage telemetry establishes readiness.

The September 4 cutover boundary is preserved in the five independent collector logs. All five profiles were producing normal operational coverage immediately beforehand, then four recorded `You were kicked from archive:` at 17:48:50–17:48:51 example host local time and the fifth received the replacement `Unable to connect to archive:` response seconds later. Current launches still initialize the same coverage mod and reach the same host, but are rejected roughly one second after their Minecraft connection begins. This simultaneous boundary across five isolated accounts is stronger evidence than a generic status ping: it rules out gradual local degradation, a single expired identity, and an individual client crash. A September 5 comparison also resolved `thearchive.world` to `116.202.162.253` through the example host resolver, Google Public DNS, and Cloudflare DNS, ruling out a stale local A record at the time of the incident. Recheck authoritative DNS if the operator later announces a host migration; do not pin this address in collector configuration.

A September 5 clean-room canary authenticated the newly created Minecraft profile `collector-six`, launched the complete 1.21.11/WDL 1.2.0/coverage 0.7.0 stack from its isolated root, and received the same Velocity `Unable to connect to archive:` backend-transfer failure within two seconds. The canary exited cleanly with no capture or queue mutation. This sixth independent identity rules out an expired or individually blacklisted original account set; the remaining failure boundary is the public proxy's transfer to its backend named `archive`. It does not by itself prove whether that backend is stopped, misaddressed, incompatible with forwarding, or otherwise rejecting the proxy; only The Archive's proxy/backend logs can distinguish those server-side causes.

A second September 5 canary tested the same authenticated `collector-six` client through an isolated Cloudflare WARP SOCKS5 route. The route was verified before launch to use a different public egress address and `warp=on`, while the host and five production workers retained their normal route. The proxied Minecraft session initialized the full stack, reached `thearchive.world:25565`, and received the identical `Unable to connect to archive:` transfer failure. The disposable proxy container and canary process were then removed cleanly. This rules out the example host household public IP as the admission failure; do not add a permanent VPN or rotate production egress in response to this incident.

During an Archive version transition, `resume-archive-parallel-supervisor.ps1` can keep a bounded rollback probe in the normal recovery loop, but only with a separate, fully version-matched install authenticated as the same unique profile assigned to that worker. The preserved `D:\AtlasExample\Ingest\archive-compat\collector-1.21.10` tree is authenticated as `atlas-owner`; it is therefore diagnostic-only and must not be assigned to worker 5 (`collector-five`) or any other production lane. Doing so causes Microsoft session eviction between workers. The status JSON records the version used by each live attempt. Never infer success from a Java process alone: a usable recovery requires a completed Archive join followed by live `ATLAS_COVER` command telemetry. The startup line `ATLAS_COVER initialized ...` proves only that the coverage mod loaded; recovery requires a later operational event such as `teleport-start`, `arrived`, `settled`, or another capture-state line from that same launch.

Use `scripts\get-archive-recovery-status.ps1 -AsJson` for a non-mutating recovery check. It correlates each worker's newest Minecraft collector log with the current wrapper launch and requires the worker to remain running with a post-initialization coverage event. Its `allFiveOperational` field is the recovery gate; a reachable TCP port, Java process, loaded mod, old successful log, or `ATLAS_COVER initialized` line is insufficient. Each worker also receives a bounded normalized outcome (`live`, `archive-backend-rejected`, `server-disconnected`, `connecting`, `backoff`, or `idle`) so monitoring can explain a closed gate without copying raw logs or authentication material. The status-file read is retried for up to two seconds because the collector's frequent status rewrite can briefly hold an exclusive handle; a transient writer lock or incomplete in-place write must not terminate the watchdog probe.

The five-minute Atlas watchdog invokes the same gate after its supervisor/finalizer ownership checks. The first verified 6/6 recovery sends one Pushover notification and records `C:\AtlasExample\Ops\state\atlas-archive-recovery-notified.json`; failed notifications remain retryable, while a successful marker prevents repeated alerts. A closed gate performs no writes and never restarts an otherwise-owned collector.

Run `scripts\install-archive-collector-toolchain.ps1` to acquire the pinned HeadlessMc, WDL, and Fabric API artifacts with SHA-256 verification. The script deliberately does not authenticate, connect to The Archive, install a scheduled task, or read launcher credentials. HeadlessMc installs Fabric and its version-matched specifics through its own commands; the generated `NEXT-STEPS.txt` records the supervised one-time sequence.

Run `scripts\new-archive-collector-queue.ps1` after each catalog pass. It normalizes `/warp` spelling case-insensitively, deduplicates by warp identity, merges live Archive and Atlas provenance, carries known owning location/dimension/coordinates, includes Archive-only entries, excludes completed collector-state entries, and reports any normalized warp mapped to multiple Atlas locations as a stop-and-review conflict.

Run `scripts\invoke-archive-collector.ps1` for the authenticated headless capture loop. It defaults to one warp, paces transitions, and writes validated, SHA-256-verified ZIPs to the private D-drive `captured` staging directory. The NAS is deliberately kept out of the latency-sensitive capture loop. Use `archive-collector-captures.ps1` to hash-verify completed artifacts into E before promotion, and use `-MoveToReady` only after review to atomically hand captures to the ingestion inbox. Each warp is checkpointed as `captured`, `ready`, `missing`, or `retryable`; authentication failure, client exit, low disk, malformed output, and changed chat contracts stop or quarantine work rather than guessing. `-MinecraftVersion` is a diagnostic override for a fully version-matched installed mod stack; changing only the launch ID is invalid because Coverage, WDL, Fabric API, Baritone, and HMC-specifics are version-specific.

Run `scripts\invoke-archive-parallel-collector.ps1` for the long initial pass. It groups date variants and entries sharing an Atlas location so related warps remain on one worker, balances those groups across isolated client roots, and writes a private queue and state file per worker. Worker starts are staggered by 45 seconds to avoid an Archive login burst. A supervisor atomically merges completed checkpoints into the canonical state every 15 seconds and records aggregate health in `parallel-collector-status.json`; a nonzero worker receives at most three delayed restarts. A normal pass does not automatically replay `retryable` records: it advances through untouched candidates first, while an explicit `-RecheckRetryable` pass handles the bounded retry phase later. `needs-footprint-review` remains an operator decision and is never auto-replayed. Never point two workers at the same install, game, auth, queue, or state path.

Workers 5 (`collector-five`) and 6 (`collector-six`) are throughput lanes, each with a 30-minute per-WDL discovery budget. Workers 1-4 retain the 12-hour large-WDL fuse. When a fast capture reaches 30 minutes, it stops cleanly without producing a cropped artifact, checkpoints the attempt `retryable`, and durably transfers the full queue entry to the least-loaded long worker. `fast-lane-deferred.json` persists the transfer intent before either queue changes; supervisor startup reconciliation guarantees exactly one owner after a reboot. The long queue receives the item at priority, and a boundary signal asks that worker to reload its queue only after its current WDL reaches a durable terminal checkpoint. This changes scheduling only—the no-radius/no-world-border final standard remains intact. `parallel-collector-status.json` reports both fast worker IDs, each lane/fuse, and aggregate/per-worker deferral counts.

If only the parent supervisor is lost, run `resume-archive-parallel-supervisor.ps1` against that exact `parallel-runs` directory. It attaches to every existing PowerShell collector without restarting its Minecraft session, reacquires the canonical merge lock, and resumes state/status merging. If a shard owner is already dead or later exits before its shard is complete, the recovered supervisor relaunches only that missing profile from its existing queue/checkpoint, with bounded retries and staggered joins. Recovery defaults `AdaptiveBackgroundRadiusBlocks` to `0`, matching the primary parallel launcher; the status JSON records the effective value so a stale distance gate is immediately visible. Process ownership is matched by the worker state path, so it never launches a duplicate client for a healthy shard. The final-standard controller can then begin its normal unresolved retry pass from the merged canonical queue.

The scheduled `Atlas Example Watchdog` must run the repository-matched `C:\AtlasExample\Ops\atlas-ensure-running.ps1`. It relaunches the supervisor with six unique 1.21.11 production identities, four long-running lanes, and two 30-minute discovery lanes; it does not attach the diagnostic 1.21.10 canary. Validate the deployed/repository script hashes after changing recovery arguments, parse the deployed script before activation, and exercise it while a supervisor is already live to prove the watchdog remains idempotent and does not spawn a duplicate owner.

All collector JSON checkpoints now use the shared `archive-json-io.ps1` writer. On Windows PowerShell 5.1 it writes a same-directory temporary file, atomically replaces an existing destination through `System.IO.File.Replace` with a unique transient backup, and retries bounded sharing violations. Do not replace this with `Move-Item -Force`: an active reader can make that command report that the destination already exists and kill only the parent supervisor while its six capture children continue. The writer is used by catalog, queue, worker, supervisor, recovery, archiver, promoter/import handoff, weekly sync, and SEO status paths. Recovery is safe when `parallel-collector-status.json` is stale but the worker shard `state.json` files and collector processes continue advancing: verify the six owners, confirm the canonical `.parallel.lock` is absent, then use the resume script rather than starting a second collector pass.

`add-prestandard-captures-to-queue.ps1` performs the one-time, idempotent final-standard queue expansion and preserves a timestamped queue backup. It also re-queues the former `skipped-inside-background` records now that the provisional origin-distance rule is retired. If a parallel run was already sharded before that expansion, `expand-archive-parallel-backlog.ps1` safely appends only previously unassigned eligible identities to the shard queues, preserves location-family grouping, balances load, and backs up every old queue. Stop and resume only the supervisor around this repair; already-running Minecraft clients keep their in-progress saves. `wait-and-finalize-archive-parallel.ps1` invokes `invoke-archive-rolling-handoff.ps1` every 15 seconds while the six capture workers continue. Each completed v2 capture is copied from D to E with hash verification, strictly promoted into `ready`, and submitted to the production API immediately; it no longer waits for the whole catalog. The controller performs the untouched initial pass before explicitly enabling up to two bounded retry passes, and blocks rather than declaring the initial pass complete if any non-missing queue identity remains pre-standard. Canonical checkpoint writers use same-volume atomic replacement, and supervisors/finalizers read through `Read-AtlasJsonWithRetry`, which shares read/write/delete access and retries transient replacement, antivirus, NAS, and JSON-parse races instead of terminating the handoff.

Collector telemetry must use the newest per-launch `logs\collector-*.log` as its live identity/progress source. Large WDLs can push their initial teleport message more than one mebibyte behind the tail of Minecraft's `latest.log`, and a recovered supervisor's redirected stdout path can be stale. The admin API therefore scans the current collector log for the last teleport while keeping coverage parsing bounded near the tail; process IDs and host paths remain private.

There is no separate operator promotion/import step in the normal live path. The rolling handoff drains any completed backlog before waiting for new captures, then performs archive, strict promotion, and production submission after each final-standard checkpoint. The production ingestion worker keeps its API lease alive while reading the NAS archive, avoids re-hashing the same canonical X-drive object twice, and can rebuild a generation removed by a failed-registration rollback from authenticated renderer output. This makes capture-to-production resumable without re-rendering when only publication was rolled back.

`scripts\stop-archive-collector-at-checkpoint.ps1` is the safe maintenance boundary. It waits for the named warp's `CAPTURED` or `RETRYABLE` checkpoint, waits for the canonical merge, stops only the verified supervisor process tree, and quarantines stale lock files rather than deleting capture data. Do not kill Java mid-save merely to install a collector update.

`-Warp` accepts a reviewed array and resolves every requested identity back to the merged queue so its catalog/location provenance is retained. It refuses to recapture `captured` or `ready` entries only when they already satisfy the current collector standard; pre-standard terminal captures are deliberately eligible for replacement. A recorded missing warp requires the explicit `-RecheckMissing` switch. Omitting `-Warp` processes the next `-MaxWarps` eligible queue entries.

The operational collector uses The Archive's official 1.2.0 release for Minecraft 1.21.11. Its installed artifact SHA-256 is `a837d4075f56a0da4248dde6d50cdec0070ea05629a2294bc63e04587e48d57e`. Release 1.2.0 incorporates the earlier yaw/pitch and incomplete-save corrections, so the former private 1.21.10 post-release build is retained only in disabled rollback files and is not part of the active mod set.

The default safe capture mode remains `landing-view`: wait at the exact Archive warp and save only the chunks the server sends around that stationary player. Fusionia and Dust Falls produced the same 336-by-336-block render footprint because they received the same approximate 21-by-21-chunk view; this is a send-radius shape, not proof of equal exhibit size. Hurtville produced only seven occupied chunks and a 48-by-48-block render. Dust Falls' older 800-by-800 render is direct evidence that the landing capture is incomplete.

`bounded-route` is the reviewed alternative when trusted block bounds exist. The client walks a serpentine route with the pinned official Baritone runtime under a non-destructive policy (no breaking, placing, parkour, or inventory manipulation), records every chunk-load event for the exact museum dimension, waits for bounded load-quiet windows, and clusters remaining holes into at most three repair passes. Headless flight permission is recorded but is not preferred because permission alone did not make synthetic movement reliable. The collector accepts the result only when the live ledger says `exact=true`; it then independently reads every saved Anvil location header and requires every target chunk to exist in the ZIP before hashing or quarantining it. Either proof can fail the capture. `exact=true` means exact within the supplied rectangle—not that the rectangle is the Archive's complete original WDL.

`adaptive-footprint` is the canary alternative when authoritative bounds do not exist. The controller enters spectator mode, uses the Archive-supported `/tppos` command, begins with a configurable core square, and inspects actual block palettes and block entities for every received chunk. It normally follows the probable-construction component nearest the warp and joins evidence across no more than a three-chunk gap. Some Archive warps land on a tiny observation platform beside the real exhibit; when that landing component is weak and another component is both at least three times larger and decisively stronger, mod 0.7.0 locks onto the dominant component instead. The strict fallback preserves the Dust Falls/Cliff Resort separation that a broader connection gap broke. Expansion occurs only when the selected component approaches the survey edge. The configured expansion is also the context margin around the component. Discovery now begins with WDL active, so an exact discovery save can be accepted directly; the older clean second traversal remains a fallback when the independent disk audit does not prove the discovery artifact. Completion metadata includes survey and capture bounds, component count, landing/dominant/selected evidence, selection strategy, orphan evidence, iterations, waypoint count, optional safety-cap status, known neighboring Atlas warps, live warp position/radius, and confidence. The raw ZIP must contain every coordinate in the discovery manifest; this correctly permits Archive WDL to omit pure void under `skipVoidChunks=true`. A separately hashed render derivative then clears every out-of-bounds region/entity/POI header slot and must retain every manifested non-void chunk with zero chunks outside its bounds. The immutable raw ZIP remains preserved. The PowerShell controller rejects `-AdaptiveScan -MoveToReady`; review and a separate promotion decision are mandatory.

Collector standard v2 is the first promotable standard that requires mod 0.7 component-selection evidence. Every prior `captured`/`ready` result—including otherwise valid adaptive captures—is re-queued once and replaced; final-standard records carry `adaptive.standardVersion = 2` plus the complete `componentSelection` evidence. Reuse between date variants is also restricted to v2 sources. This prevents a small historical landing-component ZIP from silently satisfying a later capture.

The Interdimensional Bridge End regression exposed the landing-component failure directly. Mod 0.6.0 selected a 6-chunk component with one strong chunk and left 108 probable-build chunks orphaned, producing a 608-by-560-block / 681-header derivative. Mod 0.7.0 recorded `selection=dominant-fallback`, retained the original 6/1 landing evidence, selected a 151-chunk component with 131 strong chunks, expanded one additional survey strip, and enclosed it at `-183024,-339568..-182128,-338896` (896 by 672 blocks). The exact derivative contains 1,410 headers, all 1,409 manifested non-void chunks are present, survey/capture misses are zero, and no unrelated known Atlas warp falls inside the bounds.

The 2026-08-24 private Dust Falls canary used the reviewed older render bounds `3424,-23184..4224,-22384` (800 by 800 blocks, 2,500 chunks). The nine-waypoint base route reproducibly left 20 chunks at the four outer corners because the server's send radius is circular rather than square. The controller grouped those holes into four repair waypoints; one repair pass reached a live `2500/2500`, and the finalized ZIP independently reported `2500/2500` from its Anvil headers. The capture contains 4,212 chunks overall because movement also receives terrain outside the target rectangle. Its quarantined SHA-256 is `667a59d4e92969c2183b755970b82caf877c59ad0db6db1127c4084133ffa47f`. A cropped uNmINeD comparison reproduced the expected 800-by-800 footprint versus the 336-by-336 landing-only render. Nothing from this canary entered `ready`, Atlas ingestion, tile publication, or the public site.

The final private adaptive Dust Falls regression used a 1,024-by-1,024 survey around the warp. It reproducibly found an 80-chunk primary construction component with 34 strong chunks, inferred `3552,-23184..4384,-22448` (832 by 736 blocks) after the 256-block context margin, and excluded Cliff Resort. The discovery survey reached 4,096/4,096; the clean capture reached 2,392/2,392 and its raw ZIP contained 4,419 chunks including the unavoidable send halo (SHA-256 `c78623575a8e9ba010659f5d9d829a517237838d175774c3b86896e80d82b6a8`). The compact footprint derivative rebuilt region headers/sectors, retained exactly 2,392/2,392 target chunks with zero outside chunks, removed 2,030 header slots and 8,892,416 uncompressed region bytes, and rendered pixel-identically to the non-compacted footprint. Its canary SHA-256 is `391b518ab4f9df9c941f671d14f9615fc56ac4badea86b1bd177df6188befa09`. All artifacts remain under private `canary`/`captured`; none entered `ready`, ingestion, publication, or the public site.

The sparse Hurtville regression exercised the void-aware path. Its complete 1,024-by-1,024 discovery survey found 119 non-void chunks, 27 probable-build chunks, 13 strong chunks, and no orphan build evidence. The component plus margin inferred `137632,-166704..138256,-166064` (624 by 640 blocks). The exact second pass received all 1,560 server targets; Archive WDL intentionally persisted only the 119 non-void chunks. Mod 0.5.2's manifest and the independent ZIP audit agreed on all 119 coordinates with zero missing, and the compact derivative contains 119 headers with zero outside its inferred bounds (SHA-256 `2d5dc3563de18f361ca65a0c47bf5cda708aad9e9816c16424ff5b05d1995277`). The canary remains private and was not moved to `ready` or ingested.

The provisional 100,000-block ingestion boundary is retired. Every high-confidence 2b2t capture may proceed regardless of distance or whether Atlas knew its coordinates before capture. The collector still records live position and distance as evidence, but neither the collector, promoter, nor importer uses origin distance as an acceptance gate. The production queue has no Minecraft world-border clamp or arbitrary maximum footprint; the evidence-driven void/context boundary remains authoritative. `new-archive-collector-queue.ps1` and the rolling handoff independently reject explicit Constantiam/3b3t suffixes or catalog provenance so removing the distance limit cannot admit foreign-server warps.

Treat every automated landing capture as a preview, every adaptive capture as review-required, and every bounded capture as a coverage canary until its bound source is authoritative. Direct live probes found that unloaded positions in the tested Archive museum dimension return empty/void chunks rather than ordinary generated terrain. That makes void useful as a stopping signal, but it does not solve exhibit ownership: Dust Falls Manor and Hurtville reported the same custom backing dimension, and known nearby warps can coexist inside that merged world. Adaptive scans therefore key expansion to player-construction evidence and keep expanding until the evidence is enclosed by surveyed context; no Minecraft world-border clamp or arbitrary size cap is applied by the production pass. An optional operator-supplied cap is recorded and lowers confidence when reached. These scans do not claim to reproduce an original ZIP's chunk inventory. Exact provenance still requires original chunk metadata or bounds supplied by The Archive. The GUI tooltip remains useful catalog provenance: it exposes display name, establishment date, and builders, while the leaf click returns the canonical warp string. Adaptive timeout is an inactivity watchdog, not a wall-clock capture cap: every verified coverage waypoint or expansion renews the lease. A separate 12-hour operational fuse records `needs-footprint-review`, preserves the stopped working save, and prevents automatic replay; it never promotes a cropped artifact. The controller also applies a catalog-aware runaway guard to coordinate/regional entries filed by The Archive under `Along the Axes`. If one of those views is still open after five adaptive expansions, it is quarantined instead of following connected highway or milestone terrain indefinitely. Named builds now also have class-specific survey budgets that stop for review without clipping or publishing a partial world; see [Collector footprints](COLLECTOR_FOOTPRINTS.md).

The catalog crawler is bounded by page, depth, and newly discovered warp counts, shares the collector's single-instance lock, and is resumable. It retries transient GUI-open misses and whole dropped-slot clicks with bounded backoff, waits through bounded slow teleport transitions, and skips already checkpointed leaf breadcrumbs during an interrupted crawl so recovery does not replay every known teleport. A positively observed `Client disconnected`/socket-reset marker permits one bounded in-process reconnect and lobby-settle before path recovery; absence of that marker never authorizes a blind reconnect. Every requested path click must yield a nonempty menu fingerprint different from its parent, preventing a dropped post-reconnect click from mis-nesting the root menu under a leaf. GUI parsing waits only until all clickable inventory slots (0-53) have been emitted; it does not depend on decorative slot 89, which asynchronous client logging can corrupt. Three failures of the same reopened slot still stop the crawl rather than guessing. Use `-RootLabels` for a narrow supervised resume because it resolves the live session's slot by a unique case-insensitive label. `-RootSlots` remains a low-level diagnostic probe only; numeric slots are not stable across Archive sessions. Omit both to traverse the full 2b2t menu. Use `-RevalidateKnown` for a later deliberate refresh that re-clicks known leaves and confirms their current canonical identities. Records retain the exact warp, display label, category path, server, and first/last-seen timestamps. A label-based pass upgrades temporary `root-slot-*` breadcrumbs to their canonical `Warps > label` path. Canonical and compounded suffixes such as `@End`, `@The_End`, `@End@Spawnmasons`, `@Nether@Spawnmason_lodge`, test/rollback keys, and project namespaces are preserved verbatim as provenance; they are useful evidence but do not independently override world inspection or an explicit historical-dimension conflict.

All catalog, queue, state, and sidecar JSON reads and writes use explicit UTF-8 without BOM. This is mandatory under Windows PowerShell 5.1: `Get-Content` without `-Encoding UTF8` can reinterpret Unicode and create false warp identities on a later save. `repair-archive-catalog-utf8.ps1` backs up the input, repeatedly repairs only marker-reducing CP1252/UTF-8 mojibake, recomputes normalized warp keys, merges converged duplicates, reapplies foreign-server filters, writes atomically, and emits an audit JSON. Review that audit before deleting any backup.

The current footprint policy applies review budgets independently of distance from origin. It separates lodges, individual builds, linear exhibits and regional views, and checks both expansion and completion. Budget exhaustion preserves the working save for extent review; it never authorizes a truncated final WDL. Offline, height-aware boundary proposals can be generated from preserved source chunks without collecting them again. See [Collector footprints](COLLECTOR_FOOTPRINTS.md) for thresholds and the review workflow.

## Dimension handling

Modern releases of Archive World Downloader route worlds by dimension type into canonical `DIM-1`, `DIM1`, or Overworld storage even when the server's dimension key is custom. Atlas handles these automatically.

For older exports and museum re-exports:

- canonical folders are authoritative;
- exactly one custom root whose tokenized identifier explicitly says `nether`, `hell`, `end`, `overworld`, or `surface` can be normalized automatically in the private work copy;
- arbitrary custom IDs require an explicit original dimension;
- multiple custom roots always fail closed;
- the immutable source ZIP is never rewritten;
- missing `level.dat`, `level.dat_old`, legacy `.mcr`, and old per-chunk `.dat` layouts are accepted when valid chunk storage exists.

Future acquisition metadata should record the Archive warp, GUI title/path, server dimension key, server dimension type, command timestamp, source client version, and capture session ID. Those fields make otherwise opaque museum dimension names deterministic.
