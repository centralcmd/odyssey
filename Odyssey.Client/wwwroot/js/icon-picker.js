// Keyboard bridge for OdsTagIconPicker (Odyssey Design System · TagIconPicker).
// Blazor resolves @onkeydown:preventDefault once per render, not per key, so the grid's navigation
// keys would also scroll the dialog. This stops the default for exactly those keys — Tab and
// everything else pass through — and reports the live column count the arrows move over.
// Space is not here: on a button it activates rather than scrolls, and the native click is the pick.
// The grid scrolls inside a fixed window, so attach also brings the selected cell into it — an edit
// opening on a far icon starts with that icon in view, without moving focus or scrolling the dialog.
const NAV_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End']);

export function attach(grid) {
    if (!grid) return { detach: () => { } };
    const onKey = (e) => {
        if (NAV_KEYS.has(e.key) && e.target && e.target.getAttribute('role') === 'radio') e.preventDefault();
    };
    grid.addEventListener('keydown', onKey);
    const selected = grid.querySelector('[aria-checked="true"]');
    if (selected) grid.scrollTop = Math.max(0, selected.offsetTop - (grid.clientHeight - selected.offsetHeight) / 2);
    return { detach: () => grid.removeEventListener('keydown', onKey) };
}

export function columns(grid) {
    if (!grid) return 0;
    const template = getComputedStyle(grid).gridTemplateColumns;
    return template ? template.split(' ').filter(Boolean).length : 0;
}
