/**
 * Odyssey DS — ActionMenu
 * The row-level overflow menu (the `more_vert` kebab) used by every record
 * table and list row. Maps to a MudMenu of MudMenuItems.
 *
 * Pass `items` — each is either a divider (`{ divider: true }`) or an action
 * (`{ icon, label, onClick, danger, trailingIcon }`). `danger` tints the item
 * for destructive actions (Delete). `icon` is a Material Icons ligature or any
 * non-ligature glyph (e.g. "§"). `trailingIcon` renders a right-aligned Material
 * icon revealed on hover/focus — the `content_copy` affordance on a "Copy ID"
 * item. The popover is `position: fixed`, measured off
 * the trigger on open, so it escapes the cards' / table's `overflow:hidden`
 * instead of being clipped; it closes on outside-click, scroll, or resize.
 *
 * RecordTable renders this for you (build the items from its `actions` prop);
 * use it directly for bespoke list rows (Files, Budgets, …).
 */
export function ActionMenu({ items }) {
  const { useState, useRef, useEffect } = React;
  const noteId = React.useId();
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const ref = useRef(null);
  const btnRef = useRef(null);

  const toggle = () => {
    if (!open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 4, right: window.innerWidth - r.right });
    }
    setOpen((o) => !o);
  };

  useEffect(() => {
    if (!open) return;
    const onDoc = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    const close = () => setOpen(false);
    // Esc closes the menu and restores trigger focus — capture + stop so an
    // enclosing Modal doesn't close with it.
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      e.stopPropagation();
      setOpen(false);
      const b = btnRef.current && btnRef.current.querySelector('button');
      if (b) b.focus();
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [open]);

  // Menu keyboard pattern (matches Menu.jsx): focus moves to the first item on
  // open; ↑/↓ rove (wrapping), Home/End jump, activating restores the trigger.
  const popRef = useRef(null);
  const itemBtns = () =>
    popRef.current ? Array.from(popRef.current.querySelectorAll('.acct-menu-item:not([disabled])')) : [];
  const focusAt = (idx) => {
    const btns = itemBtns();
    if (!btns.length) return;
    btns[((idx % btns.length) + btns.length) % btns.length].focus();
  };
  useEffect(() => {
    if (open) requestAnimationFrame(() => focusAt(0));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  const closeMenu = (restore) => {
    setOpen(false);
    if (restore && btnRef.current) {
      const b = btnRef.current.querySelector('button');
      if (b) b.focus();
    }
  };
  const onPopKey = (e) => {
    const btns = itemBtns();
    const idx = btns.indexOf(document.activeElement);
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(idx + 1); break;
      case 'ArrowUp': e.preventDefault(); focusAt(idx - 1); break;
      case 'Home': e.preventDefault(); focusAt(0); break;
      case 'End': e.preventDefault(); focusAt(btns.length - 1); break;
      case 'Tab': closeMenu(false); break;
      default: break;
    }
  };

  return (
    <div className="acct-menu" ref={ref} onClick={(e) => e.stopPropagation()}>
      <span ref={btnRef}>
        <button type="button" className="odc-iconbtn" aria-label="More actions"
          aria-haspopup="menu" aria-expanded={open} onClick={toggle}>
          <span className="material-icons" aria-hidden="true">more_vert</span>
        </button>
      </span>
      {open && pos && (
        <div className="acct-menu-pop" role="menu" ref={popRef} style={{ top: pos.top, right: pos.right }} onKeyDown={onPopKey}>
          {items.map((it, i) => it.divider ? (
            <div key={i} className="acct-menu-sep" />
          ) : (
            <React.Fragment key={i}>
              <button
                role="menuitem"
                tabIndex={-1}
                className={`acct-menu-item ${it.danger ? 'danger' : ''}`}
                aria-disabled={it.disabled ? true : undefined}
                aria-describedby={it.note ? `${noteId}-${i}` : undefined}
                onClick={() => { if (it.disabled) return; closeMenu(true); it.onClick && it.onClick(); }}
              >
                {/^[a-z0-9_]+$/.test(it.icon)
                  ? <span className="material-icons" aria-hidden="true" style={{ fontSize: 18 }}>{it.icon}</span>
                  : <span className="acct-menu-glyph" aria-hidden="true">{it.icon}</span>}
                <span>{it.label}</span>
                {it.trailingIcon ? (
                  <span className="material-icons acct-menu-item-trail" aria-hidden="true" style={{ fontSize: 16 }}>{it.trailingIcon}</span>
                ) : null}
              </button>
              {/* Why a disabled action is unavailable — as text, never the dimmed
                  state alone. aria-disabled, not the disabled attribute, so the
                  item keeps its place in the roving-focus order. */}
              {it.note ? <p className="acct-menu-note" id={`${noteId}-${i}`}>{it.note}</p> : null}
            </React.Fragment>
          ))}
        </div>
      )}
    </div>
  );
}
