// Protocol tests; optional installed BlueMap source map exercises native mode setters.
// Usage: node scripts/test-bluemap-controls.mjs [path/to/index.js.map]
import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

const root = new URL('../', import.meta.url);
const bridge = fs.readFileSync(new URL('2b2tAtlas.Server/ClientAssets/atlas-controls-bridge-v1.js', root), 'utf8');
const parentCode = fs.readFileSync(new URL('2b2tAtlas.Client/wwwroot/js/atlas-map.js', root), 'utf8');
const start = parentCode.indexOf('export function setBlueMapNavigation(');
const end = parentCode.indexOf('\n}\n', start) + 2;
const parentFunction = parentCode.slice(start, end).replace('export ', '');
const origin = 'http://127.0.0.1:5297';
const hostOrigin = 'https://atlas.example';
const messages = [];
const parentListeners = new Set();
let childListener;
const cm = { position: { x: 17, y: 400, z: 21 }, distance: 350, angle: 1, rotation: 0, ortho: 0, tilt: 0 };
let localeAvailable = true;
const app = { appState: { controls: { state: 'perspective' } },
    settings: { version: '5.23' },
    mapViewer: { controlsManager: cm, map: { data: { freeFlightView: true, perspectiveView: true }, terrainHeightAt: () => 70 } },
    freeFlightControls: {}, mapControls: { reset() {} }, updatePageAddress() {} };
cm.controls = app.mapControls;
// Emulate a runtime/reactivity wrapper: observable controls are not necessarily
// reference-equal to the raw controller stored on BlueMapApp.
let rawController = cm.controls;
Object.defineProperty(cm, 'controls', { get: () => rawController ? new Proxy(rawController, {}) : null,
    set: value => { rawController = value; } });
if (process.argv[2]) {
    const map = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
    const source = map.sourcesContent[map.sources.findIndex(s => s.endsWith('/BlueMapApp.js'))];
    const utils = map.sourcesContent[map.sources.findIndex(s => s.endsWith('/util/Utils.js'))];
    function balanced(text, from) {
        const open = text.indexOf('{', from);
        let depth = 1, cursor = open + 1;
        for (; depth; cursor++) {
            if (text[cursor] === '{') depth++;
            if (text[cursor] === '}') depth--;
            if (cursor >= text.length) throw Error('Unbalanced source');
        }
        return text.slice(from, cursor);
    }
    const animate = balanced(utils, utils.indexOf('export const animate =')).replace('export ', '');
    const methods = ['setFreeFlight', 'setPerspectiveView'].map(name => balanced(source, source.indexOf(`    ${name}(`)));
    // Do not stub this native callback: a failed locale load throws from its
    // translated page title AFTER the controller switches, before state/ACK.
    methods.push(balanced(source, source.indexOf('    updatePageAddress ='))
        .replace('updatePageAddress = () =>', 'updatePageAddress()'));
    const context = vm.createContext({ Math, window: {},
        history: { replaceState() {} }, document: {},
        i18n: { t() { if (!localeAvailable) throw new SyntaxError(); return 'BlueMap'; } },
        round: (number, digits) => Math.round(number * 10 ** digits) / 10 ** digits,
        MathUtils: { lerp: (a,b,t) => a+(b-a)*t, clamp: (v,a,b) => Math.min(b,Math.max(a,v)) },
        MapControls: { getMaxPerspectiveAngleForDistance: () => 1.5 },
        EasingFunctions: { easeInOutQuad: t => t } });
    vm.runInContext(`${animate}; globalThis.methods = ({${methods.join(',')}});`, context);
    Object.assign(app, context.methods);
    console.log('Testing installed BlueMap native setters and animation implementation.');
} else {
    app.setFreeFlight = function () { cm.controls = this.freeFlightControls; cm.position.y = 73; cm.distance = 0; this.appState.controls.state = 'free'; };
    app.setPerspectiveView = function (_, distance) { cm.controls = this.mapControls; cm.distance = Math.max(distance,5); this.appState.controls.state = 'perspective'; };
}
const child = { bluemap: app, setTimeout, addEventListener: (_, cb) => { childListener = cb; },
    focus() {}, postMessage(data, target) {
        assert.equal(target, origin);
        queueMicrotask(() => childListener({ data, source: parentWindow, origin: hostOrigin }));
    } };
