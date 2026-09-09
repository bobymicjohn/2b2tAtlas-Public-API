# Recovery storage

A collector can be healthy enough to retry but unable to recover its saved
world when staging space falls below the reserve. Check the current wrapper's
stderr before restarting anything. Keep the space guard and completion checks;
free staging space instead of reconnecting over an unrecovered capture.

Recovery keeps its input saves and parent seeds. The final ZIP produced inside
an attempt is renamed to `partial-wdl.zip`, avoiding a second full copy of that
output. External seeds and preserved ZIPs are copied, never moved.

The capture journal records the verified preservation directory before packing
starts. On retry, its receipt, capture identity, lengths and fresh file hashes
must match before reuse. A packing failure therefore does not trigger another
full preservation copy. Invalid snapshots remain held for inspection.

An interrupted downloader may have written regions without reaching its final
`level.dat` and report flush. Private recovery retains that terrain without
inventing world metadata, labels its report interrupted, and rebuilds coverage
from persisted chunks. The completed continuation must supply real metadata;
the full publication audit remains in place.
Region files can also lack the trailing sector padding normally written on
close. Recovery verifies every referenced payload ends within the original
file, then pads the final sector in its output copy. Sector overlap, encoding
and bounds checks still apply. It never pads a missing chunk payload into
existence or modifies the source file.

During the potentially long `/atlascover resume` wait, the wrapper reasserts
spectator mode once per minute to keep the server session active without moving
away from the verified landing. This does not extend the 900-second resume
deadline or count as coverage progress. A server disconnect ends the wait
promptly and retains its network failure reason for the bounded retry path.

## Old repeated preservation copies

`scripts/compact-interrupted-captures.ps1` is an on-demand Windows maintenance
tool for immutable copies under a private `interrupted` directory. It preserves
every path and receipt while replacing byte-identical files with NTFS hardlinks.
It does not operate on live saves, published WDLs, or backups. Hardlinks share
storage, so these paths must remain immutable and are not independent backups.

Supply an inventory with this shape, grouping receipt entries by SHA-256 and
length. Paths must identify files in timestamp/GUID preservation directories:

```json
{
  "root": "D:\\AtlasExample\\DeferredCaptures\\interrupted",
  "duplicates": [
    {"bytes": 1234567, "sha256": "<64 hexadecimal characters>",
     "paths": ["<first preserved file>", "<identical preserved file>"]}
  ]
}
```

Run without `-Apply` to validate paths and count candidates. With `-Apply`, each
distinct pair is freshly hashed under handles that reject writers. Files below
1 MiB are left alone. Reparse points and paths outside the specified root are
rejected before any replacement. The replacement keeps the original as a
temporary backup until its new contents pass verification. An interrupted
operation may leave `.atlas-original-*` evidence; inspect it before cleanup.

```powershell
.\scripts\compact-interrupted-captures.ps1 `
  -InventoryPath C:\AtlasExample\recovery\duplicates.json `
  -InterruptedRoot D:\AtlasExample\DeferredCaptures\interrupted `
  -AuditPath C:\AtlasExample\recovery\compaction.jsonl `
  -Apply
```

Use a new audit filename per run. Run only one compaction at a time. A replay
recognizes existing hardlinks and does not claim to reclaim their space twice.
Keep the audit with the original inventory. Allow stalled workers to recover
through their existing retry cycle, then verify restored chunk counts and new
progress in their current capture logs. A running wrapper may still be packing
or verifying a recovery ZIP; it does not by itself prove active collection.
