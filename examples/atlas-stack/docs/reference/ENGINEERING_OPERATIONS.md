# Building and releasing Atlas

Use the SDK selected by `global.json`. The normal build needs .NET; the separate
map tests need Node.js, and collector fixtures also use Python and PowerShell.
The Fabric companion needs Java 21. Run checks locally; GitHub Actions is disabled.

## Check a change

From `examples/atlas-stack`:

```powershell
dotnet restore .\2b2tAtlas.sln
dotnet build .\2b2tAtlas.sln -c Release --no-restore
dotnet test .\2b2tAtlas.sln -c Release --no-build
dotnet list .\2b2tAtlas.sln package --vulnerable --include-transitive
```

Run the relevant script fixtures too. [Contributing](../../CONTRIBUTING.md)
lists the local checks. A successful .NET test run says nothing about a Python,
PowerShell or JavaScript test that was never run. Use synthetic worlds and a
temporary database; keep real queues and credentials out of test setup.

Map changes need checks at both sides of a tile boundary and at negative
coordinates. Collector changes need interruption and resume tests. Database
changes need both a fresh database and an existing schema. Keep a regression
test for any bug you fix.

## Build packages

| Output | Script |
| --- | --- |
| Static browser client | `scripts/build-namecheap-package.ps1` |
| Self-contained API | `scripts/publish-atlas-api.ps1` |
| Self-contained ingestion worker | `scripts/publish-ingestion-worker.ps1` |
| Verified SQLite backup | `scripts/backup-atlas-db.ps1` |

Build into a new directory. Keep the previous package and use the same tested
source revision for the client, API and worker.

The public example's static packager builds browser assets and writes public
client settings. It does not recreate the live site's catalog exports or SEO
pages. It rejects SEO arguments rather than quietly claiming those files were
built. The retained entity generators and SEO scripts need installation-specific
configuration before they can be used together.

## Before a deployment

Read the [installation guide](../DEPLOY_FROM_SCRATCH.md) and check the target
paths. Back up the database, verify the backup, and retain the current binaries.
Replace the ingestion worker between jobs. Publishing files into a staging
directory is a build step; it does not prove the running service changed.

After cutover, check anonymous reads, login, CORS, a real tile, optional 3D links
and the configured worker connection. Inspect the running version and logs.
A local synthetic test cannot certify your Minecraft login, remote backup store,
renderer installation or public proxy. Record those checks separately.

## Rollback

Restore the previous frontend or binary package while keeping data directories
in place. Restore the database only when a data or schema problem requires it,
using a verified backup. Preserve job receipts and partial captures. For a bad
render, remove its public association and publish a corrected generation; do
not overwrite a tile tree that browsers may still have cached.
