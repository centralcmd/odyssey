// The location map's interop seam (issue #371 §4): the ONE place that touches MapLibre. OdsLocationMap.razor
// drives it through create / update / setStyle / dispose and hears back through [JSInvokable] callbacks,
// each carrying the generation it was created under so a late callback from a disposed map is ignored.
//
// MapLibre is vendored (wwwroot/lib/maplibre-gl, see its README) and loaded on the first map only, never
// at app start. Every node built here uses createElement + textContent: no HTML string ever reaches the
// DOM, and the style's own attribution never renders (attributionControl: false — the configured caption
// is Blazor's, under the frame).

const LIB_DIR = new URL('../lib/maplibre-gl/', import.meta.url);
const PMTILES_SCRIPT = new URL('../lib/pmtiles/pmtiles.js', import.meta.url);
const STYLESHEETS = [new URL('maplibre-gl.css', LIB_DIR), new URL('../css/location-map.css', import.meta.url)];

// The app's own English strings (§6.10, X11). MapLibre's built-in table is replaced through `locale`.
const LOCALE = {
    'Map.Title': '',
    'Marker.Title': '',
    'Popup.Close': 'Close',
    'NavigationControl.ZoomIn': 'Zoom in',
    'NavigationControl.ZoomOut': 'Zoom out',
    'CooperativeGesturesHandler.WindowsHelpText': 'Use Ctrl + scroll to zoom the map',
    'CooperativeGesturesHandler.MacHelpText': 'Use ⌘ + scroll to zoom the map',
    'CooperativeGesturesHandler.MobileHelpText': 'Use two fingers to move the map',
};

const CONTROLS = [
    { key: 'n', label: 'Pan north', glyph: 'keyboard_arrow_up' },
    { key: 'w', label: 'Pan west', glyph: 'keyboard_arrow_left' },
    { key: 'c', label: 'Re-centre on pin', glyph: 'my_location' },
    { key: 'e', label: 'Pan east', glyph: 'keyboard_arrow_right' },
    { key: 's', label: 'Pan south', glyph: 'keyboard_arrow_down' },
];

const LOAD_TIMEOUT_MS = 15000;
const TILE_ERRORS_BEFORE_LOAD = 3;
const DEGRADED_ERRORS = 5;
const DEGRADED_WINDOW_MS = 10000;
const DEGRADED_CLEAR_MS = 30000;
const CONTEXT_RESTORE_MS = 3000;

let libPromise = null;
let nextId = 0;
// Live maps by handle. .NET holds only the integer, so nothing on its side can outlive a map's teardown.
const live = new Map();

/** WebGL 2 or 1 context probe. Creates no map. */
export function isSupported() {
    try {
        const canvas = document.createElement('canvas');
        return !!(canvas.getContext('webgl2') || canvas.getContext('webgl'));
    } catch {
        return false;
    }
}

function loadStylesheet(href) {
    const existing = document.querySelector(`link[data-location-map="${href}"]`);
    if (existing) return Promise.resolve();
    return new Promise(resolve => {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        link.dataset.locationMap = href;
        // A stylesheet that fails still lets the map render; its controls are then unstyled, not absent.
        link.addEventListener('load', () => resolve(), { once: true });
        link.addEventListener('error', () => resolve(), { once: true });
        document.head.appendChild(link);
    });
}

// pmtiles@4.5.0's ESM build imports the bare specifier "fflate", which a browser cannot resolve without an
// import map the CSP forbids, so the self-contained classic build is loaded instead: a <script src> from
// 'self', which defines the global `pmtiles` (lib/pmtiles/README.md).
function loadClassicScript(href) {
    if (globalThis.pmtiles) return Promise.resolve();
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = href;
        script.async = true;
        script.addEventListener('load', () => resolve(), { once: true });
        script.addEventListener('error', () => { script.remove(); reject(new Error('pmtiles failed to load')); }, { once: true });
        document.head.appendChild(script);
    });
}

