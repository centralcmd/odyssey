// OdsSortableList (Odyssey Design System · components/SortableList, version 1795477868-c9a2) — the two things
// Blazor cannot do for the list: pointer-drag geometry and key-dependent event control. Everything else (the
// lift state, the moves, the announcements, the ⋯ menu) is .NET.
//
// • Pointer drag: a press on a row's handle captures the pointer, tracks which gap the pointer is over (the
//   slot before row i, or after the last row) from the rows' own boxes, scrolls the nearest scrolling ancestor
//   while the pointer is within 48px of its edge — on every animation frame, so holding the pointer still at
//   the edge keeps scrolling — and reports the slot to .NET as it changes (DragOver) and once on release
//   (DragEnd). Nothing in the list moves while dragging.
// • Keys: Space / Enter on a handle never activate it as a button, and ↑ / ↓ on a LIFTED handle never scroll
//   the page (Blazor's preventDefault cannot depend on which key was pressed). Escape on a lifted handle is
//   stopped HERE, before it bubbles to the dialog's key interceptor, so it cancels the move and does not also
//   close an enclosing OdsModal; .NET is told through CancelLift.
const EDGE = 48;
const STEP = 16;

function rows(list) {
    return Array.from(list.querySelectorAll(':scope > [data-sort-row]'));
}

function slotAt(list, y) {
    const rs = rows(list);
    for (let i = 0; i < rs.length; i++) {
        const r = rs[i].getBoundingClientRect();
        if (y < r.top + r.height / 2) {
            return i;
        }
    }
    return rs.length;
}

function scroller(el) {
    for (let p = el && el.parentElement; p; p = p.parentElement) {
        const o = getComputedStyle(p).overflowY;
        if ((o === 'auto' || o === 'scroll') && p.scrollHeight > p.clientHeight) {
            return p;
        }
    }
    return null;
}

function handleOf(target) {
    return target && target.closest ? target.closest('[data-sort-handle]') : null;
}

export function attach(list, dotNet) {
    if (!list) {
        return null;
    }

    let activeDrag = null;

    const onKeyDown = (e) => {
        const handle = handleOf(e.target);
        if (!handle || !list.contains(handle)) {
            return;
        }
        const lifted = handle.getAttribute('aria-pressed') === 'true';
        if (e.key === ' ' || e.key === 'Enter') {
            e.preventDefault();
        } else if (lifted && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
            e.preventDefault();
        } else if (lifted && e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            e.stopImmediatePropagation();
            dotNet.invokeMethodAsync('CancelLift').catch(() => { /* the list went away */ });
        }
    };

    const onPointerDown = (e) => {
        const handle = handleOf(e.target);
        if (!handle || !list.contains(handle) || e.button !== 0 || handle.getAttribute('aria-pressed') === 'true') {
            return;
        }
        const from = Number(handle.getAttribute('data-sort-handle'));
        if (Number.isNaN(from)) {
            return;
        }
        e.preventDefault();
        try { handle.setPointerCapture(e.pointerId); } catch { /* not capturable; moves still arrive */ }
        const sc = scroller(list);
        let over = from;
        let y = e.clientY;
        let frame = 0;
        dotNet.invokeMethodAsync('DragOver', from, over).catch(() => { });

        const track = () => {
            const next = slotAt(list, y);
            if (next !== over) {
                over = next;
                dotNet.invokeMethodAsync('DragOver', from, over).catch(() => { });
            }
        };
        // Runs while the pointer sits in an edge band: scroll one step, re-read the slot (the rows moved under the
        // pointer), and go again next frame. It stops itself once the pointer leaves the band or the drag ends.
        const autoScroll = () => {
            frame = 0;
            if (!sc) {
                return;
            }
            const r = sc.getBoundingClientRect();
            const before = sc.scrollTop;
            if (y < r.top + EDGE) {
                sc.scrollTop -= STEP;
            } else if (y > r.bottom - EDGE) {
                sc.scrollTop += STEP;
            } else {
                return;
            }
            if (sc.scrollTop !== before) {
                track();
                frame = requestAnimationFrame(autoScroll);
            }
        };
        const onMove = (ev) => {
            y = ev.clientY;
            track();
            if (!frame) {
                autoScroll();
            }
        };
        const stop = () => {
            if (frame) {
                cancelAnimationFrame(frame);
                frame = 0;
            }
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onUp);
            handle.removeEventListener('pointercancel', onUp);
            activeDrag = null;
        };
        const onUp = () => {
            stop();
            dotNet.invokeMethodAsync('DragEnd', from, over).catch(() => { });
        };
        // Detaching mid-drag (the dialog closed) stops the loop and the listeners without reporting a drop.
        activeDrag = stop;
        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onUp);
        handle.addEventListener('pointercancel', onUp);
    };

    list.addEventListener('keydown', onKeyDown);
    list.addEventListener('pointerdown', onPointerDown);
    return {
        dispose: () => {
            if (activeDrag) {
                activeDrag();
            }
            list.removeEventListener('keydown', onKeyDown);
            list.removeEventListener('pointerdown', onPointerDown);
        },
    };
}

export function detach(handle) {
    if (handle && typeof handle.dispose === 'function') {
        handle.dispose();
    }
}
