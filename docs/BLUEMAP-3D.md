# Open a 3D view

BlueMap lets you explore a historical world download in your browser. Each view
belongs to one render, date, and dimension. It shows the saved world, not the
current server.

## Find a view

Read a location's renders and look for `blueMapUrl`:

```http
GET https://api.blackportal.cloud/api/locations/904/renders
```

```javascript
const response = await fetch(
  "https://api.blackportal.cloud/api/locations/904/renders"
);
if (!response.ok) throw new Error(`Atlas returned HTTP ${response.status}`);
const renders = await response.json();
for (const render of renders) {
  if (render.blueMapUrl) console.log(render.worldDownloadDate, render.blueMapUrl);
}
```

| Field | Meaning |
| --- | --- |
| `blueMapUrl` | Full URL to open in a browser |
| `blueMapPath` | The same path relative to the API host |
| `blueMapProfileVersion` | The generation settings version used for this view |

Use the returned URL. Do not build one from a render ID or profile number.

## When there is no link

A null or missing `blueMapUrl` means that no approved 3D view is currently
available for that render. Keep the 2D map available. A new render can gain a 3D
view later because the two are generated separately. Public clients cannot
start a render job.

If an old link returns `404`, fetch the render record again and use its current
link. Do not substitute another date or dimension just because it has 3D output.

## Add it to your app

A normal link is the simplest option. An iframe or webview also works where
browser policy permits it. Load the viewer only when the user opens 3D; it needs
WebGL, model files, and textures. Close the iframe or webview when finished to
release its graphics memory.

Show the selected render's date and dimension beside the view. One location can
have several historical captures, and overlapping saves remain separate records.
Cache according to the response headers and refresh the API record when a view
becomes unavailable.

## Use the viewer

The viewer starts at the selected capture's saved warp coordinates when they are
available. **Return to warp** brings you back there. **Return to start** means the
exact warp was unavailable and the viewer used a point inside the saved area.
A shared camera link keeps its chosen viewpoint.

Right-click a visible block to copy its coordinates or a pathfinding command.
Coordinates use the render's dimension. Fullscreen is an explicit button, so
clicking or dragging the scene does not unexpectedly enter fullscreen.

## Get the actual world

A BlueMap view is a map, not a playable save. Follow the render's
`worldDownloadMetadataUrl` and `worldDownloadUrl` when available to get the source
ZIP and checksum. See [world downloads](API-REFERENCE.md#download-a-world).

The MCP tool `get_render_metadata` returns the same 3D links. It does not return
the viewer's model or texture files.
