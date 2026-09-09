# Atlas Archive Coverage

See [disk recovery and sparse repair](../../docs/COLLECTOR_RECOVERY.md)
for journal ownership, disconnect handling, replay and test results.

## Coverage 0.10.0 scheduling and recovery

Adaptive interior waypoints are anchored to the warp's chunk grid, not the moving
survey boundary. Edge centers still cover the full requested extent. Scheduling
skips a waypoint only when its entire clipped, square terrain window has actually
been received and classified, or restored from its verified checkpoint, including void cells. Missing cells
remain discovery/repair work. Existing five-second minimum settle, two-second quiet
period, twenty-second maximum settle, repair limits, component rules, and final
ZIP/footprint audits are retained. A fully observed window can finish settling
after the minimum without waiting for unrelated duplicate chunk traffic.

Per-dimension sparse region bitsets provide an incremental whole-survey missing
counter and bounded window queries. No world-sized allocation is used.
The build runs 12,000 deterministic differential cases against a simple oracle,
plus negative-coordinate, dimension, reset, and expanded-grid coverage checks.

`/atlascover checkpoint` stops an adaptive survey and writes its bounds, anchor,
iteration/waypoint counts and observed void coordinates (schema 3, with a capture identity).
On a fast-lane timeout the wrapper also cleanly closes and hash-verifies the partial
WDL, preserving it privately under
`D:\AtlasExample\Ingest\DeferredCaptures\<identity-sha>\<attempt>`.
Nothing here is public or eligible for ingestion. Either lane may use
`/atlascover resume` only for an exact server, dated warp, dimension, landing,
policy/route-parameter and SHA-verified handoff. Queue age does not expire a dated
snapshot. Saved NBT is read off the game thread, validates chunk coordinates and
packed block palettes, and uses the same block classifier as live observations.
The restored terrain and void ledger populate the missing-chunk index. Fresh
observations take precedence. Schema-1 handoffs restore terrain too, but cannot
recover void coordinates that were never recorded. Changed, corrupted, or
undated checkpoint identities stop for review with the original files retained.

The wrapper combines old and new ZIPs with `archive_capture_resume.py`: every
Anvil chunk slot is selected independently, fresh payloads win overlaps, and
old-only terrain/entities/POI and external `.mcc` files survive. Inputs stay
immutable; a failed write never publishes a partial combined ZIP. Only the normal
final saved-ZIP/footprint audit can accept the result for ingestion. Resumed and
retained/refreshed chunk counts are written to logs and capture provenance.

Validation includes saved-NBT/external-chunk fixtures, chunk-slot union and repeat
handoffs, interrupted writes, corrupt hashes, and a real 29,270-chunk partial.

Rollouts use per-instance `pending-coverage-mod.json` and the existing
`reload-queue-after-current-wdl.signal`. The wrapper applies a SHA-pinned staged
JAR only before launching Java, after the current WDL reaches a durable boundary.
Previous JARs remain `.disabled-<timestamp>` for rollback. Never install a second
enabled version alongside an active client's mod.

This private client-only Fabric companion gives the headless Archive World Downloader collector two auditable
coverage modes:

- `route` covers a trusted operator-supplied rectangle. It uses the pinned Baritone runtime under a
  non-destructive profile and repairs circular-send-radius holes before reporting exact coverage.
- `adaptive` uses The Archive's spectator `/tppos` command to scan an initial square, classify block evidence in
  every received chunk, and find the construction component nearest the warp. Probable-build chunks connect
  across at most three empty chunks. If the landing component is weak and a separate component is both at least
  three times larger and decisively stronger, the scanner locks onto that dominant component instead. This covers
  Archive exhibits whose warp lands on a small observation platform beside the real build without generally
  merging neighboring museum exhibits. The survey expands only when the selected component approaches an edge.
  It stops when the component plus context margin is enclosed by the detected void boundary. A maximum of `0`
  (the default) means unbounded: there is no vanilla-world-border or base-size clamp. The collector retains only
  a signed coordinate-representation overflow guard, far beyond Minecraft's ordinary +/-30M border.
- `teleport` covers a fixed rectangle with `/tppos`. The wrapper records the adaptive discovery pass
  directly; it uses a clean second pass only when the independent saved-chunk audit fails.

