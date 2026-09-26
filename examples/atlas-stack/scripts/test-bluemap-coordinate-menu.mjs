import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

// Pass the Three.js module shipped with the installed BlueMap webapp.
if (!process.argv[2]) {
    throw new Error('Usage: node test-bluemap-coordinate-menu.mjs path/to/three.module.js');
}

const THREE = await import(pathToFileURL(process.argv[2]).href);
const source = fs.readFileSync(
    new URL('../2b2tAtlas.Server/ClientAssets/atlas-controls-bridge-v1.js', import.meta.url),
    'utf8',
);
const context = vm.createContext({
    window: { addEventListener() {} },
    Number,
    Math,
    Object,
    URL,
    AbortSignal,
});
vm.runInContext(
    source.replace(
        /\}\)\(\);\s*$/,
        'globalThis.fixture = { blockAt, blockActions, installCoordinateMenu, installFullscreenControl };})();',
    ),
    context,
);
const { blockAt, blockActions } = context.fixture;
const scene = new THREE.Scene();
const cube = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial());
cube.position.set(-8.5, -63.5, 16.5);
scene.add(cube);
const camera = new THREE.PerspectiveCamera(70, 1, 0.01, 1000);
const viewer = {
    map: { isLoaded: true, hiresTileManager: { scene } },
    controlsManager: { distance: 0 },
    renderer: {
        domElement: {
            getBoundingClientRect: () => ({ left: 100, top: 200, width: 600, height: 600 }),
        },
    },
    camera,
    raycaster: new THREE.Raycaster(),
};
const plain = (value) => JSON.parse(JSON.stringify(value));
for (const delta of [
    [3, 0, 0],
    [-3, 0, 0],
    [0, 3, 0],
    [0, -3, 0],
    [0, 0, 3],
    [0, 0, -3],
]) {
    camera.position.copy(cube.position).add(new THREE.Vector3(...delta));
    camera.lookAt(cube.position);
    scene.position.set(10000, 0, -20000);
    assert.deepEqual(plain(blockAt(viewer, 400, 500)), { x: -9, y: -64, z: 16 });
    assert.deepEqual(
        scene.position.toArray(),
        [10000, 0, -20000],
        'restore BlueMap precision offset',
    );
}
cube.visible = false;
assert.equal(blockAt(viewer, 400, 500), null);
cube.visible = true;
camera.lookAt(camera.position.clone().add(new THREE.Vector3(0, 1, 0)));
assert.equal(blockAt(viewer, 400, 500), null, 'sky');
viewer.controlsManager.distance = 1200;
assert.equal(blockAt(viewer, 400, 500), null, 'lowres is not a block');
viewer.controlsManager.distance = 0;
const raycast = viewer.raycaster.intersectObject;
viewer.raycaster.intersectObject = () => {
    throw Error('fixture');
};
assert.throws(() => blockAt(viewer, 400, 500));
assert.deepEqual(scene.position.toArray(), [10000, 0, -20000]);
viewer.raycaster.intersectObject = raycast;
const block = { x: -9, y: -64, z: 16 };
const ow = plain(blockActions(block, 'overworld'));
assert.equal(ow.find((a) => a.label === 'Copy block XYZ').value, '-9 -64 16');
assert.equal(ow.find((a) => a.label === 'Copy Nether coords').value, '-2 2');
assert.equal(ow.find((a) => a.label === 'Copy goal (Nether)').value, '#goal -2 2');
assert.equal(ow.find((a) => a.label === 'Copy goto (Overworld)').value, '#goto -9 16');
assert.equal(
    plain(blockActions(block, 'nether')).find((a) => a.label === 'Copy Overworld coords').value,
    '-72 128',
);
assert.equal(blockActions(block, 'end').length, 4);
assert.equal(blockActions(block, null).length, 1, 'do not guess dimension');
assert.ok(ow.every((a) => !a.value.includes('elytra')));
console.log(
    'PASS: six block faces, negative XYZ, scene-offset restoration, hidden/sky/lowres exclusion, portal conversions, commands and unknown dimensions.',
);

class Element {
    constructor() {
        this.listeners = new Map();
        this.children = [];
        this.style = {};
        this.offsetWidth = 270;
        this.offsetHeight = 310;
    }
    addEventListener(type, fn) {
        const list = this.listeners.get(type) || [];
        list.push(fn);
        this.listeners.set(type, list);
    }
    removeEventListener(type, fn) {
        this.listeners.set(
            type,
            (this.listeners.get(type) || []).filter((item) => item !== fn),
        );
    }
    emit(type, event) {
        for (const fn of this.listeners.get(type) || []) {
            fn(event);
        }
    }
    appendChild(child) {
        this.children.push(child);
        child.parent = this;
    }
    remove() {
        this.parent.children = this.parent.children.filter((child) => child !== this);
    }
    contains(target) {
        return target === this || this.children.some((child) => child.contains(target));
    }
    setAttribute() {}
    focus() {
        doc.activeElement = this;
    }
}
const doc = new Element();
doc.body = new Element();
doc.head = new Element();
doc.createElement = () => new Element();
const canvas = new Element();
canvas.getBoundingClientRect = viewer.renderer.domElement.getBoundingClientRect;
viewer.renderer.domElement = canvas;
viewer.rootElement = canvas;
camera.position.copy(cube.position).add(new THREE.Vector3(0, 0, -3));
camera.lookAt(cube.position);
let nativeClicks = 0;
const free = { target: canvas, onMouseUp: () => nativeClicks++ };
canvas.addEventListener('mouseup', free.onMouseUp);
context.window.document = doc;
context.window.location = { href: 'https://example.com/bluemap/render-1/web/' };
context.window.innerWidth = 390;
context.window.innerHeight = 720;
context.window.fetch = () => new Promise(() => {});
context.fixture.installFullscreenControl({ freeFlightControls: free }, new Element());
context.fixture.installCoordinateMenu({
    mapViewer: viewer,
    freeFlightControls: free,
    appState: { controls: { state: 'free' } },
});
const event = (x, y, button = 2) => ({
    clientX: x,
    clientY: y,
    button,
    target: canvas,
    preventDefault() {},
    stopPropagation() {},
});
doc.emit('mousedown', event(400, 500));
doc.emit('mousemove', event(470, 500));
doc.emit('mousemove', event(400, 500));
doc.emit('mouseup', event(400, 500));
assert.equal(doc.body.children.length, 0, 'a drag returning to its start is still a drag');
doc.emit('mousedown', event(400, 500));
canvas.emit('mouseup', event(400, 500));
doc.emit('mouseup', event(400, 500));
assert.equal(nativeClicks, 0, 'right click never invokes fullscreen/pointer lock');
assert.equal(doc.body.children.length, 1, 'stationary right click opens the menu');
assert.equal(doc.body.children[0].style.left, '112px', 'small viewport clamps menu');
doc.emit('mousedown', event(400, 500, 0));
canvas.emit('mouseup', event(400, 500, 0));
assert.equal(doc.body.children.length, 0, 'outside click dismisses');
assert.equal(nativeClicks, 0, 'left click does not invoke fullscreen/pointer lock');
console.log(
    'PASS: right-drag suppression, return-to-start drag, click fullscreen disabled, outside dismissal and narrow viewport clamping.',
);
