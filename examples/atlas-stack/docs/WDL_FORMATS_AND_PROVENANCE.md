# WDL formats and source attribution

Upload a ZIP through **Admin → Location Management → Add Location → From World Download** (or Bulk Upload), using an account permitted by the deployment’s upload policy. The API accepts resumable chunks; the worker inspects, renders and matches the completed input. BlueMap processes eligible renders independently after 2D publication.

For setup and storage, see [WDL quick start](WDL_GETTING_STARTED.md). For headless acquisition, coverage audits, checkpoint recovery and lane configuration, see [Archive sync](ARCHIVE_SYNC_AUTOMATION.md). Keep mutable collection and rendering on local scratch storage, then verify completed artifacts into the durable serving tier. A museum re-export is not necessarily the original WDL.

## Historical formats expected from 2b2t

2b2t has existed across all of these Java storage eras, and old downloads may have been opened or upgraded by a newer client:

| Likely capture era | Expected chunk storage | Recognized form |
| --- | --- | --- |
| Alpha / early 2b2t | one gzip-NBT file per chunk | two hashed directories ending in `c.<base36-x>.<base36-z>.dat` |
| Beta 1.3–1.1 | McRegion | `region/r.<x>.<z>.mcr` |
| 1.2+ | Anvil | `region/r.<x>.<z>.mca`, including external `.mcc` payloads |
| Upgraded or assembled archive | mixed eras by dimension | each dimension inspected independently; reported as `mixed` |

The chunk reader supports gzip, zlib, raw, and bounded Minecraft lz4-java region payloads. It validates occupied slots against NBT `xPos`/`zPos` and skips bounded corruption while reporting skipped counts. Missing version text is acceptable. A corrupt `level.dat` can fall back to `level.dat_old` or empty cosmetic metadata. Old exports with no level file at all are discovered from valid region/McRegion/Alpha storage; Atlas writes a minimal synthetic `level.dat` only into the private renderer scratch tree and never changes the archived ZIP.

ZIPs may wrap the world in one or several folders. Multiple candidate worlds fail closed; retry with the exact archive-relative **World root** offered by preparation. `__MACOSX`, client-mod metadata, maps, player data, WorldTools reports, and other non-chunk files do not become published content.

## Dimension storage and Archive exports

Canonical layouts recognized automatically are:

- Overworld: world root (`.`) or `dimensions/minecraft/overworld`
- Nether: `DIM-1` or `dimensions/minecraft/the_nether`
- End: `DIM1` or `dimensions/minecraft/the_end`

If both recognized layouts contain occupied chunks for the same dimension, preparation stops and asks for normalization instead of merging them. A single custom identifier whose own tokens explicitly say `overworld`, `surface`, `nether`, `hell`, or `end` can be classified automatically when it does not conflict with canonical storage. An arbitrary custom identifier is never guessed: select its documented original 2b2t dimension in the upload dialog, and Atlas normalizes only the private work copy. Multiple custom roots or contradictory canonical/custom storage always stop for review.

The Archive is a museum server. A historical 2b2t build can be pasted or imported into the museum's Overworld or into a custom exhibit dimension. Once that happens, the save path describes the **museum storage dimension**, not necessarily the build's **original 2b2t dimension**. Chunk NBT, terrain, blocks, biomes, seed fields, and filenames cannot prove the original dimension after such a transform.

For an Archive-derived export, retain this evidence with the job:

1. Archive catalog URL or catalog identifier.
2. Museum warp/room and coordinates.
3. Original WDL/source attribution when known.
4. Explicit original 2b2t dimension supplied by the source custodian or catalog.
5. Original 2b2t coordinates and whether the museum moved the build.

If the export uses a custom museum dimension, ask for a canonical vanilla export preserving the attributed target dimension. Otherwise an operator must normalize a private working copy only after the attribution above is established. Do not publish a guessed dimension or guessed coordinates.

WorldTools' `Dimension Tree.txt` is useful inventory, but its own documentation says it lists every server dimension path, not only dimensions downloaded into that snapshot. Treat it as a hint, then require actual occupied chunk storage and source attribution.

### What Atlas now detects automatically

Atlas reads these files with strict size and collection limits and keeps the result as **untrusted, reviewable evidence** on the ingestion job:

- Archive World Downloader 1.2.0+: `wdl/download.jsonl` (`downloadName`, source address/name/MOTD, and its reported dimension).
- WorldTools captures: `WorldTools/Capture Metadata.md` and `WorldTools/Dimension Tree.txt`.
- `level.dat` / `level.dat_old`: saved player `Pos` and `Dimension`, including the raw namespaced key retained by older WorldTools captures.
- The original uploaded ZIP name. WorldTools' trailing epoch timestamp is stripped, but a historical date embedded in a warp name is retained.

Newer Archive World Downloader deliberately writes custom live worlds into canonical vanilla dimension folders based on dimension type. Its report therefore identifies the downloader and source, but generally cannot recover the Archive's original custom live dimension key. Older WorldTools saves can retain that raw key in player NBT and their dimension inventory. Atlas displays raw identifiers verbatim and never converts an unknown identifier into Overworld, Nether, or End.

