# Storage and recovery

Use private fast storage for intake, active Minecraft saves, extraction and rendering.
Publish only verified completed outputs to the public asset origin. Retain canonical
WDL objects separately from disposable working copies. Put versioned backups on another
storage device or host, with credentials unavailable to application contributors.

Configure `Database__Path` and `Recovery__Root` explicitly in a deployed API. The server
uses the same database path for pre-edit snapshots. Human mutations fail closed when
the recovery journal, free-space reserve or SQLite integrity check fails. These snapshots
complement scheduled database and preservation-object backups; they are not a replacement.

Before granting editor access, make a named baseline outside normal rotation. Restore
it into a disposable database, verify integrity and record counts, then exercise targeted
record recovery. Keep the commands and backup credentials in a private operator runbook.
Do not post production backup metadata in this source repository.

- [Database backup](../scripts/backup-atlas-db.ps1)
- [Preservation backup](../scripts/backup-atlas-preservation.ps1)
- [Targeted record restore](../scripts/restore-atlas-record.py)
- [Highway history and restore](HIGHWAY_CONTRIBUTORS.md)

The sample operation scripts describe a Windows installation with distinct work,
publication and backup roots. Review paths and retention before scheduling them. Do not
run a bulk database restore while the API is writing; use online snapshots for backups.
