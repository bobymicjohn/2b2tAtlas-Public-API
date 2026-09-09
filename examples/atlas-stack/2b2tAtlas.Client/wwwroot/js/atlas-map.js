// atlas-map.js — Leaflet CRS.Simple map for the 2b2t world.
//
// Coordinate scheme is copied 1:1 from the proven production (Vue) atlas so the
// existing tile pyramids on tiles.atlas.example line up exactly. Do NOT "simplify"
// the CRS math without re-verifying tile alignment against a known base.
//
// World: 60,000,000 x 60,000,000 blocks, centered at (0,0). Leaflet CRS.Simple
// is a flat pixel plane (no projection), so Minecraft block (x,z) maps linearly
// to map coordinates. Per-dimension `offset`/`scaleFactor` position blocks onto
// the shared tile pyramid.

// --- Per-dimension configuration (from production map.vue) --------------------
// Each dimension shares one coordinate config (offset/scaleFactor/tileOptions)
// but exposes several named world-download renders (its own tile pyramid).
const TILE_BASE = "https://tiles.atlas.example/AtlasTiles";

const DIMENSIONS = {
    overworld: {
        offset: 128000,
        scaleFactor: 8,
        renderMinZoom: -2,
        tileOptions: { noWrap: true, zoomOffset: 1 },
        renders: [
            { id: "256k", label: "256k (2021)", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/256k/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "100k2025", label: "100k (2025)", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/100k(256k)/2025/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "100k", label: "100k Spawn (late 2018; released 2019)", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/100k(256k)/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "7kY255", label: "7k (Y255)", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/7k(256k)/Y255/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "7kY254", label: "7k (Y254)", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/7k(256k)/Y254/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "OwO", label: "OwO", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/OwO(256k)/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "Pekora", label: "Pekora", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/Pekora(256k)/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "TGG", label: "TGG", dayNight: true, url: (d) => `${TILE_BASE}/Overworld/TGG(256k)/${d ? "day" : "night"}/{z}/{y}/{x}.png` },
            { id: "place-world", label: "1m World (2026)", dayNight: false, place: true, placeLayer: "base" },
            { id: "place-obsidian", label: "1m Obsidian (2026)", dayNight: false, place: true, placeLayer: "overlay" },
        ],
    },
    nether: {
        // sf=4 (power of two) so native sparse nether tiles align, mirroring End.
        // The atlas-nether-sparse-v1 TileOffset is offset/256 = 84.
        offset: 21504,
        scaleFactor: 4,
        renderMinZoom: -2,
        tileOptions: { noWrap: true },
        renders: [
            // Preserve v1 pyramids in their original CRS; reproject when selected.
            { id: "43k", label: "43k Nether (Aug 15–17, 2019)", dayNight: false, maxNativeZoom: 9,
                legacy: { offset: 21503.36, scaleFactor: 3.3599 },
                url: () => `${TILE_BASE}/Nether/43k/7/{z}/{y}/{x}.png` },
            // The archived 5k image has no verified world transform; do not
            // reuse the 43k CRS for it. Keep it out of the selectable catalog.
            { id: "place-world", label: "100k World (2026)", dayNight: false, place: true, placeLayer: "base" },
        ],
    },
    end: {
        offset: 20992,
        scaleFactor: 4,
        renderMinZoom: 0,
        tileOptions: { noWrap: true },
        renders: [
            { id: "42k", label: "42k End (Aug–Sep 2019)", dayNight: false, maxNativeZoom: 9, url: () => `${TILE_BASE}/End/42k/{z}/{y}/{x}.png` },
            { id: "place-world", label: "256k World (2026)", dayNight: false, place: true, placeLayer: "base" },
        ],
    },
};

const DEFAULT_ZOOM = 5;
const MIN_ZOOM = -10;
const MAX_ZOOM = 10;
const MIN_LOCATION_ZOOM = 1;

// Touch targets need to be substantially larger than their painted geometry.
// Leaflet's native click handling is retained for mouse/keyboard users; these
// values are used only by the touch/pen fallback installed on the map canvas.
const TOUCH_TAP_MAX_MOVE_PX = 14;
const TOUCH_LOCATION_HIT_PX = 30;
const TOUCH_HIGHWAY_HIT_PX = 24;
const TOUCH_MAP_CLICK_SUPPRESSION_MS = 700;

// Per-location WDL pyramids are intentionally virtualized. Keeping hundreds of
// off-screen L.TileLayer instances attached makes every pan/zoom fan out across
// the entire render catalog. A render becomes useful when it intersects a
// padded viewport and projects to a meaningful on-screen size. The padding
// hides layer churn during ordinary pans; the cap is only a dense-overlap guard.
const BASE_RENDER_MIN_SCREEN_PX = 24;
const BASE_RENDER_VIEWPORT_PADDING = 0.4;
const BASE_RENDER_MAX_ACTIVE = 96;

// Dimension int (DB/DIM_INDEX) -> DIMENSIONS key.
const DIM_KEY = { 0: "overworld", 1: "nether", 2: "end" };

/// Merge ingest-registered world renders (from GET /api/maprenders) into the
/// per-dimension render lists so they appear in the picker with no code change.
/// Idempotent: replaces any previously-registered DB renders (id prefix "db:").
export function registerWorldRenders(renders) {
    for (const key of Object.keys(DIMENSIONS)) {
        DIMENSIONS[key].renders = DIMENSIONS[key].renders.filter((r) => !String(r.id).startsWith("db:"));
    }
    for (const r of renders || []) {
        const cfg = DIMENSIONS[DIM_KEY[r.dimension]];
        if (!cfg || !r.urlTemplate) continue;
        if (cfg.renders.some((entry) => entry.id === `db:${r.slug}`)) continue;
        const template = r.urlTemplate;
        const descriptor = {
            id: `db:${r.slug}`,
            label: r.name,
            dayNight: !!r.hasDayNight,
            url: (d) => template.replace("{dn}", d ? "day" : "night"),
        };
        if (r.maxNativeZoom != null) descriptor.maxNativeZoom = r.maxNativeZoom;
        cfg.renders.push(descriptor);
    }
    return true;
}

// Registry of live maps keyed by container element id.
const registry = new Map();
let nocomFilterSequence = 0;

// --- CRS ---------------------------------------------------------------------
// Build a fresh CRS object each time so we never mutate the shared L.CRS.Simple
// singleton (a classic Leaflet footgun).
function makeCrs() {
    const mapMaxResolution = 0.0625;
    const mapMinResolution = Math.pow(2, 10) * mapMaxResolution; // 64
    const tileExtent = [0, -12800, 12800, 0];

    const crs = L.Util.extend({}, L.CRS.Simple);
    crs.transformation = new L.Transformation(1, -tileExtent[0], -1, tileExtent[3]); // (1, 0, -1, 0)
    crs.scale = (zoom) => Math.pow(2, zoom) / mapMinResolution;
    crs.zoom = (scale) => Math.log(scale * mapMinResolution) / Math.LN2;
    return crs;
}

// --- Coordinate conversions --------------------------------------------------
// Block (x, z) -> Leaflet LatLng, using the active dimension's offset/scaleFactor.
function blockToLatLng(cfg, x, z) {
    return L.latLng(-(z + cfg.offset) / cfg.scaleFactor, (x + cfg.offset) / cfg.scaleFactor);
}

// Leaflet LatLng -> block (x, z).
function latLngToBlock(cfg, lat, lng) {
    const o = cfg.offset / cfg.scaleFactor;
    const x = Math.round((lng - o) * cfg.scaleFactor);
    const z = Math.round(-(lat + o) * cfg.scaleFactor);
    return { x, z };
}

// --- Highways & axes ---------------------------------------------------------
// The 2b2t highway network is deterministic geometry radiating from (0,0):
// axis highways, diagonals, concentric square + diamond ring roads, a nether
// star, and the 50k grid. Points are stored in NETHER-coord units (the 8:1
// ratio) so ×8 gives Overworld blocks — matching the production transform.
// This is a temporary client-side seed; the editable `Highway` entity + API
// (Phase 2E) will eventually supply this data with full lore metadata.

const squareRing = (r) => [[-r, r], [r, r], [r, -r], [-r, -r], [-r, r]];
const diamondRing = (r) => [[-r, 0], [0, r], [r, 0], [0, -r], [-r, 0]];

const HIGHWAYS = [
    // Axis highways — run to ±30,000,000 NETHER units. On 2b2t the nether axis
    // highways extend FAR past the ±3.75M nether world border (the border only
    // scales the ring roads); this matches the original 2b2tAtlas data. ×8 in overworld.
    { name: "+X Highway", points: [[0, 0], [30000000, 0]], width: 6, type: "axis" },
    { name: "-X Highway", points: [[0, 0], [-30000000, 0]], width: 6, type: "axis" },
    { name: "+Z Highway", points: [[0, 0], [0, 30000000]], width: 6, type: "axis" },
    { name: "-Z Highway", points: [[0, 0], [0, -30000000]], width: 6, type: "axis" },
    // Diagonal highways (also run out to ±30M nether)
    { name: "+X,+Z Diagonal Highway", points: [[0, 0], [30000000, 30000000]], width: 3, type: "diagonal" },
    { name: "+X,-Z Diagonal Highway", points: [[0, 0], [30000000, -30000000]], width: 3, type: "diagonal" },
    { name: "-X,+Z Diagonal Highway", points: [[0, 0], [-30000000, 30000000]], width: 3, type: "diagonal" },
    { name: "-X,-Z Diagonal Highway", points: [[0, 0], [-30000000, -30000000]], width: 3, type: "diagonal" },
];

// Square ring roads: [name, radius (nether units), display width]
[
    ["World Border Ring Road", 3750000, 6], ["2.5m Ring Road", 2500000, 6],
    ["1.875m Ring Road", 1875000, 6], ["Farlands Ring Road", 1568852, 6],
    ["1.25m Ring Road", 1250000, 6], ["1m Ring Road", 1000000, 6],
    ["750k Ring Road", 750000, 6], ["500k Ring Road", 500000, 6],
    ["250k Ring Road", 250000, 6], ["125k Ring Road", 125000, 4],
    ["100k Ring Road", 100000, 4], ["75k Ring Road", 75000, 4],
    ["62.5k Ring Road", 62500, 4], ["55k Ring Road", 55000, 4],
    ["50k Ring Road", 50000, 4], ["30k Ring Road", 30000, 4],
    ["25k Ring Road", 25000, 4], ["24k Ring Road", 24000, 2],
    ["23k Ring Road", 23000, 2], ["22k Ring Road", 22000, 2],
    ["21k Ring Road", 21000, 2], ["20k Ring Road", 20000, 4],
    ["15k Ring Road", 15000, 4], ["10k Ring Road", 10000, 4],
    ["7.5k Ring Road", 7500, 4], ["5k Ring Road", 5000, 4],
    ["2.5k Ring Road", 2500, 4], ["2k Ring Road", 2000, 4],
    ["1.5k Ring Road", 1500, 4], ["1k Ring Road", 1000, 4],
    ["500 Ring Road", 500, 4], ["200 Ring Road", 200, 4],
].forEach(([name, r, width]) => HIGHWAYS.push({ name, points: squareRing(r), width, type: "ring", radius: r }));

// Diamond ring roads
[
    ["World Border Diamond Ring Road", 3750000], ["500k Diamond Ring Road", 500000],
    ["250k Diamond Ring Road", 250000], ["125k Diamond Ring Road", 125000],
    ["50k Diamond Ring Road", 50000], ["25k Diamond Ring Road", 25000],
    ["15k Diamond Ring Road", 15000], ["10k Diamond Ring Road", 10000],
    ["5k Diamond Ring Road", 5000], ["2.5k Diamond Ring Road", 2500],
    ["2k Diamond Ring Road", 2000], ["1k Diamond Ring Road", 1000],
].forEach(([name, r]) => HIGHWAYS.push({ name, points: diamondRing(r), width: 4, type: "diamond", radius: r }));

// Nether Star Ring Road
HIGHWAYS.push({
    name: "Nether Star Ring Road",
    points: [
        [-50000, 50000], [0, 125000], [50000, 50000], [125000, 0],
        [50000, -50000], [0, -125000], [-50000, -50000], [-125000, 0], [-50000, 50000],
    ],
    width: 4,
    type: "star",
});

// 50k grid (horizontal + vertical lines every 5k between -45k..45k)
[45000, 40000, 35000, 30000, 25000, 20000, 15000, 10000, 5000,
 -5000, -10000, -15000, -20000, -25000, -30000, -35000, -40000, -45000].forEach((v) => {
    HIGHWAYS.push({ name: `50k Grid z=${v}`, points: [[-50000, v], [50000, v]], width: 2, type: "grid" });
    HIGHWAYS.push({ name: `50k Grid x=${v}`, points: [[v, -50000], [v, 50000]], width: 2, type: "grid" });
});

// Convert highway points to LatLng. Nether roads project ×8 on Overworld;
// End roads are stored and rendered in native End coordinates.
function highwayLatLngs(cfg, dimension, routeDimension, points) {
    const nativeDimension = routeDimension == null ? 1 : Number(routeDimension);
    const mul = dimension === "overworld" && nativeDimension === 1 ? 8 : 1;
    return points.map((p) => blockToLatLng(cfg, p[0] * mul, p[1] * mul));
}

