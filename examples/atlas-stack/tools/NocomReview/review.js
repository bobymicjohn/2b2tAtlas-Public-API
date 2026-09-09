import * as atlas from '/atlas-map.js';

const byId = id => document.getElementById(id);
const manifest = await fetch('/manifest.json').then(response => response.json());
const maps = ['oldMap', 'newMap'];
const areas = {
    overworld: { spawn: [0, 0], terrain: [22000, -12000], sparse: [100000, 100000] },
    nether: { spawn: [0, 0], terrain: [5000, -3000], sparse: [22000, 22000] },
    end: { spawn: [0, 0], terrain: [1500, 1200], sparse: [6000, 6000] }
};
let active = 'newMap', lastView = '', sweeping = false;

atlas.setApiBase('http://127.0.0.1:5297');
for (const id of maps) {
    atlas.initialize(id, 'overworld', true, null);
    atlas.setRenders(id, ['place-world']);
    atlas.setHighways(id, false);
    atlas.setAxes(id, false);
    atlas.setBorders(id, false);
    atlas.setSpawnRadius(id, false);
    byId(id).addEventListener('pointerdown', () => active = id);
    byId(id).addEventListener('wheel', () => active = id, { passive: true });
}
for (let zoom = -10; zoom <= 10; zoom++) byId('zoom').add(new Option(zoom, zoom));
byId('zoom').value = 2;

function updatePeriods() {
    const select = byId('period');
    select.replaceChildren(new Option('All time', 'total'));
    for (const frame of manifest.frames.filter(f => f.dimension === byId('dimension').value))
        select.add(new Option(frame.label, frame.key));
}

function selectedLayer() {
    const dimension = byId('dimension').value;
    return byId('period').value === 'total'
        ? manifest.totals.find(f => f.dimension === dimension)
        : manifest.frames.find(f => f.dimension === dimension && f.key === byId('period').value);
}

function overlay() {
    const layer = selectedLayer(), opacity = Number(byId('opacity').value) / 100;
    byId('opacityValue').textContent = Math.round(opacity * 100) + '%';
    if (!layer) return;
    atlas.setNocomLayer('oldMap', '/original/' + layer.urlTemplate, layer.maxNativeUrlZoom, opacity, 'classic');
    atlas.setNocomLayer('newMap', '/candidate/' + layer.urlTemplate + '?profile=' + encodeURIComponent(manifest.displayProfile), layer.maxNativeUrlZoom, opacity, 'contrast');
}

function view() {
    const x = Number(byId('x').value), z = Number(byId('z').value), zoom = Number(byId('zoom').value);
    for (const id of maps) {
        atlas.setZoom(id, zoom);
        atlas.goToCoords(id, x, z, zoom);
    }
    lastView = JSON.stringify(atlas.getView(active));
    status();
}

function area() {
    const [x, z] = areas[byId('dimension').value][byId('area').value];
    byId('x').value = x;
    byId('z').value = z;
    view();
}

function dimension() {
    for (const id of maps) {
        atlas.setDimension(id, byId('dimension').value);
        atlas.setRenders(id, ['place-world']);
    }
    updatePeriods();
    overlay();
    area();
}

function visibleImages(id) {
    const bounds = byId(id).getBoundingClientRect();
    const sourceZoom = Math.min(Number(byId('zoom').value) + (byId('dimension').value === 'overworld' ? 1 : 0), selectedLayer().maxNativeUrlZoom);
    return [...byId(id).querySelectorAll('.nocom-world-pulse img')].filter(image => {
        const b = image.getBoundingClientRect();
        const match = image.src.match(/\/(-?\d+)\/-?\d+\/-?\d+\.png/);
        return match && Number(match[1]) === sourceZoom && b.right > bounds.left && b.left < bounds.right && b.bottom > bounds.top && b.top < bounds.bottom;
    });
}

function imageStats(id) {
    const images = visibleImages(id);
    return { loaded: images.filter(i => i.complete && i.naturalWidth > 0).length, pending: images.filter(i => !i.complete).length };
}

function status() {
    const current = atlas.getView('newMap'), tiles = imageStats('newMap');
    byId('status').textContent = `${current.x.toLocaleString()}, ${current.z.toLocaleString()} · zoom ${current.zoom} · ${tiles.loaded} visible candidate tiles${current.zoom < -2 ? ' · terrain unavailable at this zoom' : ''}`;
}

