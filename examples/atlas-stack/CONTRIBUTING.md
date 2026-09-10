# Working on the stack

Build and test from this folder; the surrounding repository also has standalone API
examples with their own dependencies. Keep changes to this source snapshot in this
folder unless they also affect the shared documentation.

Contribute original changes under the [Unlicense](LICENSE). Preserve any separate
third-party notices and source context; Atlas credit itself is optional.

Run the .NET suite and relevant collector/map fixtures, then stage changes and run
`python scripts/check-public-export.py`. Never stage `.local`, credentials, databases,
WDLs, game profiles, downloaded tools, private records or generated tiles. Use synthetic
worlds and temporary databases in tests. Keep dependency and third-party notices intact.

Run all checks locally. GitHub Actions is disabled; do not add, enable, or dispatch
workflows. Test with synthetic fixtures and disposable databases. Never pass
deployment secrets to untrusted code or test against production state.