// Build a LayerGroup of highways relevant to the active dimension.
// Each highway is drawn as a thin visible line plus a wide invisible "hit"
// line so it is easy to hover/click; hover highlights, click selects.
const HW_COLOR = "#4da3ff";
const HW_HOVER = "#8fd0ff";
const HW_SELECTED = "#ffd24a";

/// Restore a highway's default (unselected, unhovered) style.
function styleHighwayBase(entry) {
    entry.visible.setStyle({ color: entry.hw.color || HW_COLOR, weight: entry.baseWeight, opacity: entry.baseOpacity, dashArray: "6 4" });
    entry.emphasis.setStyle({ opacity: 0 });
}

/// Apply the hover style to a highway.
function styleHighwayHover(entry) {
    entry.visible.setStyle({ color: HW_HOVER, weight: entry.baseWeight + 1, opacity: 1, dashArray: "6 4" });
    entry.emphasis.setStyle({ color: "#f5fbff", weight: entry.baseWeight + 3, opacity: 0.95, dashArray: "6 4" });
    entry.emphasis.bringToFront();
}

/// Apply the persistent selected style. Feedback is raised in the dedicated
/// emphasis pane; the base line keeps its width-derived stacking order.
function styleHighwaySelected(entry) {
    entry.visible.setStyle({
        color: HW_SELECTED,
        weight: Math.max(3, entry.baseWeight + 2),
        opacity: 1,
        dashArray: null,
    });
    entry.emphasis.setStyle({
        color: "#fff3a6",
        weight: Math.max(5, entry.baseWeight + 4),
        opacity: 1,
        dashArray: null,
    });
    entry.emphasis.bringToFront();
}

/// Select a highway, notifying Blazor so it can show the info panel.
function selectHighway(state, entry) {
    deselectLocation(state);
    if (state.selectedHighway && state.selectedHighway !== entry) styleHighwayBase(state.selectedHighway);
    state.selectedHighway = entry;
    styleHighwaySelected(entry);
    if (state.dotNetRef) {
        const hw = entry.hw;
        state.dotNetRef.invokeMethodAsync("OnHighwaySelected", {
            id: hw.id || 0,
            name: hw.name,
            type: hw.type || "highway",
            width: hw.width || 0,
            material: hw.material || null,
            status: hw.status || null,
            builderGroup: hw.builderGroup || null,
            description: hw.description || null,
            wiki: hw.wiki || null,
        });
    }
}

/// Clear the current highway selection (and notify Blazor).
function deselectHighway(state) {
    if (!state.selectedHighway) return;
    styleHighwayBase(state.selectedHighway);
    state.selectedHighway = null;
    if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnHighwayDeselected");
}

function buildHighwayLayer(dimension, state) {
    const cfg = DIMENSIONS[dimension];
    const group = L.layerGroup();
    state.highwayLines = [];
    // Prefer highways loaded from the API; fall back to the built-in canonical set.
    const highways = (state.highways && state.highways.length)
        ? state.highways.filter((highway) => dimension === "end"
            ? Number(highway.dimension) === 2
            : dimension === "nether"
                ? Number(highway.dimension) === 1
                : Number(highway.dimension) === 0 || Number(highway.dimension) === 1)
        : dimension === "end" ? [] : HIGHWAYS;
    // Paint broad infrastructure first and narrow routes last. When two routes
    // overlap (notably the Southern Canal and projected +Z Nether highway), the
    // narrow centerline remains the top target while the broad route exposes
    // selectable shoulders on either side.
    const orderedHighways = highways
        .map((hw, sourceOrder) => ({
            hw,
            sourceOrder,
            baseWeight: Math.max(1, Math.min(hw.displayWeight || hw.width || 1, 12)),
        }))
        .sort((a, b) => b.baseWeight - a.baseWeight || a.sourceOrder - b.sourceOrder);
    for (const { hw, baseWeight } of orderedHighways) {
        const latlngs = highwayLatLngs(cfg, dimension, hw.dimension, hw.points);
        const visible = L.polyline(latlngs, {
            color: hw.color || HW_COLOR,
            weight: baseWeight,
            opacity: state.highwayOpacity,
            dashArray: "6 4",
            interactive: false,
            pane: "hwPane",
            renderer: state.highwayRenderer,
        });
        // Scale the transparent hit stroke with the visible route. A canal's
        // target therefore extends beyond a highway drawn over its centerline.
        const hitWeight = (state.coarsePointer ? 28 : 16) + (baseWeight * 3);
        const touchHitRadius = TOUCH_HIGHWAY_HIT_PX + (Math.max(0, baseWeight - 1) * 2);
        const hit = L.polyline(latlngs, {
            color: "#000", weight: hitWeight, opacity: 0, interactive: true,
            bubblingMouseEvents: false,
            pane: "hwPane", renderer: state.highwayRenderer,
        });
        // World Pulse normally sits above highways. Repeat only active feedback
        // above the heatmap so hover/click remains immediately obvious.
        const emphasis = L.polyline(latlngs, {
            color: "#f5fbff", weight: baseWeight + 3, opacity: 0,
            interactive: false, pane: "hwEmphasisPane", renderer: state.highwayEmphasisRenderer,
        });
        const entry = {
            hw,
            visible,
            hit,
            emphasis,
            baseWeight,
            hitWeight,
            touchHitRadius,
            baseOpacity: state.highwayOpacity,
        };
        hit.bindTooltip(hw.name, { sticky: true });
        hit.on("mouseover", () => { if (state.selectedHighway !== entry) styleHighwayHover(entry); });
        hit.on("mouseout", () => { if (state.selectedHighway !== entry) styleHighwayBase(entry); });
        hit.on("click", (e) => {
            L.DomEvent.stopPropagation(e);
            if (Date.now() < state.suppressMapClickUntil) return;
            if (state.editing) { addEditVertex(state, e.latlng); return; }
            selectHighway(state, entry);
        });
        group.addLayer(visible);
        group.addLayer(hit);
        group.addLayer(emphasis);
        state.highwayLines.push(entry);
    }
    return group;
}

// Build the X/Z axis lines through spawn.
function buildAxesLayer(dimension) {
    const cfg = DIMENSIONS[dimension];
    // Match the axis-highway extent: 30M nether / 240M overworld (nether highways ×8).
    const reach = dimension === "overworld" ? 30000000 * 8 : 30000000;
    const segments = [
        [[0, 0], [reach, 0]], [[0, 0], [-reach, 0]],
        [[0, 0], [0, reach]], [[0, 0], [0, -reach]],
    ];
    const group = L.layerGroup();
    for (const seg of segments) {
        group.addLayer(
            L.polyline(seg.map((p) => blockToLatLng(cfg, p[0], p[1])), {
                color: "#888",
                weight: 1.5,
                opacity: 0.5,
                interactive: false,
                pane: "hwPane",
            })
        );
    }
    return group;
}

// Add/remove the highway overlay, respecting the current dimension.
function applyHighways(state, show) {
    if (state.highwayLayer) {
        state.map.removeLayer(state.highwayLayer);
        state.highwayLayer = null;
    }
    // Rebuilding the layer invalidates any selection.
    if (state.selectedHighway) {
        state.selectedHighway = null;
        if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnHighwayDeselected");
    }
    state.showHighways = show;
    if (show) {
        state.highwayLayer = buildHighwayLayer(state.dimension, state).addTo(state.map);
    }
}

// Add/remove the axes overlay.
function applyAxes(state, show) {
    if (state.axesLayer) {
        state.map.removeLayer(state.axesLayer);
        state.axesLayer = null;
    }
    state.showAxes = show;
    if (show) {
        state.axesLayer = buildAxesLayer(state.dimension).addTo(state.map);
    }
}

// --- Tile layer --------------------------------------------------------------
/// Default enabled renders (top-to-bottom z-order) for a dimension.
function defaultRenders(dim) {
    if (dim === "overworld") return ["place-obsidian", "place-world"];
    return ["place-world"];
}

function uniqueRenderIds(dim, ids) {
    const known = new Set(DIMENSIONS[dim].renders.map((render) => render.id));
    return [...new Set(Array.isArray(ids) ? ids : [])].filter((id) => known.has(id));
}

/// The enabled render descriptors for the current dimension, in the user's
/// stacking order (renderIds is top-to-bottom: index 0 = front/top).
function enabledRenders(state) {
    const cfg = DIMENSIONS[state.dimension] || DIMENSIONS.overworld;
    return uniqueRenderIds(state.dimension, state.renderIds).map((id) => cfg.renders.find((r) => r.id === id));
}

/// Rebuild the stacked spawn-render tile layers for the current dimension.
function rebuildTiles(state) {
    const cfg = DIMENSIONS[state.dimension] || DIMENSIONS.overworld;
    (state.tileLayers || []).forEach((l) => state.map.removeLayer(l));
    state.tileLayers = [];
    const baseOptions = Object.assign({ minZoom: cfg.renderMinZoom ?? MIN_ZOOM, maxZoom: MAX_ZOOM }, cfg.tileOptions);
    // renderIds are top-to-bottom; add the bottom layer first so index 0 ends on top.
    [...enabledRenders(state)].reverse().forEach((render) => {
        // The 2b2t.place render is a client-side resampled GridLayer, not a plain URL tile set.
        if (render.place) {
            state.tileLayers.push(makePlaceLayer(cfg, state.dimension, render.placeLayer || "base").addTo(state.map));
            return;
        }
        if (render.legacy) {
            state.tileLayers.push(makeLegacyLayer(cfg, render).addTo(state.map));
            return;
        }
        const url = render.dayNight ? render.url(state.isDay) : render.url();
        // A render's tile pyramid may be shallower than MAX_ZOOM (e.g. Nether 43k
        // stops at native zoom 9, End 42k at 9, Nether 5k at 6). Setting
        // maxNativeZoom makes Leaflet upscale the deepest native tiles past that
        // depth instead of requesting nonexistent tiles (which blank the map).
        const options = render.maxNativeZoom != null
            ? Object.assign({}, baseOptions, { maxNativeZoom: render.maxNativeZoom })
            : baseOptions;
        state.tileLayers.push(L.tileLayer(url, options).addTo(state.map));
    });
}

// Map a current-CRS output tile into the original v1 pyramid. The original
// Nether scale is not a power of two: assigning its PNGs directly to the new
// CRS shifts spawn and stretches all distances by ~19%. Keep both transforms.
function planLegacyTiles(cfg, render, coords, size = 256) {
    const old = render.legacy;
    const bpp = 64 * cfg.scaleFactor / 2 ** coords.z;
    const x0 = coords.x * size * bpp - cfg.offset;
    const z0 = coords.y * size * bpp - cfg.offset;
    const sourceZoom = Math.max(0, Math.min(render.maxNativeZoom,
        Math.round(Math.log2(64 * old.scaleFactor / bpp))));
    const sourceBpp = 64 * old.scaleFactor / 2 ** sourceZoom;
    const span = 256 * sourceBpp;
    // The original positive-index pyramids fit inside this quadtree. Clipping
    // also makes overview tiles bounded: zoom -10 still needs only the root PNG.
    const maxIndex = 2 ** sourceZoom - 1;
    const minX = Math.max(0, Math.floor((x0 + old.offset) / span));
    const minY = Math.max(0, Math.floor((z0 + old.offset) / span));
    const maxX = Math.min(maxIndex, Math.ceil((x0 + size * bpp + old.offset) / span) - 1);
    const maxY = Math.min(maxIndex, Math.ceil((z0 + size * bpp + old.offset) / span) - 1);
    const tiles = [];
    for (let y = minY; y <= maxY; y++) {
        for (let x = minX; x <= maxX; x++) {
            tiles.push({ x, y, z: sourceZoom,
                dx: (x * span - old.offset - x0) / bpp,
                dy: (y * span - old.offset - z0) / bpp,
                width: span / bpp });
        }
    }
    return tiles;
}

function makeLegacyLayer(cfg, render) {
    const LegacyGrid = L.GridLayer.extend({
        createTile(coords, done) {
            const canvas = L.DomUtil.create("canvas", "leaflet-tile");
            canvas.width = canvas.height = 256;
            const context = canvas.getContext("2d");
            context.imageSmoothingEnabled = false;
            canvas._legacyControllers = [];
            const plan = planLegacyTiles(cfg, render, coords);
            Promise.all(plan.map(async (tile) => {
                const controller = new AbortController();
                canvas._legacyControllers.push(controller);
                const url = render.url().replace("{z}", tile.z).replace("{y}", tile.y).replace("{x}", tile.x);
                let bitmap;
                try {
                    const response = await fetch(url, { mode: "cors", credentials: "omit", cache: "force-cache", signal: controller.signal });
                    if (!response.ok || canvas._legacyCancelled) return;
                    bitmap = await createImageBitmap(await response.blob());
                    if (!canvas._legacyCancelled)
                        context.drawImage(bitmap, tile.dx, tile.dy, tile.width, tile.width);
                } catch (error) {
                    if (error?.name !== "AbortError") console.debug("Legacy tile unavailable", url);
                } finally { bitmap?.close(); }
            })).then(() => { if (!canvas._legacyCancelled) done(null, canvas); });
            return canvas;
        },
    });
    const layer = new LegacyGrid({ minZoom: MIN_ZOOM, maxZoom: MAX_ZOOM,
        updateWhenIdle: true, updateWhenZooming: false, keepBuffer: 1 });
    layer.on("tileunload", ({ tile }) => {
        tile._legacyCancelled = true;
        for (const controller of tile._legacyControllers || []) controller.abort();
        tile._legacyControllers = [];
    });
    return layer;
}

