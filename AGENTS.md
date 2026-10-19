# Repository checks

Run builds, tests, and release checks locally. GitHub Actions is disabled by owner
request to avoid consuming the account's runner allowance. Do not add, enable, or
dispatch Actions workflows, including for validation or publishing, unless the
owner explicitly changes this policy. Do not change billing or spending limits.

This policy also applies to examples/atlas-stack. Follow the contribution guides
for local validation and record the checks actually run when reporting a change.

Keep API examples as ordinary HTTP requests and document the live OpenAPI
contract. Do not add client libraries, SDKs or downloadable contributor tools
without an explicit request. Maintained Atlas application code uses .NET 10,
EF Core for catalog data and Blazor WebAssembly with Radzen. The dated application
snapshot retains its own dependencies and must remain reproducible.

Shop, advertising, checkout and payment implementation stays private. Before a
public push, run `scripts/check-private-code.py` for both staged files and the
commit range, and `examples/atlas-stack/scripts/check-public-export.py`.
