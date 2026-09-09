# WDL ingestion E2E harness (Playwright)

Drives the real upload flow end-to-end — logs in, cleans up prior jobs for the slug, uploads a
WDL through the **Add Location from World Download** dialog, and verifies the resulting ingestion
jobs (dimensions, attached location, status) via the API. This is the automated version of the
manual flow validated on 2026-08-12.

## Requirements
- **Node 18+** (the workspace default `node` is v12 — too old; use `nvm`/`fnm` or a newer install).
- Chromium via Playwright.

## Setup
```bash
cd 2b2tAtlas.New/e2e
npm install
npx playwright install chromium
```

## Credentials & config (never hard-coded)
Set these in your shell (PowerShell shown); the password is only ever read from the environment.
```powershell
$env:ATLAS_USER = 'your-archivist-username'   # needs renders.manage
$env:ATLAS_PASS = 'your-password'
# optional overrides:
$env:ATLAS_BASE_URL = 'https://atlas.example'          # default
$env:ATLAS_API_URL  = 'http://127.0.0.1:5297'  # default
$env:WDL_DIR = 'C:/.../discord-border-wdls'            # base dir for the matrix files
$env:HEADED = '1'                                      # watch it run
```
> Running against **production** creates real jobs/renders. For repeated experimentation, point
> `ATLAS_BASE_URL`/`ATLAS_API_URL` at a local/dev instance instead.

## Run
```bash
npm test            # headless
npm run test:headed # watch the browser
```

## Add more WDL types
Edit `wdl-matrix.ts` and add a `WdlCase` (file path, slug, optional `attachLocation`, expected
`dimensions`/`attachedTo`/`allQueued`). Each case cancels its slug's prior jobs first, so re-runs
are idempotent.
