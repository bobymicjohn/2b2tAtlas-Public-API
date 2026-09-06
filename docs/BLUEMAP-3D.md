# BlueMap 3D render derivatives

2b2tAtlas can publish an interactive BlueMap viewer for an individual
historical WDL-derived render. The 3D view is a generated derivative of one
exact render observation; it is not a live view of 2b2t and it does not replace
the canonical Atlas location, 2D tiles, Archive warp, or source WDL.

## Discovering a 3D view

Use the normal render resources:

```http
GET https://api.blackportal.cloud/api/renders
GET https://api.blackportal.cloud/api/renders/{renderId}
GET https://api.blackportal.cloud/api/locations/{locationId}/renders
```

A render with a validated derivative includes three additive nullable fields:

```json
{
  "renderId": 1,
  "locationId": 1316,
  "dimension": 0,
  "worldDownloadDate": "2021-04-07",
  "blueMapUrl": "https://api.blackportal.cloud/bluemap/render-1-...-v5.23-p7/web/",
  "blueMapPath": "/bluemap/render-1-...-v5.23-p7/web/",
  "blueMapProfileVersion": 7
}
```

| Field | Meaning |
| --- | --- |
| `blueMapUrl` | Absolute public HTTPS URL for the interactive static viewer |
| `blueMapPath` | Same-origin-relative viewer path, beginning with `/bluemap/` |
| `blueMapProfileVersion` | Atlas generation profile that passed the current public quality gate |

Follow `blueMapUrl`; do not construct a generation path from IDs, hashes, or
profile numbers. Generation directories are immutable and content-addressed,
but Atlas may raise its minimum accepted profile or supersede an unsafe
generation. The API always advertises the currently accepted URL.

```javascript
const renders = await fetch(
  "https://api.blackportal.cloud/api/renders?locationId=1316&limit=100"
).then(response => {
  if (!response.ok) throw new Error(`Atlas returned ${response.status}`);
  return response.json();
});

for (const render of renders) {
  if (render.blueMapUrl) {
    console.log(`${render.name}: ${render.blueMapUrl}`);
  }
}
```

## Nullable means pending or unavailable

The BlueMap fields are optional. A null or absent value means Atlas is not
currently advertising a validated derivative for that render. It does **not**
mean the 2D render or source WDL is invalid. A client should retain its normal
2D experience and disable, hide, or label the 3D action as unavailable.

Generation is an asynchronous downstream process. New 2D renders can appear
before their 3D derivative. There is no anonymous API for starting generation
or reading the private operational queue.

## Render identity and dimensions

Treat each `renderId` independently:

- one location can have several historical dates;
- a source WDL can produce separate Overworld, Nether, and End render records;
- overlapping WDLs remain separate observations rather than a merged world;
- a BlueMap URL belongs to the exact render, dimension, source, and generation
  profile represented by that API record.

Use `dimension`, `worldDownloadDate`, Archive warp/source fields, and the owning
location when labeling tabs. Do not silently substitute a different date or
dimension when the selected render lacks 3D output.

## Embedding and performance

BlueMap is a WebGL application with nested static model and texture requests.
Open it only after a user asks for 3D. A normal link or new tab is the simplest
integration; an iframe can be used when the embedding browser's policies allow
it. Do not preload one viewer per catalog row or try to merge model resources
from many generations client-side.

Recommended behavior:

1. Load and cache Atlas JSON outside the Minecraft render/tick thread.
2. Show the normal 2D map, marker, or render preview first.
3. Enable 3D only when the selected render's `blueMapUrl` is non-null.
4. Preserve the selected date and dimension in the surrounding UI.
5. Release the iframe/webview when the user closes 3D to reclaim GPU memory.
6. Link back to the render API and canonical Atlas location for provenance.

The viewer and its nested `/bluemap/` assets are immutable-cacheable. Treat a
later `404` as a withdrawn or superseded generation, refresh the render record,
and fall back to 2D. Do not probe guessed generations.

## Relationship to source worlds

BlueMap output is derived data, not a playable save. When available,
`worldDownloadMetadataUrl` and `worldDownloadUrl` point to the separately
preserved partial Java world behind the render. Follow those fields if your
tool needs block analysis or offline play, and retain their checksum and scope
warning. The 3D URL is for browser visualization only.

The MCP `get_render_metadata` tool exposes the same validated URL/profile as a
link. MCP returns metadata, not BlueMap asset bytes.
