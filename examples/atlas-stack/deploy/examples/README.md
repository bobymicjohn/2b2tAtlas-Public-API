# Host launchers

Copy these launchers outside the checkout and adjust their parameters for the
service account and paths you actually use. They are intentionally small: load a
private environment file, select the right working directory, run the binary.
Use Task Scheduler or your service manager to own that process. Do not add a
second supervisor around an already supervised worker.

- `start-atlas-api.ps1`: default copy location `C:\AtlasExample\Api`.
- `start-atlas-api.vbs`: optional hidden launcher expected by the example host
  watchdog; copy it beside the API PowerShell launcher.
- `start-atlas-worker.ps1`: default copy location `C:\AtlasExample\Ingest`. Honors the
  `pause-worker` maintenance marker used by the example host watchdog.
- Create separate private `config\secrets.ps1` files containing `$env:...`
  assignments. The API needs the JWT key and worker-key **hash**; the worker needs
  the raw `ATLAS_INGEST_WORKER_KEY`. Restrict each file to its service identity and
  the host administrator. Never copy it back into this directory.

The complete setting list is in
[the deployment guide](../../docs/DEPLOY_FROM_SCRATCH.md). A launcher is not a
permission setup, reverse proxy or backup job. Configure those before public use.
The scripts fail if a binary/config/environment file is missing. Run them once in
a terminal to verify startup before registering a hidden, non-elevated task.
