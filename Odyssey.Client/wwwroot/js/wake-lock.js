// Screen wake lock for the Recipes page's "Keep screen awake" (issue #526; design system components-recipes.md ·
// Cooking actions). Loaded lazily on the first open card. One lock at a time: the page holds at most one open card.
let sentinel = null;

export function supported() {
    return typeof navigator !== 'undefined' && 'wakeLock' in navigator;
}

// Returns whether the lock is held. A refusal (battery saver, a hidden tab) returns false; the page says so.
export async function request() {
    try {
        if (!supported()) {
            return false;
        }
        await release();
        sentinel = await navigator.wakeLock.request('screen');
        sentinel.addEventListener('release', () => { sentinel = null; });
        return true;
    } catch {
        sentinel = null;
        return false;
    }
}

export async function release() {
    const held = sentinel;
    sentinel = null;
    if (held) {
        try { await held.release(); } catch { /* already released by the browser */ }
    }
}
