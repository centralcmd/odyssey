// Keyboard bridge for OdsTagIconPicker (Odyssey Design System · TagIconPicker).
// Blazor resolves @onkeydown:preventDefault once per render, not per key, so the grid's navigation
// keys would also scroll the dialog. This stops the default for exactly those keys — Tab and
// everything else pass through — and reports the live column count the arrows move over.
// Space is not here: on a button it activates rather than scrolls, and the native click is the pick.
// The grid scrolls inside a fixed window, so attach also brings the selected cell into it — an edit
// opening on a far icon starts with that icon in view, without moving focus or scrolling the dialog.
// The search box: Arrow Down (into the grid) and Enter (pick the first match) must not move the caret
// or submit a surrounding form, and Escape on a non-empty query clears it rather than closing the dialog.
const NAV_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End']);

export function attach(grid, search) {
    if (!grid) return { detach: () => { } };
    const onKey = (e) => {
        if (NAV_KEYS.has(e.key) && e.target && e.target.getAttribute('role') === 'radio') e.preventDefault();
    };
    const onSearchKey = (e) => {
        if (e.key === 'ArrowDown' || e.key === 'Enter') e.preventDefault();
    };
    // MudBlazor's dialog takes Escape before the event reaches the box, so this listens on the window in
    // the capture phase — ahead of every other handler — and only for the box holding a query. Blazor
    // never sees the key, so the clear is reported as an ordinary input event.
    const onEscape = (e) => {
        if (e.key !== 'Escape' || e.target !== search || search.value === '') return;
        e.preventDefault();
        e.stopImmediatePropagation();
        search.value = '';
        search.dispatchEvent(new Event('input', { bubbles: true }));
    };
    grid.addEventListener('keydown', onKey);
    if (search) {
        search.addEventListener('keydown', onSearchKey);
        window.addEventListener('keydown', onEscape, true);
    }
    const selected = grid.querySelector('[aria-checked="true"]');
    if (selected) grid.scrollTop = Math.max(0, selected.offsetTop - (grid.clientHeight - selected.offsetHeight) / 2);
    return {
        detach: () => {
            grid.removeEventListener('keydown', onKey);
            if (search) {
                search.removeEventListener('keydown', onSearchKey);
                window.removeEventListener('keydown', onEscape, true);
            }
        },
    };
}

export function columns(grid) {
    if (!grid) return 0;
    const template = getComputedStyle(grid).gridTemplateColumns;
    return template ? template.split(' ').filter(Boolean).length : 0;
}
