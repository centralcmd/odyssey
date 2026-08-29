/**
 * Odyssey DS — Menu
 * The overflow / row-actions dropdown used on every record row (the
 * `more_vert` menu) and anywhere a compact action list is needed. Maps to
 * a MudMenu + MudMenuItem.
 *
 * Self-contained: it owns its open state, closes on outside-click and Esc.
 * Pass `items` as a flat list; mark separators with `{divider:true}`, group
 * headers with `{header:'…'}`, and destructive actions with `{danger:true}`.
 *
 * A disabled item can carry a `note` — one line saying WHY it is unavailable,
 * rendered under the label and wired as its `aria-describedby`. Such an item
 * uses `aria-disabled` rather than the `disabled` attribute, so it stays in the
 * roving-focus order: a keyboard or screen-reader user reaches the reason
 * instead of a silently skipped item.
 * Defaults to an icon-button trigger (more_vert); pass your own `trigger`
 * element to anchor it to a Button instead.
 *
 * The popover is portaled to <body> and positioned against the trigger, so it
 * escapes any overflow:hidden/auto ancestor — a scrollable table, card, or
 * modal — instead of being clipped. It flips above the trigger when there
 * isn't room below and repositions on scroll / resize. (`placement` is now
 * auto; the prop is accepted for back-compat but ignored.)
 *
 * Keyboard (WAI-ARIA menu pattern): opening moves focus to the first item;
 * ↑/↓ roves between items (wrapping), Home/End jump to the ends, Esc or Tab
 * closes and returns focus to the trigger. Activating an item closes the
 * menu and restores focus to the trigger before the action runs.
 */

/* ---- odcUsePopover — fixed-position, portaled popover ----
 * Measures the trigger and renders the panel into <body> so it escapes any
 * overflow:hidden/auto ancestor (a scrollable table, card, modal, or
 * collapsible). Flips above when there isn't room below, clamps to the
 * viewport horizontally, repositions on scroll / resize, and closes on
 * outside-click + Esc. Mirrors the kit's usePopover, hardened with collision
 * handling. Attach `anchorRef` to the trigger wrapper and `popRef` to the
 * portaled panel; spread `floatStyle` onto the panel. */
function odcUsePopover({ align = 'start', gap = 6, matchWidth = false } = {}) {
  const { useState, useRef, useCallback, useLayoutEffect } = React;
  const [open, setOpen] = useState(false);
  const [box, setBox] = useState(null);
  const anchorRef = useRef(null);
  const popRef = useRef(null);

  const place = useCallback(() => {
    const a = anchorRef.current;
    if (!a) return;
    const r = a.getBoundingClientRect();
    const pop = popRef.current;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const ph = pop ? pop.offsetHeight : 0;
    const pw = pop ? pop.offsetWidth : r.width;
    const roomBelow = vh - r.bottom;
    const roomAbove = r.top;
    let top;
    let placement;
    if (roomBelow >= ph + gap || roomBelow >= roomAbove) {
      top = r.bottom + gap;
      placement = 'down';
    } else {
      top = Math.max(gap, r.top - gap - ph);
      placement = 'up';
    }
    let left = align === 'end' ? r.right - pw : r.left;
    left = Math.min(Math.max(gap, left), Math.max(gap, vw - pw - gap));
    setBox({ top, left, width: matchWidth ? r.width : null, placement });
  }, [align, gap, matchWidth]);

  useLayoutEffect(() => {
    if (!open) { setBox(null); return undefined; }
    place();
    const onScroll = (e) => { if (popRef.current && popRef.current.contains(e.target)) return; place(); };
    const onResize = () => place();
    const onDoc = (e) => {
      const a = anchorRef.current;
      const p = popRef.current;
      if ((a && a.contains(e.target)) || (p && p.contains(e.target))) return;
      setOpen(false);
    };
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      // Capture-phase + stopPropagation: Esc closes only this popover — a
      // Modal underneath (bubble-phase document listener) never sees it —
      // and keyboard focus returns to the trigger.
      e.stopPropagation();
      setOpen(false);
      const t = anchorRef.current && anchorRef.current.querySelector('button:not([disabled]), input, select, textarea, [tabindex]');
      if (t) t.focus();
    };
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey, true);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [open, place]);

  const floatStyle = box
    ? { position: 'fixed', top: box.top, left: box.left, right: 'auto', bottom: 'auto', margin: 0, width: box.width || undefined }
    : { position: 'fixed', top: 0, left: 0, visibility: 'hidden' };
  const toggle = useCallback(() => setOpen((o) => !o), []);
  return { open, setOpen, toggle, anchorRef, popRef, floatStyle, placement: box ? box.placement : 'down' };
}

