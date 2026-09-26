# Running the WDL worker

The browser client, API and ingestion worker have separate jobs. A static host
serves the client. The API handles login, metadata and uploads. The Windows worker
reads completed uploads from shared intake storage and runs the pinned renderer.
A static host never needs the database, worker credentials or raw world downloads.

Start with [the installation guide](DEPLOY_FROM_SCRATCH.md) for the complete setup
or [the first-WDL guide](WDL_GETTING_STARTED.md) for one local job. All paths below
are examples; configure them for your machine.

## Worker credential

Generate a worker key while signed in as the account that will run the worker:

```powershell
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $bytes = New-Object byte[] 32
    $random.GetBytes($bytes)
    $key = [Convert]::ToBase64String($bytes)
    $hashBytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key))
    $hash = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
    [Environment]::SetEnvironmentVariable('ATLAS_INGEST_WORKER_KEY', $key, 'User')
    $hash
} finally {
    $random.Dispose()
    $sha.Dispose()
}
```

Set `IngestionWorker__ApiKeySha256` in the API process environment to the printed
hash. Start the worker in a new session so it receives its user environment.
Keep the raw key and service configuration out of Git. A missing or invalid hash
prevents the API from accepting worker requests.

## API configuration

Use the API launcher's private environment for these settings:

```text
Database__Path=C:\AtlasExample\Api\data\atlas.db
JwtSettings__SecretKey=<independent random signing key>
Bootstrap__OwnerPassword=<independent random password for a fresh database>
IngestionWorker__ApiKeySha256=<worker key hash>
IngestionWorker__IntakeRoot=D:\AtlasExample\Ingest\intake
Recovery__Root=B:\AtlasExample\Backups\human-edits
```

Expose the API through HTTPS. Public reads allow cross-origin requests without
cookies; protected writes still require the owner's token or a worker key,
depending on the endpoint. The sample `Cors` configuration does not restrict that
public policy. Keep the database private and verify its backup before an upgrade.

## Publish and configure the worker

```powershell
.\scripts\publish-ingestion-worker.ps1 -OutputDirectory C:\AtlasExample\Ingest\worker-next
```

The Windows x64 package includes its .NET runtime. Copy `worker.example.json` to
a private configuration file and set:

| Setting | Purpose |
| --- | --- |
| `apiBase` | API origin; use HTTPS outside local testing |
| `intakeRoot` | Same completed-upload directory used by the API |
| `workRoot` | Private extraction and render staging |
| `rendererProfile` | Local profile with the renderer version and SHA-256 |
| `publishRoot` | Directory served by the tile host |
| `publicTileRoot` | HTTPS URL prefix for that directory |

Intake, work and publication directories must not overlap. Publication may use a
different volume: the worker copies into private destination staging, verifies
the inventory, then renames it into place. Configure the tile host yourself;
publishing the worker does not install NGINX or create a public route.

Use a dedicated non-administrator account. Give it access to the paths it needs
and keep unrelated credentials out of its environment. Follow the
[ingestion security guide](INGESTION_SECURITY.md) for renderer isolation.

## Check one job

Upload a small WDL through the owner's **From World Download** control. Use a
world with a landmark and coordinates you can independently verify. Then run:

```powershell
C:\AtlasExample\Ingest\worker-next\2b2tAtlas.Ingestor.exe worker `
  --config C:\AtlasExample\Ingest\config\worker.json --once true
```

Check the job result, preserved source hash, tile URL and landmark position.
Only remove `--once true` after that path works. A successful build does not prove
that your renderer, file permissions or tile host are configured correctly.

For collector inputs, the private capture directory is quarantine. The reviewed
inbox importer verifies the source before submitting it to normal ingestion.
Do not assign a location from proximity alone: use the warp identity, date,
dimension, bounds and source context. Resolve `needs-match` jobs in Admin.
See [Archive automation](ARCHIVE_SYNC_AUTOMATION.md) for that separate workflow.

BlueMap runs after primary ingestion. It reads preserved sources and writes a
separate derivative; it must not modify the source ZIP or decide location matches.
Its coordinator and publication checks are described in
[the BlueMap guide](BLUEMAP_PIPELINE.md).

## Static frontend

```powershell
.\scripts\build-namecheap-package.ps1 -ApiBaseUrl https://api.example
```

Extract the ZIP directly into the static host's document root. Keep `.htaccess`
for Apache, or configure the equivalent SPA and MIME rules on another host.
Check the frontend, a direct client route, an API request and a real tile URL.

This public packager builds browser assets only. It does not include the live
site's catalog exports or generate SEO evidence. It rejects SEO arguments until
a configured exporter is supplied. The static package contains no API binary,
database, renderer, WDL or credential.
