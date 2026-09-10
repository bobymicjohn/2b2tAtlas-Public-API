# Shared Archive worlds and lodge entries

The Archive can expose several named exhibits in one connected world. A distinct
warp name identifies an exhibit, not necessarily a standalone base or WDL extent.
Equal render rectangles are also insufficient evidence that two dated saves are
interchangeable.

## The Isle, reviewed September 10, 2026

Hatch Lodge, Phoenix Fortress and Time is Running Out lead to different arrival
points around The Isle in the End. Opera Lodge and Escaping from Sky to our New
Home share that setting too. Their collected render rectangles all cover
X -24,768 to -23,504 and Z 5,952 to 7,200 (exclusive maxima), matching The Isle's
own Archive capture. A top-down render of the preserved Isle source confirms the
shared island and surrounding builds.

The collector followed connected construction beyond each named lodge. The old
neighbor check consulted only that worker's catalog queue and used Atlas
coordinates. Fresh catalog entries had zero coordinates, so the overlaps were
incorrectly treated as high-confidence individual footprints.

The five lodge entries now include `(The Isle)` in their titles. Their descriptions
and render labels identify the shared area. The original IDs, warp commands,
arrival coordinates, catalog dates and downloadable assets remain intact. These
are individual lodge entries within a base, not five unrelated whole bases.
Do not repeat the retired Sky-prefix consolidation: an Archive collection prefix
alone does not justify merging locations.

`scripts/clarify-isle-lodges.py` applies this reviewed metadata correction with an
online SQLite backup, expected-state checks, one transaction and audit records.
It requires explicit database and private backup-directory arguments. Existing
text changes cause it to stop for review. Keep its backup and change receipt
outside Git. Use the record restore tool with that verified backup if rollback
is needed; preview the affected records first.

## Collector guard

Both parallel supervisors pass the canonical state path and current worker-run
directory to the collector. At footprint completion it reads observed Archive
arrival coordinates from canonical and peer state, including active capture
journals. Matching requires the same server and exact live world ID. Two Archive
worlds can have identical coordinates in the same dimension and remain distinct.

A neighboring exhibit within the inferred rectangle downgrades confidence and
prevents initial automatic ingestion. This is a review signal, not permission to
crop, merge historical saves or reuse a different date. Unknown/unvisited warps
cannot yet be detected; this guard does not establish exclusive ownership of an
area. An unreadable configured state fails the check rather than assuming there
are no neighbors.

The separate area/span/iteration limits and disk reserve remain essential: a
connected road or underground generated structure can lead discovery beyond the
intended build before any known neighbor is reached. A stopped oversized capture
must remain held until its footprint is reviewed. Never treat packet counts as
distinct persisted chunks or trim a world merely to fit a budget.

Regression coverage: cross-worker completed and active captures, unknown Atlas
coordinates, exact world/server isolation, fractional/exclusive boundaries,
date variants, duplicated observations, and unreadable state.
