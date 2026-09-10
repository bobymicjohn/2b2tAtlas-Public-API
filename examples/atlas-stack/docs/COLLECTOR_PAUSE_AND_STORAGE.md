# Pausing collection without losing the save

Create `C:\AtlasExample\Ingest\pause-collector` to latch an operator pause. The collector
checks this file while downloading, including when no coverage messages arrive.
It checkpoints the survey, stops travel, asks WDL to save, and waits for the
writer to finish. The supervisors refuse new launches and the watchdog respects
the latch. API, rendering and backup watchdog checks remain enabled.

The collector checks both its saves drive and capture output drive every five
seconds during capture, before recovery, and before starting another warp. Falling
below `MinimumFreeGiB` (100 GiB by default), or being unable to read the available
space, creates the same pause latch. Recovering free space does not resume work.
Do not force-kill a game while WDL is flushing.

## Footprints are review budgets, not completeness claims

An adaptive survey can follow construction through an Archive world containing
many exhibits. Underground generated structures can also resemble construction.
Large rectangles and repeated expansions therefore do not prove a base is large.
See [the footprint policy](COLLECTOR_FOOTPRINTS.md) for exhibit-specific limits.

An oversized or operator-held capture keeps its working save, active journal and
parent checkpoints. `requiresFootprintReviewBeforeRecovery` prevents automatic
reconstruction and preservation from making more copies of that same held world.
It also blocks a new capture from overwriting the active journal. A held capture
is neither complete nor eligible for publication.

Before resuming, inspect the stopped save and its parents, establish the intended
extent, and verify the recovery plan. Preserve the original terrain when preparing
a smaller derivative. Release the individual review hold only after that review,
then remove the operator pause file and start the supervisor. Removing the pause
file alone does not release an individual review hold.

## Existing duplicate snapshots

Interrupted snapshots are immutable. Byte-identical files in these private
snapshots may share an NTFS hardlink after both files have been checked against
their receipt SHA-256 hashes. Every original path and receipt must remain valid.
Never link a snapshot to a mutable working save, or edit a preserved snapshot in
place. Copy it to a new working directory for recovery. Different dated worlds
are not duplicates merely because their bounds or chunk counts match.
