# Missing tiles and underground views

A working BlueMap does not prove the 2D tile tree is healthy. The two renderers
publish separate derivatives of the preserved WDL. Check the record's tile URL,
its on-disk generation, the public HTTP response, and the browser's image loading
before changing coordinates.

`scripts/audit-render-availability.py` takes explicit `--database`, `--tiles-root`
and `--output` paths. It opens SQLite read-only and checks generated tile folders,
day/night overview tiles, and whether each same-dimension location marker falls
inside the saved footprint. It does not hash every tile or certify coordinates.

## SBA 78, September 10, 2026

Location 1687/render 1170 referenced a missing tile generation while its full-height
WDL and BlueMap remained available. The original processing cache still matched
both publication receipts: 178 day and 178 night tiles, 24,816,873 bytes total.
The original generation was restored by verifying the plan, archive identity,
destination, tile inventories and copied bytes before an atomic directory move.

Cloudflare continued serving cached 404s for the old URLs, so a fresh immutable
generation was used. Do not overwrite an existing generation with different
pixels or assume restoring a file clears cached errors.

The warp arrives at Y -37. Restoring the surface tiles exposed a second problem:
terrain concealed the actual underground build. Local previews at Y -20, -30 and
-36 were inspected. The published 2D view uses the Y -20 cutaway through the same
hash-pinned renderer, day/night grading, tile adapter and publication verification.
The original surface generations, full-height WDL and existing BlueMap are retained.
This is a 2D-only derivative; the ingestion job's shared `RenderTopY` was not changed,
because that would also change future BlueMap masks.

The catalog audit covered 1,157 public generated render records. After recovery,
all had their generation directories and day/night overview tiles. Twenty-two
render records have a location marker outside the saved bounds. Those require
identity and coordinate review: do not translate their pixels or replace location
coordinates solely to make the rectangles line up. The existing Zoom action
shows a render's stored footprint.

## Registration is an uncertain commit boundary

Once the worker sends a completed-render registration, the API may commit even
when its response times out or the connection is lost. The previous failure path
could then delete the newly published generation, leaving a valid database row
with missing files. The worker now disarms tile rollback before making that
request. A failed acknowledgement preserves the immutable generation and its
receipts for reconciliation/retry.

The worker also retains earlier generations after successful registration. They
may still be referenced by cached clients, another render, or rollback evidence.
Any later retention job must compare against catalog references and backup policy.

Regression tests exercise retained new/previous bytes after uncertain registration,
cleanup of truly unpublished output, and the existing generation/receipt guards.
