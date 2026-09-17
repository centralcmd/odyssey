// Return focus after a re-render destroyed the element that had it (WCAG 2.4.3 Focus Order).
//
// Blazor has no way to focus "whatever is still there" — an ElementReference to a removed node is
// already gone by the time OnAfterRenderAsync runs — so a caller that shrinks a rendered list names
// its preferred landing spot plus the fallbacks to try when that is gone too. Same shape as
// journal-board.js's focusMove, generalized: the kanban has exactly one fallback (the card), a list
// whose group can empty entirely needs a chain.
export function focusFirst(selectors) {
    for (const selector of selectors ?? []) {
        if (!selector) {
            continue;
        }

        const target = document.querySelector(selector);
        if (target && typeof target.focus === 'function') {
            // A node can be detached between the render and this call; treat that as "try the next
            // selector" rather than letting the exception abandon the whole chain.
            try {
                target.focus();
                return true;
            } catch {
                /* detached; fall through */
            }
        }
    }

    return false;
}
