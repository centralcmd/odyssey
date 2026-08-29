// Save/restore focus around a transient overlay (e.g. the photo lightbox) so that closing it
// returns focus to the control that opened it (WCAG 2.4.3 Focus Order). The host calls remember()
// synchronously before opening — when the activating control is still document.activeElement — and
// restore() after the overlay unmounts.
let saved = null;

export function remember() {
    saved = document.activeElement;
}

export function restore() {
    const el = saved;
    saved = null;
    if (el && typeof el.focus === 'function') {
        try { el.focus(); } catch { /* element may have been removed; ignore */ }
    }
}
