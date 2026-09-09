# Group enrichment rerun

Atlas separates source collection, deterministic identity checks, model-written prose, and moderation.
This prevents a wiki mention, shared WDL footprint, or similar base name from becoming published ownership.

## Daily evidence refresh

`scripts/invoke-atlas-group-evidence-refresh.ps1` reads the live Atlas database without modifying it,
audits all main-namespace 2b2t Wiki group categories, pins MediaWiki revision IDs, and atomically replaces:

`C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json`

Register the optional 05:25 daily refresh from an elevated PowerShell (ten minutes after the separate wiki
metadata report, avoiding simultaneous MediaWiki sweeps):

```powershell
scripts\register-atlas-group-evidence-task.ps1
```

The full report history is retained under `C:\AtlasExample\Api\data\enrichment\history`. The AI Enrichment
status card reports whether the runtime index is present, fresh, and how many articles it contains.

## After a collector batch or the completed Archive crawl

1. Run the evidence refresh (or wait for the daily task).
2. Open Admin > AI Enrichment and confirm **Group evidence** is available.
3. Leave **Only locations missing wiki/description** enabled. The server also includes locations with a
   new explicit group/build candidate even when their wiki and description are already populated.
4. Run batches. Revision-pinned evidence-backed locations are evaluated first, followed by newer Archive
   locations.
5. Review Pending suggestions. Exact infobox base evidence may be auto-applied when enabled, but stays
   in the queue until a moderator keeps or reverts it. Title and section evidence are always Pending.
6. Use **Discover new groups** to draft Pending group records only for unrepresented articles that explicitly
   name an existing Atlas build. Applying a proposal creates only the reviewed group identity. Refresh the
   evidence index and run enrichment again to route each candidate build through the normal location-level
   attribution policy. This prevents one loose article section from binding every mentioned location at once.

Application is additive: a group suggestion can add a missing link but cannot delete or replace a human
attribution. Reverting an auto-applied suggestion removes only links introduced by that revision.
Locations with an open Pending/Applied AI revision are excluded before each batch limit is applied, so
repeated runs advance through the corpus instead of repeatedly skipping the same newest rows.
Within the eligible set, revision-pinned locations with missing metadata run before speculative wiki searches,
including locations whose reviewed group link already exists. Existing links are supplied to the model only as
pinned drafting context and are filtered from the additive write set, so they cannot be duplicated. This keeps
known attribution and thin-page work from being delayed by a recent location with no matching article.

## SEO publication after group changes

No separate manual export is required after a reviewed group edit. The daily 06:30 `2b2t Atlas SEO Package`
task fingerprints the public group list and every `/api/groups/{id}` detail payload, including attributed
locations and highways. A metadata-only or relationship-only change therefore creates a fresh pending
Namecheap ZIP and Pushover notification even when no location record changed. The package contains canonical
group HTML, `Organization` plus build/highway `ItemList` JSON-LD, schema-v2 `entities/groups.jsonl`, sitemap
`lastmod`, `llms.txt` discovery, and schema-v5 combined dataset metadata (including the media catalog). The task does not upload; the owner
extracts the pending ZIP into cPanel `public_html`. Package validation must pass for every group graph before
the ZIP is written. Canonical aliases reviewed in `GroupSeeder` are exposed as visible "Also known as" text,
JSONL `aliases`, and Schema.org `Organization.alternateName`; the package gate requires those representations
to remain identical. Both the direct package builder and the scheduled wrapper require the revision-pinned
group evidence index and fail before creating output when it is absent. This prevents a manual metadata-only
rebuild from silently dropping the reviewed group/build citations already exposed by location HTML, JSONL,
JSON-LD, and the dataset catalog.

## Evidence policy encoded in the pipeline

- One Archive WDL/warp and one Atlas location are not the same concept as group ownership.
- Visits, residence, griefs, alliances, article links, coordinate proximity, render overlap, and museum
  terrain are not builder evidence.
- Numbered iterations remain distinct. Roman/Arabic trailing numerals may be equivalent; a missing numeral
  is never silently dropped.
- The Imperials, The Emperium, and Imperator's Group are separate identities.
- Exact group/build candidates come only from an Infobox group base list, a building-group article whose
  title equals the location, or an explicitly labelled base/build/project/outpost/lodge section.
- Inside a labelled section, only the declared list subject, a concrete project heading, or an explicit
  `Main Article` target is eligible. Links in comparisons, memorial descriptions, builder/member lists,
  former-member history, and explanatory prose are retained as context but never proposed as ownership.
- Only an explicit infobox base declaration can meet the strict auto-apply threshold. Group-title identity
  and section evidence are review-only because a same-named Atlas location may represent the group rather
  than one of its builds.
- Every proposal retains the public source URL, MediaWiki revision ID, evidence type, and confidence.
- Unknown wiki groups are surfaced in the audit report. Candidates tied to an Atlas build can be drafted into
  the moderation queue, but are never auto-created; identity, classification, public links, logo license,
  historical claims, and every proposed build require approval. Unverified outbound website and Discord URLs
  are never copied into a discovery proposal. Known false group-category pages such as
  `Omega City` are denied before proposal generation.

## Regression benchmark

Every refresh compares deterministic suggestions with the existing reviewed `LocationGroups` corpus. The
report records candidate agreement, strong-candidate agreement, reviewed-link coverage, new candidates, and
reviewed links not rediscovered. Coverage is intentionally not expected to reach 100% because Archive
provenance, Reddit, videos, and operator research establish relationships absent from group wiki pages.

After a reviewed enrichment batch, recompute the descriptive/source/render baseline in
`docs/research/SEO_ENRICHMENT_GAPS.md`. That ledger prioritizes thin group-linked location pages but is not
ownership evidence and never relaxes the moderation or source requirements above.
