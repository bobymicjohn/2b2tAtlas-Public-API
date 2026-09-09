# World Pulse contrast review

An old/new comparison using the actual Atlas map module and the live 2b2t.place
terrain. The left side reads the existing v1 tiles. The right side renders a
separate candidate cache as you explore. Pan either map; the other follows.

The candidate uses violet, magenta and pale pink to separate observations from
water, grass, netherrack and end stone. Weak native hits have roughly twice their
old opacity. Dense areas retain an intensity gradient, and enlarged chunk cells
stay crisp. The opacity slider affects both sides equally. Terrain muting is
optional and starts off.

## Run locally

Requires Python 3.11+, NumPy and Pillow, plus internet access for Leaflet and the
terrain API. From the repository root:

```powershell
python scripts/serve-nocom-review.py --source F:\AtlasExample\AtlasTiles\Nocom\v1 --output D:\AtlasExample\Ingest\nocom-review\contrast-20260908-r2 --port 8772
```

Open <http://127.0.0.1:8772/>. The server binds only to loopback. On example-host the
bundled Python at
`C:\Users\atlas-operator\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`
already has the required libraries; the older system Python does not have NumPy.

Only requested candidate PNGs are generated, with at most four conversions in
parallel. The source tree is read-only. Existing candidate generations are kept,
and the tool rejects overlapping source/output paths or a mismatched profile.
The full 1.3-million-tile set is **not** prebuilt by the review server.

## What is being compared

- Overworld: 1m terrain; Nether: 100k terrain; End: 256k terrain.
- All three totals and all 39 fixed 30-day historical periods.
- Map zoom -10 through 10. Terrain is available from -2 upward; at the farthest
  zooms only the heatmap remains. The End's small historical footprint becomes
  just a few pixels at -10, which is the correct geographic scale.
- Identical tile coordinates and nontransparent-cell masks. No smoothing,
  dilation or inferred observations are added to the candidate rasters. Below
  zoom -2 a small screen-space glow helps locate tiny footprints; it is a visual
  aid, not additional observed area, and switches off over the terrain levels.

This is a display transform of the existing rasters. Native RGB is projected
onto the original color scale and restyled; overview colors already contain
aggregation from v1. The legend is deliberately qualitative. It does not recover
exact counts, player locations or finer resolution from those images.

The comparison itself does not replace any existing files. The owner approved
this profile on September 8; the production client now selects `contrast` and v2
by default while retaining v1 as Original heat/custom color. See the
[rollout runbook](../../docs/NOCOM_RENDERING.md) for publication
gates and the separate Namecheap frontend upload.

## Check it

```powershell
python scripts/test-nocom-contrast.py --source F:\AtlasExample\AtlasTiles\Nocom\v1 --output D:\AtlasExample\Ingest\nocom-review\contrast-20260908-r2
```

This samples an edge tile and a spawn tile at each advertised pyramid level. It
checks coverage, dimensions, unchanged source hashes, independent tile edges,
weak-hit opacity, concurrent cache writes and output-directory guards. It writes
`raster-validation.json` into the candidate directory.

The browser's **Check zooms & periods** button checks 63 dimension/zoom views and
39 historical views. It checks actual zoom, matching old/new loaded tile counts,
pending requests, decoded nonzero alpha and zoom-dependent display styling. Empty space outside a sparse source
footprint legitimately returns missing tiles on both sides. These are layer
loading checks; inspect the comparison visually to judge contrast against terrain.

## After review

Keep the existing v1 tree until the owner approves replacement. To prebuild an
approved candidate, the renderer accepts `--all`:

```powershell
python scripts/nocom_contrast_tiles.py --source F:\AtlasExample\AtlasTiles\Nocom\v1 --output D:\AtlasExample\Ingest\nocom-review\contrast-20260908-r2 --all
```

That command only fills the separate candidate tree. It does not publish or
delete anything. A later rollout should validate completeness, copy the finished
tiles to a versioned directory on F, and switch the manifest URL with the matching
legend/style. Retaining v1 makes rollback a URL/style change.
