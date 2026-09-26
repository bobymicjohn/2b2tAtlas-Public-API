# Local validation

Checked on Windows on September 25, 2026, using .NET SDK 10.0.401, Python 3.12,
Node.js 24, PowerShell 7 and Java 21. These results describe this source review,
not a running deployment.

- All 436 .NET tests passed. The new pagination regression first failed against
  the old query, then passed after unassigned warps were filtered before paging.
- All 35 offline Python, PowerShell and JavaScript suites passed: capture/recovery,
  handoff, storage, metadata, map coordinates and controls.
- The changed survey-handoff and package-evidence checks also passed under
  Windows PowerShell 5.1.
- All three standalone API clients passed four fixture scenarios each: optional
  fields, multiple pages, API failure and an unknown location.
- The Java collector built. Checks included 12,000 differential coverage cases,
  1,000 sparse-repair trials, terrain parsing/corruption cases and draining
  400,000 chunks without loss.
- The API and worker published as self-contained Windows packages. The worker's
  help command ran from its package.
- The static package built with the requested API URL. Its ZIP has the expected
  root files, including `.htaccess`, and contains no database or WDL.
- The published API passed 15 HTTP checks against a disposable database: public
  reads, login, authenticated creation/readback, rejected anonymous writes,
  public OpenAPI boundaries and data/login preservation after restart.
- Browser checks passed at 1440 and 390 pixels wide: directory, About, login,
  missing routes, denied anonymous Admin access and authenticated Admin.
  No page errors or horizontal overflow were observed. External traffic was
  blocked; these checks do not verify external avatars, map tiles or CDNs.
- NuGet reported no vulnerable direct or transitive packages.
- Python syntax, PowerShell parsing, local Markdown links and the public source
  export gate passed.

Client publishing reports that optional WebAssembly build tools are not installed.
The package runs; this review did not benchmark an optimized WebAssembly build.

## Repeat the checks

Follow [Contributing](CONTRIBUTING.md) to install the fixture dependencies and run
`scripts/test-local.ps1`. Build the Java companion separately without `-Install`.
Run the API-client fixtures from the repository root with
`python tests/test_examples.py`. Build packages into new directories.

Run all checks locally. GitHub Actions is disabled. Before sharing source, inspect
its proposed files and run `python scripts/check-public-export.py` against them
in the Git index. This checks known private/runtime patterns; it does not replace
a credential review.

## Checks that need a configured environment

Synthetic fixtures do not verify a Minecraft login, an Archive capture, a real
uNmINeD/BlueMap render, remote backup/restore, the complete Nocom dataset or a
public reverse proxy. No scheduled tasks were installed and no deployment was
performed. Run those integration checks before a live rollout.
