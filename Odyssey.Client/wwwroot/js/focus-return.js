// Return focus after a re-render destroyed the element that had it (WCAG 2.4.3 Focus Order).
//
// Blazor has no way to focus "whatever is still there" — an ElementReference to a removed node is
// already gone by the time OnAfterRenderAsync runs — so a caller that shrinks a rendered list names
// its preferred landing spot plus the fallbacks to try when that is gone too. Same shape as
// journal-board.js's focusMove, generalized: the kanban has exactly one fallback (the card), a list
// whose group can empty entirely needs a chain.
// Takes the candidates either as one array or as separate arguments, and that is not politeness: .NET's
// InvokeVoidAsync(identifier, params object?[] args) binds a string[] as the ARGS array itself, so the
// natural-looking `InvokeVoidAsync("focusFirst", candidates)` arrives here spread into separate string
// arguments rather than as one array. Bound as `selectors`, the first of those is a string, and
// iterating a string yields characters — the first being "#", which throws "'#' is not a valid
// selector" and takes the whole focus return down with it. Both callers had it that way, so the caller
// is not where this can be relied on to stay right; flattening here is.
export function focusFirst(...args) {
    for (const selector of args.flat()) {
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