const parentWindow = {
    addEventListener: (_, cb) => parentListeners.add(cb), removeEventListener: (_, cb) => parentListeners.delete(cb),
    postMessage(data, target) {
        assert.equal(target, hostOrigin);
        messages.push(data);
        queueMicrotask(() => { for (const cb of parentListeners) cb({ data, source: child, origin }); });
    }
};
child.parent = parentWindow;
class Frame { src = origin + '/bluemap/generation/web/?atlas-controls=1'; contentWindow = child; focus() {} }
const frame = new Frame();
vm.runInNewContext(bridge, { window: child, Date, Set, Error });
const parentContext = vm.createContext({ window: parentWindow, document: { baseURI: hostOrigin, getElementById: () => frame },
    HTMLIFrameElement: Frame, URL, crypto: { randomUUID }, setTimeout, clearTimeout, setInterval, clearInterval });
vm.runInContext(parentFunction, parentContext);
if (process.argv[2]) {
    localeAvailable = false;
    await assert.rejects(parentContext.setBlueMapNavigation('viewer', 'free'), /could not change controls/);
    assert.equal(rawController, app.freeFlightControls, 'Reproduce reported symptom: camera switches despite error');
    assert.equal(app.appState.controls.state, 'perspective', 'Missing locale prevents mode state reaching Fly');
    assert.equal(parentListeners.size, 0);
    localeAvailable = true;
    await parentContext.setBlueMapNavigation('viewer', 'perspective');
}
assert.equal(await parentContext.setBlueMapNavigation('viewer', 'free'), 'free');
assert.equal(rawController, app.freeFlightControls);
assert.equal(cm.position.y, 73, 'Entry uses native terrain + 3, not saved orbit Y');
assert.equal(cm.distance, 0);
cm.position.y = 40;
const freeReply = messages.at(-1);
childListener({ source: parentWindow, origin: hostOrigin,
    data: { ...freeReply, type: 'set-navigation', mode: 'free' } });
assert.equal(cm.position.y, 40, 'A retried command must not reset flight height');
assert.equal(await parentContext.setBlueMapNavigation('viewer', 'perspective'), 'perspective');
assert.equal(rawController, app.mapControls);
assert.ok(cm.distance >= 100, 'Orbit pulls camera back out of interior');
assert.equal(parentListeners.size, 0, 'Acknowledgement listener cleaned up');
child.focus = () => { throw new Error('Browser denied focus'); };
assert.equal(await parentContext.setBlueMapNavigation('viewer', 'free'), 'free', 'Focus failure cannot invalidate a successful mode change');
child.focus = () => {};
// Commands from unrelated windows cannot manipulate the camera.
const last = messages.at(-1);
const count = messages.length;
childListener({ source: {}, origin: hostOrigin, data: { ...last, type: 'set-navigation', mode: 'free' } });
assert.equal(messages.length, count, 'Non-parent commands ignored');
const readyMap = app.mapViewer.map;
app.mapViewer.map = null;
const pending = parentContext.setBlueMapNavigation('viewer', 'free');
setTimeout(() => { app.mapViewer.map = readyMap; }, 25);
assert.equal(await pending, 'free', 'Commands wait for map initialization');
assert.equal(parentListeners.size, 0);
await parentContext.setBlueMapNavigation('viewer', 'perspective');
app.mapViewer.map.data.freeFlightView = false;
if (process.argv[2]) {
    await assert.rejects(parentContext.setBlueMapNavigation('viewer', 'free'), /did not activate/);
    assert.equal(parentListeners.size, 0);
}
console.log('PASS: missing-locale failure reproduced; loaded-locale native controls, title updates, acknowledgements, focus failure and listener cleanup.');