function clearNocomLayer(state) {
    if (state.nocomLayer) {
        state.map.removeLayer(state.nocomLayer);
        state.nocomLayer = null;
    }
}

function normalizeNocomColor(value) {
    const color = String(value || "").trim();
    return /^#[0-9a-f]{6}$/i.test(color) ? color.toLowerCase() : "#00e5ff";
}

function ensureNocomColorFilter(state) {
    if (state.nocomColorMatrix) return;
    const svgNamespace = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(svgNamespace, "svg");
    svg.setAttribute("width", "0");
    svg.setAttribute("height", "0");
    svg.setAttribute("aria-hidden", "true");
    svg.style.position = "absolute";
    svg.style.pointerEvents = "none";
    const filter = document.createElementNS(svgNamespace, "filter");
    filter.id = `atlas-nocom-color-${++nocomFilterSequence}`;
    filter.setAttribute("color-interpolation-filters", "sRGB");
    filter.setAttribute("x", "-5%");
    filter.setAttribute("y", "-5%");
    filter.setAttribute("width", "110%");
    filter.setAttribute("height", "110%");
    const matrix = document.createElementNS(svgNamespace, "feColorMatrix");
    matrix.setAttribute("type", "matrix");
    filter.appendChild(matrix);
    svg.appendChild(filter);
    document.body.appendChild(svg);
    state.nocomColorFilter = svg;
    state.nocomColorFilterId = filter.id;
    state.nocomColorMatrix = matrix;
}

function applyNocomColor(state, value) {
    state.nocomColor = normalizeNocomColor(value);
    if (!state.nocomLayer) return;
    const container = state.nocomLayer.getContainer();
    if (!container) return;
    if (state.nocomStyle === "contrast") {
        // Preserve tiny overview trails below the terrain provider's last LOD.
        // At terrain zooms, avoid shadows that exaggerate dense tile boundaries.
        container.style.filter = state.map.getZoom() < -2
            ? "brightness(1.35) drop-shadow(0 0 1px #ffd7ff) drop-shadow(0 0 2px #d393ff)"
            : "";
        container.style.imageRendering = "pixelated";
        return;
    }
    container.style.imageRendering = "";
    if (state.nocomStyle !== "custom") {
        container.style.filter = "";
        return;
    }
    ensureNocomColorFilter(state);
    const red = parseInt(state.nocomColor.slice(1, 3), 16) / 255;
    const green = parseInt(state.nocomColor.slice(3, 5), 16) / 255;
    const blue = parseInt(state.nocomColor.slice(5, 7), 16) / 255;
    state.nocomColorMatrix.setAttribute("values",
        `0 0 0 0 ${red}  0 0 0 0 ${green}  0 0 0 0 ${blue}  0 0 0 1 0`);
    container.style.filter = `url("#${state.nocomColorFilterId}") drop-shadow(0 0 1px rgba(2, 5, 7, 0.98)) drop-shadow(0 0 2px ${state.nocomColor})`;
}

// --- Borders, spawn radius, location markers, measure tool -------------------

/// Escape user-supplied text before injecting into popup HTML.
function escapeHtml(s) {
    return String(s ?? "").replace(/[&<>"']/g, (c) => (
        { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]
    ));
}

// --- 2b2t.place live tiles (resampled overlay) -------------------------------
// 2b2t.place serves a 512px quadtree at /tiles/{layer}/{lod}/{dim}/{sx}/{sy}/t.{tx}.{ty}.webp
// (LOD 0 = 1px/block, origin 0,0, LOD 0..10 each doubling blocks/tile, sx=floor(tx/32)).
// Their grid origin does NOT align to our 128000-offset CRS grid at low zoom, so we
// resample: for each of our tiles, composite the overlapping 2b2t.place tiles at the
// best-matching LOD onto a canvas. Used with explicit permission from the 2b2t.place team.
//
// Tiles are fetched through the API origin (/tiles/place/...), which reverse-proxies
// 2b2t.place so Cloudflare caches them at the edge (offloads their bandwidth).
// Empty = same origin (local dev where the API hosts the client); in production the
// static frontend and API are different origins, so Blazor calls setApiBase().
let PLACE_BASE = "";
const PLACE_TILE_PX = 512;
const PLACE_MAX_LOD = 10;
const PLACE_MIN_ZOOM = -2;
const PLACE_MIN_ZOOM_BY_DIMENSION = { overworld: PLACE_MIN_ZOOM, nether: PLACE_MIN_ZOOM, end: -2 };
const PLACE_MAX_SOURCES_PER_TILE = 16;
const PLACE_DIM_INT = { overworld: 0, nether: 1, end: 2 };

/// Point the 2b2t.place proxy at the API origin (Blazor passes its configured ApiBaseUrl).
export function setApiBase(url) {
    PLACE_BASE = (url || "").replace(/\/+$/, "");
}

/// Build a resampling GridLayer that draws 2b2t.place tiles onto our CRS grid.
/// Used as a stacked spawn-render (see rebuildTiles), so it lives in the default
/// tile pane and orders with its sibling renders.
function makePlaceLayer(cfg, dimKey, layerName) {
    const dimInt = PLACE_DIM_INT[dimKey] ?? 0;
    const stats = { sourceRequests: 0, skippedTiles: 0, cancelledTiles: 0 };
    const PlaceGrid = L.GridLayer.extend({
        createTile: function (coords, done) {
            const canvas = L.DomUtil.create("canvas", "leaflet-tile");
            const size = this.getTileSize();
            canvas.width = size.x;
            canvas.height = size.y;
            const ctx = canvas.getContext("2d");
            ctx.imageSmoothingEnabled = false;

            // World-block bounds of this Leaflet tile (invert our CRS pixel math).
            const bpp = (64 * cfg.scaleFactor) / Math.pow(2, coords.z); // blocks per pixel
            const bx0 = coords.x * size.x * bpp - cfg.offset;
            const bz0 = coords.y * size.y * bpp - cfg.offset;
            const blockW = size.x * bpp;
            const blockH = size.y * bpp;

            // Pick the LOD whose native resolution best matches this tile.
            const lod = Math.max(0, Math.min(PLACE_MAX_LOD, Math.round(Math.log2(bpp))));
            const tw = PLACE_TILE_PX * Math.pow(2, lod); // their tile block-width

            const txMin = Math.floor(bx0 / tw), txMax = Math.floor((bx0 + blockW - 1e-6) / tw);
            const tyMin = Math.floor(bz0 / tw), tyMax = Math.floor((bz0 + blockH - 1e-6) / tw);
            const sourceCount = (txMax - txMin + 1) * (tyMax - tyMin + 1);

            // The upstream pyramid stops at LOD 10. Below PLACE_MIN_ZOOM one
            // output tile would otherwise fan out into hundreds or thousands of
            // source images. Leave the overview transparent so the world border,
            // highways and other reference layers remain smooth and interactive.
            if (sourceCount > PLACE_MAX_SOURCES_PER_TILE) {
                stats.skippedTiles++;
                setTimeout(() => done(null, canvas), 0);
                return canvas;
            }

            let pending = 0, settled = false;
            const finish = () => { if (!settled && pending === 0) { settled = true; done(null, canvas); } };
            canvas._placeControllers = [];
            for (let tx = txMin; tx <= txMax; tx++) {
                for (let ty = tyMin; ty <= tyMax; ty++) {
                    // 2b2t.place computes the sector as (t/32)>>0 (truncate toward zero),
                    // NOT floor — must match or negative-coord tiles 404.
                    const sx = Math.trunc(tx / 32), sy = Math.trunc(ty / 32);
                    const controller = new AbortController();
                    canvas._placeControllers.push(controller);
                    pending++;
                    stats.sourceRequests++;
                    const url = `${PLACE_BASE}/tiles/place/${layerName}/${lod}/${dimInt}/${sx}/${sy}/t.${tx}.${ty}.webp`;
                    fetch(url, { mode: "cors", credentials: "omit", cache: "no-store", signal: controller.signal })
                    .then(async (response) => {
                        if (!response.ok || response.status === 204 || canvas._placeCancelled) return;
                        const sourceLod = Number.parseInt(response.headers.get("X-Atlas-Place-Lod") ?? `${lod}`, 10);
                        const sourceTx = Number.parseInt(response.headers.get("X-Atlas-Place-Tx") ?? `${tx}`, 10);
                        const sourceTy = Number.parseInt(response.headers.get("X-Atlas-Place-Ty") ?? `${ty}`, 10);
                        const fallbackScale = Math.pow(2, Math.max(0, sourceLod - lod));
                        const blob = await response.blob();
                        const bitmap = await createImageBitmap(blob);
                        if (canvas._placeCancelled) { bitmap.close(); return; }
                        const cropW = bitmap.width / fallbackScale;
                        const cropH = bitmap.height / fallbackScale;
                        const cropX = (tx - sourceTx * fallbackScale) * cropW;
                        const cropY = (ty - sourceTy * fallbackScale) * cropH;
                        const dx = (tx * tw - bx0) / blockW * size.x;
                        const dy = (ty * tw - bz0) / blockH * size.y;
                        const dw = tw / blockW * size.x, dh = tw / blockH * size.y;
                        ctx.drawImage(bitmap, cropX, cropY, cropW, cropH, dx, dy, dw, dh);
                        bitmap.close();
                    })
                    .catch((error) => {
                        if (error?.name !== "AbortError") console.debug("Place tile unavailable", url);
                    })
                    .finally(() => {
                        pending--;
                        finish();
                    });
                }
            }
            if (pending === 0) setTimeout(finish, 0);
            return canvas;
        },
    });
    const layer = new PlaceGrid({
        minZoom: PLACE_MIN_ZOOM_BY_DIMENSION[dimKey] ?? PLACE_MIN_ZOOM,
        maxZoom: MAX_ZOOM,
        updateWhenIdle: true,
        updateWhenZooming: false,
        keepBuffer: 1,
    });
    layer._atlasPlaceStats = stats;
    layer.on("tileunload", (event) => {
        const canvas = event.tile;
        stats.cancelledTiles++;
        canvas._placeCancelled = true;
        for (const controller of canvas._placeControllers || []) controller.abort();
        canvas._placeControllers = [];
    });
    return layer;
}

function borderPolyline(cfg, blocks, color, dashArray) {
    const corners = [[blocks, blocks], [blocks, -blocks], [-blocks, -blocks], [-blocks, blocks], [blocks, blocks]];
    return L.polyline(corners.map((point) => blockToLatLng(cfg, point[0], point[1])), {
        color, weight: 2, opacity: 0.85, dashArray, interactive: false,
    });
}

/// World-border references. Red is the Overworld border; amber is the Nether
/// border. Cross-dimension projections use the 8:1 portal coordinate ratio.
function buildBordersLayer(dimension) {
    const cfg = DIMENSIONS[dimension];
    if (dimension === "overworld") {
        return L.layerGroup([
            borderPolyline(cfg, 30000000, "#ff5252", "10 6"),
            borderPolyline(cfg, 240000000, "#ffb300", "5 5"),
        ]);
    }
    if (dimension === "nether") {
        return L.layerGroup([
            borderPolyline(cfg, 3750000, "#ff5252", "10 6"),
            borderPolyline(cfg, 30000000, "#ffb300", "5 5"),
        ]);
    }
    return L.layerGroup([borderPolyline(cfg, 30000000, "#ff5252", "10 6")]);
}

/// 2048-block spawn radius circle around (0,0).
function buildSpawnRadiusLayer(dimension) {
    const cfg = DIMENSIONS[dimension];
    return L.circle(blockToLatLng(cfg, 0, 0), {
        radius: 2048 / cfg.scaleFactor,
        color: "#00e676", weight: 2, opacity: 0.7,
        fillColor: "#00e676", fillOpacity: 0.06, interactive: false,
    });
}

function applyBorders(state, show) {
    if (state.bordersLayer) { state.map.removeLayer(state.bordersLayer); state.bordersLayer = null; }
    state.showBorders = show;
    if (show) state.bordersLayer = buildBordersLayer(state.dimension).addTo(state.map);
}

function applySpawnRadius(state, show) {
    if (state.spawnLayer) { state.map.removeLayer(state.spawnLayer); state.spawnLayer = null; }
    state.showSpawnRadius = show;
    if (show) state.spawnLayer = buildSpawnRadiusLayer(state.dimension).addTo(state.map);
}

// --- Location markers --------------------------------------------------------

const DIM_INDEX = { overworld: 0, nether: 1, end: 2 };

const LOC_COLOR = "#ffd24a";

