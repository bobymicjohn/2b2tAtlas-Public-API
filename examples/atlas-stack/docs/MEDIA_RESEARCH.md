# Media research

Find the footage, keep the receipts. This runner matches cached YouTube captions
and catalog titles, public timeline event indexes, and historical blog posts
against the Atlas catalog. It produces leads for review. It never writes to Atlas.

## Run it

Python 3.10+ runs the matcher and reference fetchers using the standard library.
Install `yt-dlp` only when refreshing YouTube metadata/captions:

```powershell
python -m pip install yt-dlp
python scripts/run-media-research.py --state-dir C:\AtlasExample\Research\media --refresh-youtube
```

The example config lists the researched channels, including Shorts feeds and
non-English caption preferences, plus the 2b2t Blog and two public timelines.
Copy `scripts/media-research.example.json` outside the checkout and pass `--config`
to change the sources. Channels are a starting set, not a claim of full coverage.

Each refresh rotates through two configured feeds, with at most ten new caption
attempts per feed and five minutes per feed. Increase these with `--max-feeds`
(maximum 10) and `--max-videos` (maximum 50). Existing normalized captions do not
consume that budget. Failed/captionless attempts cool down for seven days so they
cannot keep older uploads out of reach. The underlying caption command's
`--video-id` or `--refresh` explicitly retries an item. No audio/video is downloaded.

Blog and timeline snapshots refresh at most once per 24 hours. Requests have
30-second timeouts and 16 MiB response limits. Feed truncation, access challenges,
and changed markup become visible gaps; a failed refresh keeps the previous
snapshot. Do not bypass a source's access challenge.

Reprocess cached inputs without network access:

```powershell
python scripts/run-media-research.py --state-dir C:\AtlasExample\Research\media --offline
```

`--youtube-root` reuses an existing transcript directory directly. `--api-base`
selects a development API; the default is the public Atlas API. Attachment reads
are paginated. Keep the state directory outside the checkout and on local storage.
The process uses a lock to reject overlapping runs. If a process was killed,
confirm its PID from `run.lock` is no longer running before removing that file.

## Outputs and review

- `review-queue.json`: stable candidate IDs, source links, exact caption moments
  or article excerpts, live-link deduplication, coverage and fetch gaps.
- `last-run.md`: compact counts and missing-caption coverage.
- `sources/`: last successful historical source snapshots and content hashes.
- `youtube-matches.json`: reusable matching results keyed by the catalog,
  transcript contents and matcher source. Unchanged inputs skip the expensive pass.
- `decisions.json`: optional reviewer-owned decisions; the runner never edits it.

Record decisions by copying the candidate's ID and evidence hash:

```json
{
  "<candidate id>": {
    "evidenceHash": "<evidenceHash from review-queue.json>",
    "status": "rejected",
    "reason": "This is another iteration of the base."
  }
}
```

Statuses are `pending`, `accepted`, `rejected`, and `deferred`. Changed evidence
reopens a decision. Accepted means reviewed, not published. Already-linked items
remain visible for auditing but are excluded from pending counts. YouTube identity
is case-sensitive and independent of share/Shorts/chapter URL formatting.

A timeline title proves only that its author included an event. Open the event
before accepting its dates or historical claims. An article mention is a lead,
not proof of ownership. Short/generic and duplicate names are excluded; inspect
`skippedLocations` for manual work. The name matcher is conservative and does not
translate foreign captions or resolve every alias. Review the full source before
expanding a description, and retain contradictory accounts as attributed claims.

Publish reviewed links through normal audited admin edits or a reviewed media
manifest PR. The checked-in manifests live in `2b2tAtlas.Server/Data`; see
[YouTube research](YOUTUBE_RESEARCH_PIPELINE.md) for description-review tooling.
Never add the raw research cache or a generated queue to a source PR.

Exit 0 means configured inputs were processed; it does not mean every relevant
video has captions or every historical source was discovered. Exit 2 means a
partial run with gaps. Other failures leave the previous queue intact. Read the
timestamp and coverage before treating any result as current.

## example host and BlackBrain

The reference deployment keeps state at `C:\AtlasExample\Research\media` and reuses
`C:\AtlasExample\Research\2b2t-youtube`. Run from the Atlas checkout:

```powershell
python scripts/run-media-research.py `
  --state-dir C:\AtlasExample\Research\media `
  --youtube-root C:\AtlasExample\Research\2b2t-youtube `
  --api-base http://127.0.0.1:5297 `
  --digest-path C:\Source\OptionalAgent\docs\ATLAS_MEDIA_RESEARCH_STATUS.md
```

Add `--refresh-youtube` when collecting new captions. The digest destination is
already in BlackBrain's configured documentation directories. The digest contains
counts and gaps, not unreviewed source prose. After a run, BlackBrain's existing
`reindex_docs(force=false)` can update its hash-based index; `search_docs` retrieves
the report and `atlas_search` checks public location records through Atlas MCP.
Reindexing uses BlackBrain's normal approved chat flow; its direct HTTP route
rejects mutating tools. The runner does not bypass that boundary or claim to have
reindexed automatically. No BlackBrain restart or new elevated endpoint is needed.

This is an on-demand batch, not a newly scheduled service. It uses CPU and small
metadata reads, so it does not load a model or compete with WDL rendering for GPU
memory. BlackBrain/Ollama outages do not prevent matching. Model-written history
and automatic publication are deliberately outside this workflow.

## Checks

```powershell
python scripts/test-media-research.py
python scripts/extract-youtube-location-candidates.py --self-test
```

Run once against current inputs, then once with `--offline`. The second run should
report `youtubeMatchCacheReused: true` and retain candidate IDs and review state.
