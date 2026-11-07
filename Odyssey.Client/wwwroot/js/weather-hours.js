// The weather hours panel's browser half (issue #415 §6): Escape that closes only the panel, the overflow
// measurement that decides whether the table's wrapper is a focusable scroll region, and the focus check Blazor
// cannot make for itself. Nothing here builds markup; every node is rendered by Blazor.

const escapes = new WeakMap();
const observers = new WeakMap();

// Escape inside the panel closes the panel and nothing else: propagation stops HERE, for Escape only, so an
// enclosing dialog or popover is not closed in the same keystroke. Every other key propagates as usual.
export function attachEscape(panel, dotnet) {
    if (!panel || escapes.has(panel)) {
        return;
    }

    const onKeyDown = (event) => {
        if (event.key === 'Escape') {
            event.stopPropagation();
            event.preventDefault();
            dotnet.invokeMethodAsync('CloseFromEscape');
        }
    };
    panel.addEventListener('keydown', onKeyDown);
    escapes.set(panel, onKeyDown);
}

export function detachEscape(panel) {
    const onKeyDown = panel && escapes.get(panel);
    if (onKeyDown) {
        panel.removeEventListener('keydown', onKeyDown);
        escapes.delete(panel);
    }
}

// Reports whether the table overflows its wrapper. A ResizeObserver on both catches a change from the content
// or from a late font, not only a window resize. Before reporting "no longer overflows" while the wrapper itself
// holds focus, focus moves to the panel — the wrapper is about to lose its tabindex, and focus must never
// fall to <body>.
export function observeOverflow(wrapper, focusId, dotnet) {
    if (!wrapper || observers.has(wrapper) || typeof ResizeObserver === 'undefined') {
        return;
    }

    let last = null;
    const measure = () => {
        const overflows = wrapper.scrollWidth > wrapper.clientWidth + 1;
        if (overflows === last) {
            return;
        }

        if (!overflows && document.activeElement === wrapper) {
            const panel = document.getElementById(focusId);
            if (panel) {
                panel.focus();
            }
        }

        last = overflows;
        dotnet.invokeMethodAsync('OnOverflowChanged', overflows);
    };
    const observer = new ResizeObserver(measure);
    observer.observe(wrapper);
    if (wrapper.firstElementChild) {
        observer.observe(wrapper.firstElementChild);
    }

    observers.set(wrapper, observer);
    measure();
}

export function unobserveOverflow(wrapper) {
    const observer = wrapper && observers.get(wrapper);
    if (observer) {
        observer.disconnect();
        observers.delete(wrapper);
    }
}

// True when focus is inside any of the given elements — asked BEFORE a re-render removes them.
export function containsFocus(...elements) {
    const active = document.activeElement;
    return elements.some((el) => el && active && (el === active || el.contains(active)));
}

// Focus the panel by id: it lives in a child component, and Try again moves focus there before the
// skeleton replaces the error box, so focus never falls to <body>.
export function focusById(id) {
    const el = document.getElementById(id);
    if (el && typeof el.focus === 'function') {
        el.focus();
    }
}