// 16 named marker colors (Minecraft dye palette), matching the original atlas.
const MARKER_PALETTE = {
    black: "#212529", blue: "#3b5bdb", brown: "#8b5a2b", cyan: "#22b8cf",
    gray: "#868e96", green: "#37b24d", lightblue: "#74c0fc", lightgray: "#ced4da",
    lime: "#82c91e", magenta: "#e64980", orange: "#f76707", pink: "#f783ac",
    purple: "#9c36b5", red: "#f03e3e", white: "#f8f9fa", yellow: "#ffd43b",
};
const PALETTE_NAMES = Object.keys(MARKER_PALETTE);

// Default marker settings; overridable from Blazor via setMarkerSettings.
const DEFAULT_MARKER_SETTINGS = { icon: "circles", color: "dimension", opacity: 0.9, scale: 1.0 };

/// Case-insensitive test for the special "End Portal" rows (own layer).
function isEndPortal(loc) {
    return typeof loc.name === "string" && loc.name.trim().toLowerCase() === "end portal";
}

/// Resolve a marker's palette color *name* (fixed, or a stable random one).
function resolveColorName(state, loc) {
    const c = state.markerSettings.color;
    if (c === "dimension") return state.dimension === "nether" ? "yellow" : "red";
    if (c && c !== "random") return c;
    if (!loc._colorName) loc._colorName = PALETTE_NAMES[(Math.random() * PALETTE_NAMES.length) | 0];
    return loc._colorName;
}

// The "pins" mode is an inline-SVG teardrop; mansions / gapples / banner use the
// original 2b2tAtlas PNG sprites (banner = the colored minecraft-banner marker.{color}.png).
const PIN_SVG = (c) => `<svg viewBox="0 0 24 34" width="100%" height="100%"><path d="M12 1C5.9 1 1 5.9 1 12c0 7.7 11 21 11 21s11-13.3 11-21C23 5.9 18.1 1 12 1z" fill="${c}" stroke="#111" stroke-width="1.5"/><circle cx="12" cy="12" r="4.5" fill="#000" fill-opacity="0.35"/></svg>`;

/// Build the divIcon for the inline-SVG teardrop pin.
function pinIcon(color, scale, opacity, selected) {
    const mul = selected ? 1.28 : 1;
    const w = 24 * scale * mul, h = 34 * scale * mul;
    const html = `<div class="atlas-svg-marker${selected ? " sel" : ""}" style="width:${w}px;height:${h}px;opacity:${selected ? 1 : opacity}">${PIN_SVG(color)}</div>`;
    return L.divIcon({ className: "atlas-marker-wrap", html, iconSize: [w, h], iconAnchor: [12 * scale * mul, 34 * scale * mul] });
}

// PNG icon defs for mansions / gapples / banner (original atlas sprites, from
// markers.vue). `banner` picks a per-color minecraft-banner PNG.
const LOC_PNG_DEFS = {
    mansions: { url: () => "Images/marker.mansion.png", w: 24, h: 24, ax: 12, ay: 12, selUrl: "Images/marker.mansion.selected.png", sw: 30, sh: 30, sax: 15, say: 15 },
    gapples: { url: () => "Images/marker.apple.png", w: 24, h: 30, ax: 12, ay: 15, selUrl: "Images/marker.apple.selected.png", sw: 30, sh: 37, sax: 15, say: 20 },
    banner: { url: (name) => `Images/marker.${name}.png`, w: 24, h: 24, ax: 12, ay: 24, selUrl: "Images/marker.selected.png", sw: 30, sh: 30, sax: 15, say: 30 },
};

/// Build the divIcon for an End Portal marker (original atlas end-portal-frame image).
function endPortalIcon(selected, scale) {
    const s = (selected ? 30 : 25) * scale;
    return L.icon({
        iconUrl: selected ? "Images/portal_selected.png" : "Images/portal.png",
        iconSize: [s, s],
        iconAnchor: [s / 2, s / 2],
        className: "atlas-portal-marker",
    });
}

/// Create a location marker honoring the current marker settings. `opacityMul`
/// dims cross-projected markers. Attaches an `_applySel(bool)` closure so
/// selection works for both circles and SVG/image icons.
function makeLocationMarker(state, loc, latlng, opacityMul = 1) {
    const ms = state.markerSettings;
    const op = ms.opacity * opacityMul;
    const name = resolveColorName(state, loc);
    const color = MARKER_PALETTE[name] || LOC_COLOR;
    if (ms.icon === "circles") {
        const r = 7 * ms.scale;
        const marker = L.circleMarker(latlng, {
            radius: r,
            color: "#111",
            weight: 1,
            fillColor: color,
            fillOpacity: op,
            bubblingMouseEvents: false,
        });
        marker._applySel = (sel) => {
            if (sel) marker.setStyle({ radius: r + 3, color: "#fff", weight: 2, fillColor: color, fillOpacity: 1 });
            else marker.setStyle({ radius: r, color: "#111", weight: 1, fillColor: color, fillOpacity: op });
        };
        return marker;
    }
    if (ms.icon === "markers") { // inline-SVG teardrop pin
        const marker = L.marker(latlng, {
            icon: pinIcon(color, ms.scale, op, false),
            bubblingMouseEvents: false,
        });
        marker._applySel = (sel) => { marker.setIcon(pinIcon(color, ms.scale, op, sel)); marker.setZIndexOffset(sel ? 1000 : 0); };
        return marker;
    }
    // PNG modes: mansions / gapples / banner (banner = colored marker.{name}.png)
    const def = LOC_PNG_DEFS[ms.icon] || LOC_PNG_DEFS.banner;
    const s = ms.scale;
    const baseIcon = L.icon({ iconUrl: def.url(name), iconSize: [def.w * s, def.h * s], iconAnchor: [def.ax * s, def.ay * s], className: "atlas-img-marker" });
    const selIcon = L.icon({ iconUrl: def.selUrl, iconSize: [def.sw * s, def.sh * s], iconAnchor: [def.sax * s, def.say * s], className: "atlas-img-marker sel" });
    const marker = L.marker(latlng, { icon: baseIcon, opacity: op, bubblingMouseEvents: false });
    marker._applySel = (sel) => { marker.setIcon(sel ? selIcon : baseIcon); marker.setOpacity(sel ? 1 : op); marker.setZIndexOffset(sel ? 1000 : 0); };
    return marker;
}

/// Where a location should be drawn in the CURRENT dimension, honoring the
/// Nether:Overworld 1:8 ratio so overworld bases appear at their nether travel
/// coordinate (and nether bases at their overworld coordinate). Returns
/// {x, z, cross, homeDim} in the current dimension's block space, or null if the
/// location does not project into this dimension (e.g. End <-> others).
function locDisplayCoords(state, loc) {
    const locDim = loc.dimension ?? 0;
    const curDim = DIM_INDEX[state.dimension];
    if (locDim === curDim) return { x: loc.x, z: loc.z, cross: false };
    if (state.dimension === "nether" && locDim === 0) return { x: Math.round(loc.x / 8), z: Math.round(loc.z / 8), cross: true, homeDim: "Overworld" };
    if (state.dimension === "overworld" && locDim === 1) return { x: loc.x * 8, z: loc.z * 8, cross: true, homeDim: "Nether" };
    return null;
}

/// Wire a freshly-built marker into the layer + selection registry.
function registerMarker(state, group, marker, loc, i, crossLabel) {
    marker._locIndex = loc.index ?? i;
    const tip = crossLabel
        ? `${escapeHtml(loc.name)} <span style="opacity:.7">· ${crossLabel}</span>`
        : escapeHtml(loc.name);
    marker.bindTooltip(tip, { direction: "top", offset: [0, -6] });
    marker.on("click", (e) => {
        L.DomEvent.stopPropagation(e);
        if (Date.now() < state.suppressMapClickUntil) return;
        if (state.editing) { addEditVertex(state, marker.getLatLng()); return; }
        if (state.measuring) { addMeasurePoint(state, marker.getLatLng()); return; }
        selectLocation(state, marker);
    });
    state.markerByIndex[marker._locIndex] = marker;
    group.addLayer(marker);
}

/// Build a LayerGroup of regular location markers for the current dimension
/// (incl. cross-projected Overworld<->Nether markers).
function buildLocationLayer(state) {
    const cfg = DIMENSIONS[state.dimension];
    const group = L.layerGroup();
    state.locations.forEach((loc, i) => {
        if (isEndPortal(loc)) return; // shown on the dedicated End-portal layer
        const d = locDisplayCoords(state, loc);
        if (!d) return;
        const marker = makeLocationMarker(state, loc, blockToLatLng(cfg, d.x, d.z), d.cross ? 0.55 : 1);
        registerMarker(state, group, marker, loc, i, d.cross ? d.homeDim : null);
    });
    return group;
}

/// Build the dedicated End Portal layer (only the "End Portal" rows), including
/// cross-projected markers so nether travellers can find them.
function buildEndPortalLayer(state) {
    const cfg = DIMENSIONS[state.dimension];
    const group = L.layerGroup();
    state.locations.forEach((loc, i) => {
        if (!isEndPortal(loc)) return;
        const d = locDisplayCoords(state, loc);
        if (!d) return;
        const marker = L.marker(blockToLatLng(cfg, d.x, d.z), {
            icon: endPortalIcon(false, state.markerSettings.scale),
            bubblingMouseEvents: false,
        });
        marker._applySel = (sel) => { marker.setIcon(endPortalIcon(sel, state.markerSettings.scale)); marker.setZIndexOffset(sel ? 1000 : 0); };
        if (d.cross) marker.setOpacity(0.6);
        registerMarker(state, group, marker, loc, i, d.cross ? d.homeDim : null);
    });
    return group;
}

/// Draw every applicable marker as a tiny interactive Canvas dot at world
/// overview zooms. This preserves global visibility without creating thousands
/// of SVG/icon DOM nodes. Leaflet's shared Canvas renderer spatially hit-tests
/// hover/click events with a larger invisible tolerance than the painted dots.
function buildLocationOverviewLayer(state) {
    const cfg = DIMENSIONS[state.dimension];
    const group = L.layerGroup();
    state.locations.forEach((loc, i) => {
        const portal = isEndPortal(loc);
        if (portal ? !state.showEndPortals || state.dimension === "end" : !state.showLocations) return;
        const display = locDisplayCoords(state, loc);
        if (!display) return;
        const color = portal
            ? "#55efc4"
            : MARKER_PALETTE[resolveColorName(state, loc)] || LOC_COLOR;
        const radius = Math.max(3, 4 * state.markerSettings.scale);
        const opacity = Math.max(0.65, state.markerSettings.opacity * (display.cross ? 0.7 : 1));
        const marker = L.circleMarker(blockToLatLng(cfg, display.x, display.z), {
            renderer: state.locationOverviewRenderer,
            pane: "locOverviewPane",
            radius,
            stroke: false,
            fillColor: color,
            fillOpacity: opacity,
            interactive: true,
            bubblingMouseEvents: false,
        });
        marker._applySel = (selected) => {
            marker.setRadius(selected ? radius + 2 : radius);
            marker.setStyle({ fillOpacity: selected ? 1 : opacity });
        };
        registerMarker(state, group, marker, loc, i, display.cross ? display.homeDim : null);
    });
    return group;
}

/// Reset a marker to its default (unselected) style.
function styleMarkerBase(marker) {
    if (marker._applySel) marker._applySel(false);
}

/// Select a location marker, highlight it, and notify Blazor to show the detail panel.
function selectLocation(state, marker) {
    deselectHighway(state);
    if (state.selectedMarker && state.selectedMarker !== marker) styleMarkerBase(state.selectedMarker);
    state.selectedMarker = marker;
    if (marker._applySel) marker._applySel(true);
    if (marker.bringToFront) marker.bringToFront();
    if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnLocationSelected", marker._locIndex);
}

/// Clear the current location selection (and notify Blazor).
function deselectLocation(state) {
    if (!state.selectedMarker) return;
    styleMarkerBase(state.selectedMarker);
    state.selectedMarker = null;
    if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnLocationDeselected");
}

// --- Touch/pen feature selection --------------------------------------------

function distanceSquared(a, b) {
    const dx = a.x - b.x;
    const dy = a.y - b.y;
    return (dx * dx) + (dy * dy);
}

function pointToSegmentDistanceSquared(point, a, b) {
    const vx = b.x - a.x;
    const vy = b.y - a.y;
    if (vx === 0 && vy === 0) return distanceSquared(point, a);
    const t = Math.max(0, Math.min(1,
        (((point.x - a.x) * vx) + ((point.y - a.y) * vy)) / ((vx * vx) + (vy * vy))));
    return distanceSquared(point, L.point(a.x + (t * vx), a.y + (t * vy)));
}

function visitLatLngSegments(latlngs, visitor) {
    if (!Array.isArray(latlngs) || latlngs.length === 0) return;
    if (typeof latlngs[0]?.lat === "number") {
        for (let i = 1; i < latlngs.length; i++) visitor(latlngs[i - 1], latlngs[i]);
        return;
    }
    for (const nested of latlngs) visitLatLngSegments(nested, visitor);
}

