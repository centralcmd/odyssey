// Focus a form control by id — used by the onboarding gate to move focus to the first offending
// field on a failed save (issue #316 §3 accessibility). No-op if the element is gone.
export function focusById(id) {
    const el = document.getElementById(id);
    if (el && typeof el.focus === 'function') {
        try { el.focus(); } catch { /* element may have been removed; ignore */ }
    }
}
