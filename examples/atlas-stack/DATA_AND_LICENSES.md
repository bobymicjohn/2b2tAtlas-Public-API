# Source, data and dependencies

This folder derives from the 2b2tAtlas application by bobymicjohn and its contributors.
Its application source retains the [MIT license](LICENSE). The surrounding repository's
Unlicense applies to its original API examples and documentation; it does not replace
the license on this imported application or third-party components.

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
