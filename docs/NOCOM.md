# Nocom historical observations

The heatmap is the overview. The released aggregates are what you want when
comparing periods or asking which highway direction had more positive observations.
Keep the bucket dates and measurement alongside the number; otherwise it is very
easy to turn a useful historical dataset into a bad player-count graph.

Atlas exposes the published Nocom aggregates through anonymous, read-only JSON
and MCP. These are historical loaded-chunk observations, not exact player
positions, unique visitors, trips, current activity, or proof of base ownership.

| Endpoint | Content |
|---|---|
| [`/api/nocom`](https://api.blackportal.cloud/api/nocom) | Provenance, original source, hashes, caveats, dimension counts and tile links |
| [`/api/nocom/periods?dimension=overworld`](https://api.blackportal.cloud/api/nocom/periods?dimension=overworld) | Fixed 30-day observation aggregates, observed extents and sparse PNG templates |
| [`/api/nocom/highways?dimension=nether&direction=northeast`](https://api.blackportal.cloud/api/nocom/highways?dimension=nether&direction=northeast) | Released historical directional highway observation series |

The complete period response contains 39 records: 17 Overworld, 17 Nether and
5 End periods. Highway responses contain at most 136 rows for one dimension,
or 17 for one of eight compass directions. End has no highway series.

`dimension` accepts `overworld`, `nether` or `end`. Period queries optionally accept
`from` and `to` in YYYY-MM-DD form. They select overlapping **whole buckets**:
`from=2020-04-08&to=2020-04-08` selects the April 8 bucket, not one day's count.
Reversed ranges and unsupported dimensions return 400. Dates outside coverage
return an empty array.

The source's -1/0/1 dimension ordinals differ from Atlas's 0/1/2 mapping. Period
records expose both explicitly. Extents use native Minecraft block coordinates:
source chunk X -1 covers blocks -16 through -1, inclusive. Extent rectangles do
not establish observation coverage at every interior point. Tile templates use
`{z}/{y}/{x}` where z is the URL zoom and y/x are tile indexes; use the manifest's
Atlas-aligned sparse tiling contract rather than geographic Web Mercator.

The grouped product starts March 9, 2020; End starts February 2, 2021. Nocom's
broader history began in 2018. July's last grouped bucket ends August 1, 2021,
but the exploit was patched July 15: bucket bounds are not exact observation dates.
PNG colors are visual intensities and cannot recover exact counts.

MCP tools: `get_nocom_dataset`, `get_nocom_periods`,
`get_nocom_highway_activity`. `get_dataset_stats` also links the Nocom dataset.
All use the existing public endpoint `https://api.blackportal.cloud/mcp`.

Run the dependency-free example:

```powershell
python examples/python/nocom_activity.py --dimension nether --direction northeast
python examples/python/nocom_activity.py --dimension end
```

The End has period data but no released highway series. The example keeps those
cases separate and supports `ATLAS_API_BASE_URL` for an offline fixture.

```javascript
const response = await fetch(
  "https://api.blackportal.cloud/api/nocom/highways?dimension=nether&direction=northeast"
);
if (!response.ok) throw new Error(`HTTP ${response.status}`);
const observations = await response.json();
console.table(observations.map(row => ({
  bucketStarts: row.periodStartUtc,
  positiveObservations: row.observations
})));
```

The crawlable [dataset page](https://2b2tatlas.com/nocom/) and its `dataset.json`,
`periods.jsonl` and `highways.jsonl` exports are included in the next static-site
package. API/MCP availability is independent of that upload. Original-source
attribution and terms remain separate from Atlas-authored catalog metadata.
See the [Nerds Inc release](https://github.com/nerdsinspace/nocom-explanation/blob/main/torrent.md).
