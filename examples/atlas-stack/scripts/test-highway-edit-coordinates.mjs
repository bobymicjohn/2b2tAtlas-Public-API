// Exercise the actual editor transforms, including projected Nether roads.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source = fs.readFileSync(new URL('../2b2tAtlas.Client/wwwroot/js/atlas-map.js', import.meta.url), 'utf8');
const context = vm.createContext({ console, setTimeout, L: {
    divIcon: () => ({}), GridLayer: { extend: () => function() {} },
    latLng: (lat, lng) => ({lat, lng})
}});
vm.runInContext(source.replace(/^export /gm, '') + '\nthis.subject = { DIMENSIONS, blockToLatLng, latLngToHighwayPoint, highwayPointToLatLng };', context);
const m = context.subject;
let checks = 0;
for (const dimension of ['overworld', 'nether', 'end']) {
    for (const [x,z] of [[0,0],[1000,8000],[-30000000,30000000],[12345,-98765]]) {
        const cfg = m.DIMENSIONS[dimension];
        const latlng = m.blockToLatLng(cfg,x,z);
        assert.deepEqual(Array.from(m.latLngToHighwayPoint(cfg,dimension,latlng), value => value === 0 ? 0 : value), [x,z]);
        assert.deepEqual(m.highwayPointToLatLng(cfg,dimension,[x,z]), latlng);
        checks += 2;
    }
}
const projected = m.blockToLatLng(m.DIMENSIONS.overworld, 8000, -16000);
assert.deepEqual(Array.from(m.latLngToHighwayPoint(m.DIMENSIONS.overworld,'overworld',projected,'nether')), [1000,-2000]);
assert.deepEqual(m.highwayPointToLatLng(m.DIMENSIONS.overworld,'overworld',[1000,-2000],'nether'), projected);
console.log(`Passed ${checks + 2} highway editor coordinate checks.`);
