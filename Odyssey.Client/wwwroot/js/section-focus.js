// Move focus to a section heading after the row that held it was deleted (issue #155 §6.7).
//
// The row is gone, the next row is not "the same place", and letting the browser drop focus to
// <body> loses a keyboard user's position entirely — so the heading is the honest destination. It
// can only receive focus because OdsSectionDivider.HeadingId emits tabindex="-1" on it.
//
// THE DEFERRAL IS LOAD-BEARING, not defensive padding: the row's ⋯ menu restores focus to its own
// invoker as it closes, after this call would otherwise run, and a synchronous focus() is silently
// overwritten by it — landing the user somewhere else on the page entirely. The element is looked
// up inside the timeout because the delete re-renders the section.
export function focusHeading(id) {
    setTimeout(() => {
        const el = document.getElementById(id);
        if (el && typeof el.focus === 'function') {
            try { el.focus(); } catch { /* removed between the delete and the timeout; ignore */ }
        }
    }, 0);
}
