/**
 * Odyssey DS — Combobox
 * Searchable single-select with optional inline create — the contact
 * picker ("search an existing contact or create one") and tag picker.
 * Maps to a MudAutocomplete. Type to filter; ↑/↓ to move, Enter to pick,
 * Esc to close; outside-click closes.
 *
 * Controlled: pass `value` + `onChange(value, option)`. Provide `onCreate`
 * to offer a "Create …" row for a query that matches nothing; it receives
 * the typed text and returns the new value (or an {value,label} option).
 *
 * Options may carry an `icon` (+ `iconColor`) — a leading Material glyph shown
 * in each row and beside the selected value. `clearable` adds a keyboard-
 * operable clear (×) button once a value is chosen, clearing to '' via
 * onChange. `loading` shows an announced loading row in place of results.
 *
 * `freeText` turns it into a SUGGEST-don't-constrain field: the value is a
 * string the user may type freely, and the options are recognised names rather
 * than the only legal answers. The typed query commits on blur and on Enter
 * when no row is highlighted, so a name that is never explicitly picked is not
 * silently dropped — the one behaviour a constrained select must NOT have.
 *
 * The option list is portaled to <body> and positioned against the input, so
 * it escapes any overflow:hidden/auto ancestor (a modal body, card, or
 * scrollable form) instead of being clipped — and flips above the field when
 * there isn't room below. The active option is kept scrolled into view as you
 * arrow through a long list.
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
    // For a width-matched popover use the trigger width, not the measured pop
    // width: before the matched width applies, CSS (left:0;right:0) lets the
    // panel stretch to the viewport, which would clamp `left` to the gutter.
    const pw = matchWidth ? r.width : (pop ? pop.offsetWidth : r.width);
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
    : { position: 'fixed', top: 0, left: 0, right: 'auto', bottom: 'auto', visibility: 'hidden' };
  const toggle = useCallback(() => setOpen((o) => !o), []);
  return { open, setOpen, toggle, anchorRef, popRef, floatStyle, placement: box ? box.placement : 'down' };
}

export function Combobox({
  value,
  onChange,
  options = [],
  placeholder = 'Search…',
  onCreate,
  createLabel = 'Create',
  createKinds,
  disabled = false,
  id,
  emptyText = 'No matches',
  clearable = false,
  loading = false,
  ariaLabel,
  ariaDescribedBy,
  invalid = false,
  required = false,
  freeText = false,
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  // In freeText mode a value that matches no option is still the value — it is
  // a name the user typed, not an invalid selection.
  const selected = opts.find((o) => o.value === value)
    || (freeText && value ? { value, label: value } : undefined);
  const showClear = clearable && !!selected && !disabled;

  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ matchWidth: true });
  const [query, setQuery] = React.useState('');
  const [active, setActive] = React.useState(0);

  const q = query.trim().toLowerCase();
  const filtered = q ? opts.filter((o) => o.label.toLowerCase().includes(q)) : opts;
  const showCreate = !!onCreate && !!q && !opts.some((o) => o.label.toLowerCase() === q);
  // One create row per kind when the created record has a type the user must
  // choose up front (a contact is a person OR a company); a single unqualified
  // row otherwise.
  const kinds = (createKinds && createKinds.length ? createKinds : [null]);
  const createRows = showCreate ? kinds : [];
  const rowCount = filtered.length + createRows.length;

  // Start the highlight on the first SELECTABLE row: a disabled row (a tag
  // already planned for) at index 0 would otherwise swallow the first Enter
  // with no feedback. Same rule when arrowing — disabled rows are skipped.
  const nextSelectable = (from, step) => {
    for (let i = from; i >= 0 && i < rowCount; i += step) {
      if (i >= filtered.length || !filtered[i].disabled) return i;
    }
    return -1;
  };
  React.useEffect(() => {
    if (!open) return;
    const first = nextSelectable(0, 1);
    setActive(first === -1 ? 0 : first);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, query]);

  // Keep the active option scrolled into view as the highlight moves.
  React.useEffect(() => {
    if (!open || !popRef.current) return;
    const rows = popRef.current.querySelectorAll('.odc-combo-opt');
    const el = rows[active];
    if (!el) return;
    const pop = popRef.current;
    const top = el.offsetTop;
    const bottom = top + el.offsetHeight;
    if (top < pop.scrollTop) pop.scrollTop = top;
    else if (bottom > pop.scrollTop + pop.clientHeight) pop.scrollTop = bottom - pop.clientHeight;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, open, rowCount]);

  const closeAndClear = () => {
    setOpen(false);
    setQuery('');
  };

  // Closing by any path (outside click, doc-level Esc) also resets the filter.
  React.useEffect(() => { if (!open) setQuery(''); }, [open]);

  const pick = (o) => {
    if (!o || o.disabled) return;
    if (onChange) onChange(o.value, o);
    closeAndClear();
  };
  const create = (kind) => {
    const made = onCreate(query.trim(), kind ? kind.key : undefined);
    if (made != null && onChange) {
      const opt = typeof made === 'string' ? { value: made, label: made } : made;
      onChange(opt.value, opt);
    }
    closeAndClear();
  };

  // freeText: the query IS an answer. Committing it on blur is what separates
  // "suggests" from "constrains".
  const commitQuery = () => {
    if (!freeText) return false;
    const t = query.trim();
    if (!t) return false;
    const hit = opts.find((o) => o.label.toLowerCase() === t.toLowerCase());
    const opt = hit || { value: t, label: t };
    if (onChange && opt.value !== value) onChange(opt.value, opt);
    return true;
  };

  const onKey = (e) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setOpen(true);
      setActive((a) => { const n = nextSelectable(a + 1, 1); return n === -1 ? a : n; });
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActive((a) => { const n = nextSelectable(a - 1, -1); return n === -1 ? a : n; });
    } else if (e.key === 'Enter') {
      if (!open) return;
      e.preventDefault();
      if (showCreate && active >= filtered.length) create(createRows[active - filtered.length]);
      else if (filtered[active]) pick(filtered[active]);
      else if (commitQuery()) closeAndClear();
    } else if (e.key === 'Escape') {
      // Stop propagation so Esc dismisses only the popover — an enclosing
      // Modal keeps its own Esc for the next press.
      e.stopPropagation();
      closeAndClear();
    }
  };

  const clear = () => {
    if (onChange) onChange('', null);
    setQuery('');
    setActive(0);
  };

  const leadingIcon = !open && selected && selected.icon ? selected.icon : null;

  return (
    <div className="odc-combo" ref={anchorRef}>
      <div className={`odc-input-wrap${showClear ? ' has-clear' : ''}`}>
        {leadingIcon ? (
          <span className="material-icons odc-input-icon" style={selected.iconColor ? { color: selected.iconColor } : undefined} aria-hidden="true">{leadingIcon}</span>
        ) : null}
        <input
          id={fieldId}
          className={`odc-input${leadingIcon ? ' has-icon' : ''}${showClear ? ' has-clear' : ''}`}
          role="combobox"
          aria-label={ariaLabel}
          aria-describedby={ariaDescribedBy}
          aria-invalid={invalid ? true : undefined}
          aria-required={required ? true : undefined}
          aria-expanded={open}
          aria-controls={`${fieldId}-list`}
          aria-activedescendant={open && rowCount > 0 && active < rowCount ? `${fieldId}-opt-${active}` : undefined}
          aria-autocomplete="list"
          autoComplete="off"
          disabled={disabled}
          placeholder={selected ? selected.label : placeholder}
          value={open ? query : (selected ? selected.label : '')}
          onFocus={() => setOpen(true)}
          onClick={() => setOpen(true)}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
            setActive(0);
          }}
          onKeyDown={onKey}
          onBlur={() => { if (commitQuery()) closeAndClear(); else if (freeText) setOpen(false); }}
        />
        {showClear ? (
          <button
            type="button"
            className="odc-input-clear"
            aria-label="Clear selection"
            onMouseDown={(e) => { e.preventDefault(); clear(); }}
            onClick={(e) => { e.preventDefault(); clear(); }}
          >
            <span className="material-icons" aria-hidden="true">close</span>
          </button>
        ) : null}
        <span className="material-icons odc-select-chev" aria-hidden="true">expand_more</span>
      </div>
      {open
        ? ReactDOM.createPortal(
          <ul className="odc-combo-pop" id={`${fieldId}-list`} role="listbox" ref={popRef} style={floatStyle} aria-busy={loading || undefined}>
            {loading ? <li className="odc-combo-empty" role="status" aria-live="polite">Loading…</li> : null}
            {!loading && filtered.map((o, i) => (
              <li
                key={o.value}
                id={`${fieldId}-opt-${i}`}
                role="option"
                aria-selected={o.value === value}
                className={`odc-combo-opt${i === active ? ' active' : ''}${o.value === value ? ' selected' : ''}${o.disabled ? ' disabled' : ''}`}
                aria-disabled={o.disabled ? true : undefined}
                onMouseEnter={() => { if (!o.disabled) setActive(i); }}
                onMouseDown={(e) => {
                  e.preventDefault();
                  pick(o);
                }}
              >
                {o.icon ? (
                  <span className="material-icons odc-opt-icon" style={o.iconColor ? { color: o.iconColor } : undefined} aria-hidden="true">{o.icon}</span>
                ) : null}
                <span className="odc-combo-opt-label">{o.label}</span>
                {o.note ? <span className="odc-combo-opt-note">{o.note}</span> : null}
                {o.value === value ? (
                  <span className="material-icons odc-combo-opt-check" aria-hidden="true">check</span>
                ) : null}
              </li>
            ))}
            {!loading && createRows.map((kind, k) => {
              const i = filtered.length + k;
              return (
                <li
                  key={kind ? kind.key : 'create'}
                  id={`${fieldId}-opt-${i}`}
                  role="option"
                  aria-selected={false}
                  className={`odc-combo-opt odc-combo-create${k === 0 ? ' first' : ''}${active === i ? ' active' : ''}`}
                  onMouseEnter={() => setActive(i)}
                  onMouseDown={(e) => {
                    e.preventDefault();
                    create(kind);
                  }}
                >
                  <span className="material-icons" aria-hidden="true">add</span>
                  <span>{`${createLabel} "${query.trim()}"`}</span>
                  {kind ? (
                    <span className="odc-combo-create-kind">
                      <span className="material-icons" aria-hidden="true">{kind.icon}</span>
                      <span>{kind.label}</span>
                    </span>
                  ) : null}
                </li>
              );
            })}
            {!loading && rowCount === 0 ? <li className="odc-combo-empty" role="status" aria-live="polite">{emptyText}</li> : null}
          </ul>,
          document.body,
        )
        : null}
    </div>
  );
}
