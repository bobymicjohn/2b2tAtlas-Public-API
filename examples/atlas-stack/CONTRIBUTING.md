# Working on the stack

Build and test from this folder; the surrounding repository also has standalone API
examples with their own dependencies. Keep changes to this source snapshot in this
folder unless they also affect the shared documentation.

## Writing code

Use names that tell the reader what a value contains. Keep one statement per line
and use braces around conditionals and loops. Split a long method when the smaller
pieces have clear jobs; avoid adding another layer just to move code around.
Comments should explain a constraint or decision the code cannot show on its own.
Keep API names, JSON fields, URLs and saved-file formats stable during cleanup.

Write docs as instructions someone can follow: what the tool does, what they need,
what to run, and what to check. Put installation-specific paths in configuration.
Keep historical source citations, but do not present an old rollout log as current
behavior.

## Local checks

Use the SDK from `global.json`, Python 3.12 or newer, Node.js 18 or newer, and
PowerShell. Install the Python fixture dependencies in a virtual environment:

```powershell
python -m venv .local/test-env
.local/test-env/Scripts/python -m pip install -r scripts/requirements-test.txt
./scripts/test-local.ps1 -Python .local/test-env/Scripts/python.exe
```

The runner builds and tests the .NET solution, then runs the offline Python,
PowerShell and map JavaScript fixtures. It saves each result under `.artifacts`.
It uses an explicit test list so a live maintenance script cannot be picked up
just because its name begins with `test`.

Build the Fabric companion separately with Java 21:

```powershell
./scripts/build-archive-coverage-mod.ps1 -Java 'C:/Program Files/Java/jdk-21/bin/java.exe' -WrapperCache .local/gradle
```

Do not pass `-Install` for validation. Renderer, Minecraft login, remote backup
and deployment checks require your own configured environment. Report them
separately from the local fixtures.

Contribute original changes under the [Unlicense](LICENSE). Preserve any separate
third-party notices and source context; Atlas credit itself is optional.

Run the .NET suite and relevant collector/map fixtures, then stage changes and run
`python scripts/check-public-export.py`. Never stage `.local`, credentials, databases,
WDLs, game profiles, downloaded tools, private records or generated tiles. Use synthetic
worlds and temporary databases in tests. Keep dependency and third-party notices intact.

Run all checks locally. GitHub Actions is disabled; do not add, enable, or dispatch
workflows. Test with synthetic fixtures and disposable databases. Never pass
deployment secrets to untrusted code or test against production state.

## Documentation style

Write for someone using Atlas for the first time. Start with a working example,
explain unfamiliar terms once, and link to deeper details. Use short, direct
sentences. Cut repeated warnings, filler, and promotional language. Avoid em
dashes; use a period, comma, parentheses, or a spaced hyphen instead. Check
commands, links, and API fields against the code before publishing.
