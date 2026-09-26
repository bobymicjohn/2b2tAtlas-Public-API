import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

// Use r180 or later to reproduce the stale terrain transform.
if (!process.argv[2]) {
    throw new Error('Usage: node test-bluemap-terrain-transforms.mjs path/to/three.module.js');
}

const { Scene, Group, Object3D, Vector3 } = await import(pathToFileURL(process.argv[2]));
const bridge = fs.readFileSync(
    new URL('../2b2tAtlas.Server/ClientAssets/atlas-controls-bridge-v1.js', import.meta.url),
    'utf8',
);
function terrain() {
    const parent = new Scene(),
        scene = new Group(),
        model = new Object3D();
    parent.add(scene);
    scene.add(model);
    scene.matrixWorldAutoUpdate = false;
    return { parent, scene, model };
}
function install(map) {
    let changed;
    const viewer = {
        map,
        controlsManager: { controls: {} },
        events: {
            addEventListener(name, fn) {
                assert.equal(name, 'bluemapMapChanged');
                changed = fn;
            },
        },
    };
    const window = {
        __atlasBlueMapLanding: null,
        location: { hash: '#atlas:1:2:3:0:0:1.57:0:0:free' },
        bluemap: { updateLoop: 1, mapViewer: viewer },
        addEventListener() {},
        setTimeout() {},
        document: {
            getElementById: () => null,
            createElement: () => ({ style: {}, addEventListener() {} }),
            body: { appendChild() {} },
        },
    };
    vm.runInNewContext(bridge, { window, Date, Set, Error, Number });
    return { viewer, changed };
}
for (const [x, z] of [
    [14, 14],
    [-48067, -208375],
    [43324, -58284],
    [5168000, 10312000],
]) {
    const hires = terrain(),
        lowres = [terrain(), terrain(), terrain()];
    const sx = Math.round(x / 10000) * 10000,
        sz = Math.round(z / 10000) * 10000;
    for (const t of [hires, ...lowres]) {
        t.model.position.set(x, 72, z);
        t.parent.updateMatrixWorld(true);
        t.scene.position.set(-sx, 0, -sz);
        t.parent.updateMatrixWorld(true);
        if (sx || sz) {
            assert.notEqual(
                t.model.matrixWorld.elements[12],
                x - sx,
                'Reproduce stale terrain transform',
            );
        }
    }
    const { viewer, changed } = install({ hiresTileManager: hires, lowresTileManager: lowres });
    for (const t of [hires, ...lowres]) {
        t.parent.updateMatrixWorld(true);
        assert.deepEqual(new Vector3().setFromMatrixPosition(t.model.matrixWorld).toArray(), [
            x - sx,
            72,
            z - sz,
        ]);
    }
    const next = terrain();
    viewer.map = { hiresTileManager: next, lowresTileManager: [] };
    changed();
    assert.equal(next.scene.matrixWorldAutoUpdate, true, 'Also handles map switches');
}
console.log('PASS: near-origin and distant terrain, all detail layers, and map switches.');