// The self-hosted basemap (issue #384): one PMTiles archive the client container serves at exactly
// /basemap/basemap.pmtiles, behind nginx's auth_request. It is registered under that URL before any style can
// name it, so the library never builds a default source of its own for it. Registered once per page.
//
// 'same-origin' sends the session cookie, and only to the deployment: the file is authorized by
// auth_request (#384 §7.2), the one deliberate exception to "map requests carry no cookie". Styles, glyphs
// and the sprite still go out without one (transformRequest in build()).
function registerBasemapProtocol(lib) {
    const protocol = new pmtiles.Protocol();
    protocol.add(new pmtiles.PMTiles(new pmtiles.FetchSource('/basemap/basemap.pmtiles', new Headers(), 'same-origin')));
    lib.addProtocol('pmtiles', protocol.tile);
}

function loadLibrary() {
    libPromise ??= (async () => {
        await Promise.all(STYLESHEETS.map(u => loadStylesheet(u.href)));
        const [lib] = await Promise.all([import(new URL('maplibre-gl.js', LIB_DIR).href), loadClassicScript(PMTILES_SCRIPT.href)]);
        // Same-origin, so MapLibre starts it as `new Worker(url, { type: 'module' })`: worker-src 'self'
        // is enough and no blob: worker is ever created (#370 §7.7).
        lib.setWorkerUrl(new URL('maplibre-gl-worker.js', LIB_DIR).href);
        registerBasemapProtocol(lib);
        return lib;
    })();
    // A failed load is not cached: Try again re-imports.
    libPromise.catch(() => { libPromise = null; });
    return libPromise;
}

// MapLibre 6 refuses a root-relative sprite URL ("must be absolute"), which the self-hosted styles (issue #384)
// use so they name no host. Resolve a root-relative sprite and glyphs URL against the page's own origin when a
// style loads: still same-origin, and a remote style's absolute URLs pass through untouched.
function sameOriginStyle(_previous, next) {
    // Joined, not new URL(): that would percent-encode the {fontstack} and {range} tokens MapLibre fills in.
    const absolute = url => (typeof url === 'string' && url.startsWith('/') && !url.startsWith('//')) ? location.origin + url : url;
    // Only keys the style has: an added `glyphs: undefined` fails MapLibre's validation.
    const style = { ...next };
    if (typeof next.glyphs === 'string') style.glyphs = absolute(next.glyphs);
    if (typeof next.sprite === 'string') style.sprite = absolute(next.sprite);
    else if (Array.isArray(next.sprite)) style.sprite = next.sprite.map(entry => ({ ...entry, url: absolute(entry.url) }));
    return style;
}

const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;

function node(tag, attrs = {}, text) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
        if (v !== null && v !== undefined) el.setAttribute(k, v);
    }
    if (text !== undefined) el.textContent = text;
    return el;
}

function glyph(name) {
    return node('span', { class: 'material-icons', 'aria-hidden': 'true' }, name);
}

/**
 * Builds the map. Resolves to an integer handle, or rejects when MapLibre cannot be loaded. Every later outcome
 * (ready, failure, degraded, context loss) arrives through `dotnet`, tagged with `generation`.
 */
export async function create(element, options, dotnet, generation) {
    const lib = await loadLibrary();
    const handle = ++nextId;
    const h = { handle, disposed: false, timers: {}, listeners: [] };
    // Registered first, so a build that throws part-way is torn down by dispose like any other map.
    live.set(handle, h);
    try {
        build(lib, h, element, options, dotnet, generation);
    } catch (e) {
        dispose(handle);
        // The Map constructor throws only when the GPU context cannot be created: the browser can't draw
        // maps, the webgl-unavailable state, not a tile failure. 0 tells .NET exactly that.
        if (!h.map) return 0;
        throw e;
    }
    return handle;
}

