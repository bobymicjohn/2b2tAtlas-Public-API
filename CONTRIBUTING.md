# Contributing

This repository contains public API documentation, HTTP request examples and an
[archived Atlas application](examples/atlas-stack/README.md). For changes to that
snapshot, follow its own contribution guide.

Contribute original changes under the [Unlicense](LICENSE), and keep existing
third-party notices intact. Source links are useful even when Atlas credit is optional.

## Ground rules

1. Keep API consumer examples read-only against the public service. Test stack administration, ingestion and other mutations only against your own disposable instance; never against the live Atlas.
2. Never commit tokens, passwords, session files, private coordinates, production collector state, or production configuration. Generalized collector source belongs in `examples/atlas-stack`, with synthetic fixtures and configurable paths.
3. Check request examples and contract changes against the live read-only API.
4. Use a descriptive User-Agent where the language permits it and cache bulk data locally.
5. Keep entity links and source-specific provenance when practical.
6. Ignore unknown response fields and handle nullable historical fields.
7. Treat `blueMapUrl` as an optional render-scoped link: do not construct generation paths, auto-load the catalog, or substitute another date/dimension.

## Before opening a pull request

Run `scripts/smoke-test.ps1` or `scripts/smoke-test.sh` for the public read checks.
Check any requests you changed in `examples/requests.http`.
GitHub Actions is disabled; do not add or run workflows. Build, test, and publish
from a local environment using the documented commands.
For endpoint or response-field changes, also check the
[live OpenAPI schema](https://api.blackportal.cloud/openapi/v1.json).

For the current public guides and machine-readable examples, run
`python tests/test_documentation.py --live`. Before publishing, stage the reviewed
files and run `python scripts/check-private-code.py` plus
`python examples/atlas-stack/scripts/check-public-export.py`. Check the committed
range with `python scripts/check-private-code.py --base origin/main --head HEAD`
before pushing. These checks do not publish or enable GitHub Actions.

Registry metadata is published locally with the official `mcp-publisher` CLI.
Verify its release checksum, increment `server.json` to a previously unpublished
version, and keep its description within 100 characters. After the checks above,
run `mcp-publisher validate server.json`, authenticate with
`mcp-publisher login github`, and run `mcp-publisher publish server.json`.
Verify the official latest registry entry against the committed manifest.
Published versions are immutable; metadata-only updates still need a new version.
Do not enable the retained GitHub Actions publication workflow.

In the pull request, describe the change and the checks you ran.

The maintained Atlas application uses .NET 10, EF Core for catalog data and
Blazor WebAssembly with Radzen. Keep HTTP examples language-neutral. Do not add
SDKs, client libraries or downloadable contributor tools without an explicit request.

## Documentation style

Write for someone using Atlas for the first time. Start with a working example,
explain unfamiliar terms once, and link to deeper details. Use short, direct
sentences. Cut repeated warnings, filler, and promotional language. Avoid em
dashes; use a period, comma, parentheses, or a spaced hyphen instead. Check
commands, links, and API fields against the code before publishing.
