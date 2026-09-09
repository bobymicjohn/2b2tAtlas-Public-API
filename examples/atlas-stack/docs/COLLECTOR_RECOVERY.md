# Collector progress and recovery

Each lane owns a queue entry, a Minecraft profile, a journal and its save directory.
Never run two collectors against one profile or reassign an active capture. A supervisor
restart should reattach to live wrappers; changing lane budgets should apply at the
next saved-world boundary.

The coverage companion checkpoints terrain and observed voids separately. Recovery
inspects persisted Anvil/NBT data, unions it with retained parent captures, and verifies
the merged result before reconnecting. Cached recovery ZIPs are reused only after hash
verification. An interrupted ZIP is not proof that its saved region files were lost.
Chunks existing only in RAM at a hard crash may still need acquisition again.

Fast-lane deferral retains a partial WDL and checkpoint for a long worker. In all-long
mode, every lane uses the long survey budget and can borrow queued work at a safe boundary.
An expanding survey's current target is not a final completion percentage. Timeouts and
uncertain footprints become review holds, not successful captures.

The final missing-chunk repair requests missing terrain, rather than redownloading the
entire footprint. It still runs the full coverage audit afterward. Preserve unique
source bytes, active seeds, checkpoints and provenance when reclaiming scratch space.
Deduplicate only after hashing actual files; receipt-only size estimates are insufficient.
See [Recovery storage](RECOVERY_STORAGE.md) for the preservation-copy compactor
and the recovery ZIP storage behavior.

Relevant implementations:

- [Recovery wrapper](../scripts/archive-capture-recovery.ps1)
- [Persisted terrain recovery](../scripts/archive_capture_recovery.py)
- [Saved-terrain union](../scripts/archive_capture_resume.py)
- [Survey handoff](../scripts/archive-survey-handoff.ps1)
- [Lane policy](../scripts/archive-collector-lane-policy.ps1)
- [Peer refill](../scripts/archive-fast-lane-refill.ps1)
- [Coverage companion](../tools/AtlasArchiveCoverage/README.md)

Disk guards intentionally prevent unsafe new work. Retrying the same out-of-space
recovery does not free space. Keep monitoring separate from cleanup, provide sufficient
scratch capacity, and never lower completeness checks to make a job appear finished.
