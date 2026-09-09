# YouTube research transcript pipeline

For the combined caption, timeline and article batch, start with
[Media research](MEDIA_RESEARCH.md). This page covers the individual tools and
publication review.

Atlas can download caption tracks and metadata without downloading video or audio. The normalized output retains timestamps and source URLs so group/build claims remain auditable.

```powershell
python -m pip install --user yt-dlp
python scripts/sync-youtube-research-transcripts.py `
  --channel "https://www.youtube.com/@FitMC/videos" `
  --slug fitmc `
  --max-videos 25
```

The default output root is `C:\AtlasExample\Research\2b2t-youtube`. Each channel receives:

- `catalog.json`: merged video, Shorts, and stream catalogs, keyed by video ID;
- `raw/`: original JSON3 caption tracks and selected yt-dlp metadata;
- `normalized/*.json`: source metadata plus timestamped transcript segments;
- `normalized/*.txt`: human- and RAG-friendly timestamped text;
- `last-run.json`: resumable run report, including unavailable captions.
- `runs/*.json`: dated run reports, so checking another feed does not erase the previous caption-gap inventory.

Use repeated `--video-id ID` arguments for a reviewed priority set. Existing normalized transcripts are skipped unless `--refresh` is supplied.

YouTube puts uploads, Shorts, and archived streams on separate feeds. Check all
three using the same slug. Run feeds for a given slug sequentially; the catalog
is a local JSON file, not a concurrent database. `--catalog-only` inventories a
feed without fetching captions. A slug cannot be reused for a different channel.

```powershell
python -X utf8 scripts/sync-youtube-research-transcripts.py `
  --channel "https://www.youtube.com/@fastvincent1/shorts" `
  --slug fastvincent1 --max-videos 0

python -X utf8 scripts/sync-youtube-research-transcripts.py `
  --channel "https://www.youtube.com/channel/UCLiQ--EXgzUV03q5jKQYWcw/videos" `
  --slug oniya --video-id GIPCPnOWHFo `
  --caption-language ja-orig --caption-language ja --caption-language en
```

The default caption preferences are `en-orig`, then `en`. Repeat
`--caption-language` to request native tracks such as `es-orig`, `es`, `ru-orig`,
or `ru`. Normalized records retain the chosen language and original text. Title
selection recognizes full-width text such as `２ｂ２ｔ`. Native names still require
language-aware review; the location matcher does not translate them. An empty or
unavailable Shorts feed is a coverage gap, not proof that a creator never made
Shorts.

## Evidence rules

After a sync, run `python scripts/extract-youtube-attribution-candidates.py` to
create timestamped JSON and Markdown review queues under
`C:\AtlasExample\Research\2b2t-youtube`. This step is intentionally non-mutating:
caption matches are research leads and must be reviewed and corroborated before
an Atlas relationship is added.

To match captions to the current location catalog, run:

```powershell
py -3.10 scripts/extract-youtube-location-candidates.py
```

This reads the live local `api/locations` response and writes
`location-candidates.json` plus a readable Markdown queue. Duplicate location
names, short names, and generic names are excluded. One-word names require
explicit entity framing, and each accepted hit retains its timestamp and nearby
caption context. The JSON marks whether the video is already linked and assigns a
review priority; it never modifies the database.

Run `py -3.10 scripts/extract-youtube-location-candidates.py --self-test` after
changing the matching rules. Use `--include-mentions` only for exploratory work;
the default queue requires title or nearby historical/build language.

A transcript mention is research evidence, not automatic ownership. Enrichment may propose an association only when the language explicitly identifies a builder, founder, owner, maintainer, or group project. Every proposal must retain the video ID, URL, and timestamp. Ambiguous references such as “Imperium” must not be silently mapped to The Imperials or The Emperium.

YouTube can begin returning a bot challenge during a large caption run. The sync
is resumable: successfully normalized records remain usable, and a later rerun
skips them. Do not bypass a challenge by weakening provenance or substituting an
unverified transcript source.

## Publishing reviewed material

Add reviewed videos to `2b2tAtlas.Server/Data/youtube-location-media.json`.
The startup seeder checks location ID and name and deduplicates by YouTube video
ID within each location, including primary video links and timestamp variants.
Use chapter timestamps for multi-base tours and distinguish Archive visits from
footage recorded on 2b2t. Music-only flyovers can be useful attachments, but do not
invent a transcript or infer historical facts from the title.

Descriptions use a separate reviewed manifest. Each row has `locationId`,
`locationName`, `expectedDescription` (including an explicit null where applicable),
`description`, and timestamped `sources`. See
`research/YOUTUBE_DESCRIPTION_REVIEW_2026-09-08.json` for a completed batch.

```powershell
# Validate against current data without writing anything.
python -X utf8 scripts/apply-reviewed-location-descriptions.py `
  --database C:\AtlasExample\Api\data\atlas.db `
  --manifest docs/research/YOUTUBE_DESCRIPTION_REVIEW_2026-09-08.json

# Apply an operator-reviewed batch, with a verified online database backup.
python -X utf8 scripts/apply-reviewed-location-descriptions.py `
  --database C:\AtlasExample\Api\data\atlas.db `
  --manifest docs/research/YOUTUBE_DESCRIPTION_REVIEW_2026-09-08.json `
  --apply --backup-dir B:\AtlasExample\Backups
```

The updater compares the exact old text under a write lock. A changed name or
description blocks the batch. It updates only description and modification time,
and records before/after text, sources, and backup hashes in `AuditLogs` under
`location.description.research`. Rerunning an applied batch does nothing. For a
targeted rollback, prepare a reverse manifest with the published description as
`expectedDescription` and the previous text as the replacement; restoring an empty
description requires the existing record-restore workflow instead. Avoid restoring
the entire live database over unrelated changes made since the backup.

After deployment, compare local and public API responses and rebuild the static
SEO package. API-backed pages update immediately; the Namecheap entity pages need
the generated package uploaded separately.

## Attachment cards and channel profiles

`AttachmentCard.razor` renders the location gallery. YouTube cards have a red top
border, a thumbnail, and the creator's linked avatar. Chapter badges show the
link's start time, not the video's duration. Other cards identify images, wiki
pages, documents, archives, and ordinary links. Captions and credits stay compact;
their full text remains available in tooltips and the attachment's Details link.

The client embeds `2b2tAtlas.Client/Data/youtube-creators.json`. Its 19 profiles
were checked against public channel/video metadata on September 8, 2026. Match a
video to its actual uploader's channel ID, not someone appearing in the video.
The directory retains the channel URL, verified video IDs, and the original
avatar URL. Small profile images are bundled under `wwwroot/Images/creators`, so
opening a location does not require channel lookups. An unknown uploader keeps
its existing attribution; a failed avatar falls back to an initial.

To add a creator, verify the channel ID and video ownership from YouTube metadata,
add the profile and a small square avatar, then rebuild the client. Preserve
case in video IDs. Names are only an exact-match fallback; title fragments are
not identity evidence. Watch, Shorts, embed, live, and short links are supported.

The Namecheap packager versions the scoped stylesheet URL by its content hash.
It also updates the shell's asset-manifest hash and removes stale precompressed
copies of files it rewrites; Apache handles compression for those files.