function build(lib, h, element, options, dotnet, generation) {
    const id = `loc-map-${h.handle}`;
    Object.assign(h, {
        id, generation, dotnet, ready: false, failed: false,
        lngLat: [options.lng, options.lat], name: options.name,
        tileErrors: [], degraded: false,
    });

    const call = (method, ...args) => {
        if (h.disposed) return;
        dotnet.invokeMethodAsync(method, h.generation, ...args).catch(() => { });
    };
    const fail = kind => {
        if (h.failed || h.disposed) return;
        h.failed = true;
        clearTimeout(h.timers.load);
        call('OnFailed', kind);
    };

    const motion = reducedMotion();
    // No style in the constructor: it cannot take a transformStyle, so the style is set right after (below).
    const map = new lib.Map({
        container: element,
        center: h.lngLat,
        zoom: options.zoom,
        maxZoom: options.maxZoom,
        attributionControl: false,
        cooperativeGestures: true,
        locale: LOCALE,
        dragRotate: false,
        pitchWithRotate: false,
        touchPitch: false,
        fadeDuration: motion ? 0 : 300,
        // Tiles, glyphs, sprites and styles go out with no cookie, same-origin styles included (§5).
        transformRequest: url => ({ url, credentials: 'omit' }),
    });
    h.map = map;
    map.setStyle(options.styleUrl, { transformStyle: sameOriginStyle });
    map.keyboard.disableRotation();
    map.touchZoomRotate.disableRotation();
    if (motion) map.dragPan.enable({ maxSpeed: 0 });

    // The surface: exactly one map element (§6.2). MapLibre's own role and label are replaced.
    const canvas = map.getCanvas();
    canvas.setAttribute('role', 'group');
    canvas.setAttribute('aria-roledescription', 'map');
    canvas.setAttribute('aria-label', options.name);
    if (options.describedBy) canvas.setAttribute('aria-describedby', options.describedBy);
    map.getContainer().removeAttribute('aria-label');

    // The pin: a real <button>, named "{Name} pin", controlling the popup (§6.4).
    const popupId = `${id}-popup`;
    const headingId = `${id}-popup-h`;
    const pin = node('button', { type: 'button', class: 'loc-map-pin', 'aria-label': `${options.name} pin`, 'aria-expanded': 'false', 'aria-controls': popupId });
    pin.appendChild(node('span', { class: 'loc-map-pin-shape', 'aria-hidden': 'true' }));
    h.pin = pin;
    h.marker = new lib.Marker({ element: pin, anchor: 'bottom' }).setLngLat(h.lngLat).addTo(map);

    const content = node('div', { id: popupId, class: 'loc-map-popup', role: 'dialog', 'aria-labelledby': headingId });
    const heading = node('h3', { id: headingId }, options.name);
    const close = node('button', { type: 'button', class: 'loc-map-popup-close', 'aria-label': 'Close' });
    close.appendChild(glyph('close'));
    content.append(heading, close);
    h.heading = heading;
    h.popup = new lib.Popup({ closeButton: false, closeOnClick: false, closeOnMove: false, focusAfterOpen: false, maxWidth: 'none', className: 'loc-map-popup-host' })
        .setDOMContent(content);

    const openPopup = () => {
        const frameHeight = map.getContainer().clientHeight;
        const below = map.project(h.lngLat).y <= frameHeight / 2;
        const gap = 8;
        const offset = below ? [0, gap] : [0, -(pin.offsetHeight + gap)];
        h.popup.options.anchor = below ? 'top' : 'bottom';
        h.popup.setOffset(offset).setLngLat(h.lngLat).addTo(map);
        pin.setAttribute('aria-expanded', 'true');
        close.focus();
    };
    const closePopup = returnFocus => {
        if (!h.popup.isOpen()) return;
        const focusWasInside = content.contains(document.activeElement) || document.activeElement === pin;
        h.popup.remove();
        pin.setAttribute('aria-expanded', 'false');
        if (returnFocus || focusWasInside) pin.focus();
    };
    h.closePopup = closePopup;
    on(h, pin, 'click', () => (h.popup.isOpen() ? closePopup(true) : openPopup()));
    on(h, pin, 'keydown', e => { if (e.key === 'Escape' && h.popup.isOpen()) { e.preventDefault(); closePopup(true); } });
    on(h, close, 'click', () => closePopup(true));
    on(h, content, 'keydown', e => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePopup(true); } });
    map.on('click', e => {
        const target = e.originalEvent?.target;
        if (target instanceof Element && target.closest('.loc-map-pin, .loc-map-popup')) return;
        if (!h.popup.isOpen()) return;
        // A click on the map closes the popup; focus stays wherever that click put it (§6.4).
        h.popup.remove();
        pin.setAttribute('aria-expanded', 'false');
    });

    buildControls(h, map, motion);

    // Failure and degradation (§3).
    h.timers.load = setTimeout(() => fail('timeout'), LOAD_TIMEOUT_MS);
    map.on('load', () => {
        if (h.disposed || h.failed) return;
        h.ready = true;
        clearTimeout(h.timers.load);
        call('OnReady');
    });
    map.on('error', e => {
        if (h.disposed) return;
        // A source-level error (a sourceId, no tile) means the source's own index could not load — a TileJSON
        // or, for the self-hosted basemap, the archive's header (a 401 after sign-out, a 429, a 404). Such a
        // source draws nothing at all, and MapLibre still fires 'load' after it, so counting it as one tile
        // error would leave a blank map reporting ready (issue #384 review). Fail at once, before or after ready.
        if (e.sourceId && !e.tile) { fail('tiles'); return; }
        const isTile = !!(e.tile || e.sourceId);
        if (!h.ready) {
            if (!isTile) { fail('style'); return; }
            h.tileErrors.push(Date.now());
            if (h.tileErrors.length >= TILE_ERRORS_BEFORE_LOAD) fail('tiles');
            return;
        }
        if (!isTile) {
            // After ready, a non-tile error is a style swap that failed.
            if (h.styleSwapping) fail('style');
            return;
        }
        const now = Date.now();
        h.tileErrors = h.tileErrors.filter(t => now - t < DEGRADED_WINDOW_MS);
        h.tileErrors.push(now);
        if (!h.degraded && h.tileErrors.length >= DEGRADED_ERRORS) {
            h.degraded = true;
            call('OnDegraded');
        }
        if (h.degraded) {
            clearTimeout(h.timers.degraded);
            h.timers.degraded = setTimeout(() => {
                h.degraded = false;
                h.tileErrors = [];
                call('OnRecovered');
            }, DEGRADED_CLEAR_MS);
        }
    });
    map.on('style.load', () => { h.styleSwapping = false; });

    on(h, canvas, 'webglcontextlost', () => {
        clearTimeout(h.timers.context);
        h.timers.context = setTimeout(() => call('OnContextLost'), CONTEXT_RESTORE_MS);
    });
    on(h, canvas, 'webglcontextrestored', () => {
        if (!h.timers.context) return;
        clearTimeout(h.timers.context);
        h.timers.context = null;
        call('OnContextRestored');
    });

    // The frame's width follows the card; MapLibre does not watch it on its own (§4, F2).
    if (typeof ResizeObserver !== 'undefined') {
        h.resizeObserver = new ResizeObserver(() => { if (!h.disposed) map.resize(); });
        h.resizeObserver.observe(element);
    }
}

