import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

if (!process.argv[2]) {
    throw new Error('Usage: node test-bluemap-landing.mjs path/to/viewer.js.map');
}

const bridge = fs.readFileSync(
    new URL('../2b2tAtlas.Server/ClientAssets/atlas-controls-bridge-v1.js', import.meta.url),
    'utf8',
);
const sourceMap = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const source =
    sourceMap.sourcesContent[sourceMap.sources.findIndex((s) => s.endsWith('/BlueMapApp.js'))];
const utils =
    sourceMap.sourcesContent[sourceMap.sources.findIndex((s) => s.endsWith('/util/Utils.js'))];
function balanced(text, from) {
    assert.ok(typeof text === 'string' && from >= 0, 'Expected BlueMap function in source map');
    const open = text.indexOf('{', from);
    let depth = 1,
        cursor = open + 1;
    for (; depth; cursor++) {
        if (text[cursor] === '{') {
            depth++;
        }
        if (text[cursor] === '}') {
            depth--;
        }
        if (cursor >= text.length) {
            throw Error('Could not find the end of the BlueMap function');
        }
    }
    return text.slice(from, cursor);
}
const animate = balanced(utils, utils.indexOf('export const animate =')).replace('export ', '');
const free = balanced(source, source.indexOf('    setFreeFlight('));
for (const y of [0, -48.25, 153.6366, null]) {
    const controls = {
        position: { x: 500, y: 400, z: 600 },
        distance: 350,
        angle: 1,
        rotation: 0,
        ortho: 0,
        tilt: 0,
        controls: {},
    };
    let loaded = 0,
        button,
        click,
        scheduled;
    const app = {
        appState: { controls: { state: 'perspective' } },
        mapViewer: {
            controlsManager: controls,
            map: { data: { freeFlightView: true }, terrainHeightAt: () => 70 },
            updateLoadedMapArea() {
                loaded++;
            },
        },
        freeFlightControls: {},
        updatePageAddress() {},
        updateLoop: null,
    };
    const window = {
        __atlasBlueMapLanding: {
            x: -100.125,
            y,
            z: 250.75,
            source: y === null ? 'warp-terrain-height' : 'archive-warp',
        },
        location: { hash: '' },
        bluemap: app,
        setTimeout: (cb) => {
            scheduled = cb;
        },
        addEventListener() {},
        focus() {},
        document: {
            getElementById: () => null,
            createElement() {
                return (button = {
                    style: {},
                    addEventListener(_, cb) {
                        click = cb;
                    },
                });
            },
            body: { appendChild() {} },
        },
    };
    const context = vm.createContext({
        window,
        Date,
        Set,
        Error,
        Number,
        Math,
        MathUtils: { lerp: (a, b, t) => a + (b - a) * t },
        EasingFunctions: { easeInOutQuad: (t) => t },
    });
    vm.runInContext(`${animate}; globalThis.methods=({${free}});`, context);
    app.setFreeFlight = context.methods.setFreeFlight;
    vm.runInContext(bridge, context);
    assert.equal(button, undefined, 'Waits until BlueMap finishes initial load');
    app.updateLoop = 1;
    scheduled();
    assert.equal(controls.position.x, -100.125);
    assert.equal(controls.position.z, 250.75);
    assert.equal(controls.position.y, y ?? 73);
    assert.equal(controls.distance, 0);
    assert.equal(app.appState.controls.state, 'free');
    assert.equal(controls.controls, app.freeFlightControls);
    assert.equal(button.textContent, y === null ? 'Return to start' : 'Return to warp');
    controls.position.x = 900;
    controls.position.y = 1000;
    controls.position.z = 800;
    click();
    assert.equal(controls.position.x, -100.125);
    assert.equal(controls.position.y, y ?? 73);
    assert.equal(controls.position.z, 250.75);
    assert.equal(loaded, 2);
}
// A valid explicit shared viewpoint must survive initialization.
let controls = { position: { x: 1, y: 2, z: 3 }, controls: {} };
let window = {
    __atlasBlueMapLanding: { x: 20, y: 80, z: 30, source: 'archive-warp' },
    location: { hash: '#atlas:1:2:3:0:0:1.57:0:0:free' },
    bluemap: { updateLoop: 1, mapViewer: { map: {}, controlsManager: controls } },
    addEventListener() {},
    setTimeout() {},
    document: {
        getElementById: () => null,
        createElement: () => ({ style: {}, addEventListener() {} }),
        body: { appendChild() {} },
    },
};
vm.runInNewContext(bridge, { window, Date, Set, Error, Number });
assert.deepEqual(controls.position, { x: 1, y: 2, z: 3 });
console.log(
    'PASS: native free flight, exact fractional XYZ, zero/negative Y, terrain fallback, delayed load, return button and shared viewpoints.',
);
