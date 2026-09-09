# Collector memory and save pacing

The client can receive terrain faster than Archive World Downloader writes it.
In WDL 1.2.0, the asynchronous writer queue is unbounded and holds detached chunk
snapshots. A busy collector can accumulate gigabytes of snapshots even though the
writer itself streams its output to disk. More heap buys time; it does not bound
that backlog.

Coverage 0.11.0 pauses travel when the writer has 1,024 queued tasks, then resumes
at 256 or fewer. Network handling, chunk capture and disk writing continue while
travel waits. Nothing is removed from the writer queue by the coverage mod.
Already requested terrain can still arrive, so the threshold is a pacing trigger,
not a strict maximum. Heap use at 85% also pauses travel, with a 75% resume threshold.

The collector logs `ATLAS_COVER writer-wait pending=... received=...`. These messages
keep the ordinary progress timeout alive but do not extend the total capture
budget. A spectator command every minute avoids the Archive's inactivity timeout.
The WDL integration reads the installed mod's controller/session/writer queue
through reflection; an incompatible installed version holds travel and reports
`probeError` instead of continuing without protection. Standalone coverage without
WDL still works.

`invoke-archive-collector.ps1 -JavaHeapGiB 6` is the default. Size the total budget
for all concurrent clients, native JVM memory, renderers and other host services.
The parameter allows 3–8 GiB per client. Six clients can therefore reserve up to
36 GiB of Java heap at the default; this is not their total process memory.

The game launches with `-XX:+ExitOnOutOfMemoryError`. A fatal OOM can kill an IO
executor while leaving its save future unresolved. Waiting for that future forever
does not preserve additional terrain. After an OOM, recovery requires evidence
that the owning Minecraft JVM has exited; a surviving HeadlessMC launcher or another
worker does not block that check. A live writer remains protected from forced
termination by the regular flush wait.

On restart, the existing capture journal preserves disk files, verifies the parent
checkpoint hashes, merges saved chunk slots, and restores coverage from real NBT.
It does not declare a partial capture complete. RAM-only chunks lost in a fatal
crash must be collected again. The final missing-chunk audit and publication gates
still apply.

For an older client already stuck after OOM, retain its journal, checkpoint,
working save and diagnostic evidence before a targeted restart. Never delete the
partial save or restart unrelated lanes. Stage mod updates for the next safe
capture boundary.

Validation: the Java build includes a slow-disk simulation that drains 400,000
chunks without loss, alongside the coverage, persisted-terrain and sparse-repair
regressions. PowerShell fixtures cover OOM detection, live-writer protection,
launcher survival, worker isolation, recovery replay and resume keepalive.