byId('dimension').onchange = dimension;
byId('period').onchange = overlay;
byId('zoom').onchange = view;
byId('area').onchange = area;
byId('go').onclick = view;
byId('opacity').oninput = overlay;
byId('dimTerrain').onchange = () => byId('newMap').classList.toggle('dim-terrain', byId('dimTerrain').checked);
byId('reset').onclick = () => { byId('area').value = 'spawn'; byId('zoom').value = 2; area(); };
setInterval(() => {
    if (sweeping) return;
    const next = atlas.getView(active), key = JSON.stringify(next);
    if (key !== lastView) {
        lastView = key;
        atlas.goToCoords(active === 'oldMap' ? 'newMap' : 'oldMap', next.x, next.z, next.zoom);
        byId('zoom').value = next.zoom;
        byId('x').value = next.x;
        byId('z').value = next.z;
    }
    status();
}, 200);

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function settled() {
    let stable = 0;
    for (let i = 0; i < 50; i++) {
        await delay(150);
        const old = imageStats('oldMap'), next = imageStats('newMap');
        if (old.pending === 0 && next.pending === 0 && old.loaded > 0 && next.loaded > 0 && atlas.getView('newMap').zoom === Number(byId('zoom').value)) {
            if (++stable >= 3) return;
        } else stable = 0;
    }
}

function inspect() {
    const original = imageStats('oldMap'), candidate = imageStats('newMap');
    // Decode actual browser-loaded PNGs, including alpha. An empty image must
    // not count as a working heatmap. The count covers tiles, not just the crop.
    let observedPixelsInTiles = 0;
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 256;
    const context = canvas.getContext('2d', { willReadFrequently: true });
    for (const image of visibleImages('newMap').filter(i => i.complete && i.naturalWidth > 0)) {
        context.clearRect(0, 0, 256, 256);
        context.drawImage(image, 0, 0);
        const rgba = context.getImageData(0, 0, 256, 256).data;
        for (let i = 3; i < rgba.length; i += 4) if (rgba[i] > 0) observedPixelsInTiles++;
    }
    const diagnostics = atlas.getDiagnostics('newMap');
    const zoom = Number(byId('zoom').value);
    const style = getComputedStyle(byId('newMap').querySelector('.nocom-world-pulse'));
    const overviewGlow = style.filter.includes('drop-shadow');
    const correctStyle = overviewGlow === (zoom < -2) && style.imageRendering === 'pixelated';
    return {
        dimension: byId('dimension').value, period: byId('period').value,
        zoom, actualZoom: diagnostics.zoom, original, candidate, observedPixelsInTiles,
        terrainAvailable: zoom >= -2, terrain: diagnostics.placeLayers,
        overviewGlow, correctStyle,
        passed: diagnostics.zoom === zoom && original.loaded > 0 && candidate.loaded === original.loaded && candidate.pending === 0 && original.pending === 0 && observedPixelsInTiles > 0 && correctStyle
    };
}

byId('sweep').onclick = async () => {
    if (sweeping) return;
    sweeping = true;
    const controls = [...document.querySelectorAll('nav input, nav select, nav button')];
    controls.forEach(control => control.disabled = true);
    byId('results').textContent = 'Checking every dimension/zoom and historical period…';
    const results = [], expected = 63 + manifest.frames.length;
    async function check() {
        await settled();
        const result = inspect();
        results.push(result);
        byId('results').textContent = `Checked ${results.length}/${expected} · ${result.dimension} · ${result.period} · zoom ${result.zoom} · ${result.passed ? 'PASS' : 'FAIL'}`;
    }
    try {
        byId('area').value = 'spawn';
        for (const dim of ['overworld', 'nether', 'end']) {
            byId('dimension').value = dim;
            byId('zoom').value = 2;
            dimension();
            for (let zoom = -10; zoom <= 10; zoom++) {
                byId('zoom').value = zoom;
                view();
                await check();
            }
            byId('zoom').value = 0;
            view();
            for (const frame of manifest.frames.filter(f => f.dimension === dim)) {
                byId('period').value = frame.key;
                overlay();
                await check();
            }
        }
        byId('results').textContent = JSON.stringify({ profile: manifest.displayProfile, checkedAt: new Date().toISOString(), checked: results.length, passed: results.filter(r => r.passed).length, results }, null, 2);
    } catch (error) {
        byId('results').textContent = JSON.stringify({ error: String(error), results }, null, 2);
    } finally {
        sweeping = false;
        controls.forEach(control => control.disabled = false);
        byId('dimension').value = 'overworld';
        byId('zoom').value = 2;
        dimension();
    }
};

updatePeriods();
overlay();
view();
