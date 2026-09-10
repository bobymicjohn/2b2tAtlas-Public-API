# Reviewing and replacing oversized WDLs

Size is a review signal, not a build boundary. A collector can follow a road into
another exhibit; a supplied save can contain stray spawn or dimension fragments.
Keep the original snapshot while reviewing a smaller derivative.

1. Rank completed sources by ZIP bytes and inspect their occupied chunk bounds.
   Use the exact dated Archive warp, rather than another date's location marker.
2. Propose horizontal bounds from saved data and inspect the surface render.
   Ask for extent review when detached buildings, islands or regional ownership
   are uncertain. Never infer permission to remove them from a size threshold.
3. Crop into a new ZIP with `scripts/new-wdl-footprint-artifact.ps1`. For sparse
   saves, pass an explicit retained-chunk manifest instead of requiring a filled
   rectangle. Preserve full-height terrain, entities, POI and other dimensions.
4. Verify retained NBT payloads against the source with
   `scripts/compare-wdl-payloads.py`. There must be no changed or added payloads.
   Record source/candidate SHA-256, exact bounds, counts and the review decision.
5. Back up the database and originals to separate storage and verify the copies.
   Submit the derivative through normal intake, preserving its snapshot date.
   Bind it to the existing render before it can be claimed; do not rewrite the
   original ingestion job or create another card for the same dated source.
6. Verify public WDL metadata and bytes, day/night tiles, and the separately
   generated BlueMap. Retain the old generation until the replacement passes.
7. After replacement verification, move superseded source retention to backup
   storage. Recheck the original's backup SHA-256 and make sure no warp, latest
   completed render source, active job, or direct attachment still needs that
   object. Keep a retirement receipt mapping the old hash to its backup and new
   source. Remove the superseded object from live storage only after those checks.

Live storage holds the approved WDL that generated the published render. Scratch
holds pending captures and disposable processing output; backup storage holds
superseded originals and recovery history. Keeping an original for rollback does
not require leaving another copy on the live volume. Restore a retired source
from its receipt before intentionally rerunning an old ingestion job.

Completed scratch caches can be removed after verifying their canonical source,
backup, publication receipt, and absence of active jobs. Preserve small provenance
receipts. A deferred or unmatched capture is unfinished work, not a disposable
cache: review or resolve it before clearing its recovery state.

The Archive warp matcher can bind a reviewed replacement automatically. A source
without a warp needs an explicit operator replacement binding; the ordinary
location-selection field alone does not identify a render to replace.

2D renders show the surface, including for underground builds. An analysis height
window only helps propose horizontal boundaries; it never cuts vertical WDL data.

## September 10 review

| Snapshot | Original ZIP | Reviewed ZIP | Overworld chunks retained |
| --- | ---: | ---: | ---: |
| Boat Lodge, 2019-09-21 | 867.5 MiB | 1.50 MiB | 440 of 315,186 |
| Boyland rebuild, 2022-12-18 | 818.1 MiB | 2.42 MiB | 729 of 336,144 |
| Mu Megabase | 238.8 MiB | 236.24 MiB | 61,073 of 61,910 |
| Chunk Haven, 2021-05-22 | 371.8 MiB | 43.13 MiB | 14,178 of 116,332 |

The first two extents were explicitly reviewed. Mu removes only 837 distant
Overworld fragments; all 23,257 Nether and 289 End chunks remain byte-identical.
These are reductions in served downloads, not reclaimed storage while the
originals and rollback generations remain preserved. No new Archive downloads
are needed for this workflow.

Chunk Haven's initial 480-block square excluded outer builds. Doubling the sides
to 960 blocks still cut through structures. After further extent review, a
1,376-block square included the nearer edge structures, but a second visual
review identified a farther northern walled complex and eastern pinwheel map art.
The boundary now extends north by 848 blocks and east by 256 blocks, keeping
the west and south edges unchanged: X [27,184, 28,816), Z [88,256, 90,480).
This 1,632 by 2,224-block rectangle includes both additions with surrounding
terrain. All 14,178 retained terrain payloads and 178 entity payloads match
the source exactly, with full vertical height preserved. The existing
dated render is replaced in place, with its original source and generations
preserved. Other ambiguous crops still await extent review.

Download links include the current source SHA-256 in their query string. Only a
matching digest-qualified URL is cached as immutable; unversioned links must
revalidate. A stale digest returns HTTP 409 rather than a different ZIP under
the same URL. This prevents a cached pre-crop download from silently outliving
its replacement. Existing cached unversioned bookmarks may still contain an
older copy; use the current download link from the location or API metadata.

## Camera coordinates belong to the dated render

Boat Lodge's 2018 and 2019 captures occupy different coordinates. The main marker
was explicitly corrected to the 2019 Archive arrival. The older render still
needs to open at its own position.

`scripts/atlas-bluemap-camera.ps1` keeps the catalog anchor when it lies within
that render's bounds. Otherwise it uses the render's own in-bounds Archive arrival
in native dimension coordinates. It does not translate terrain or change catalog
coordinates. Missing or contradictory evidence retains the existing anchor for
manual review; a sparse world's bounding-box midpoint is not a safe substitute.

Both rendering and coordinator validation use this rule. For existing models,
back up their settings/manifests, then use
`scripts/repair-atlas-bluemap-start-positions.ps1 -RenderId <ids>` to repair only
presentation metadata. This avoids repeating relighting or model generation.
The manifest's `CameraAnchor` records the selected coordinates and source;
`LocationCoordinates` retains the catalog coordinates. The historical
`QualityGate.LocationStartExact` field remains a compatibility flag for successful
start-position validation against the selected anchor.