function buildControls(h, map, motion) {
    const container = map.getContainer();
    const dpad = node('div', { class: 'loc-map-dpad' });
    const third = axis => Math.round((axis === 'x' ? container.clientWidth : container.clientHeight) / 3);
    const actions = {
        n: () => map.panBy([0, -third('y')], { duration: 0 }),
        s: () => map.panBy([0, third('y')], { duration: 0 }),
        w: () => map.panBy([-third('x'), 0], { duration: 0 }),
        e: () => map.panBy([third('x'), 0], { duration: 0 }),
        c: () => map.jumpTo({ center: h.lngLat }),
    };
    for (const c of CONTROLS) {
        const b = node('button', { type: 'button', class: `loc-map-btn ${c.key}`, 'aria-label': c.label });
        b.appendChild(glyph(c.glyph));
        on(h, b, 'click', actions[c.key]);
        dpad.appendChild(b);
    }

    const zoom = node('div', { class: 'loc-map-zoom' });
    const zoomIn = node('button', { type: 'button', class: 'loc-map-btn', 'aria-label': 'Zoom in' });
    zoomIn.appendChild(glyph('add'));
    const zoomOut = node('button', { type: 'button', class: 'loc-map-btn', 'aria-label': 'Zoom out' });
    zoomOut.appendChild(glyph('remove'));
    // Steps accumulate onto the zoom being eased TO, so a second click mid-animation is not lost.
    let target = null;
    const current = () => target ?? map.getZoom();
    // aria-disabled, never disabled: a control at its limit keeps focus and does nothing (§6.3).
    const atMax = () => current() >= map.getMaxZoom() - 1e-6;
    const atMin = () => current() <= map.getMinZoom() + 1e-6;
    const mark = (button, limit) => (limit ? button.setAttribute('aria-disabled', 'true') : button.removeAttribute('aria-disabled'));
    const sync = () => {
        mark(zoomIn, atMax());
        mark(zoomOut, atMin());
        // The settled zoom, readable from outside: the E2E tier asserts the start zoom and the cap with it.
        container.dataset.zoom = map.getZoom().toFixed(2);
    };
    const step = delta => {
        target = Math.max(map.getMinZoom(), Math.min(map.getMaxZoom(), Math.round(current()) + delta));
        map.easeTo({ zoom: target, duration: motion ? 0 : 200 });
        sync();
    };
    on(h, zoomIn, 'click', () => { if (!atMax()) step(1); });
    on(h, zoomOut, 'click', () => { if (!atMin()) step(-1); });
    map.on('zoom', () => { if (target === null) sync(); });
    map.on('zoomend', () => { target = null; sync(); });
    sync();
    zoom.append(zoomIn, zoomOut);

    container.append(dpad, zoom);
}

