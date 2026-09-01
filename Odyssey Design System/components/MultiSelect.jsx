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
  /* Search follows the same contract as TagMultiSelect's: the same box, the same
     row list beneath it. On by default once the list is long enough to be worth
     filtering, so a filter over a whole contact book is usable. */
  searchable,
  searchLabel,
  searchPlaceholder = 'Search…',
  loading = false,
  loadingText = 'Loading…',
  emptyText = 'No matches',
}) {
  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ align });
  const [query, setQuery] = React.useState('');
  const searchOn = searchable != null ? searchable : opts.length > 8;
  const q = query.trim().toLowerCase();
  const filtered = q ? opts.filter((o) => o.label.toLowerCase().includes(q)) : opts;

  React.useEffect(() => { if (!open) setQuery(''); }, [open]);

  const set = new Set(value);
  const toggle = (v) => {
    const next = new Set(set);
    if (next.has(v)) next.delete(v);
    else next.add(v);
    if (onChange) onChange([...next]);
  };
  const clear = () => onChange && onChange([]);

  // Move focus into the popover on open — the search field when there is one,
  // else the checkbox list (native: Tab/Space work for free); ↑/↓ also rove.
  React.useEffect(() => {
    if (!open) return;
    const pop = popRef.current;
    if (!pop) return;
    const first = pop.querySelector('.odc-ms-search input') || pop.querySelector('input[type="checkbox"]');
    if (first) first.focus();
  }, [open, popRef]);

  /* Keyboard inside the popover. It is portaled to <body>, so the browser's own
     Tab order would walk straight out of it — Tab and Shift+Tab therefore CYCLE
     within the popover, ↑/↓ rove the option rows (from the search field, ↓
     enters the list), and Enter toggles the focused row. */
  /* Keep the roved row visible — the checkbox is visually replaced, so the
     browser's scroll-on-focus has nothing to scroll to. */
  const revealRow = (el) => {
    const row = el && el.closest('.odc-ms-opt');
    const list = popRef.current && popRef.current.querySelector('.odc-ms-list');
    if (!row || !list) return;
    const r = row.getBoundingClientRect();
    const l = list.getBoundingClientRect();
    if (r.top < l.top) list.scrollTop -= (l.top - r.top) + 4;
    else if (r.bottom > l.bottom) list.scrollTop += (r.bottom - l.bottom) + 4;
  };

  /* Native, window-capture: the popover is portaled to <body> (outside the React
     root container, so a React onKeyDown there never fires) and any Modal above
     traps Tab on document — capturing on window lets the popover own its keys.
     Tab/Shift+Tab cycle inside it, ↑/↓ rove the rows, Enter toggles. */
  React.useEffect(() => {
    if (!open) return undefined;
    const onKey = (ev) => {
      const pop = popRef.current;
      if (!pop || !pop.contains(ev.target)) return;
      const els = Array.from(pop.querySelectorAll('.odc-ms-search input, .odc-ms-opt input[type="checkbox"], .odc-ms-foot .odc-btn:not([disabled])'));
      if (!els.length) return;
      const i = els.indexOf(document.activeElement);
      if (ev.key === 'Tab') {
        ev.preventDefault();
        ev.stopPropagation();
        const n = els.length;
        const next = ev.shiftKey ? (i <= 0 ? n - 1 : i - 1) : (i < 0 || i === n - 1 ? 0 : i + 1);
        els[next].focus();
        revealRow(els[next]);
        return;
      }
      if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
        const boxes = els.filter((x) => x.type === 'checkbox');
        if (!boxes.length) return;
        ev.preventDefault();
        ev.stopPropagation();
        const bi = boxes.indexOf(document.activeElement);
        const target = bi < 0
          ? boxes[ev.key === 'ArrowDown' ? 0 : boxes.length - 1]
          : boxes[Math.min(Math.max(bi + (ev.key === 'ArrowDown' ? 1 : -1), 0), boxes.length - 1)];
        target.focus();
        revealRow(target);
        return;
      }
      if (ev.key === 'Enter') {
        const el = document.activeElement;
        if (el && el.type === 'checkbox') { ev.preventDefault(); ev.stopPropagation(); el.click(); }
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [open]);

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
          <div className={`odc-ms-pop ${align}`} role="group" aria-label={label} ref={popRef} style={floatStyle}>
            {searchOn ? (
              <div className="odc-ms-search">
                <span className="material-icons" aria-hidden="true">search</span>
                <input value={query} aria-label={searchLabel || `Search ${label.toLowerCase()}`}
                  placeholder={searchPlaceholder} onChange={(e) => setQuery(e.target.value)} />
              </div>
            ) : null}
            <div className="odc-ms-list">
            {loading ? (
              <div className="odc-ms-loading" role="status">
                <span className="material-icons" aria-hidden="true">hourglass_top</span>
                <span>{loadingText}</span>
              </div>
            ) : filtered.map((o) => (
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
            {!loading && filtered.length === 0 ? <div className="odc-tagms-empty">{emptyText}</div> : null}
            </div>
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
