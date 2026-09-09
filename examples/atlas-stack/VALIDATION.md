# Export validation

Validated on Windows with the SDK from `global.json`:

- Release solution build: zero warnings/errors; server publish succeeds.
- 430 .NET tests pass, including a new local bootstrap regression: required password,
  exactly one owner, no automatic collaborators, and no reactivation/password reset on restart.
- Seven PowerShell fixture suites pass for recovery, checkpoints, retained partial saves,
  survey handoff, lane routing/refill and BlueMap coordination.
- Python capture recovery, saved-terrain merge and media-research suites pass.
- 28 map coordinate/zoom checks and 26 highway editor coordinate checks pass.
- Java coverage companion builds; saved-terrain, coverage and sparse-repair regressions pass.
- Published app tested against a fresh local database: generated owner login, authenticated
  location creation, anonymous mutation denied with 401, and private routes excluded from
  public OpenAPI. Browser directory/admin/About checks pass at desktop/mobile sizes with
  no page errors, mobile horizontal overflow or production Atlas requests.

These tests use synthetic fixtures and local data. This export did not log Minecraft
accounts in, collect from The Archive, run a real uNmINeD/BlueMap render, install scheduled
tasks, or exercise a remote backup destination. Configure and test those integrations
on your own host before running a production queue.

Before publication, the staged source is checked for runtime/private material and
scanned with Gitleaks. No historical commits from the private application are imported.
The public CI repeats the application and collector fixture checks on a hosted runner.