export function Menu({
  items = [],
  align = 'end',
  placement, // accepted for back-compat; vertical side is now auto (flips on collision)
  trigger,
  ariaLabel = 'More actions',
}) {
  const { open, setOpen, anchorRef, popRef, floatStyle, placement: side } = odcUsePopover({ align });
  const triggerElRef = React.useRef(null);
  const menuId = React.useId();
  void placement;

  const itemButtons = () =>
    popRef.current
      ? Array.from(popRef.current.querySelectorAll('.odc-menu-item:not([disabled])'))
      : [];

  const focusAt = (idx) => {
    const btns = itemButtons();
    if (!btns.length) return;
    const i = ((idx % btns.length) + btns.length) % btns.length;
    btns[i].focus();
  };

  const close = (restore = true) => {
    setOpen(false);
    if (restore && triggerElRef.current) triggerElRef.current.focus();
  };

  // On open, move focus to the first menu item (menu pattern).
  React.useEffect(() => {
    if (open) focusAt(0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const toggle = (e) => {
    e.stopPropagation();
    triggerElRef.current = e.currentTarget;
    setOpen((o) => !o);
  };

  const onKey = (e) => {
    const btns = itemButtons();
    const idx = btns.indexOf(document.activeElement);
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(idx + 1); break;
      case 'ArrowUp': e.preventDefault(); focusAt(idx - 1); break;
      case 'Home': e.preventDefault(); focusAt(0); break;
      case 'End': e.preventDefault(); focusAt(btns.length - 1); break;
      case 'Escape': e.preventDefault(); close(true); break;
      case 'Tab': close(false); break;
      default: break;
    }
  };

  const run = (it, e) => {
    e.stopPropagation();
    close(true);
    if (it.onClick) it.onClick();
  };

  return (
    <div className="odc-menu" ref={anchorRef}>
      {trigger ? (
        React.cloneElement(trigger, {
          onClick: toggle,
          'aria-haspopup': 'menu',
          'aria-expanded': open,
          'aria-controls': open ? menuId : undefined,
        })
      ) : (
        <button
          type="button"
          className="odc-iconbtn"
          aria-label={ariaLabel}
          aria-haspopup="menu"
          aria-expanded={open}
          aria-controls={open ? menuId : undefined}
          onClick={toggle}
        >
          <span className="material-icons" aria-hidden="true">more_vert</span>
        </button>
      )}
      {open
        ? ReactDOM.createPortal(
          <ul
            ref={popRef}
            id={menuId}
            className={`odc-menu-pop ${align} ${side}`}
            role="menu"
            aria-orientation="vertical"
            style={floatStyle}
            onKeyDown={onKey}
          >
            {items.map((it, i) => {
              if (it.divider) return <li key={i} role="separator" className="odc-menu-divider" />;
              if (it.header) return <li key={i} role="presentation" className="odc-menu-label">{it.header}</li>;
              const noteId = it.note ? `${menuId}-note-${i}` : undefined;
              return (
                <li key={i} role="none">
                  <button
                    type="button"
                    role="menuitem"
                    tabIndex={-1}
                    className={`odc-menu-item${it.danger ? ' danger' : ''}`}
                    disabled={it.disabled && !it.note}
                    aria-disabled={it.disabled ? true : undefined}
                    aria-describedby={noteId}
                    onClick={(e) => (it.disabled ? e.stopPropagation() : run(it, e))}
                  >
                    {it.icon ? <span className="material-icons" aria-hidden="true">{it.icon}</span> : null}
                    <span>{it.label}</span>
                  </button>
                  {it.note ? <p className="odc-menu-note" id={noteId}>{it.note}</p> : null}
                </li>
              );
            })}
          </ul>,
          document.body,
        )
        : null}
    </div>
  );
}
