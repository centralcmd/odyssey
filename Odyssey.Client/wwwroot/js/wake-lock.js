// Screen wake lock for the Recipes page's "Keep screen awake" (issue #526; design system components-recipes.md ·
// Cooking actions). Loaded lazily. One lock at a time: the page holds at most one open card.
//
// A browser releases a screen wake lock whenever the tab is hidden. While .NET still wants the lock, this module
// takes it back when the tab is visible again; if that fails it tells .NET (OnWakeLockLost) so the page drops its
// "Screen stays awake" chip and says the screen can't be kept awake, rather than showing a lock it no longer holds.
let sentinel = null;
let wanted = false;
let owner = null;

export function supported() {
    return typeof navigator !== 'undefined' && 'wakeLock' in navigator;
}

async function acquire() {
    const lock = await navigator.wakeLock.request('screen');
    lock.addEventListener('release', onReleased);
    sentinel = lock;
}

function onReleased() {
    sentinel = null;
    // Released while visible (a battery saver, the system) with the lock still wanted: try once to take it back.
    if (wanted && document.visibilityState === 'visible') {
        reacquire();
    }
}

function onVisibility() {
    if (wanted && !sentinel && document.visibilityState === 'visible') {
        reacquire();
    }
}

// One re-acquire in flight at a time: `release` and `visibilitychange` can both fire before the first request
// settles, and a second request would leak the first lock.
let reacquiring = false;

async function reacquire() {
    if (reacquiring || sentinel) {
        return;
    }
    reacquiring = true;
    try {
        await acquire();
        if (!wanted) {
            // The page let the screen sleep while the request was in flight.
            const held = sentinel;
            sentinel = null;
            held?.removeEventListener('release', onReleased);
            try { await held?.release(); } catch { /* already released */ }
        }
    } catch {
        const lost = owner;
        stop();
        if (lost) {
            try { await lost.invokeMethodAsync('OnWakeLockLost'); } catch { /* the page is gone */ }
        }
    } finally {
        reacquiring = false;
    }
}

function stop() {
    wanted = false;
    owner = null;
    document.removeEventListener('visibilitychange', onVisibility);
}

// Returns whether the lock is held. A refusal (battery saver, a hidden tab) returns false; the page says so.
// dotNetRef receives OnWakeLockLost when a lock the page still wants cannot be taken back.
export async function request(dotNetRef) {
    await release();
    if (!supported()) {
        return false;
    }
    try {
        await acquire();
    } catch {
        return false;
    }
    wanted = true;
    owner = dotNetRef ?? null;
    document.addEventListener('visibilitychange', onVisibility);
    return true;
}

// Lets the screen sleep and drops the listeners and the .NET reference.
export async function release() {
    stop();
    const held = sentinel;
    sentinel = null;
    if (held) {
        held.removeEventListener('release', onReleased);
        try { await held.release(); } catch { /* already released by the browser */ }
    }
}
