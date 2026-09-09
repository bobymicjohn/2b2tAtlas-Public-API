/* Read-only camera bridge. Uses BlueMap's native controls, not URL reloads. */
(() => {
    "use strict";
    if (window.__atlasBlueMapControlsBridge) return;
    window.__atlasBlueMapControlsBridge = true;
    const namespace = "2b2t-atlas-bluemap-controls";
    let active = null;
    let completed = null;
    function currentMode() {
        const app = window.bluemap;
        const manager = app?.mapViewer?.controlsManager;
        // Vue may wrap controls in reactive proxies. Compare observable native
        // state, not object identity across the Vue/runtime boundary.
        if (!manager?.controls) return null;
        if (app.appState?.controls?.state === "free" && Math.abs(manager.distance) < 0.01) return "free";
        if (app.appState?.controls?.state === "perspective" && manager.distance >= 4.99) return "perspective";
        return null;
    }
    const reply = (request, error = null) => {
        const result = { namespace, version: 1, type: "navigation-result",
            requestId: request.id, mode: currentMode(), error };
        completed = { id: request.id, result };
        window.parent.postMessage(result, request.origin);
        active = null;
    };
    function apply(request) {
        if (active !== request) return;
        const app = window.bluemap;
        const manager = app?.mapViewer?.controlsManager;
        if (Date.now() - request.started > 18000) {
            reply(request, "BlueMap is still loading. Try again when terrain is visible.");
            return;
        }
        if (!app?.mapViewer?.map || !manager?.controls) {
            window.setTimeout(() => apply(request), 100);
            return;
        }
        try {
            if (request.mode === "free" && !app.mapViewer.map.data.freeFlightView)
                throw new Error("This map did not activate the requested controller.");
            if (request.mode === "free") {
                // No targetY: native terrain-height + 3 entry, then noclip flight.
                app.setFreeFlight(0);
            } else {
                app.setPerspectiveView(0, app.appState.controls.state === "free" ? 100 : 0);
            }
            // Let reactive state settle before acknowledging. A controller can
            // start successfully before the UI's mode state has propagated.
            confirm(request);
        } catch (error) {
            reply(request, error.message || "BlueMap could not change controls.");
        }
    }
    function confirm(request) {
        if (active !== request) return;
        if (currentMode() === request.mode) { reply(request); return; }
        if (Date.now() - request.started > 18000) {
            reply(request, "This map did not activate the requested controller.");
            return;
        }
        window.setTimeout(() => confirm(request), 50);
    }
    window.addEventListener("message", event => {
        if (window.parent === window || event.source !== window.parent || event.origin === "null") return;
        const data = event.data;
        if (data?.namespace !== namespace || data.version !== 1 || data.type !== "set-navigation" ||
            !["free", "perspective"].includes(data.mode) || typeof data.requestId !== "string") return;
        if (completed?.id === data.requestId) {
            window.parent.postMessage(completed.result, event.origin);
            return;
        }
        if (active?.id === data.requestId) return;
        active = { id: data.requestId, mode: data.mode, origin: event.origin, started: Date.now() };
        apply(active);
    });
})();
