# Contributing

Contributions that make the public 2b2tAtlas API easier to use are welcome.

Good contributions include:

- a small, runnable example in another language;
- Fabric, Forge, map-mod, Discord, or web integration guidance;
- corrections grounded in the live OpenAPI contract;
- accessibility, caching, retry, or client-thread safety improvements;
- a project showcase demonstrating a real use of Atlas data.

## Ground rules

1. Keep examples read-only. This repository documents anonymous public `GET` routes, not moderation or ingestion capabilities.
2. Never commit tokens, passwords, session files, private coordinates, collector details, or production configuration.
3. Test examples against `https://api.blackportal.cloud` without producing high request volume.
4. Use a descriptive User-Agent where the language permits it and cache bulk data locally.
5. Keep entity links and source-specific provenance when practical.
6. Ignore unknown response fields and handle nullable historical fields.
7. Treat `blueMapUrl` as an optional render-scoped link: do not construct generation paths, auto-load the catalog, or substitute another date/dimension.

## Before opening a pull request

Run the examples you touched and verify the public contract:

```bash
curl --fail --silent --show-error https://api.blackportal.cloud/api
curl --fail --silent --show-error https://api.blackportal.cloud/openapi/v1.json > /dev/null
```

For the bundled examples:

```bash
dotnet run --project examples/csharp -- "Mu Megabase"
node examples/javascript/atlas-examples.mjs "Mu Megabase"
python examples/python/atlas_examples.py "Mu Megabase"
```

Keep pull requests focused. Explain the player/developer workflow the change enables.
