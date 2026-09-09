# Nocom render tools

The source includes a heatmap generator, contrast processing, tile staging/publication
checks and a local comparison viewer. Supply the original authors' public grouped data
and your own terrain tile origins. No raw source archive or rendered heatmap tree ships
with this example.

- [Contrast tile implementation](../scripts/nocom_contrast_tiles.py)
- [Heatmap generation](../scripts/generate-nocom-heatmaps.py)
- [Contrast tests](../scripts/test-nocom-contrast.py)
- [Publication verification](../scripts/test-nocom-production.py)
- [Browser comparison tool](../tools/NocomReview/README.md)
- [Data provenance](../DATA_AND_LICENSES.md)

Generate into a new versioned directory. Compare each dimension over terrain at multiple
zoom levels before switching public references. Verify copied tile inventories and hashes;
keep the previous generation until the new appearance and URLs have been checked. Update
the example origin placeholders in the viewer and public manifest for your deployment.

Loaded-chunk observation intensity is not a count of players, visits or base owners.
Keep the source-period and sampling caveats visible alongside any derived visualization.