/// Find the closest currently-rendered location/end-portal marker to a screen
/// point. This avoids depending on a 6-14px SVG/Canvas target on touch screens.
function findTouchLocation(state, point) {
    const limitSquared = TOUCH_LOCATION_HIT_PX * TOUCH_LOCATION_HIT_PX;
    let best = null;
    let bestDistance = limitSquared;
    for (const marker of Object.values(state.markerByIndex || {})) {
        if (!marker?.getLatLng) continue;
        const markerPoint = state.map.latLngToContainerPoint(marker.getLatLng());
        const distance = distanceSquared(point, markerPoint);
        if (distance <= bestDistance) {
            best = marker;
            bestDistance = distance;
        }
    }
    return best;
}

/// Find the closest visible highway to a screen point. Highway geometry may be
/// nested (multi-polylines), so walk every projected segment.
function findTouchHighway(state, point) {
    if (!state.showHighways || !state.highwayLayer) return null;
    let best = null;
    let bestDistance = Number.POSITIVE_INFINITY;
    for (const entry of state.highwayLines || []) {
        const hitRadius = entry.touchHitRadius || TOUCH_HIGHWAY_HIT_PX;
        const limitSquared = hitRadius * hitRadius;
        visitLatLngSegments(entry.hit?.getLatLngs?.(), (from, to) => {
            const distance = pointToSegmentDistanceSquared(
                point,
                state.map.latLngToContainerPoint(from),
                state.map.latLngToContainerPoint(to));
            if (distance > limitSquared) return;
            // At the same geometric distance prefer the narrower route, which
            // is also the route painted on top. Wider routes remain reachable
            // through the extra hit radius beyond that center target.
            if (distance < bestDistance ||
                (distance === bestDistance && entry.baseWeight < (best?.baseWeight ?? Number.POSITIVE_INFINITY))) {
                best = entry;
                bestDistance = distance;
            }
        });
    }
    return best;
}

/// Select the topmost nearby interactive feature after a genuine tap. Locations
/// win over roads, matching the map pane order. The following synthesized click
/// is ignored so it cannot immediately clear the selection.
function selectTouchFeature(state, clientX, clientY) {
    if (state.editing || state.measuring || state.placingMarker) return false;
    const rect = state.map.getContainer().getBoundingClientRect();
    const point = L.point(clientX - rect.left, clientY - rect.top);
    const marker = findTouchLocation(state, point);
    if (marker) {
        selectLocation(state, marker);
        state.suppressMapClickUntil = Date.now() + TOUCH_MAP_CLICK_SUPPRESSION_MS;
        return true;
    }
    const highway = findTouchHighway(state, point);
    if (highway) {
        selectHighway(state, highway);
        state.suppressMapClickUntil = Date.now() + TOUCH_MAP_CLICK_SUPPRESSION_MS;
        return true;
    }
    return false;
}

/// Leaflet synthesizes click events after touchend/pointerup. Some mobile
/// browsers target the map rather than a tiny SVG/Canvas feature, so install a
/// passive gesture observer that resolves a nearby feature without interfering
/// with drag, pinch-zoom, controls, or ordinary desktop mouse handling.
function installTouchFeatureSelection(state) {
    const container = state.map.getContainer();
    let gesture = null;
    const movedTooFar = (clientX, clientY) => gesture &&
        (((clientX - gesture.x) ** 2) + ((clientY - gesture.y) ** 2) >
            (TOUCH_TAP_MAX_MOVE_PX * TOUCH_TAP_MAX_MOVE_PX));
    const isControl = (target) => target instanceof Element && !!target.closest(".leaflet-control");

    const cleanupCallbacks = [];
    const listen = (name, handler, options) => {
        container.addEventListener(name, handler, options);
        cleanupCallbacks.push(() => container.removeEventListener(name, handler, options));
    };

    if (window.PointerEvent) {
        const isTouchPointer = (event) => event.pointerType === "touch" || event.pointerType === "pen";
        const onPointerDown = (event) => {
            if (!isTouchPointer(event) || isControl(event.target)) return;
            if (gesture && gesture.pointerId !== event.pointerId) {
                gesture.cancelled = true;
                return;
            }
            gesture = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, cancelled: false };
        };
        const onPointerMove = (event) => {
            if (!gesture || gesture.pointerId !== event.pointerId) return;
            if (movedTooFar(event.clientX, event.clientY)) gesture.cancelled = true;
        };
        const onPointerUp = (event) => {
            if (!gesture || gesture.pointerId !== event.pointerId) return;
            const tapped = !gesture.cancelled && !movedTooFar(event.clientX, event.clientY) && !isControl(event.target);
            gesture = null;
            if (tapped) selectTouchFeature(state, event.clientX, event.clientY);
        };
        const onPointerCancel = (event) => {
            if (gesture?.pointerId === event.pointerId) gesture = null;
        };
        listen("pointerdown", onPointerDown, { capture: true, passive: true });
        listen("pointermove", onPointerMove, { capture: true, passive: true });
        listen("pointerup", onPointerUp, { capture: true, passive: true });
        listen("pointercancel", onPointerCancel, { capture: true, passive: true });
    } else {
        const onTouchStart = (event) => {
            if (event.touches.length !== 1 || isControl(event.target)) {
                gesture = null;
                return;
            }
            const touch = event.touches[0];
            gesture = { identifier: touch.identifier, x: touch.clientX, y: touch.clientY, cancelled: false };
        };
        const onTouchMove = (event) => {
            if (!gesture || event.touches.length !== 1) {
                gesture = null;
                return;
            }
            const touch = event.touches[0];
            if (touch.identifier !== gesture.identifier || movedTooFar(touch.clientX, touch.clientY)) gesture.cancelled = true;
        };
        const onTouchEnd = (event) => {
            if (!gesture || event.touches.length !== 0) {
                gesture = null;
                return;
            }
            const touch = Array.from(event.changedTouches).find((item) => item.identifier === gesture.identifier);
            const tapped = touch && !gesture.cancelled && !movedTooFar(touch.clientX, touch.clientY) && !isControl(event.target);
            gesture = null;
            if (tapped) selectTouchFeature(state, touch.clientX, touch.clientY);
        };
        const cancelTouch = () => { gesture = null; };
        listen("touchstart", onTouchStart, { capture: true, passive: true });
        listen("touchmove", onTouchMove, { capture: true, passive: true });
        listen("touchend", onTouchEnd, { capture: true, passive: true });
        listen("touchcancel", cancelTouch, { capture: true, passive: true });
    }

    state.touchSelectionCleanup = () => {
        gesture = null;
        for (const cleanup of cleanupCallbacks) cleanup();
    };
}

/// Rebuild the regular + End-portal marker layers from current state, preserving
/// the active selection where possible (used on toggle, dimension + settings change).
function refreshLocations(state) {
    if (state.locationLayer) { state.map.removeLayer(state.locationLayer); state.locationLayer = null; }
    if (state.endPortalLayer) { state.map.removeLayer(state.endPortalLayer); state.endPortalLayer = null; }
    if (state.locationOverviewLayer) { state.map.removeLayer(state.locationOverviewLayer); state.locationOverviewLayer = null; }
    const selIdx = state.selectedMarker ? state.selectedMarker._locIndex : null;
    state.selectedMarker = null;
    state.markerByIndex = {};

    if (state.locations.length) {
        if (state.map.getZoom() >= MIN_LOCATION_ZOOM) {
            if (state.showLocations) state.locationLayer = buildLocationLayer(state).addTo(state.map);
            if (state.showEndPortals && state.dimension !== "end") state.endPortalLayer = buildEndPortalLayer(state).addTo(state.map);
        } else if (state.showLocations || state.showEndPortals) {
            state.locationOverviewLayer = buildLocationOverviewLayer(state).addTo(state.map);
        }
    }

    if (selIdx != null && state.markerByIndex[selIdx]) {
        state.selectedMarker = state.markerByIndex[selIdx];
        state.selectedMarker._applySel(true);
        if (state.selectedMarker.bringToFront) state.selectedMarker.bringToFront();
    } else if (selIdx != null && state.dotNetRef) {
        state.dotNetRef.invokeMethodAsync("OnLocationDeselected");
    }
}

/// Toggle the regular locations layer.
function applyLocations(state, show) {
    state.showLocations = show;
    refreshLocations(state);
}

// --- Base renders (data-driven per-location render overlays) -----------------
// Each Render row is a sparse high-detail world download placed from authoritative
// chunk bounds. Legacy rows fall back to a scale-centered rectangle.

/// Parse a Scale string like "256k", "7k", "1.25m" (or a number) into block extent.
function parseScaleBlocks(scale) {
    if (typeof scale === "number") return scale;
    if (typeof scale !== "string") return NaN;
    const m = scale.trim().toLowerCase().match(/^([\d.]+)\s*([km]?)$/);
    if (!m) return NaN;
    const n = parseFloat(m[1]);
    return m[2] === "k" ? n * 1e3 : m[2] === "m" ? n * 1e6 : n;
}

/// Resolve one render's exact or legacy fallback bounds in the current CRS.
function getBaseRenderBounds(cfg, r) {
    const exact = [r.minX, r.minZ, r.maxXExclusive, r.maxZExclusive]
        .every(Number.isFinite) && r.minX < r.maxXExclusive && r.minZ < r.maxZExclusive;
    if (exact) {
        return L.latLngBounds(
            blockToLatLng(cfg, r.minX, r.minZ),
            blockToLatLng(cfg, r.maxXExclusive, r.maxZExclusive)
        );
    }
    const extent = parseScaleBlocks(r.scale);
    if (!isFinite(extent) || extent <= 0 || !Number.isFinite(r.x) || !Number.isFinite(r.z)) return null;
    const half = extent / 2;
    return L.latLngBounds(
        blockToLatLng(cfg, r.x - half, r.z - half),
        blockToLatLng(cfg, r.x + half, r.z + half)
    );
}

/// Prepare lightweight spatial descriptors; no Leaflet tile layer is created yet.
function prepareBaseRenderEntries(state) {
    const cfg = DIMENSIONS[state.dimension];
    const dimIndex = DIM_INDEX[state.dimension];
    state.baseRenderEntries = [];
    for (let index = 0; index < state.baseRenders.length; index++) {
        const r = state.baseRenders[index];
        if ((r.dimension ?? 0) !== dimIndex) continue;
        if (!r.tilesPath) continue;
        const bounds = getBaseRenderBounds(cfg, r);
        if (!bounds?.isValid()) continue;
        const key = r.id != null
            ? `render:${r.id}`
            : `${r.locationId ?? "location"}:${r.tilesPath}:${index}`;
        state.baseRenderEntries.push({ key, render: r, bounds });
    }
}

/// Create the expensive tile layer only after its descriptor passes culling.
function createBaseRenderTileLayer(state, entry) {
    const cfg = DIMENSIONS[state.dimension];
    const r = entry.render;
    const template = r.tilesPath.includes("{z}") ? r.tilesPath : `${r.tilesPath}/{z}/{y}/{x}.png`;
    const url = template.replace("{dn}", state.isDay ? "day" : "night");
    const options = Object.assign({}, cfg.tileOptions, {
        bounds: entry.bounds,
        pane: "baseRenderPane",
        minZoom: MIN_ZOOM,
        maxZoom: MAX_ZOOM,
        updateWhenIdle: true,
        updateWhenZooming: false,
        keepBuffer: 1,
    });
    if (Number.isInteger(r.maxNativeZoom)) options.maxNativeZoom = r.maxNativeZoom;
    return L.tileLayer(url, options);
}

/// Reconcile mounted layers with the padded viewport and current screen scale.
function refreshBaseRenderLayers(state) {
    if (!state.showBaseRenders || !state.baseRenderLayer || !state.map._loaded) return;
    const viewport = state.map.getBounds();
    const prefetchBounds = viewport.pad(BASE_RENDER_VIEWPORT_PADDING);
    const center = viewport.getCenter();
    const candidates = [];
    for (const entry of state.baseRenderEntries) {
        if (!prefetchBounds.intersects(entry.bounds)) continue;
        const northWest = state.map.latLngToContainerPoint(entry.bounds.getNorthWest());
        const southEast = state.map.latLngToContainerPoint(entry.bounds.getSouthEast());
        const projectedSize = Math.max(
            Math.abs(southEast.x - northWest.x),
            Math.abs(southEast.y - northWest.y)
        );
        if (projectedSize < BASE_RENDER_MIN_SCREEN_PX) continue;
        const renderCenter = entry.bounds.getCenter();
        const dx = renderCenter.lng - center.lng;
        const dy = renderCenter.lat - center.lat;
        candidates.push({
            entry,
            visible: viewport.intersects(entry.bounds) ? 1 : 0,
            distance: (dx * dx) + (dy * dy),
            projectedSize,
        });
    }
    candidates.sort((a, b) =>
        b.visible - a.visible || a.distance - b.distance || b.projectedSize - a.projectedSize);
    const desired = new Set(candidates.slice(0, BASE_RENDER_MAX_ACTIVE).map((candidate) => candidate.entry.key));

    for (const [key, layer] of state.baseRenderLayers) {
        if (desired.has(key)) continue;
        state.baseRenderLayer.removeLayer(layer);
        state.baseRenderLayers.delete(key);
    }
    for (const candidate of candidates.slice(0, BASE_RENDER_MAX_ACTIVE)) {
        const entry = candidate.entry;
        if (state.baseRenderLayers.has(entry.key)) continue;
        const layer = createBaseRenderTileLayer(state, entry);
        state.baseRenderLayers.set(entry.key, layer);
        state.baseRenderLayer.addLayer(layer);
    }
    state.baseRenderStats = {
        available: state.baseRenderEntries.length,
        nearby: candidates.length,
        mounted: state.baseRenderLayers.size,
        capped: candidates.length > BASE_RENDER_MAX_ACTIVE,
    };
}

