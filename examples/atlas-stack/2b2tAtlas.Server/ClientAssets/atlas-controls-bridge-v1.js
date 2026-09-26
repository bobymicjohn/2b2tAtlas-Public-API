/* Read-only camera bridge. Uses BlueMap's native controls, not URL reloads. */
(() => {
    "use strict";
    if (window.__atlasBlueMapControlsBridge) {
        return;
    }
    window.__atlasBlueMapControlsBridge = true;
    const namespace = "2b2t-atlas-bluemap-controls";
    let active = null;
    let completed = null;
    const landing = window.__atlasBlueMapLanding;
    const hasLanding = landing && Number.isFinite(landing.x) && Number.isFinite(landing.z);
    // Explicit shared camera links take precedence over a fresh-view default.
    const initialHash = window.location?.hash?.slice(1).split(":") ?? [];
    const hasSharedView =
        initialHash.length === 10 &&
        initialHash
            .slice(1, 9)
            .every((value) => value.trim() !== "" && Number.isFinite(Number(value))) &&
        ["free", "perspective", "flat"].includes(initialHash[9]);
    function returnToLanding() {
        const app = window.bluemap;
        const manager = app?.mapViewer?.controlsManager;
        if (!app?.mapViewer?.map?.data.freeFlightView || !manager?.controls) {
            return false;
        }
        if (hasLanding) {
            manager.position.x = landing.x;
            manager.position.z = landing.z;
        }
        app.setFreeFlight(0, hasLanding && Number.isFinite(landing.y) ? landing.y : undefined);
        // Native setFreeFlight treats Y=0 as absent. Restore the precise saved Y,
        // including zero/negative heights, after the zero-duration transition.
        if (hasLanding && Number.isFinite(landing.y)) {
            manager.position.y = landing.y;
        }
        manager.distance = 0;
        app.updatePageAddress();
        app.mapViewer.updateLoadedMapArea();
        return currentMode() === "free";
    }
    function startViewer(attempt = 0) {
        const app = window.bluemap;
        // load() may still reset the camera after switchMap(). updateLoop is set
        // at the end of that initialization, so don't race its default orbit.
        if (!app?.updateLoop || !app.mapViewer?.map || !app.mapViewer.controlsManager?.controls) {
            if (attempt < 1800) {
                window.setTimeout(() => startViewer(attempt + 1), 100);
            }
            return;
        }
        function updateTerrainTransforms() {
            // Recent Three.js versions honor this flag on child groups too.
            // Terrain must follow BlueMap's camera-origin shift far from spawn.
            const map = app.mapViewer.map;
            for (const manager of [map?.hiresTileManager, ...(map?.lowresTileManager ?? [])]) {
                if (manager?.scene) {
                    manager.scene.matrixWorldAutoUpdate = true;
                }
            }
            app.mapViewer.redraw?.();
        }
        updateTerrainTransforms();
        app.mapViewer.events?.addEventListener("bluemapMapChanged", updateTerrainTransforms);
        if (!hasSharedView) {
            returnToLanding();
        }
        const button = window.document.createElement("button");
        button.id = "atlas-return-to-warp";
        const exactWarp = hasLanding && landing.source === "archive-warp";
        button.textContent = exactWarp ? "Return to warp" : "Return to start";
        button.title = exactWarp
            ? "Fly to this dated WDL's Archive landing coordinates"
            : "Fly to the available start point; exact warp coordinates are unavailable";
        button.style.cssText =
            "position:fixed;right:10px;bottom:10px;height:34px;box-sizing:border-box;z-index:10000;padding:0 12px;border:1px solid #66737e;border-radius:6px;background:#20282eee;color:#fff;font:13px system-ui;cursor:pointer;box-shadow:0 2px 6px #0005";
        button.addEventListener("click", () => {
            if (returnToLanding()) {
                try {
                    window.focus();
                } catch {
                    /* Focus is optional. */
                }
            }
        });
        window.document.body.appendChild(button);
        installFullscreenControl(app, button);
        installFlightHint(app);
        installCoordinateMenu(app);
        installContainerPopupCleanup();
    }
    function installFullscreenControl(app, returnButton) {
        const doc = window.document;
        // Native free-flight mouseup only enters fullscreen and pointer lock.
        // Replace that shortcut, leaving drag rotation and object picking alone.
        const free = app.freeFlightControls;
        if (free?.onMouseUp && free.target) {
            free.target.removeEventListener("mouseup", free.onMouseUp);
            free.onMouseUp = () => {};
        }
        const root = doc.documentElement;
        if (!root?.requestFullscreen) {
            return;
        }
        const actions = doc.createElement("div");
        actions.id = "atlas-viewer-actions";
        actions.style.cssText =
            "position:fixed;right:10px;bottom:10px;z-index:10000;display:flex;gap:6px;max-width:calc(100% - 20px);flex-wrap:wrap;justify-content:flex-end";
        const button = doc.createElement("button");
        button.id = "atlas-fullscreen";
        button.type = "button";
        button.style.cssText = returnButton.style.cssText;
        for (const control of [returnButton, button]) {
            control.style.position = "static";
            control.style.background = "#202020ee";
        }
        const update = () => {
            const full = !!doc.fullscreenElement;
            button.textContent = full ? "Exit fullscreen" : "Fullscreen";
            button.title = full
                ? "Exit fullscreen (Esc)"
                : "View fullscreen; click objects to inspect them";
            button.setAttribute("aria-label", button.textContent);
            button.setAttribute("aria-pressed", String(full));
        };
        button.addEventListener("click", async (e) => {
            e.preventDefault();
            e.stopPropagation();
            try {
                if (doc.pointerLockElement) {
                    doc.exitPointerLock();
                }
                if (doc.fullscreenElement) {
                    await doc.exitFullscreen();
                } else {
                    await root.requestFullscreen();
                }
                update();
            } catch {
                button.title = "Fullscreen is unavailable in this browser or embed";
            }
        });
        doc.addEventListener("fullscreenchange", update);
        actions.appendChild(button);
        actions.appendChild(returnButton);
        doc.body.appendChild(actions);
        update();
    }
    function installContainerPopupCleanup(attempt = 0) {
        const panel = window.document.getElementById("saved-inspector");
        if (!panel) {
            if (attempt < 100) {
                window.setTimeout(() => installContainerPopupCleanup(attempt + 1), 100);
            }
            return;
        }
        // Older immutable viewers still append this section. Keep their container
        // popups consistent without rebuilding terrain or changing cached assets.
        const containers = new Set([
            "chest",
            "trapped chest",
            "ender chest",
            "shulker box",
            "shulkerbox",
            "barrel",
            "hopper",
            "furnace",
            "blast furnace",
            "smoker",
            "dispenser",
            "dropper",
            "brewing stand",
        ]);
        const clean = () => {
            const link = panel.querySelector('a[href*="minecraft.wiki/w/Special:Search"]');
            const type = link && new URL(link.href).searchParams.get("search");
            if (!containers.has(type)) {
                return;
            }
            for (const summary of panel.querySelectorAll("details > summary")) {
                if (summary.textContent === "About this snapshot") {
                    summary.parentElement.remove();
                }
            }
        };
        new MutationObserver(clean).observe(panel, { childList: true, subtree: true });
        clean();
    }
    function blockAt(viewer, x, y) {
        const scene = viewer.map?.hiresTileManager?.scene;
        if (!scene || !viewer.map.isLoaded || viewer.controlsManager.distance >= 1000) {
            return null;
        }
        const rect = viewer.renderer.domElement.getBoundingClientRect();
        if (!rect.width || !rect.height) {
            return null;
        }
        const previous = scene.position.clone();
        try {
            // BlueMap shifts terrain while drawing to retain precision far from spawn.
            scene.position.x = 0;
            scene.position.z = 0;
            scene.updateMatrixWorld(true);
            viewer.camera.updateMatrixWorld();
            viewer.raycaster.setFromCamera(
                {
                    x: ((x - rect.left) / rect.width) * 2 - 1,
                    y: 1 - ((y - rect.top) / rect.height) * 2,
                },
                viewer.camera,
            );
            const hit = viewer.raycaster.intersectObject(scene, true).find((candidate) => {
                if (!candidate.face) {
                    return false;
                }
                for (let node = candidate.object; node; node = node.parent) {
                    if (!node.visible) {
                        return false;
                    }
                }
                return true;
            });
            if (!hit) {
                return null;
            }
            const normal = hit.face.normal.clone().transformDirection(hit.object.matrixWorld);
            // Step inside the clicked face, not into the adjacent air block.
            const block = {
                x: Math.floor(hit.point.x - normal.x * 0.0001),
                y: Math.floor(hit.point.y - normal.y * 0.0001),
                z: Math.floor(hit.point.z - normal.z * 0.0001),
            };
            return Object.values(block).every(Number.isSafeInteger) ? block : null;
        } finally {
            scene.position.copy(previous);
            scene.updateMatrixWorld(true);
        }
    }
    function blockActions(block, dimension) {
        const actions = [
            {
                label: "Copy block XYZ",
                value: `${block.x} ${block.y} ${block.z}`,
                title: "Copy the clicked block's X Y Z in this dimension.",
            },
        ];
        const targets =
            dimension === "end"
                ? [{ name: "End", ...block }]
                : dimension === "nether"
                  ? [
                        { name: "Overworld", x: block.x * 8, z: block.z * 8 },
                        { name: "Nether", ...block },
                    ]
                  : dimension === "overworld"
                    ? [
                          { name: "Overworld", ...block },
                          {
                              name: "Nether",
                              x: Math.floor(block.x / 8),
                              z: Math.floor(block.z / 8),
                          },
                      ]
                    : [];
        for (const command of ["", "goal", "goto"]) {
            for (const target of targets) {
                const value = `${command ? `#${command} ` : ""}${target.x} ${target.z}`;
                actions.push({
                    label: command
                        ? `Copy ${command} (${target.name})`
                        : `Copy ${target.name} coords`,
                    value,
                    title: command
                        ? `Run in the ${target.name}. ${command === "goal" ? "Sets the destination; does not start travel." : "Starts regular pathing immediately."}`
                        : `Copy X Z: ${value}`,
                });
            }
        }
        return actions;
    }
    function installCoordinateMenu(app) {
        const viewer = app.mapViewer,
            doc = window.document,
            canvas = viewer.renderer?.domElement;
        if (!canvas || !viewer.raycaster) {
            return;
        }
        let menu = null,
            gesture = null,
            dimension = null;
        // Read the generation's own dimension; never infer it from its title or lighting.
        const manifest = new URL("../manifest.json", window.location.href);
        window
            .fetch(manifest, { credentials: "omit", signal: AbortSignal.timeout(10000) })
            .then((response) => {
                if (!response.ok) {
                    throw new Error("Manifest unavailable");
                }
                return response.json();
            })
            .then((value) => {
                if (["overworld", "nether", "end"].includes(value.Dimension)) {
                    dimension = value.Dimension;
                }
            })
            .catch(() => {
                /* XYZ remains available without a verified dimension. */
            });
        const style = doc.createElement("style");
        style.textContent = `#atlas-block-menu{position:fixed;z-index:11000;box-sizing:border-box;width:270px;max-width:calc(100vw - 16px);max-height:calc(100vh - 16px);overflow:auto;padding:9px;border:1px solid #596674;border-radius:8px;background:#182127f5;color:#e9eef3;box-shadow:0 5px 22px #0008;font:13px/1.4 system-ui}#atlas-block-menu strong{display:block;padding:3px 7px}#atlas-block-menu button{display:flex;justify-content:space-between;gap:10px;width:100%;padding:8px 7px;background:transparent;border:0;border-radius:4px;color:inherit;text-align:left;font:inherit;cursor:pointer}#atlas-block-menu button:hover,#atlas-block-menu button:focus-visible{background:#335579;outline:2px solid #9cc8ff}#atlas-block-menu small{color:#b3c5d5;white-space:nowrap;font-size:11px}#atlas-block-menu p{font-size:11px;color:#b3c5d5;margin:6px 7px}#atlas-block-menu input{box-sizing:border-box;width:100%;color:#fff;background:#10171d;border:1px solid #7fa6cc;padding:6px}`;
        doc.head.appendChild(style);
        function close(focus = false) {
            menu?.remove();
            menu = null;
            if (focus) {
                canvas.tabIndex = 0;
                canvas.focus({ preventScroll: true });
            }
        }
        function show(x, y) {
            close();
            let block;
            try {
                block = blockAt(viewer, x, y);
            } catch {
                return;
            }
            if (!block) {
                return;
            }
            const controls = viewer.controlsManager.controls;
            controls?.reset?.();
            for (const key of [
                "keyMove",
                "keyHeight",
                "keyRotate",
                "keyAngle",
                "keyZoom",
                "mouseRotate",
                "mouseAngle",
            ]) {
                controls?.[key]?.onStop?.();
                controls?.[key]?.reset?.();
            }
            if (controls?.keyHeight) {
                controls.keyHeight.deltaY = 0;
            }
            controls?.keyMove?.deltaPosition?.set(0, 0);
            if (doc.pointerLockElement) {
                doc.exitPointerLock();
            }
            const element = doc.createElement("div");
            menu = element;
            element.id = "atlas-block-menu";
            element.setAttribute("role", "menu");
            element.setAttribute("aria-label", "Copy block coordinates");
            const heading = doc.createElement("strong");
            heading.textContent = `Block: ${block.x}, ${block.y}, ${block.z}`;
            element.appendChild(heading);
            const status = doc.createElement("p");
            status.setAttribute("role", "status");
            status.textContent = dimension ? "" : "Dimension unavailable; copying native XYZ only.";
            status.hidden = !!dimension;
            const buttons = [];
            let commandsHeading = false;
            for (const action of blockActions(block, dimension)) {
                if (!commandsHeading && action.value.startsWith("#")) {
                    const section = doc.createElement("strong");
                    section.textContent = "Baritone commands";
                    element.appendChild(section);
                    commandsHeading = true;
                }
                const button = doc.createElement("button");
                button.type = "button";
                button.setAttribute("role", "menuitem");
                button.textContent = action.label;
                button.title = action.title;
                const detail = doc.createElement("small");
                detail.textContent = action.value;
                button.appendChild(detail);
                button.addEventListener("click", async () => {
                    // execCommand also supports existing embeds without clipboard-write delegation.
                    const input = doc.createElement("input");
                    input.readOnly = true;
                    input.value = action.value;
                    input.setAttribute("aria-label", "Coordinates to copy manually");
                    element.querySelector("input")?.remove();
                    element.appendChild(input);
                    input.focus();
                    input.select();
                    let copied = false;
                    try {
                        await window.navigator.clipboard.writeText(action.value);
                        copied = true;
                    } catch {
                        /* Older embeds use the selected input. */
                    }
                    if (!copied) {
                        try {
                            copied = doc.execCommand("copy");
                        } catch {
                            /* Keep selected text. */
                        }
                    }
                    if (menu !== element) {
                        return;
                    }
                    status.hidden = false;
                    status.textContent = copied
                        ? `Copied: ${action.value}`
                        : "Clipboard blocked. Copy the selected text.";
                    if (copied) {
                        input.remove();
                        button.focus({ preventScroll: true });
                    }
                });
                buttons.push(button);
                element.appendChild(button);
            }
            element.appendChild(status);
            for (const type of ["pointerdown", "mousedown", "mouseup", "click", "wheel"]) {
                element.addEventListener(type, (e) => e.stopPropagation());
            }
            element.addEventListener("contextmenu", (e) => {
                e.preventDefault();
                e.stopPropagation();
            });
            element.addEventListener("keydown", (e) => {
                e.stopPropagation();
                if (e.key === "Escape") {
                    e.preventDefault();
                    close(true);
                } else if (e.key === "Tab") {
                    close(true);
                } else if (
                    ["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key) &&
                    doc.activeElement?.tagName !== "INPUT"
                ) {
                    e.preventDefault();
                    const index = buttons.indexOf(doc.activeElement);
                    buttons[
                        e.key === "Home"
                            ? 0
                            : e.key === "End"
                              ? buttons.length - 1
                              : (index + (e.key === "ArrowDown" ? 1 : -1) + buttons.length) %
                                buttons.length
                    ].focus();
                }
            });
            doc.body.appendChild(element);
            element.style.left = `${Math.max(8, Math.min(x, window.innerWidth - element.offsetWidth - 8))}px`;
            element.style.top = `${Math.max(8, Math.min(y, window.innerHeight - element.offsetHeight - 8))}px`;
            buttons[0].focus({ preventScroll: true });
        }
        const onTerrain = (target) => target === canvas || target === viewer.rootElement;
        doc.addEventListener(
            "mousedown",
            (e) => {
                if (menu?.contains(e.target)) {
                    return;
                }
                close();
                gesture =
                    e.button === 2 && onTerrain(e.target)
                        ? { x: e.clientX, y: e.clientY, moved: false }
                        : null;
            },
            true,
        );
        doc.addEventListener(
            "mousemove",
            (e) => {
                if (
                    gesture &&
                    (Math.abs(e.clientX - gesture.x) > 5 || Math.abs(e.clientY - gesture.y) > 5)
                ) {
                    gesture.moved = true;
                }
            },
            true,
        );
        doc.addEventListener("mouseup", (e) => {
            if (e.button !== 2 || !gesture) {
                return;
            }
            const click =
                !gesture.moved &&
                Math.abs(e.clientX - gesture.x) <= 5 &&
                Math.abs(e.clientY - gesture.y) <= 5;
            gesture = null;
            if (click && onTerrain(e.target)) {
                const rect = canvas.getBoundingClientRect();
                show(
                    doc.pointerLockElement ? rect.left + rect.width / 2 : e.clientX,
                    doc.pointerLockElement ? rect.top + rect.height / 2 : e.clientY,
                );
            }
        });
        doc.addEventListener("contextmenu", (e) => {
            if (onTerrain(e.target)) {
                e.preventDefault();
            }
        });
        canvas.tabIndex = 0;
        canvas.addEventListener("keydown", (e) => {
            if (e.key === "ContextMenu" || (e.shiftKey && e.key === "F10")) {
                e.preventDefault();
                e.stopPropagation();
                const rect = canvas.getBoundingClientRect();
                show(rect.left + rect.width / 2, rect.top + rect.height / 2);
            }
        });
        window.addEventListener("blur", () => {
            gesture = null;
            close();
        });
        window.addEventListener("resize", () => close());
        doc.addEventListener(
            "wheel",
            (e) => {
                if (!menu?.contains(e.target)) {
                    close();
                }
            },
            true,
        );
        app.events?.addEventListener("bluemapCameraMoved", () => close());
    }
    function installFlightHint(app) {
        if (!app.events?.addEventListener) {
            return;
        }
        const hint = window.document.createElement("div");
        hint.id = "atlas-flight-hint";
        hint.setAttribute("role", "status");
        hint.setAttribute("aria-live", "polite");
        const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        hint.style.cssText =
            "position:fixed;left:50%;bottom:54px;transform:translateX(-50%);width:max-content;max-width:calc(100% - 24px);box-sizing:border-box;z-index:10000;padding:7px 12px;border-radius:6px;background:#20282ed9;color:#e0e5e9;font:12px/1.6 system-ui;text-align:center;pointer-events:none;opacity:0;transition:opacity 220ms ease";
        if (reducedMotion) {
            hint.style.transition = "none";
        }
        window.document.body.appendChild(hint);
        let previousMode = null;
        let hideTimer;
        const update = () => {
            const mode = app.appState?.controls?.state;
            if (mode === previousMode) {
                return;
            }
            previousMode = mode;
            window.clearTimeout(hideTimer);
            if (mode !== "free") {
                hint.style.opacity = "0";
                return;
            }
            hint.textContent = window.matchMedia("(pointer: coarse)").matches
                ? "Drag to look · Left arrows move · Right arrows change height"
                : "WASD move · Space up · Shift down · Drag to look · Scroll speed";
            hint.style.opacity = "1";
            hideTimer = window.setTimeout(() => {
                hint.style.opacity = "0";
            }, 4500);
        };
        // Native frame events cover toolbar, keyboard and Atlas mode changes.
        app.events.addEventListener("bluemapRenderFrame", update);
        update();
    }
    // Tests and non-viewer protocol clients need only the message bridge.
    if (Object.prototype.hasOwnProperty.call(window, "__atlasBlueMapLanding")) {
        startViewer();
    }
    function currentMode() {
        const app = window.bluemap;
        const manager = app?.mapViewer?.controlsManager;
        // Vue may wrap controls in reactive proxies. Compare observable native
        // state, not object identity across the Vue/runtime boundary.
        if (!manager?.controls) {
            return null;
        }
        if (app.appState?.controls?.state === "free" && Math.abs(manager.distance) < 0.01) {
            return "free";
        }
        if (app.appState?.controls?.state === "perspective" && manager.distance >= 4.99) {
            return "perspective";
        }
        return null;
    }
    const reply = (request, error = null) => {
        const result = {
            namespace,
            version: 1,
            type: "navigation-result",
            requestId: request.id,
            mode: currentMode(),
            error,
        };
        completed = { id: request.id, result };
        window.parent.postMessage(result, request.origin);
        active = null;
    };
    function apply(request) {
        if (active !== request) {
            return;
        }
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
            if (request.mode === "free" && !app.mapViewer.map.data.freeFlightView) {
                throw new Error("This map did not activate the requested controller.");
            }
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
        if (active !== request) {
            return;
        }
        if (currentMode() === request.mode) {
            reply(request);
            return;
        }
        if (Date.now() - request.started > 18000) {
            reply(request, "This map did not activate the requested controller.");
            return;
        }
        window.setTimeout(() => confirm(request), 50);
    }
    window.addEventListener("message", (event) => {
        if (window.parent === window || event.source !== window.parent || event.origin === "null") {
            return;
        }
        const data = event.data;
        if (
            data?.namespace !== namespace ||
            data.version !== 1 ||
            data.type !== "set-navigation" ||
            !["free", "perspective"].includes(data.mode) ||
            typeof data.requestId !== "string"
        ) {
            return;
        }
        if (completed?.id === data.requestId) {
            window.parent.postMessage(completed.result, event.origin);
            return;
        }
        if (active?.id === data.requestId) {
            return;
        }
        active = { id: data.requestId, mode: data.mode, origin: event.origin, started: Date.now() };
        apply(active);
    });
})();
