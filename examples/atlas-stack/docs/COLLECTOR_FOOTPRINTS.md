# Collector footprints and local review

An Archive warp can land in a museum world containing several unrelated builds.
Following every connected rail, road or underground structure can turn a small
lodge into a regional download. Survey completion proves that the requested chunks
were received; it does not prove that the rectangle belongs to one exhibit.

## Automatic review stops

The collector now assigns a survey budget from the warp/catalog identity. It uses
actual survey bounds, never a possibly merged Atlas location coordinate.

| Identity | Survey chunks | Longest side | Review at expansion |
| --- | ---: | ---: | ---: |
| Lodge | 16,384 | 4,096 blocks | 10 |
| Individual build | 65,536 | 8,192 blocks | 12 |
| Road, highway, tunnel, canal or bridge | 131,072 | 32,768 blocks | 24 |
| Regional save | 262,144 | 16,384 blocks | 16 |
| Along the Axes catalog entry | 262,144 | 16,384 blocks | 5 |

Crossing a size budget or reaching the expansion count records
`needs-footprint-review`, cancels travel, flushes the WDL, and retains its journal
and saved terrain. It does not declare the partial save complete or automatically
retry it. The existing disconnect, writer-drain, disk recovery and coverage gates
still apply. These numbers are conservative operating budgets, not claimed build
boundaries. A legitimate giant build goes through extent review.

A checkpoint preflight checks its verified saved bounds before starting a new WDL
or rebuilding saved NBT evidence. Oversized parents remain untouched in review.
The completion path checks the budget again, including captures restored from an
old checkpoint. A rectangle of at least 16,384 chunks also needs review when at
least 1,024 construction-evidence chunks lie outside the selected component and
that count is at least half the selected component's size. This is an ambiguity
signal, not proof that every excluded chunk belongs to someone else. Batch
promotion applies the same completion check to older captures.

`ADAPTIVE-FOOTPRINT-POLICY` in each new worker log identifies the active policy.
Existing workers load updated wrapper code at their next safe capture boundary;
requesting a reload must never kill a client with an unflushed save.

## Analyze saved data before collecting it again

`SavedFootprintEvidence` reads the preserved Anvil NBT using the same block-evidence
rules as the live collector. An optional inclusive Y range focuses the *analysis*
on the actual warp landing height. The CSV is bound to the source ZIP's SHA-256;
the exporter checks that the source did not change and publishes output only after
the whole read succeeds. Resume always reads all heights.

Build the coverage project with `scripts/build-archive-coverage-mod.ps1`, then run
its `savedFootprintEvidence` Gradle task using that build's verified wrapper (or
an installed matching Gradle):

```powershell
gradle --project-dir tools/AtlasArchiveCoverage savedFootprintEvidence `
  -PfootprintSource=C:/AtlasReview/original.zip `
  -PfootprintOutput=C:/AtlasReview/evidence.csv.gz `
  -PfootprintMinY=53 -PfootprintMaxY=85 --no-daemon
python -m pip install -r scripts/requirements-footprint-review.txt
python scripts/review-wdl-footprint.py `
  --source C:/AtlasReview/original.zip --evidence C:/AtlasReview/evidence.csv.gz `
  --center-x 14195.74 --center-z -4176.85 --name "Example exhibit" `
  --output-dir C:/AtlasReview/proposal
```

Use the actual Archive landing for that dated warp. A location row can merge
similarly named exhibits from different years. The script refuses a landing that
is not in the saved footprint and refuses evidence from a different source ZIP.

The proposal finds a nearby dense construction component, prevents narrow roads
from joining separate dense areas, adds nearby building edges without recursively
following them, then adds eight chunks of context. Thin or disconnected builds
can need manual adjustment; the fallback is explicitly labeled landing context.
Natural underground structures can still resemble player construction. Compare
height windows, inspect the actual terrain, and check separate buildings before
accepting a rectangle. All proposals remain review-required, including those with
no missing saved chunks. A height window never removes vertical world data.

Create a separate full-height candidate from the preserved source:

```powershell
$p = Get-Content C:/AtlasReview/proposal/proposal.json -Raw | ConvertFrom-Json
./scripts/new-wdl-footprint-artifact.ps1 -SourcePath $p.source `
  -DestinationPath C:/AtlasReview/proposal/candidate.zip -Bounds $p.bounds `
  -Dimension Overworld
python scripts/compare-wdl-payloads.py $p.source `
  C:/AtlasReview/proposal/candidate.zip --output C:/AtlasReview/proposal/payloads.json
```

The existing cropper retains full terrain, entities, POI and external chunk payloads
inside the rectangle, compacts region files, and audits exact bounds. Its receipt
links both source and candidate hashes. Payload verification must report zero
`differentNbtPayloads` and zero `rightOnly` for every kind, with every candidate
payload accounted for. Keep originals and receipts. After approving the extent,
use the normal ingestion validation/publication flow for the new artifact; never
overwrite a content-addressed source or delete its existing renders to try a crop.

## Regional source reuse

Two exhibits with the same footprint are candidates for a common regional source,
not automatically duplicates. `compare-wdl-payloads.py` compares actual uncompressed
Anvil payloads, including entities and POI, dimension by dimension. It understands
external chunks and ignores ZIP compression and save-folder names. It does not
ignore NBT differences or merge versions. Matching payloads still require reviewed
source identity and provenance before sharing a snapshot.

In the initial audit, Poker Room and Chunk Haven had the same 116,332 terrain
coordinates and identical construction counts. Their terrain NBT payloads differed
at every coordinate, and their entity inventories also differed. They remain
separate dated originals. Locally cropping either preserved source requires no
Archive download. Automatic cross-date substitution is deliberately not enabled. The existing
covered-world reuse path now also requires the same explicit snapshot date, in
addition to its live dimension, listed neighbor, landing, coverage and hash checks.
Reused captures remain low-confidence and ineligible for automatic publication.

## Validation

The completion policy replay flagged 86 of 1,016 historical adaptive captures;
930 passed those checks. This measures completion checks only, not earlier
expansion stops or guaranteed ownership accuracy. The oversized Boat Lodge,
Boyland and Poker Room cases are regression fixtures. Synthetic tests cover a
road between two buildings, thin bridges, negative coordinates, unsaved holes,
wrong landing coordinates, external chunks and differing snapshots. Java tests
also verify that height filtering leaves full-height resume behavior intact.

Run `scripts/tests/test-archive-footprint-policy.ps1`,
`scripts/tests/test_compare_wdl_payloads.py`, and
`scripts/tests/test_review_wdl_footprint.py` (the last uses the review requirements),
plus the coverage project's normal Gradle checks.

For the operator pause latch, live storage reserve, and review-held recovery behavior, see [collector pause and storage](COLLECTOR_PAUSE_AND_STORAGE.md).