/// Coalesce zoomend + moveend into one spatial reconciliation.
function scheduleBaseRenderRefresh(state) {
    if (state.baseRenderRefreshFrame != null) return;
    state.baseRenderRefreshFrame = requestAnimationFrame(() => {
        state.baseRenderRefreshFrame = null;
        refreshBaseRenderLayers(state);
    });
}

/// Build an initially empty overlay group; nearby tile layers are mounted lazily.
function buildBaseRenderLayer(state) {
    prepareBaseRenderEntries(state);
    const group = L.layerGroup();
    state.baseRenderLayers.clear();
    return group;
}

/// Add/remove the base-render overlay group for the current dimension.
function applyBaseRenders(state, show) {
    if (state.baseRenderRefreshFrame != null) {
        cancelAnimationFrame(state.baseRenderRefreshFrame);
        state.baseRenderRefreshFrame = null;
    }
    if (state.baseRenderLayer) { state.map.removeLayer(state.baseRenderLayer); state.baseRenderLayer = null; }
    state.baseRenderLayers.clear();
    state.baseRenderEntries = [];
    state.baseRenderStats = { available: 0, nearby: 0, mounted: 0, capped: false };
    state.showBaseRenders = show;
    if (show && state.baseRenders.length) {
        state.baseRenderLayer = buildBaseRenderLayer(state).addTo(state.map);
        refreshBaseRenderLayers(state);
    }
}

// --- Measure tool ------------------------------------------------------------

// Shared draggable point handle for the measure tool.
const MEASURE_POINT_ICON = L.divIcon({ className: "atlas-measure-pt", html: "", iconSize: [12, 12], iconAnchor: [6, 6] });

/// Build a midpoint segment-distance label icon.
function segLabelIcon(text) {
    return L.divIcon({ className: "atlas-measure-seg", html: escapeHtml(text), iconSize: [0, 0] });
}

/// Total path length in blocks across the current measure points.
function measureTotalBlocks(state) {
    let total = 0;
    for (let i = 1; i < state.measurePoints.length; i++) {
        const a = state.measurePoints[i - 1], b = state.measurePoints[i];
        total += Math.hypot(b.x - a.x, b.z - a.z);
    }
    return total;
}

/// Live-update polyline + segment labels + total while a point is dragged
/// (without rebuilding the point handles, which would interrupt the drag).
function updateMeasureGeometry(state) {
    const cfg = DIMENSIONS[state.dimension];
    const pts = state.measurePoints;
    if (state._measurePoly) state._measurePoly.setLatLngs(pts.map((p) => blockToLatLng(cfg, p.x, p.z)));
    (state._measureSegLabels || []).forEach((lbl, i) => {
        const a = pts[i], b = pts[i + 1];
        lbl.setLatLng(blockToLatLng(cfg, (a.x + b.x) / 2, (a.z + b.z) / 2));
        lbl.setIcon(segLabelIcon(`${Math.round(Math.hypot(b.x - a.x, b.z - a.z)).toLocaleString()} b`));
    });
    if (state.dotNetRef) {
        state.dotNetRef.invokeMethodAsync("OnMeasureUpdated", Math.round(measureTotalBlocks(state)), pts.length);
    }
}

/// Redraw the measure polyline, per-segment labels and draggable point handles,
/// and report the total to Blazor.
function redrawMeasure(state) {
    if (state.measureLayer) { state.map.removeLayer(state.measureLayer); }
    state.measureLayer = L.layerGroup().addTo(state.map);
    state._measureSegLabels = [];
    state._measurePoly = null;
    const cfg = DIMENSIONS[state.dimension];
    const pts = state.measurePoints;

    if (pts.length > 1) {
        state._measurePoly = L.polyline(pts.map((p) => blockToLatLng(cfg, p.x, p.z)),
            { color: "#ffab40", weight: 3, dashArray: "6 6", opacity: 0.95 }).addTo(state.measureLayer);
        for (let i = 0; i < pts.length - 1; i++) {
            const a = pts[i], b = pts[i + 1];
            const lbl = L.marker(blockToLatLng(cfg, (a.x + b.x) / 2, (a.z + b.z) / 2),
                { icon: segLabelIcon(`${Math.round(Math.hypot(b.x - a.x, b.z - a.z)).toLocaleString()} b`), interactive: false, keyboard: false });
            lbl.addTo(state.measureLayer);
            state._measureSegLabels.push(lbl);
        }
    }

    // Draggable point handles — drag updates geometry live; dragend rebuilds.
    pts.forEach((p, i) => {
        const m = L.marker(blockToLatLng(cfg, p.x, p.z), { icon: MEASURE_POINT_ICON, draggable: true, zIndexOffset: 500 });
        m.bindTooltip(`${p.x}, ${p.z}`, { direction: "top", offset: [0, -8] });
        m.on("drag", (e) => {
            const nb = latLngToBlock(cfg, e.latlng.lat, e.latlng.lng);
            state.measurePoints[i] = nb;
            m.setTooltipContent(`${nb.x}, ${nb.z}`);
            updateMeasureGeometry(state);
        });
        m.on("dragend", () => redrawMeasure(state));
        m.addTo(state.measureLayer);
    });

    if (state.dotNetRef) {
        state.dotNetRef.invokeMethodAsync("OnMeasureUpdated", Math.round(measureTotalBlocks(state)), pts.length);
    }
}

function clearMeasureState(state) {
    state.measurePoints = [];
    if (state.measureLayer) { state.map.removeLayer(state.measureLayer); state.measureLayer = null; }
    if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnMeasureUpdated", 0, 0);
}

/// Append a measure point at a Leaflet latlng (used by both map clicks and
/// clicks on location / custom markers so you can measure marker-to-marker).
function addMeasurePoint(state, latlng) {
    state.measurePoints.push(latLngToBlock(DIMENSIONS[state.dimension], latlng.lat, latlng.lng));
    redrawMeasure(state);
}

// --- Highway editor ----------------------------------------------------------
// Draw/edit a highway path directly on the map. Vertices are stored in
// Nether-coordinate units (÷8 from Overworld clicks) to match the DB, so a path
// drawn in either dimension saves consistently.

const EDIT_VERTEX_ICON = L.divIcon({ className: "atlas-edit-vertex", html: "", iconSize: [14, 14], iconAnchor: [7, 7] });

/// Convert a clicked latlng to a [x,z] highway point in Nether-coordinate units.
function latLngToHighwayPoint(cfg, dimension, latlng, routeDimension = dimension) {
    const b = latLngToBlock(cfg, latlng.lat, latlng.lng);
    const mul = dimension === "overworld" && routeDimension === "nether" ? 8 : 1;
    return [Math.round(b.x / mul), Math.round(b.z / mul)];
}

/// Convert a native-dimension [x,z] highway point back to a latlng for the current dimension.
function highwayPointToLatLng(cfg, dimension, p, routeDimension = dimension) {
    const mul = dimension === "overworld" && routeDimension === "nether" ? 8 : 1;
    return blockToLatLng(cfg, p[0] * mul, p[1] * mul);
}

function reportEditPath(state) {
    if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnEditPathChanged", state.editPoints.length);
}

/// Redraw the in-progress edit polyline + draggable vertex handles.
function redrawEditPath(state) {
    if (state.editLayer) state.map.removeLayer(state.editLayer);
    state.editLayer = L.layerGroup().addTo(state.map);
    state._editPoly = null;
    const cfg = DIMENSIONS[state.dimension];
    const latlngs = state.editPoints.map((p) => highwayPointToLatLng(cfg, state.dimension, p, state.editDimension));

    if (latlngs.length > 1) {
        state._editPoly = L.polyline(latlngs, { color: "#ffd24a", weight: 3, opacity: 0.95, pane: "hwPane" }).addTo(state.editLayer);
    }

    state.editPoints.forEach((p, i) => {
        const m = L.marker(highwayPointToLatLng(cfg, state.dimension, p, state.editDimension), { icon: EDIT_VERTEX_ICON, draggable: true, zIndexOffset: 900 });
        m.bindTooltip(`${p[0]}, ${p[1]}`, { direction: "top", offset: [0, -8] });
        m.on("drag", (e) => {
            state.editPoints[i] = latLngToHighwayPoint(cfg, state.dimension, e.latlng, state.editDimension);
            m.setTooltipContent(`${state.editPoints[i][0]}, ${state.editPoints[i][1]}`);
            if (state._editPoly) state._editPoly.setLatLngs(state.editPoints.map((q) => highwayPointToLatLng(cfg, state.dimension, q, state.editDimension)));
        });
        m.on("dragend", () => redrawEditPath(state));
        m.on("contextmenu", (ev) => { L.DomEvent.stop(ev); state.editPoints.splice(i, 1); redrawEditPath(state); });
        m.addTo(state.editLayer);
    });

    reportEditPath(state);
}

function addEditVertex(state, latlng) {
    const cfg = DIMENSIONS[state.dimension];
    state.editPoints.push(latLngToHighwayPoint(cfg, state.dimension, latlng, state.editDimension));
    redrawEditPath(state);
}

function clearEditState(state, keepMode) {
    state.editPoints = [];
    if (state.editLayer) { state.map.removeLayer(state.editLayer); state.editLayer = null; }
    state._editPoly = null;
    if (!keepMode) state.editing = false;
    reportEditPath(state);
}

// --- Custom marker placement -------------------------------------------------
// User-placed, draggable annotation markers (star / mansion / gapple / diamond /
// monument). Coordinates are dimension-specific, so they clear on dimension change.

const CUSTOM_ICON_DEFS = {
    star: { url: "Images/marker.custom.png", w: 23, h: 21, ax: 10, ay: 10 },
    mansion: { url: "Images/marker.mansion.png", w: 22, h: 22, ax: 10, ay: 10 },
    gapple: { url: "Images/marker.apple.png", w: 22, h: 28, ax: 10, ay: 10 },
    diamond: { url: "Images/diamond.png", w: 25, h: 20, ax: 10, ay: 10 },
    monument: { url: "Images/marker.monument.png", w: 24, h: 24, ax: 10, ay: 10 },
    banner: { url: "Images/marker.red.png", w: 24, h: 24, ax: 12, ay: 24 },
};

/// Build the icon for a user-placed custom marker (original atlas PNG sprites).
function customMarkerIcon(type) {
    const d = CUSTOM_ICON_DEFS[type] || CUSTOM_ICON_DEFS.star;
    return L.icon({ iconUrl: d.url, iconSize: [d.w, d.h], iconAnchor: [d.ax, d.ay], className: "atlas-custom-marker" });
}

/// Notify Blazor of the current custom-marker count.
function reportCustomMarkers(state) {
    if (state.dotNetRef) state.dotNetRef.invokeMethodAsync("OnCustomMarkersChanged", state.customMarkers.length);
}

/// Drop a draggable custom marker at the given block coords (right-click removes it).
function addCustomMarker(state, x, z, type) {
    const cfg = DIMENSIONS[state.dimension];
    if (!state.customLayer) state.customLayer = L.layerGroup().addTo(state.map);
    const m = L.marker(blockToLatLng(cfg, x, z), { icon: customMarkerIcon(type), draggable: true, zIndexOffset: 700 });
    m._type = type;
    m.bindTooltip(`${x}, ${z}`, { direction: "top", offset: [0, -14] });
    const syncTip = () => {
        const b = latLngToBlock(cfg, m.getLatLng().lat, m.getLatLng().lng);
        m.setTooltipContent(`${b.x}, ${b.z}`);
    };
    m.on("drag", syncTip);
    m.on("dragend", syncTip);
    m.on("click", (e) => {
        if (state.measuring) { L.DomEvent.stopPropagation(e); addMeasurePoint(state, m.getLatLng()); }
    });
    m.on("contextmenu", (e) => {
        L.DomEvent.stop(e);
        state.customLayer.removeLayer(m);
        state.customMarkers = state.customMarkers.filter((x2) => x2 !== m);
        reportCustomMarkers(state);
    });
    state.customMarkers.push(m);
    state.customLayer.addLayer(m);
    reportCustomMarkers(state);
}

/// Remove all custom markers.
function clearCustomMarkersState(state) {
    if (state.customLayer) { state.map.removeLayer(state.customLayer); state.customLayer = null; }
    state.customMarkers = [];
    reportCustomMarkers(state);
}

// --- Public API (called from Blazor via JS interop) --------------------------

