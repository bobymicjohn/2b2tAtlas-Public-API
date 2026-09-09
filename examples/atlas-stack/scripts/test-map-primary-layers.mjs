// Run the actual map module against lightweight Leaflet stubs; no tile requests.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../2b2tAtlas.Client/wwwroot/js/atlas-map.js', import.meta.url), 'utf8');
const map = { layers: new Set(), removeLayer(layer) { this.layers.delete(layer); } };
function Layer(options) { this.options = options; }
Layer.prototype.addTo = function(map) { map.layers.add(this); return this; };
Layer.prototype.on = function() { return this; };
const context = vm.createContext({ console, setTimeout,
    L: { divIcon: () => ({}), GridLayer: { extend: () => Layer }, tileLayer: (_, options) => new Layer(options) } });
vm.runInContext(source.replace(/^export /gm, '') + '\nthis.subject = { DIMENSIONS, registry, defaultRenders, uniqueRenderIds, planLegacyTiles, setRenders, getRenders, rebuildTiles, registerWorldRenders };', context);
const m = context.subject;
const plain = value => JSON.parse(JSON.stringify(value));

assert.deepEqual(plain(m.defaultRenders('nether')), ['place-world']);
assert.deepEqual(plain(m.defaultRenders('end')), ['place-world']);
assert.deepEqual(plain(m.defaultRenders('overworld')), ['place-obsidian', 'place-world']);
assert.deepEqual(plain(m.uniqueRenderIds('nether', ['place-world','place-world','missing','43k','43k'])), ['place-world','43k']);
assert.equal(m.DIMENSIONS.overworld.renders.length, 10);
assert.ok(m.DIMENSIONS.end.renders.some(r => r.id === '42k'));
assert.deepEqual(plain(m.DIMENSIONS.nether.renders.map(r => r.id)), ['43k','place-world']);
assert.deepEqual(plain(m.uniqueRenderIds('nether', ['5k','place-world'])), ['place-world'],
    'a stale 5k selection cannot restore an uncalibrated layer');

const state = { map, dimension: 'nether', renderIds: ['place-world','place-world'], tileLayers: [] };
m.registry.set('test', state);
m.rebuildTiles(state);
assert.equal(map.layers.size, 1, 'duplicate defaults must not create duplicate tile layers');
m.setRenders('test', ['43k','43k','place-world']);
assert.equal(map.layers.size, 2);
assert.deepEqual(plain(m.getRenders('test').current), ['43k','place-world']);
m.setRenders('test', []);
assert.equal(map.layers.size, 0, 'turning off spawn renders must retain an empty selection');

m.registerWorldRenders([{dimension:1,slug:'sample',name:'Sample',urlTemplate:'https://example.test/{z}/{y}/{x}.png'},
    {dimension:1,slug:'sample',name:'Sample',urlTemplate:'https://example.test/{z}/{y}/{x}.png'}]);
assert.equal(m.DIMENSIONS.nether.renders.filter(r => r.id === 'db:sample').length, 1);
m.registerWorldRenders([]);

// Transform known world points through the old pyramid into new tile pixels.
// Test negative/positive coordinates, origin, native levels and overzoom.
let checks = 0;
const cfg = m.DIMENSIONS.nether;
for (const render of cfg.renders.filter(r => r.legacy)) {
    for (const zoom of [-10,-2,0,3,6,9,10]) {
        const bpp = 256 / 2 ** zoom;
        for (const [x,z] of [[0,0],[-20000,-17000],[21000,21000],[1200,-2300]]) {
            const coords = {x:Math.floor((x+21504)/(256*bpp)),y:Math.floor((z+21504)/(256*bpp)),z:zoom};
            const plan = m.planLegacyTiles(cfg, render, coords);
            assert.ok(plan.length > 0 && plan.length <= 9, 'source fanout stays bounded');
            const sourceZoom = plan[0].z;
            assert.ok(sourceZoom <= render.maxNativeZoom);
            const oldBpp = 64*3.3599/2**sourceZoom;
            const sx = (x+21503.36)/oldBpp, sy = (z+21503.36)/oldBpp;
            const tile = plan.find(t => t.x === Math.floor(sx/256) && t.y === Math.floor(sy/256));
            assert.ok(tile, 'the selected source plan must cover the world point');
            const dx = tile.dx + (sx-tile.x*256)/256*tile.width;
            const dy = tile.dy + (sy-tile.y*256)/256*tile.width;
            assert.ok(Math.abs(dx-((x+21504)/bpp-coords.x*256)) < 1e-8);
            assert.ok(Math.abs(dy-((z+21504)/bpp-coords.y*256)) < 1e-8);
            checks++;
        }
    }
    assert.deepEqual(plain(m.planLegacyTiles(cfg, render, {x:-100,y:-100,z:10})), []);
}
console.log(`Primary layer selection, legacy inventory and ${checks} coordinate/zoom cases passed.`);