The adaptive classifier records non-air blocks, a deliberately conservative set of artificial blocks, strong
construction markers, and block entities. A chunk is `probableBuild` when it has a block entity, at least two
strong markers, or at least 24 artificial blocks; it is `strongBuild` with two block entities, eight strong
markers, or 96 artificial blocks. Ordinary terrain is never treated as proof of ownership. These are acquisition
signals, not a forensic claim about which original WDL supplied a chunk. The three-chunk connection threshold is
empirical and intentionally conservative: on the Dust Falls canary, a four-chunk threshold merged the target into
Cliff Resort and the wider museum composite, while three chunks kept the warp-seeded base isolated.

Machine-readable `ATLAS_COVER` lines report dimension, bounds, waypoint/repair progress, received and missing
chunks, evidence totals, component count, landing/dominant component evidence, selection strategy, adaptive
iterations, coordinate-limit status, and final confidence. The controller only reports
high adaptive confidence when all target chunks were observed, meaningful build evidence exists, and no edge asks
to expand beyond the signed coordinate representation. Reaching that technical limit with continuing build evidence is
`confidence=low` and must remain in the private collector quarantine. Time and disk ceilings remain resource
safeguards; they do not redefine a large WDL as invalid. Adaptive route progress renews the controller's inactivity
lease, so a healthy multi-hour capture is no longer killed by a fixed two-hour wall-clock timeout. A separate
12-hour operational fuse quarantines the stopped working save for footprint review instead of accepting a crop or
automatically replaying it. The Archive uses shared merged museum dimensions, so a nearby exhibit can be real
terrain yet still belong to another warp; no terrain classifier can reconstruct exact source-WDL provenance.
The PowerShell controller additionally uses trusted catalog context: coordinate/regional entries filed under The
Archive's `Along the Axes` tree are not assumed to be bases only inside the known `+/-100,000` merged-background
square. When one remains open after five adaptive expansions, the controller cancels and quarantines it instead
of following that background's connected highway network forever. Outside the square, void closure remains
authoritative because a long linear footprint may be the exhibit itself. Explicitly
named bases, monuments, cities, and other builds remain unbounded unless they describe linear infrastructure; this
policy is not a global base-size or world-border clamp.

Commands:

- `/atlascover reset`
- `/atlascover status`
- `/atlascover route <minX> <minZ> <maxXExclusive> <maxZExclusive> <terrainRadiusChunks> <stepChunks>`
- `/atlascover adaptive <coreRadiusBlocks> <expansionBlocks> <maxRadiusBlocks> <terrainRadiusChunks> <stepChunks>`
- `/atlascover teleport <minX> <minZ> <maxXExclusive> <maxZExclusive> <terrainRadiusChunks> <stepChunks>`
- `/atlascover cancel`

For `route`, the controller prefers official Baritone 1.21.10 because HeadlessMc can expose flight permission
without reliably applying synthetic movement keys. Baritone may walk, sprint, jump, and swim, but it may not break
blocks, place blocks, parkour, or manipulate inventory. Direct flight remains a bounded fallback. Adaptive mode
requires spectator flight and sends only the Archive-supported `/tppos` command; the PowerShell collector enters
spectator mode before starting it. All movement has timeouts and fails closed.

After either capture route, the collector independently audits the saved ZIP's Anvil location headers. `exact=true` means
every chunk in a trusted rectangle was received during this capture. `adaptive-complete` means the final adaptive
survey was complete and reports separate survey and inferred-capture bounds. It also atomically writes a sorted
manifest of every non-void chunk inside the inferred bounds. This matters for sparse exhibits: Archive WDL intentionally
omits pure void under `skipVoidChunks=true`, so acceptance proves every manifested non-void coordinate was saved rather
than incorrectly demanding Anvil headers for empty space. Because the server sends a halo around every teleport, the
raw WDL may contain chunks outside the target. The collector preserves that raw ZIP, then creates a separate derivative
by clearing out-of-bounds Anvil/POI/entity header slots. A second audit requires all manifested chunks and zero chunks
outside the inferred bounds. Neither artifact claims to exactly equal the historical WDL's original chunk set.
