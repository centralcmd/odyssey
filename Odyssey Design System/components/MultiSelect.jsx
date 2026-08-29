/**
 * Odyssey DS — MultiSelect
 * The filter control behind every ledger header — account / status / tag /
 * direction on Transactions, account / type on Files. Maps to a MudSelect
 * with MultiSelection. A trigger button shows the label + a count badge of
 * active selections; the popover is a checkbox list with Clear / Done.
 * Outside-click and Esc close it.
 *
 * The popover is portaled to <body> and positioned against the trigger, so it
 * escapes any overflow:hidden/auto ancestor (the filter card, a scrollable
 * header) instead of being clipped — and flips above the trigger when there
 * isn't room below.
 *
 * Controlled: pass `value` (array) + `onChange(nextArray)`.
 */

/* ---- odcUsePopover — fixed-position, portaled popover ----
 * Measures the trigger and renders the panel into <body> so it escapes any
 * overflow:hidden/auto ancestor. Flips above when there isn't room below,
 * clamps to the viewport horizontally, repositions on scroll / resize, and
 * closes on outside-click + Esc. Attach `anchorRef` to the trigger wrapper
 * and `popRef` to the portaled panel; spread `floatStyle` onto the panel. */
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

export function MultiSelect({
  label = 'Filter',
  value = [],
  onChange,
  options = [],
  icon,
  align = 'start',
}) {
  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ align });

  const set = new Set(value);
  const toggle = (v) => {
    const next = new Set(set);
    if (next.has(v)) next.delete(v);
    else next.add(v);
    if (onChange) onChange([...next]);
  };
  const clear = () => onChange && onChange([]);

  // Move focus into the popover on open — the checkbox list is native
  // (Tab/Space work for free); ↑/↓ also rove for menu-style ergonomics.
  React.useEffect(() => {
    if (!open) return;
    const first = popRef.current && popRef.current.querySelector('input[type="checkbox"]');
    if (first) first.focus();
  }, [open, popRef]);

  const onPopKey = (e) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    const els = popRef.current
      ? Array.from(popRef.current.querySelectorAll('input[type="checkbox"], .odc-btn:not([disabled])'))
      : [];
    if (!els.length) return;
    e.preventDefault();
    const idx = els.indexOf(document.activeElement);
    els[e.key === 'ArrowDown' ? Math.min(idx + 1, els.length - 1) : Math.max(idx - 1, 0)].focus();
  };

  return (
    <div className="odc-ms" ref={anchorRef}>
      <button
        type="button"
        className="odc-ms-trigger"
        aria-haspopup="true"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        {icon ? <span className="material-icons" aria-hidden="true">{icon}</span> : null}
        <span>{label}</span>
        {value.length ? <span className="odc-ms-count">{value.length}</span> : null}
        <span className="material-icons" aria-hidden="true">expand_more</span>
      </button>
      {open
        ? ReactDOM.createPortal(
          <div className={`odc-ms-pop ${align}`} role="group" aria-label={label} ref={popRef} style={floatStyle} onKeyDown={onPopKey}>
            {opts.map((o) => (
              <label className="odc-ms-opt odc-check" key={o.value}>
                <input
                  type="checkbox"
                  checked={set.has(o.value)}
                  onChange={() => toggle(o.value)}
                />
                <span className="odc-check-box" aria-hidden="true">
                  <span className="material-icons">check</span>
                </span>
                {o.icon ? (
                  <span className="material-icons odc-opt-icon" style={o.iconColor ? { color: o.iconColor } : undefined} aria-hidden="true">{o.icon}</span>
                ) : null}
                <span className="odc-check-label">{o.label}</span>
              </label>
            ))}
            <div className="odc-ms-foot">
              <button type="button" className="odc-btn text" onClick={clear} disabled={!value.length}>Clear</button>
              <button type="button" className="odc-btn text" onClick={() => setOpen(false)}>Done</button>
            </div>
          </div>,
          document.body,
        )
        : null}
    </div>
  );
}