function on(h, target, type, fn) {
    target.addEventListener(type, fn);
    h.listeners.push(() => target.removeEventListener(type, fn));
}

/** Moves the pin (keeping the zoom) and renames the pin, popup and map. */
export function update(handle, values) {
    const h = live.get(handle);
    if (!h) return;
    const moved = values.lat !== h.lngLat[1] || values.lng !== h.lngLat[0];
    if (moved) {
        h.lngLat = [values.lng, values.lat];
        h.marker.setLngLat(h.lngLat);
        if (h.popup.isOpen()) h.popup.setLngLat(h.lngLat);
        h.map.jumpTo({ center: h.lngLat });
    }
    if (values.name !== h.name) {
        h.name = values.name;
        h.pin.setAttribute('aria-label', `${values.name} pin`);
        h.heading.textContent = values.name;
        h.map.getCanvas().setAttribute('aria-label', values.name);
    }
}

/** Focus the map surface (its canvas) — where Try again lands once the map is ready (§6.12). */
export function focusSurface(handle) {
    const h = live.get(handle);
    if (!h) return;
    h.map.getCanvas().focus();
}

/** Whether keyboard focus is anywhere inside this map — so a silent re-create can put it back. */
export function hasFocus(handle) {
    const h = live.get(handle);
    return !!h && h.map.getContainer().contains(document.activeElement);
}

/** The theme swap. The marker and popup are DOM and survive it. */
export function setStyle(handle, styleUrl) {
    const h = live.get(handle);
    if (!h) return;
    h.styleSwapping = true;
    h.map.setStyle(styleUrl, { transformStyle: sameOriginStyle });
}

/** Idempotent; safe on a handle whose map never loaded. */
export function dispose(handle) {
    const h = live.get(handle);
    if (!h) return;
    live.delete(handle);
    h.disposed = true;
    for (const t of Object.values(h.timers)) clearTimeout(t);
    h.resizeObserver?.disconnect();
    for (const off of h.listeners) off();
    try { h.popup?.remove(); } catch { /* already gone */ }
    try { h.map?.remove(); } catch { /* already gone */ }
}