export function initialize(containerId, dimension, isDay, dotNetRef) {
    dispose(containerId); // idempotent — safe to re-init

    const dim = DIMENSIONS[dimension] ? dimension : "overworld";
    const cfg = DIMENSIONS[dim];

    const map = L.map(containerId, {
        crs: makeCrs(),
        minZoom: MIN_ZOOM,
        maxZoom: MAX_ZOOM,
        tapTolerance: TOUCH_TAP_MAX_MOVE_PX,
        zoomControl: true,
        attributionControl: false,
        center: blockToLatLng(cfg, 0, 0),
        zoom: DEFAULT_ZOOM,
    });

    // Reference overlays (highways/axes) live in their own pane BELOW the markers
    // so location markers always stay clickable, regardless of toggle order.
    map.createPane("hwPane");
    map.getPane("hwPane").style.zIndex = 350; // tilePane 200 < hwPane 350 < overlayPane 400 < markerPane 600
    map.createPane("baseRenderPane");
    map.getPane("baseRenderPane").style.zIndex = 250; // primary tiles 200 < base renders 250 < roads/markers
    map.createPane("renderPreviewPane");
    map.getPane("renderPreviewPane").style.zIndex = 300;
    map.createPane("historyPane");
    map.getPane("historyPane").style.zIndex = 375; // above ordinary highways, below markers
    map.getPane("historyPane").style.pointerEvents = "none";
    const highwayRenderer = L.svg({ pane: "hwPane", padding: 0.5 });
    map.createPane("hwEmphasisPane");
    map.getPane("hwEmphasisPane").style.zIndex = 385;
    map.getPane("hwEmphasisPane").style.pointerEvents = "none";
    const highwayEmphasisRenderer = L.svg({ pane: "hwEmphasisPane", padding: 0.5 });
    map.createPane("locOverviewPane");
    // Keep overview dots below interactive highways (350) and detailed markers
    // (overlayPane 400 / markerPane 600), so the full-map Canvas cannot mask
    // their pointer events after zoom transitions.
    map.getPane("locOverviewPane").style.zIndex = 340;
    const coarsePointer = window.matchMedia?.("(pointer: coarse)")?.matches || navigator.maxTouchPoints > 0;
    const locationOverviewRenderer = L.canvas({
        pane: "locOverviewPane",
        padding: 0.2,
        tolerance: coarsePointer ? 18 : 8,
    });

    const state = {
        map, dimension: dim, isDay, renderIds: defaultRenders(dim), tileLayers: [], dotNetRef,
        coarsePointer, suppressMapClickUntil: 0, touchSelectionCleanup: null,
        highwayLayer: null, axesLayer: null, showHighways: false, showAxes: false,
        selectedHighway: null, highwayLines: [], highwayRenderer, highwayEmphasisRenderer, highwayOpacity: 0.3,
        bordersLayer: null, spawnLayer: null, showBorders: false, showSpawnRadius: false,
        locations: [], locationLayer: null, locationOverviewLayer: null, locationOverviewRenderer,
        showLocations: false, selectedMarker: null,
        showEndPortals: false, endPortalLayer: null,
        markerSettings: { ...DEFAULT_MARKER_SETTINGS },
        baseRenders: [], baseRenderEntries: [], baseRenderLayer: null,
        baseRenderLayers: new Map(), baseRenderRefreshFrame: null,
        baseRenderStats: { available: 0, nearby: 0, mounted: 0, capped: false },
        showBaseRenders: true,
        renderPreviewLayer: null,
        nocomLayer: null, nocomStyle: "classic", nocomColor: "#00e5ff",
        nocomColorFilter: null, nocomColorFilterId: null, nocomColorMatrix: null,
        measuring: false, measurePoints: [], measureLayer: null,
        customMarkers: [], customLayer: null, placingMarker: false, customIcon: "star",
        editing: false, editPoints: [], editLayer: null,
    };
    state.tileLayers = [];
    rebuildTiles(state);
    clearNocomLayer(state);
    registry.set(containerId, state);
    installTouchFeatureSelection(state);

    // Map click: add a measure point while measuring, drop a custom marker while
    // placing, else clear any selection.
    map.on("click", (e) => {
        if (Date.now() < state.suppressMapClickUntil) return;
        if (state.editing) {
            addEditVertex(state, e.latlng);
        } else if (state.measuring) {
            addMeasurePoint(state, e.latlng);
        } else if (state.placingMarker) {
            const b = latLngToBlock(DIMENSIONS[state.dimension], e.latlng.lat, e.latlng.lng);
            addCustomMarker(state, b.x, b.z, state.customIcon);
        } else {
            deselectHighway(state);
            deselectLocation(state);
        }
    });

    let locationsVisible = map.getZoom() >= MIN_LOCATION_ZOOM;
    map.on("zoomend", () => {
        if (state.nocomStyle === "contrast") applyNocomColor(state, state.nocomColor);
        const nextVisible = map.getZoom() >= MIN_LOCATION_ZOOM;
        if (nextVisible !== locationsVisible) {
            locationsVisible = nextVisible;
            refreshLocations(state);
        }
        scheduleBaseRenderRefresh(state);
    });
    map.on("moveend", () => scheduleBaseRenderRefresh(state));
    map.on("resize", () => scheduleBaseRenderRefresh(state));

    // Live coordinate readout back to Blazor.
    if (dotNetRef) {
        map.on("mousemove", (e) => {
            const c = latLngToBlock(cfg, e.latlng.lat, e.latlng.lng);
            dotNetRef.invokeMethodAsync("UpdateCoordinates", c.x, c.z);
        });
    }

    // Leaflet occasionally needs a nudge when the container was hidden at init.
    setTimeout(() => map.invalidateSize(), 100);
    return true;
}

export function setDimension(containerId, dimension) {
    const state = registry.get(containerId);
    if (!state || !DIMENSIONS[dimension]) return false;

    const cfg = DIMENSIONS[dimension];
    state.dimension = dimension;
    state.renderIds = defaultRenders(dimension);

    rebuildTiles(state);
    clearNocomLayer(state);

    // Recenter on spawn in the new dimension's coordinate space.
    state.map.setView(blockToLatLng(cfg, 0, 0), state.map.getZoom());

    // Overlays are coordinate-space specific, so rebuild them for the new dimension.
    applyHighways(state, state.showHighways);
    applyAxes(state, state.showAxes);
    applyBorders(state, state.showBorders);
    applySpawnRadius(state, state.showSpawnRadius);
    applyLocations(state, state.showLocations);
    applyBaseRenders(state, state.showBaseRenders);
    // Measure points are dimension-specific coordinates; reset on dimension change.
    clearMeasureState(state);
    // Custom markers are dimension-specific too; reset them as well.
    clearCustomMarkersState(state);

    // Rebind mousemove to the new dimension's conversion.
    state.map.off("mousemove");
    if (state.dotNetRef) {
        state.map.on("mousemove", (e) => {
            const c = latLngToBlock(cfg, e.latlng.lat, e.latlng.lng);
            state.dotNetRef.invokeMethodAsync("UpdateCoordinates", c.x, c.z);
        });
    }
    return true;
}

export function setTimeOfDay(containerId, isDay) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.isDay = isDay;
    rebuildTiles(state);
    if (state.showBaseRenders) applyBaseRenders(state, true);
    return true;
}

/// Display one Atlas-aligned Nocom heatmap above ordinary highways. Its pane is
/// pointer-transparent; hover/selection feedback is repeated above it.
export function setNocomLayer(containerId, urlTemplate, maxNativeUrlZoom, opacity, style, color) {
    const state = registry.get(containerId);
    if (!state) return false;
    clearNocomLayer(state);
    if (!urlTemplate) return true;

    const cfg = DIMENSIONS[state.dimension];
    const urlZoomOffset = Number(cfg.tileOptions?.zoomOffset || 0);
    const maxNativeZoom = Number(maxNativeUrlZoom) - urlZoomOffset;
    state.nocomStyle = style === "contrast" ? "contrast" : style === "custom" ? "custom" : "classic";
    const options = Object.assign({
        minZoom: MIN_ZOOM,
        maxZoom: MAX_ZOOM,
        maxNativeZoom,
        opacity: Math.max(0.1, Math.min(1, Number(opacity) || 0.55)),
        pane: "historyPane",
        className: "nocom-world-pulse",
        attribution: "Nocom historical dataset",
    }, cfg.tileOptions);
    state.nocomLayer = L.tileLayer(urlTemplate, options).addTo(state.map);
    applyNocomColor(state, color);
    return true;
}

export function setNocomColor(containerId, color) {
    const state = registry.get(containerId);
    if (!state) return false;
    applyNocomColor(state, color);
    return true;
}

export function clearNocom(containerId) {
    const state = registry.get(containerId);
    if (!state) return false;
    clearNocomLayer(state);
    return true;
}

/// Set the enabled world-download renders (stacked) for the current dimension.
/// An empty list is allowed — the user can choose to show no spawn render.
export function setRenders(containerId, renderIds) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.renderIds = uniqueRenderIds(state.dimension, renderIds);
    rebuildTiles(state);
    return true;
}

/// Return the render list for the current dimension + the enabled render ids and
/// whether any enabled render supports day/night, so Blazor can populate the picker.
export function getRenders(containerId) {
    const state = registry.get(containerId);
    const dim = state ? state.dimension : "overworld";
    const cfg = DIMENSIONS[dim];
    const enabled = uniqueRenderIds(dim, state ? state.renderIds : defaultRenders(dim));
    const anyDayNight = cfg.renders.some((r) => enabled.includes(r.id) && r.dayNight);
    return {
        current: enabled,
        dayNight: anyDayNight,
        renders: cfg.renders.map((r) => ({ id: r.id, label: r.label, dayNight: r.dayNight })),
    };
}

export function goToCoords(containerId, x, z, zoom) {
    const state = registry.get(containerId);
    if (!state) return false;
    const cfg = DIMENSIONS[state.dimension];
    const targetZoom = typeof zoom === "number" ? zoom : state.map.getZoom();
    state.map.setView(blockToLatLng(cfg, x, z), targetZoom);
    return true;
}

/// Current viewport centre (block coords for the active dimension) and zoom.
export function getView(containerId) {
    const state = registry.get(containerId);
    if (!state) return null;
    const cfg = DIMENSIONS[state.dimension];
    const c = state.map.getCenter();
    const b = latLngToBlock(cfg, c.lat, c.lng);
    return { x: b.x, z: b.z, zoom: state.map.getZoom() };
}

/// Set an exact zoom without animation (used by deep validation and diagnostics).
export function setZoom(containerId, zoom) {
    const state = registry.get(containerId);
    if (!state || !Number.isFinite(zoom)) return null;
    const target = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, zoom));
    state.map.setView(state.map.getCenter(), target, { animate: false });
    return state.map.getZoom();
}

/// Recalculate the Leaflet viewport after a containing splitter changes size.
export function invalidateSize(containerId) {
    const state = registry.get(containerId);
    if (!state) return false;
    requestAnimationFrame(() => {
        state.map.invalidateSize({ animate: false });
        requestAnimationFrame(() => state.map.invalidateSize({ animate: false }));
    });
    return true;
}

/// Lightweight runtime counters for map performance regression checks.
export function getDiagnostics(containerId) {
    const state = registry.get(containerId);
    if (!state) return null;
    return {
        zoom: state.map.getZoom(),
        dimension: state.dimension,
        renderIds: uniqueRenderIds(state.dimension, state.renderIds),
        primaryLayerCount: state.tileLayers.length,
        highways: state.highwayLines.length,
        locationMarkers: Object.keys(state.markerByIndex || {}).length,
        locationMode: state.locationOverviewLayer ? "overview" : state.locationLayer || state.endPortalLayer ? "detail" : "hidden",
        placeLayers: state.tileLayers
            .filter((layer) => layer._atlasPlaceStats)
            .map((layer) => ({
                activeTiles: Object.keys(layer._tiles || {}).length,
                minZoom: layer.options.minZoom,
                ...layer._atlasPlaceStats,
            })),
    };
}

export function setHighways(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    applyHighways(state, show);
    return true;
}

export function setHighwayOpacity(containerId, opacity) {
    const state = registry.get(containerId);
    if (!state) return false;
    const parsed = Number(opacity);
    state.highwayOpacity = Math.max(0.1, Math.min(1, Number.isFinite(parsed) ? parsed : 0.3));
    for (const entry of state.highwayLines) {
        entry.baseOpacity = state.highwayOpacity;
        if (state.selectedHighway !== entry) styleHighwayBase(entry);
    }
    return true;
}

// Category index → internal type string (matches HighwayCategory enum order).
const HW_CATEGORY_NAMES = ["axis", "diagonal", "ring", "diamond", "grid", "star", "spur", "custom"];
// Paving material index → display label (index 0 = Unknown → omitted).
const HW_MATERIAL_NAMES = [null, "Obsidian", "Cleared netherrack", "Blackstone", "Basalt", "Blue ice", "Mixed", "Other"];
// Status index → display label (index 0 = Unknown → omitted).
const HW_STATUS_NAMES = [null, "Complete", "Partial", "Cleared only", "Under construction", "Griefed", "Lava-flooded", "Abandoned"];

