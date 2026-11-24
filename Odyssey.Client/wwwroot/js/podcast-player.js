// In-browser podcast playback (issue #594, over backend #593; design system components/PodcastPlayer). Loaded lazily
// on the first Play. The page holds ONE session; this module owns its single <audio> element, appended to
// document.body (never inside a card, so a card can collapse while it plays) with no controls of its own.
//
// Trust boundaries (#594 §4, security review): the title and content type come from third-party feeds, so nothing
// here builds markup from a string; the element never gets a crossorigin attribute; the source must be on the API's
// origin and match the stream route; and the final save is a keepalive fetch PUT carrying the antiforgery header,
// never sendBeacon (POST-only, no header) and never an exemption. PodcastPlayerSourceLintTests pins these.

let audio = null;
let owner = null;
let state = null; // { apiOrigin, saveUrl, token, canSave, lastTick }

function emit(kind, extra) {
    if (!owner || !audio) {
        return;
    }
    const duration = Number.isFinite(audio.duration) ? audio.duration : null;
    owner.invokeMethodAsync('OnPlayerEvent', kind, audio.currentTime || 0, duration, extra || null).catch(() => {});
}

function onTimeUpdate() {
    const now = Date.now();
    if (state && now - state.lastTick >= 1000) {
        state.lastTick = now;
        emit('time');
    }
}

function onError() {
    const code = audio && audio.error ? audio.error.code : 0;
    emit('error', String(code));
}

const listeners = {
    timeupdate: onTimeUpdate,
    playing: () => emit('playing'),
    pause: () => emit('pause'),
    ended: () => emit('ended'),
    waiting: () => emit('waiting'),
    loadedmetadata: () => emit('metadata'),
    error: onError,
};

function sourceAllowed(src, apiOrigin, pattern) {
    try {
        const url = new URL(src, document.baseURI);
        return url.origin === apiOrigin && new RegExp(pattern).test(url.pathname);
    } catch {
        return false;
    }
}

function body(seconds, completed) {
    return JSON.stringify({ positionSeconds: completed ? null : Math.max(0, Math.floor(seconds)), completed: !!completed });
}

// The final save on leaving: best effort, no result, no UI. A missing token skips it rather than sending a request the
// antiforgery filter would refuse.
function finalSave() {
    if (!state || !state.canSave || !state.token || !audio || audio.ended) {
        return;
    }
    try {
        fetch(state.saveUrl, {
            method: 'PUT',
            keepalive: true,
            credentials: 'include',
            headers: { 'Content-Type': 'application/json', [state.tokenHeader]: state.token },
            body: body(audio.currentTime || 0, false),
        }).catch(() => {});
    } catch {
        // keepalive quota exceeded or fetch unavailable: nothing to do on the way out.
    }
}

function onPageHide() {
    finalSave();
    stop();
}

function onVisibility() {
    // A hidden tab keeps playing; only the position is saved.
    if (document.visibilityState === 'hidden') {
        finalSave();
    }
}

// The per-browser volume (0–100). Storage can be missing or refuse; the value is validated on both sides.
export function readVolume() {
    try {
        const v = parseInt(window.localStorage.getItem('odyssey-podcast-volume'), 10);
        return Number.isInteger(v) && v >= 0 && v <= 100 ? v : 100;
    } catch {
        return 100;
    }
}

export function writeVolume(percent) {
    try {
        window.localStorage.setItem('odyssey-podcast-volume', String(Math.min(100, Math.max(0, Math.round(percent)))));
    } catch {
        // Storage unavailable: playback is unaffected.
    }
}

export function canPlay(contentType) {
    const probe = document.createElement('audio');
    return !!contentType && probe.canPlayType(String(contentType)) !== '';
}

// Starts a session. Returns 'ok', 'refused' (bad source), or the play() rejection's name ('NotAllowedError', ...).
export async function start(dotNet, options) {
    stop();
    if (!sourceAllowed(options.src, options.apiOrigin, options.pattern)) {
        return 'refused';
    }
    owner = dotNet;
    state = {
        apiOrigin: options.apiOrigin,
        saveUrl: options.saveUrl,
        token: options.token || null,
        tokenHeader: options.tokenHeader,
        canSave: !!options.canSave,
        lastTick: 0,
    };
    audio = document.createElement('audio');
    audio.preload = 'none';
    audio.setAttribute('aria-hidden', 'true');
    audio.hidden = true;
    for (const [name, fn] of Object.entries(listeners)) {
        audio.addEventListener(name, fn);
    }
    document.body.appendChild(audio);
    window.addEventListener('pagehide', onPageHide);
    document.addEventListener('visibilitychange', onVisibility);
    setVolume(options.volume, options.muted);
    audio.src = options.src;
    if (options.startAt > 0) {
        audio.currentTime = options.startAt;
    }
    return await play();
}

export async function play() {
    if (!audio) {
        return 'stopped';
    }
    try {
        await audio.play();
        return 'ok';
    } catch (e) {
        return (e && e.name) || 'error';
    }
}

export function pause() {
    if (audio) {
        audio.pause();
    }
}

export function seek(seconds) {
    if (audio && Number.isFinite(seconds)) {
        audio.currentTime = Math.max(0, seconds);
    }
}

export function setVolume(percent, muted) {
    if (!audio) {
        return;
    }
    const v = Number.isFinite(percent) ? Math.min(100, Math.max(0, percent)) : 100;
    audio.volume = v / 100;
    audio.muted = !!muted;
}

// Refreshed with every periodic save; cleared (null) after a sign-out, which then skips the final save.
export function setToken(token) {
    if (state) {
        state.token = token || null;
    }
}

export function position() {
    return audio ? audio.currentTime || 0 : 0;
}

// The page's own stop (another episode, a removal, a list change, navigation): a final save, then release.
export function leave() {
    finalSave();
    stop();
}

export function stop() {
    window.removeEventListener('pagehide', onPageHide);
    document.removeEventListener('visibilitychange', onVisibility);
    if (audio) {
        for (const [name, fn] of Object.entries(listeners)) {
            audio.removeEventListener(name, fn);
        }
        audio.pause();
        audio.removeAttribute('src');
        audio.load();
        audio.remove();
    }
    audio = null;
    owner = null;
    state = null;
}

// ── The seek slider's keys ─────────────────────────────────────────────────────────────────────────
// ←/→ and ↓/↑ step, PageUp/PageDown page, Home/End jump. A key-dependent preventDefault cannot be done from Blazor, so
// the slider's value is set here and a change event hands the committed position back to OdsPodcastPlayer.
const bound = new WeakMap();

export function bindSeek(input, step, page) {
    if (!input || bound.has(input)) {
        return;
    }
    const handler = (e) => {
        if (input.getAttribute('aria-disabled') === 'true') {
            return;
        }
        const max = Number(input.max) || 0;
        const value = Number(input.value) || 0;
        let next = null;
        switch (e.key) {
            case 'ArrowLeft': case 'ArrowDown': next = value - step; break;
            case 'ArrowRight': case 'ArrowUp': next = value + step; break;
            case 'PageDown': next = value - page; break;
            case 'PageUp': next = value + page; break;
            case 'Home': next = 0; break;
            case 'End': next = max; break;
            default: return;
        }
        e.preventDefault();
        input.value = String(Math.min(max, Math.max(0, next)));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };
    input.addEventListener('keydown', handler);
    bound.set(input, handler);
}

export function unbindSeek(input) {
    const handler = input && bound.get(input);
    if (handler) {
        input.removeEventListener('keydown', handler);
        bound.delete(input);
    }
}
