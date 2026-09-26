import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const source = fs.readFileSync(
    new URL('../2b2tAtlas.Server/ClientAssets/atlas-controls-bridge-v1.js', import.meta.url),
    'utf8',
);
class Element {
    listeners = new Map();
    children = [];
    style = {};
    attributes = {};
    addEventListener(name, fn) {
        if (!this.listeners.has(name)) {
            this.listeners.set(name, new Set());
        }
        this.listeners.get(name).add(fn);
    }
    removeEventListener(name, fn) {
        this.listeners.get(name)?.delete(fn);
    }
    async emit(name, event = {}) {
        for (const fn of this.listeners.get(name) || []) {
            await fn(event);
        }
    }
    appendChild(child) {
        this.children.push(child);
    }
    setAttribute(name, value) {
        this.attributes[name] = value;
    }
}
const doc = new Element();
doc.body = new Element();
doc.documentElement = new Element();
doc.createElement = () => new Element();
const context = vm.createContext({
    window: { document: doc, addEventListener() {} },
    Number,
    Math,
    Object,
});
vm.runInContext(
    source.replace(/\}\)\(\);\s*$/, 'globalThis.install = installFullscreenControl;})();'),
    context,
);
let native = 0,
    picked = 0,
    rotationReleased = 0,
    requested = 0,
    unlocked = 0;
const canvas = new Element();
const free = { target: canvas, onMouseUp: () => native++ };
canvas.addEventListener('mouseup', free.onMouseUp);
canvas.addEventListener('mouseup', () => rotationReleased++);
canvas.addEventListener('pointerup', () => picked++);
doc.documentElement.requestFullscreen = async () => {
    requested++;
    doc.fullscreenElement = doc.documentElement;
    await doc.emit('fullscreenchange');
};
doc.exitFullscreen = async () => {
    doc.fullscreenElement = null;
    await doc.emit('fullscreenchange');
};
doc.exitPointerLock = () => {
    unlocked++;
    doc.pointerLockElement = null;
};
context.install({ freeFlightControls: free }, new Element());
const button = doc.body.children[0].children[0];
const click = { preventDefault() {}, stopPropagation() {} };
for (const button of [0, 1, 2]) {
    await canvas.emit('mouseup', { button });
}
await canvas.emit('pointerup', { button: 0 });
assert.equal(native, 0);
assert.equal(requested, 0);
assert.equal(picked, 1);
assert.equal(rotationReleased, 3);
// Switching away and back attaches the replaced callback, never the shortcut.
canvas.addEventListener('mouseup', free.onMouseUp);
await canvas.emit('mouseup', { button: 0 });
assert.equal(native, 0);
doc.pointerLockElement = canvas;
await button.emit('click', click);
assert.equal(requested, 1);
assert.equal(unlocked, 1);
assert.equal(button.textContent, 'Exit fullscreen');
assert.equal(button.attributes['aria-pressed'], 'true');
await canvas.emit('pointerup', { button: 0 });
assert.equal(picked, 2, 'inspection remains active in fullscreen');
await button.emit('click', click);
assert.equal(doc.fullscreenElement, null);
assert.equal(button.textContent, 'Fullscreen');
await button.emit('click', click);
await doc.exitFullscreen();
assert.equal(button.attributes['aria-pressed'], 'false', 'Escape updates button state');
doc.documentElement.requestFullscreen = async () => {
    throw Error('Not allowed');
};
await button.emit('click', click);
assert.match(button.title, /unavailable/);
assert.equal(button.textContent, 'Fullscreen');
console.log(
    'PASS: no automatic fullscreen/lock, preserved picking/mouse release, mode re-entry, explicit enter/exit, Escape and rejected fullscreen.',
);
