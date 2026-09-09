# Highway contributors

Give each approved contributor their own **Highway Architect** account. The stored
role is `HighwayArchitect`; its default permissions are exactly `highways.create`
and `highways.edit`. Membership in HWU or a group credit does not grant access.
The owner creates accounts, assigns roles and resets passwords in Admin → Users.
Do not share a login or give highway contributors Cartographer/Archivist access.

Before the first grants, keep a named database baseline outside rolling backup
rotation and verify its encrypted NAS copy by restoring it into a disposable
database. Record the snapshot identity, file hashes, restore commands and row counts
in a private operator runbook. Retain this baseline even as normal pre-edit backups
continue. It complements the per-change highway history below.

## Editing

Log in, open the map in the correct dimension, select a highway and choose Edit.
The highway tool also creates new routes. Coordinates and ring radii use the
selected dimension's blocks: a Nether coordinate is not an Overworld coordinate.
When a Nether road is selected through its Overworld projection, editing retains
its Nether dimension and converts map clicks back to Nether coordinates. Native
Overworld and End routes keep their own coordinates without the legacy divide-by-eight.
Update widths, clearance, paving, lighting, construction status and geometry as
work progresses. Add HWU or another group under Builders / maintainers; preserve
the original builder's credit. Existing role/evidence notes survive ordinary edits.
An existing primary credit can be corrected by the owner after source review.

Fields absent from the editor, including video URLs and display settings, survive
saves. Ring roads keep their radius and can be edited without drawing new points.
References must be HTTP(S) links. Invalid coordinates, degenerate paths, out-of-range
dimensions/widths and nonexistent group IDs are rejected before any catalog write.

Each edit carries the state version read when the editor opened. If someone else
saved meanwhile, the API returns 409 and keeps the submitted draft in the editor.
Reload the highway and compare before resubmitting; never attach a fresh version
to an old payload without reviewing it. Old clients without a version receive 428
and must reload the updated frontend. Revision approval uses the same checks.

## Damage controls

Only the owner can delete or restore highways, withdraw approved highways, manage
accounts/roles, or change global render and ingestion settings. Highway Architects
cannot edit locations, attachments, group records, renders or WDL storage. They
can read highway-specific history but cannot read the general administrative log.

Non-owner changes are blocked for owner review when they:

- hide a route or change its dimension;
- shorten geometry by more than 25%, or expand it beyond four times its prior length;
- move its average vertex position by more than the larger of 4,096 blocks or 25%
  of its prior length;
- remove an existing group credit or replace its primary builder.

Geometry checks also compare with that editor's first saved change to the route
in the previous 24 hours. This catches repeated small trims or moves that would
otherwise evade a per-request threshold. These are damage checks, not proof that
a proposed highway is historically correct. Plausible false information still
needs human review. Blocked proposals and their reasons are retained in history;
they are never treated as applied/restorable versions.

Existing durable edit limits apply: 10/minute, 60/hour and 200/day per non-owner,
plus 120/hour and 400/day shared. Attempts admitted to the mutation pipeline count
even if validation later rejects them. Every admitted write requires a verified
online database backup and append-only admission ledger. Backup failure pauses
editing; quotas survive API restarts. Account deactivation or password reset
invalidates existing sessions on their next request.

## Review and recovery

Admin → Highways → Highway change history shows editors, times, changed fields and
attention warnings. Filter by editor or action and inspect complete before/after
states. Refresh explicitly; the history does not reset while being read.

If credentials go rogue:

1. Disable the account in Admin → Users. Preserve its account for attribution.
2. Review its highway changes. Blocked attempts did not alter the highway.
3. Inspect the current route against the saved before state, then use the owner-only
   Restore before state button. It restores that route and its group credits,
   including an owner-deleted route. It does not rewind other highways or locations.
4. A restore fails with 409 if the route changed after the preview. Refresh and review
   again. Every successful restore is itself an audited, reversible change.

Restoring an older state replaces later edits **to that highway**, including good
ones; compare first. For several malicious edits to the same route, select the
first bad change's before state. Creation has no before state: remove a malicious
new route using the owner-only delete action. If a saved group was deleted, restore
that group first; highway restore fails instead of silently dropping its credit.

Complete in-app snapshots begin with this release. Earlier audit entries remain
visible but may lack a restorable before state. The local operator can recover
those from verified pre-edit SQLite snapshots using `scripts/restore-atlas-record.py`.
Do not restore the entire production DB just to repair a highway. Snapshot storage,
NAS backup tasks and offline-recovery limits are described in
[recovery and storage](STORAGE_AND_RECOVERY.md).

## API and tests

Public reads remain under `/api/highways`. Authenticated editors use
`GET /api/highways/{id}` to obtain `editVersion`, then include it in PUT. The
highway-only history endpoint is `GET /api/highways/history?highwayId=ID&take=200`.
Omit `highwayId` for recent changes across the network; the maximum page size is 500.
Owner restore uses `POST /api/highways/history/{auditId}/restore` with
`{"expectedVersion":"CURRENT_VERSION"}` (`"deleted"` if absent). Protected history
and mutation routes are excluded from the public OpenAPI document.

`HighwayEditingTests` exercises stale/missing versions, invalid geometry, cumulative
damage checks, attribution, revision approval, audit failure rollback and deleted
route recovery. `HighwayAccessTests` uses an isolated HTTP host with production
JWT validation, live role resolution, write protection and pre-edit backups.
No production account or highway is modified by these tests.
