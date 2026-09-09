# Source, data and dependencies

This folder derives from the 2b2tAtlas application by bobymicjohn and its contributors.
Atlas-authored application source and documentation use the [Unlicense](LICENSE),
as do the surrounding API examples. Use, modify, share or build on them without
asking. Credit is appreciated, not required by Atlas.

Third-party components, media and datasets retain their own notices and terms.
Keep useful source links and be considerate of the people providing data and hosting.
Cache requests and use a provided caching proxy when possible. These are courtesies,
not extra conditions on Atlas-authored work.

No production SQLite database, user records, password hashes, Minecraft session state,
WDLs, rendered tile trees or backup archives are included. Empty media manifests preserve
the schema without importing attachments tied to production location IDs. Historical
group/highway seed definitions remain optional and are disabled by default.

The two `2b2tAtlas.Server/Data/nocom-*.json` files contain small derived summaries and
period metadata from the Nocom authors' public grouped release. Source URLs, input hashes
and interpretation caveats are retained. These summaries are not the raw SQL datasets,
player tracks or rendered heatmap tiles. Acquire source data from the
[original authors](https://github.com/nerdsinspace/nocom-explanation/blob/main/torrent.md)
when running the generators; do not infer players or ownership from observation counts.

Dependency declarations and bundled library headers retain their original notices.
Minecraft, Archive World Downloader, uNmINeD, BlueMap, Baritone and other external tools
remain their authors' work, with their own distribution terms. Install them using the
documented pinned versions; they are not relicensed by this repository. Third-party
icons and public historical source links retain their attribution context. Replace
branding and media when deploying a different project.