/// Load highways from the API (array of Highway DTOs) and, if the overlay is
/// shown, rebuild it. Falls back to the built-in set when passed nothing.
export function loadHighways(containerId, highways) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.highways = (highways || []).map((h) => ({
        id: h.id,
        name: h.name,
        dimension: h.dimension,
        points: (h.points || []).map((p) => [p.x, p.z]),
        width: h.width || 1,
        type: HW_CATEGORY_NAMES[h.category] || "custom",
        material: HW_MATERIAL_NAMES[h.pavingMaterial] || null,
        status: HW_STATUS_NAMES[h.status] || null,
        description: h.description || null,
        wiki: h.wikiUrl || null,
        builderGroup: (h.builderGroups || []).length
            ? h.builderGroups.map((g) => `${g.groupName} (${g.role})`).join("; ")
            : (h.builderGroupName || null),
        color: h.color || null,
        displayWeight: h.displayWeight || null,
    }));
    if (state.showHighways) applyHighways(state, true);
    return true;
}

/// Enter/exit highway edit mode. In edit mode, map clicks add draggable vertices.
export function setHighwayEditMode(containerId, on, routeDimension = null) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.editing = on;
    if (on) {
        state.editDimension = routeDimension === null ? state.dimension : ["overworld", "nether", "end"][Number(routeDimension)];
        state.measuring = false;
        state.placingMarker = false;
        deselectHighway(state);
        deselectLocation(state);
        if (!state.editLayer) state.editLayer = L.layerGroup().addTo(state.map);
    } else {
        clearEditState(state, false);
    }
    return true;
}

/// Current edit path as [{x,z},...] in the highway's native dimension units.
export function getEditPath(containerId) {
    const state = registry.get(containerId);
    if (!state) return [];
    return state.editPoints.map((p) => ({ x: p[0], z: p[1] }));
}

/// Load an existing path (native dimension units) into the editor for editing.
export function setEditPath(containerId, points) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.editing = true;
    state.editPoints = (points || []).map((p) => [p.x, p.z]);
    if (!state.editLayer) state.editLayer = L.layerGroup().addTo(state.map);
    redrawEditPath(state);
    return true;
}

/// Remove the last vertex.
export function undoEditVertex(containerId) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.editPoints.pop();
    redrawEditPath(state);
    return true;
}

/// Clear all vertices (stay in edit mode).
export function clearEditPath(containerId) {
    const state = registry.get(containerId);
    if (!state) return false;
    clearEditState(state, true);
    return true;
}

export function setAxes(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    applyAxes(state, show);
    return true;
}

export function clearHighwaySelection(containerId) {
    const state = registry.get(containerId);
    if (state) deselectHighway(state);
    return true;
}

export function clearLocationSelection(containerId) {
    const state = registry.get(containerId);
    if (state) deselectLocation(state);
    return true;
}

/// Fly to and select a location marker by its index. Returns false if the marker
/// isn't present (e.g. wrong dimension or markers hidden).
export function focusLocation(containerId, index, zoom) {
    const state = registry.get(containerId);
    if (!state || !state.markerByIndex) return false;
    const marker = state.markerByIndex[index];
    if (!marker) return false;
    state.map.setView(marker.getLatLng(), typeof zoom === "number" ? zoom : Math.max(state.map.getZoom(), 7));
    selectLocation(state, state.markerByIndex[index] || marker);
    return true;
}

/**
 * Switches an embedded BlueMap viewer through the Atlas control bridge.
 * The bridge invokes BlueMap's native controller in the child frame; this is
 * deliberately postMessage-based because production Atlas and BlueMap use
 * different origins.
 */
export function setBlueMapNavigation(frameId, mode) {
    if (mode !== "perspective" && mode !== "free") return Promise.reject(new Error("Unknown navigation mode."));
    const frame = document.getElementById(frameId);
    if (!(frame instanceof HTMLIFrameElement) || !frame.contentWindow)
        return Promise.reject(new Error("The 3D viewer is not ready."));
    const origin = new URL(frame.src, document.baseURI).origin;
    const source = frame.contentWindow;
    const requestId = crypto.randomUUID();
    const namespace = "2b2t-atlas-bluemap-controls";
    return new Promise((resolve, reject) => {
        const finish = (error, result) => {
            clearTimeout(timeout);
            clearInterval(retry);
            window.removeEventListener("message", receive);
            if (error) reject(new Error(error));
            else {
                // Focus is best-effort. Browser focus policy must never turn a
                // successful control acknowledgement into an unresolved call.
                try { frame.focus({ preventScroll: true }); source.focus(); } catch { }
                resolve(result);
            }
        };
        const receive = event => {
            const data = event.data;
            if (event.source !== source || event.origin !== origin ||
                data?.namespace !== namespace || data.version !== 1 || data.requestId !== requestId) return;
            if (data.type === "navigation-result")
                finish(data.error || (data.mode !== mode ? "BlueMap did not switch navigation mode." : null), data.mode);
        };
        const send = () => source.postMessage({ namespace, version: 1, type: "set-navigation", requestId, mode }, origin);
        const timeout = setTimeout(() => finish("BlueMap did not confirm the control change. Wait for the map to load and try again."), 20000);
        const retry = setInterval(send, 500);
        window.addEventListener("message", receive);
        send();
    });
}

/// Center the map on one base render and fit its complete captured footprint.
/// Exact chunk-derived bounds are preferred; legacy renders fall back to their
/// scale and owning location coordinates through getBaseRenderBounds.
export function focusBaseRender(containerId, render) {
    const state = registry.get(containerId);
    if (!state || !render) return false;
    const dimension = Number.isInteger(render.dimension)
        ? render.dimension
        : DIM_INDEX[state.dimension];
    if (dimension !== DIM_INDEX[state.dimension]) return false;

    const bounds = getBaseRenderBounds(DIMENSIONS[state.dimension], render);
    if (!bounds?.isValid()) return false;
    state.map.fitBounds(bounds, {
        padding: [36, 36],
        maxZoom: 9,
        animate: true,
    });
    scheduleBaseRenderRefresh(state);
    return true;
}

/// Select/highlight a location marker by index WITHOUT moving the view (used when
/// reloading the marker set but preserving the user's current pan/zoom).
export function highlightLocation(containerId, index) {
    const state = registry.get(containerId);
    if (!state || !state.markerByIndex) return false;
    const marker = state.markerByIndex[index];
    if (!marker) return false;
    selectLocation(state, marker);
    return true;
}

export function setBorders(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    applyBorders(state, show);
    return true;
}

export function setSpawnRadius(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    applySpawnRadius(state, show);
    return true;
}

/// Load location data (array of {name,x,z,dimension,description}) and show markers.
export function loadLocations(containerId, locations) {
    const state = registry.get(containerId);
    if (!state) return 0;
    state.locations = Array.isArray(locations) ? locations : [];
    if (state.showLocations || state.showEndPortals) refreshLocations(state);
    return state.locations.length;
}

export function setLocations(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    applyLocations(state, show);
    return true;
}

/// Fits the map to every currently loaded location in the active dimension.
/// Entity pages use this for small, bounded subsets of the complete Atlas.
export function fitLocations(containerId, padding = 40, maxZoom = 8) {
    const state = registry.get(containerId);
    if (!state || !Array.isArray(state.locations) || state.locations.length === 0) return 0;

    const cfg = DIMENSIONS[state.dimension];
    const coordinates = state.locations
        .map(loc => locDisplayCoords(state, loc))
        .filter(loc => loc && Number.isFinite(loc.x) && Number.isFinite(loc.z))
        .map(loc => blockToLatLng(cfg, loc.x, loc.z));
    if (coordinates.length === 0) return 0;

    state.map.invalidateSize(false);
    if (coordinates.length === 1) {
        state.map.setView(coordinates[0], Math.min(Number(maxZoom) || 8, MAX_ZOOM), { animate: false });
    } else {
        const requestedPadding = Math.max(0, Number(padding) || 0);
        state.map.fitBounds(L.latLngBounds(coordinates), {
            padding: [requestedPadding, requestedPadding],
            maxZoom: Math.min(Number(maxZoom) || 8, MAX_ZOOM),
            animate: false,
        });
    }
    return coordinates.length;
}

/// Toggle the dedicated End Portal layer.
export function setEndPortals(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.showEndPortals = show;
    refreshLocations(state);
    return true;
}

/// Update marker appearance ({icon, color, opacity, scale}) and rebuild markers.
export function setMarkerSettings(containerId, settings) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.markerSettings = { ...DEFAULT_MARKER_SETTINGS, ...(settings || {}) };
    refreshLocations(state);
    return true;
}

/// Load base-render descriptors ({tilesPath,x,z,scale,dimension,name,date}) and show them.
export function loadBaseRenders(containerId, renders) {
    const state = registry.get(containerId);
    if (!state) return 0;
    state.baseRenders = Array.isArray(renders) ? renders : [];
    if (state.showBaseRenders) applyBaseRenders(state, true);
    return state.baseRenders.length;
}

/// Diagnostic surface used by staging tests and future UI telemetry.
export function getBaseRenderStats(containerId) {
    const state = registry.get(containerId);
    return state ? { ...state.baseRenderStats } : { available: 0, nearby: 0, mounted: 0, capped: false };
}

/// Return selected render ids that are actually mounted and intersect the visible
/// viewport. The location page uses this snapshot when switching to 3D so distant
/// selected WDLs outside the current view are not pulled into the 3D selector.
export function getVisibleBaseRenderIds(containerId) {
    const state = registry.get(containerId);
    if (!state || !state.map?._loaded || !state.showBaseRenders) return [];
    const viewport = state.map.getBounds();
    return state.baseRenderEntries
        .filter((entry) => state.baseRenderLayers.has(entry.key) && viewport.intersects(entry.bounds))
        .map((entry) => Number(entry.render.id))
        .filter((id) => Number.isInteger(id));
}

export function setBaseRenders(containerId, show) {
    const state = registry.get(containerId);
    if (!state) return false;
    applyBaseRenders(state, show);
    return true;
}

/// Display a georeferenced PNG preview above the selected primary layers.
export function setRenderPreview(containerId, dataUrl, minX, minZ, maxXExclusive, maxZExclusive) {
    const state = registry.get(containerId);
    if (!state) return false;
    if (state.renderPreviewLayer) {
        state.map.removeLayer(state.renderPreviewLayer);
        state.renderPreviewLayer = null;
    }
    if (!dataUrl) return true;
    const cfg = DIMENSIONS[state.dimension];
    const bounds = L.latLngBounds(
        blockToLatLng(cfg, minX, minZ),
        blockToLatLng(cfg, maxXExclusive, maxZExclusive)
    );
    state.renderPreviewLayer = L.imageOverlay(dataUrl, bounds, {
        pane: "renderPreviewPane",
        opacity: 0.85,
        interactive: false,
    }).addTo(state.map);
    state.map.fitBounds(bounds, { padding: [20, 20], maxZoom: 9 });
    return true;
}

export function setRenderPreviewOpacity(containerId, opacity) {
    const state = registry.get(containerId);
    if (!state?.renderPreviewLayer) return false;
    state.renderPreviewLayer.setOpacity(Math.max(0, Math.min(1, Number(opacity))));
    return true;
}

export function setMeasureMode(containerId, on) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.measuring = on;
    // Deselect any highway so map clicks feed the measure tool cleanly.
    if (on) deselectHighway(state);
    return true;
}

export function clearMeasure(containerId) {
    const state = registry.get(containerId);
    if (!state) return false;
    clearMeasureState(state);
    return true;
}

/// Toggle custom-marker placement mode (map clicks drop markers).
export function setPlaceMarkerMode(containerId, on) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.placingMarker = on;
    if (on) { deselectHighway(state); deselectLocation(state); }
    return true;
}

/// Set the icon type used for newly-placed custom markers.
export function setCustomIcon(containerId, type) {
    const state = registry.get(containerId);
    if (!state) return false;
    state.customIcon = CUSTOM_ICON_DEFS[type] ? type : "star";
    return true;
}

/// Remove all custom markers.
export function clearCustomMarkers(containerId) {
    const state = registry.get(containerId);
    if (!state) return false;
    clearCustomMarkersState(state);
    return true;
}

/// Toggle browser fullscreen for the map view (includes overlay panels).
export function toggleFullscreen(containerId) {
    const state = registry.get(containerId);
    const mapEl = state ? state.map.getContainer() : document.getElementById(containerId);
    const target = (mapEl && mapEl.parentElement) || mapEl;
    if (!document.fullscreenElement) {
        target?.requestFullscreen?.();
    } else {
        document.exitFullscreen?.();
    }
    // Leaflet must recompute its size after the viewport changes.
    if (state) setTimeout(() => state.map.invalidateSize(), 300);
    return true;
}

export function dispose(containerId) {
    const state = registry.get(containerId);
    if (state) {
        if (state.baseRenderRefreshFrame != null) cancelAnimationFrame(state.baseRenderRefreshFrame);
        if (state.nocomColorFilter) state.nocomColorFilter.remove();
        if (state.touchSelectionCleanup) state.touchSelectionCleanup();
        state.map.off();
        state.map.remove();
        registry.delete(containerId);
    }
}
