# Contributing

This repository contains public API documentation, small API clients, and the
[self-hosted Atlas stack](examples/atlas-stack/README.md). Pull requests can improve
any of these. For application changes, follow the stack's own contribution guide.

## Ground rules

1. Keep API consumer examples read-only against the public service. Test stack administration, ingestion and other mutations only against your own disposable instance; never against the live Atlas.
2. Never commit tokens, passwords, session files, private coordinates, production collector state, or production configuration. Generalized collector source belongs in `examples/atlas-stack`, with synthetic fixtures and configurable paths.
3. Test examples against the local fixture; use the live API to verify contract changes.
4. Use a descriptive User-Agent where the language permits it and cache bulk data locally.
5. Keep entity links and source-specific provenance when practical.
6. Ignore unknown response fields and handle nullable historical fields.
7. Treat `blueMapUrl` as an optional render-scoped link: do not construct generation paths, auto-load the catalog, or substitute another date/dimension.

## Before opening a pull request

Start the local fixture in a separate terminal:

```sh
python tests/mock_atlas_api.py
```

Set `ATLAS_API_BASE_URL=http://127.0.0.1:8765` in the terminal running the examples.
Use `$env:ATLAS_API_BASE_URL = "http://127.0.0.1:8765"` in PowerShell or
`export ATLAS_API_BASE_URL=http://127.0.0.1:8765` in bash.

Run the examples you changed:

```bash
dotnet run --project examples/csharp -- "Mu Megabase"
node examples/javascript/atlas-examples.mjs "Mu Megabase"
python examples/python/atlas_examples.py "Mu Megabase"
python examples/python/nocom_activity.py --dimension nether --direction northeast
python examples/python/nocom_activity.py --dimension end
python examples/python/location_media.py 5
```

CI runs these examples and `scripts/smoke-test.sh` against the fixture.
For endpoint or response-field changes, also check the
[live OpenAPI schema](https://api.blackportal.cloud/openapi/v1.json).

In the pull request, describe the change and the checks you ran.
