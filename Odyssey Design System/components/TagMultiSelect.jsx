/**
 * Odyssey DS — TagMultiSelect
 * The multi-tag picker for the transaction forms (New transaction dialog and
 * the inline edit panel). A transaction now carries a list of TransactionTags,
 * so the single "Tag" Select is replaced by this: a field whose control box
 * shows each selected tag as a removable `.odc-chip.tag`, with an "Add tag"
 * affordance that opens a searchable, checkable list — consistent with the
 * ledger-header MultiSelect filter, but labelled and chip-displaying for data
 * entry. Provide `onCreate` to offer an inline "Create …" row for a name that
 * matches nothing (the same create affordance the single tag Combobox had).
 *
 * Controlled: `value` is an array of tag ids; `onChange(nextIds)` fires the
 * full next set on every add / remove. `options` are {value,label} (or plain
 * strings). The popover is portaled to <body> so it escapes a modal body /
 * card / collapsible overflow, and flips above when there isn't room below.
 * Shares the Field label + help + error chrome. Styled by `.odc-tagms`.
 */

/* ---- odcUsePopover — fixed-position, portaled popover (inlined; bundle
   components are standalone and can't import each other). Measures the trigger,
   renders into <body>, flips up when cramped, closes on outside-click + Esc. */
function odcUsePopover({ align = 'start', gap = 6, matchWidth = true } = {}) {
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
    if (roomBelow >= ph + gap || roomBelow >= roomAbove) top = r.bottom + gap;
    else top = Math.max(gap, r.top - gap - ph);
    let left = align === 'end' ? r.right - pw : r.left;
    left = Math.min(Math.max(gap, left), Math.max(gap, vw - pw - gap));
    setBox({ top, left, width: matchWidth ? r.width : null });
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
  return { open, setOpen, anchorRef, popRef, floatStyle };
}

export function TagMultiSelect({
  label,
  value = [],
  onChange,
  options = [],
  placeholder = 'No tags',
  addLabel = 'Add tag',
  onCreate,
  createLabel = 'Create',
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  emptyText = 'No tags match',
  className = '',
  id,
}) {
  const { useState, useRef, useEffect } = React;
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const msg = error || help;

  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const byVal = Object.fromEntries(opts.map((o) => [o.value, o]));
  const selected = value.map((v) => byVal[v] || { value: v, label: v });

  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ matchWidth: true });
  const [query, setQuery] = useState('');
  const inputRef = useRef(null);

  useEffect(() => {
    if (open) setTimeout(() => inputRef.current && inputRef.current.focus(), 20);
    else setQuery('');
  }, [open]);

  const set = new Set(value);
  const toggle = (v) => {
    const next = new Set(set);
    if (next.has(v)) next.delete(v);
    else next.add(v);
    if (onChange) onChange([...next]);
  };
  const remove = (v) => {
    if (onChange) onChange(value.filter((x) => x !== v));
  };
  const clear = () => onChange && onChange([]);

  const q = query.trim().toLowerCase();
  const filtered = q ? opts.filter((o) => o.label.toLowerCase().includes(q)) : opts;
  const exact = opts.some((o) => o.label.toLowerCase() === q);
  const showCreate = !!onCreate && !!q && !exact;

  const create = () => {
    const made = onCreate(query.trim());
    if (made != null && onChange) {
      const opt = typeof made === 'string' ? { value: made, label: made } : made;
      if (!set.has(opt.value)) onChange([...value, opt.value]);
    }
    setQuery('');
  };

  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {label ? (
        <label className="odc-field-label" htmlFor={fieldId}>
          {label}
          {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
          {optional ? <span className="odc-field-opt">Optional</span> : null}
        </label>
      ) : null}

      <div className="odc-tagms" ref={anchorRef}>
        <button
          type="button"
          id={fieldId}
          className={`odc-tagms-control${open ? ' open' : ''}${selected.length ? '' : ' placeholder'}`}
          disabled={disabled}
          aria-haspopup="listbox"
          aria-expanded={open}
          aria-invalid={error ? true : undefined}
          aria-describedby={msg ? helpId : undefined}
          onClick={() => setOpen((o) => !o)}
        >
          <span className="odc-tagms-chips">
            {selected.length === 0 ? (
              <span className="odc-tagms-ph">{placeholder}</span>
            ) : (
              selected.map((t) => (
                <span className="odc-chip tag odc-tagms-chip" key={t.value}>
                  {t.label}
                  <span
                    className="odc-tagms-x"
                    role="button"
                    aria-label={`Remove ${t.label}`}
                    onClick={(e) => { e.stopPropagation(); remove(t.value); }}
                  >
                    <span className="material-icons" aria-hidden="true">close</span>
                  </span>
                </span>
              ))
            )}
          </span>
          <span className="odc-tagms-add">
            <span className="material-icons" aria-hidden="true">{open ? 'expand_less' : 'add'}</span>
            {selected.length === 0 ? <span>{addLabel}</span> : null}
          </span>
        </button>

        {open
          ? ReactDOM.createPortal(
            <div className="odc-tagms-pop" role="listbox" aria-multiselectable="true" ref={popRef} style={floatStyle}>
              <div className="odc-tagms-search">
                <span className="material-icons" aria-hidden="true">search</span>
                <input
                  ref={inputRef}
                  value={query}
                  placeholder={onCreate ? 'Search or add a tag…' : 'Search tags…'}
                  onChange={(e) => setQuery(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter' && showCreate) { e.preventDefault(); create(); } }}
                />
              </div>
              <div className="odc-tagms-list">
                {filtered.map((o) => (
                  <label className="odc-tagms-opt odc-check" key={o.value}>
                    <input type="checkbox" checked={set.has(o.value)} onChange={() => toggle(o.value)} />
                    <span className="odc-check-box" aria-hidden="true">
                      <span className="material-icons">check</span>
                    </span>
                    <span className="odc-check-label">{o.label}</span>
                  </label>
                ))}
                {showCreate ? (
                  <button type="button" className="odc-tagms-create" onMouseDown={(e) => { e.preventDefault(); create(); }}>
                    <span className="material-icons" aria-hidden="true">add</span>
                    <span>{`${createLabel} "${query.trim()}"`}</span>
                  </button>
                ) : null}
                {filtered.length === 0 && !showCreate ? <div className="odc-tagms-empty">{emptyText}</div> : null}
              </div>
              <div className="odc-tagms-foot">
                <button type="button" className="odc-btn text" onClick={clear} disabled={!value.length}>Clear</button>
                <button type="button" className="odc-btn text" onClick={() => setOpen(false)}>Done</button>
              </div>
            </div>,
            document.body,
          )
          : null}
      </div>

      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
