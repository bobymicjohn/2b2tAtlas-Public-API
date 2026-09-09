# Stack guides

Start with the [local quick start](../README.md). These guides describe the optional
subsystems once the example UI/API is working:

- [First WDL](WDL_GETTING_STARTED.md)
- [Ingestion stages](INGESTION_PIPELINE.md) and [input security](INGESTION_SECURITY.md)
- [Worker configuration](WDL_WORKER_DEPLOYMENT.md)
- [Archive acquisition](ARCHIVE_SYNC_AUTOMATION.md)
- [BlueMap 3D](BLUEMAP_PIPELINE.md)
- [Media research](MEDIA_RESEARCH.md) and [YouTube metadata](YOUTUBE_RESEARCH_PIPELINE.md)
- [Group evidence and review](GROUP_ENRICHMENT_RUNBOOK.md)
- [Highway contributors](HIGHWAY_CONTRIBUTORS.md)
- [Map coordinates](MAP_GUIDE.md) and [API](API.md)
- [Host deployment](DEPLOY_FROM_SCRATCH.md)

Paths, domains, profile names and tasks in these adapted runbooks are examples.
The application source is complete, but external renderers, source datasets and
operator-specific configuration must be supplied locally. Old incident reports and
private machine inventories were excluded. References to those historical notes in
code comments are context, not additional dependencies.

`scripts/build-namecheap-package.ps1` produces a static client package for a new instance.
The original catalog/SEO implementation is retained as
`scripts/build-catalog-seo-reference.ps1`, alongside its entity/page generators. That
reference includes checks for the original site's curated content and metadata; adapt
those checks and canonical origins to your own catalog before using it. The local
example defaults to noindex and does not claim to host the original site's artifacts.
