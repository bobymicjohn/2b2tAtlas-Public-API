# Test WDL uploads in a browser

This optional Playwright test logs in, uploads a world download and checks the
resulting ingestion jobs. Use a disposable Atlas instance. Each run cancels
existing jobs with the test slug before uploading again.

## Setup

Install Node.js 18 or newer, then run from `examples/atlas-stack/e2e`:

```sh
npm install
npx playwright install chromium
```

Start your local Atlas instance and set these values in PowerShell:

```powershell
$env:ATLAS_BASE_URL = 'http://127.0.0.1:5297'
$env:ATLAS_API_URL = 'http://127.0.0.1:5297'
$env:ATLAS_USER = 'your-test-username'
$env:ATLAS_PASS = 'your-test-password'
$env:WDL_PLUS_Z = 'C:/test-worlds/plus-z-border.zip'
```

The account needs `renders.manage`. Keep credentials in your environment.
The test ZIP is not included in this repository.

Edit `wdl-matrix.ts` to match your fixture and local catalog. The sample expects
a two-dimension border archive attached to location 904; that ID is not created
by this test. Replace it with your own fixture's location ID, or add a separate
case with the expected dimensions and matching result.

## Run

```sh
npm test
npm run test:headed
```

The second command opens a visible browser. Failed tests save a trace and
screenshot. These upload tests are separate from `scripts/test-local.ps1`
because they need a running instance, credentials and a world archive.