The importer models the Archive relationship directly: one immutable WDL has at most one `/warp`; one Atlas
location may own many dated WDL warps. A recognized downloader `downloadName` is preferred. For an older
Archive-attributed WorldTools capture, the original ZIP filename is used after stripping only its capture epoch.

An exact existing warp normally reuses that warp's location even when WDL coordinates differ. Atlas treats an unsuffixed name and an explicit first iteration (`1`, `I`, or `one`) as the same base, while second and later iterations remain distinct. A genuine numbered warp/location iteration conflict is treated as legacy misownership: Atlas creates the distinct iteration and re-homes that unique warp rather than attaching it to an unrelated footprint match. A byte-identical WDL also
recovers its previously reviewed warp by immutable SHA-256 even when the ZIP was renamed or lost its sidecar report. Otherwise Atlas combines
date-insensitive historical-warp identity, location/render names, prior render-footprint overlap, coordinates,
dimension, and Archive provenance. A confident unique match attaches automatically and adds the new dated warp.
A trusted warp with no plausible existing candidate automatically creates the location after rendering succeeds,
then atomically adds the warp and render. Ambiguous candidates, unknown dimensions, duplicate warp destinations,
or contradictory ownership enter `needs-match` with all evidence visible. Optional local Qwen can select only from
the bounded deterministic candidate list at strict confidence thresholds; it cannot invent IDs or override conflicts.

Multiple world roots in one ZIP disable automatic Archive identity unless the operator selects the relevant
**World root**, preventing one capture's report from labeling another capture in the same bundle.

This detects likely Archive warps; it does not prove 2b2t origin. FitMC's tour explains that the Archive preserves builds at original coordinates and uses alternate dimensions for different historical states, but it also describes deliberate restoration and WDL merging. Coordinate overlap, catalog provenance, and source-custodian confirmation remain the standard for historical attribution.

Large releases packaged as SquashFS, ZVCR, tarballs, or loose region trees are outside the authenticated ZIP intake contract. Repackage a single Minecraft save as ZIP without changing its contents, or add a separately reviewed importer; do not bypass the archive validator.

## Upload checklist

1. Prefer the original WDL ZIP over a museum re-export.
2. Use a neutral render name and a stable lowercase slug; include the capture date when known.
3. Put the source URL/catalog identity in **Source / attribution**. `LastPlayed` is only a date suggestion.
4. Leave **Dimension** on Auto for an original canonical WDL. Select a dimension explicitly only when provenance establishes it.
5. Leave **Archive /warp** blank when the downloader report or Archive-attributed ZIP filename is correct. Enter the exact command only as an operator-confirmed override.
6. Leave **World root** blank unless preparation reports multiple candidates.
7. Confirm the drop-time dimension list and nearest-location preview before uploading.
8. Watch **World Download Jobs**. Most recognized Archive WDLs should complete unattended. A `needs-match` job is
   an actual identity/dimension/warp conflict or low-confidence exception; Resolve can attach an existing location,
   create a new one, and review the single `/warp` before rendering continues.
9. After completion, inspect day and night at maximum detail, negative coordinates when present, the center/extent, overlap with known terrain, and the linked location before treating it as public-quality evidence.

## Research references

- [FitMC: The Man That Saved 2b2t History](https://www.youtube.com/watch?v=35ucFNtMx7c) - public Archive history, original-coordinate placement, alternate time-slice dimensions, WDL merging, and public-location policy.
- [Terbin on GitHub](https://github.com/terbin) - Terbin's public preservation and world tooling.
- [Archive World Downloader](https://github.com/thearchive-world/archive-world-downloader) and [1.2.0 release](https://github.com/thearchive-world/archive-world-downloader/releases/tag/1.2.0) - current downloader implementation, supported versions, and the 1.21.10 incomplete-save correction.
- [Official WDL folder reference](https://wdl.docs.thearchive.world/reference/wdl-folder/) - `wdl/download.jsonl` report contract.
- [Terbin's WorldTools fork](https://github.com/terbin/WorldTools) - capture metadata, player NBT, raw dimension paths, and historical-save fixes.
- [Archive Paper](https://github.com/thearchive-world/archive-paper) - the Archive's tolerance and upgrade work for historical NBT/world data.

- [WorldTools README](https://github.com/Avanatiker/WorldTools) — captured content, metadata files, and the warning that Dimension Tree lists all server dimensions.
- [World Mirror output format](https://github.com/billstark001/world-mirror) — a current example of `dimensions/<namespace>/<path>` storage for vanilla and custom dimensions.
- [Archive+](https://2b2tarchive.org/) — public catalog/download surface for 2b2t WDLs.
- [The Archive introduction](https://www.reddit.com/r/2b2t/comments/dz13m5) — museum restoration and compositing context.
- [2b2tplace 1m release](https://github.com/2b2tplace/1m_release/blob/main/README.md) — a large preservation release that is not a single ordinary playable ZIP and therefore needs a separate import path.
