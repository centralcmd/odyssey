/**
 * Odyssey DS — CurrencySelect
 * The standalone counterpart to MoneyField's currency segment: pick an ISO 4217
 * currency with no amount attached (an account's currency, a budget's or tax
 * statement's base currency, a report's reporting currency).
 *
 * Same behaviour as the picker inside MoneyField — ISO code first in mono, the
 * currency name beside it, a search box once the list passes
 * `searchThreshold` (matching code or name), and the listbox keyboard pattern.
 * Chrome is the standard `.odc-select-trigger`, so it lines up with every other
 * Select in a form row.
 */
export function CurrencySelect({
  label = 'Currency',
  value,
  onChange,
  options = [],
  placeholder = 'Select a currency…',
  searchThreshold = 8,
  showName = true,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  className = '',
  id,
}) {
  const { useState, useRef, useEffect } = React;
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const listId = `${fieldId}-listbox`;
  const msg = error || help;

  const opts = (options || []).map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const sel = opts.find((o) => o.value === value);
  const searchable = opts.length > (searchThreshold || 0);

  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const [query, setQuery] = useState('');
  const wrapRef = useRef(null);
  const btnRef = useRef(null);
  const listRef = useRef(null);
  const searchRef = useRef(null);

  const q = query.trim().toLowerCase();
  const shown = q
    ? opts.filter((o) => o.value.toLowerCase().includes(q) || String(o.label || '').toLowerCase().includes(q))
    : opts;

  const openMenu = () => {
    setQuery('');
    const el = btnRef.current;
    if (el) {
      const r = el.getBoundingClientRect();
      const gap = 6;
      const spaceBelow = window.innerHeight - r.bottom - gap - 12;
      const spaceAbove = r.top - gap - 12;
      const flipUp = spaceBelow < 220 && spaceAbove > spaceBelow;
      const maxHeight = Math.max(180, Math.min(360, flipUp ? spaceAbove : spaceBelow));
      setPos(flipUp
        ? { bottom: window.innerHeight - r.top + gap, left: r.left, width: r.width, maxHeight }
        : { top: r.bottom + gap, left: r.left, width: r.width, maxHeight });
    }
    setOpen(true);
  };
  const close = (restore) => {
    setOpen(false);
    if (restore && btnRef.current) btnRef.current.focus();
  };

  const optButtons = () => (listRef.current
    ? Array.from(listRef.current.querySelectorAll('.odc-select-opt:not([disabled])'))
    : []);

  useEffect(() => {
    if (!open) return;
    if (searchable && searchRef.current) { searchRef.current.focus(); return; }
    const btns = optButtons();
    if (!btns.length) return;
    const idx = opts.findIndex((o) => o.value === value);
    (btns[idx >= 0 ? idx : 0] || btns[0]).focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => {
    if (!open) return undefined;
    const onDoc = (e) => {
      if (wrapRef.current && wrapRef.current.contains(e.target)) return;
      if (listRef.current && listRef.current.contains(e.target)) return;
      setOpen(false);
    };
    const onScroll = (e) => { if (wrapRef.current && wrapRef.current.contains(e.target)) return; setOpen(false); };
    const onResize = () => setOpen(false);
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(true); } };
    document.addEventListener('mousedown', onDoc);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('keydown', onKey, true);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const focusAt = (i) => {
    const btns = optButtons();
    if (!btns.length) return;
    btns[Math.min(Math.max(i, 0), btns.length - 1)].focus();
  };
  const pick = (o, e) => { if (onChange) onChange(o.value, e); close(true); };
  const onListKey = (e) => {
    const btns = optButtons();
    const idx = btns.indexOf(document.activeElement);
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(idx + 1); break;
      case 'ArrowUp':
        e.preventDefault();
        if (idx === 0 && searchable && searchRef.current) searchRef.current.focus();
        else focusAt(idx - 1);
        break;
      case 'Home': e.preventDefault(); focusAt(0); break;
      case 'End': e.preventDefault(); focusAt(btns.length - 1); break;
      case 'Escape': e.preventDefault(); e.stopPropagation(); close(true); break;
      case 'Tab': close(false); break;
      default: break;
    }
  };
  const onSearchKey = (e) => {
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(0); break;
      case 'Enter': e.preventDefault(); if (shown.length) pick(shown[0], e); break;
      case 'Escape': e.preventDefault(); e.stopPropagation(); close(true); break;
      case 'Tab': close(false); break;
      default: break;
    }
  };
  const onTriggerKey = (e) => {
    if (disabled) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); if (!open) openMenu(); }
  };

  const codeStyle = {
    font: 'var(--fw-medium) var(--fs-body2)/1 var(--font-mono)',
    letterSpacing: '0.04em', textTransform: 'uppercase', flex: 'none',
  };

  const control = (
    <div className="odc-select odc-cursel" ref={wrapRef}>
      <button
        type="button"
        id={fieldId}
        ref={btnRef}
        className={`odc-select-trigger${open ? ' open' : ''}${sel ? '' : ' placeholder'}`}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={msg ? helpId : undefined}
        onClick={() => (open ? setOpen(false) : openMenu())}
        onKeyDown={onTriggerKey}
      >
        <span className="odc-select-trigger-main" style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
          {sel ? (
            <React.Fragment>
              <span style={codeStyle}>{sel.value}</span>
              {showName && sel.label && sel.label !== sel.value ? (
                <span className="odc-select-val" style={{ color: 'var(--mud-palette-text-secondary)' }}>{sel.label}</span>
              ) : null}
            </React.Fragment>
          ) : (
            <span className="odc-select-val">{placeholder}</span>
          )}
        </span>
        <span className="material-icons odc-select-chev" aria-hidden="true">expand_more</span>
      </button>
      {open && pos ? (
        <div
          className="odc-select-pop odc-cursel-pop"
          ref={listRef}
          style={{
            top: pos.top, bottom: pos.bottom, left: pos.left,
            minWidth: Math.max(pos.width || 0, 240), maxHeight: pos.maxHeight,
            display: 'flex', flexDirection: 'column', padding: 0, overflow: 'hidden',
          }}
          onKeyDown={onListKey}
        >
          {searchable ? (
            <div className="odc-money-search" style={{
              flex: 'none', display: 'flex', alignItems: 'center', gap: 10,
              height: 44, padding: '0 12px', boxSizing: 'border-box',
              borderBottom: '1px solid var(--mud-palette-divider)',
            }}>
              <span className="material-icons" aria-hidden="true"
                style={{ fontSize: 20, lineHeight: 1, flex: 'none', color: 'var(--mud-palette-text-secondary)' }}>search</span>
              <input
                ref={searchRef}
                type="text"
                value={query}
                placeholder="Search code or name"
                aria-label="Search currencies"
                aria-controls={listId}
                autoComplete="off"
                style={{
                  flex: 1, minWidth: 0, width: '100%', height: '100%', boxSizing: 'border-box',
                  appearance: 'none', background: 'none', border: 0, outline: 'none', boxShadow: 'none',
                  margin: 0, padding: 0, borderRadius: 0,
                  font: 'var(--fw-regular) var(--fs-body2)/1 var(--font-sans)',
                  color: 'var(--mud-palette-text-primary)',
                }}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={onSearchKey}
              />
            </div>
          ) : null}
          <ul id={listId} role="listbox" aria-label={typeof label === 'string' ? label : 'Currency'}
            style={{ flex: 1, minHeight: 0, overflowY: 'auto', margin: 0, padding: 4, listStyle: 'none' }}>
            {shown.map((o) => {
              const on = o.value === value;
              return (
                <li key={o.value} style={{ listStyle: 'none', margin: 0, padding: 0 }}>
                  <button
                    type="button"
                    role="option"
                    aria-selected={on}
                    tabIndex={-1}
                    className={`odc-select-opt${on ? ' selected' : ''}`}
                    onClick={(e) => pick(o, e)}
                  >
                    <span className="odc-select-tick">
                      {on ? <span className="material-icons" aria-hidden="true">check</span> : null}
                    </span>
                    <span className="odc-money-opt-code" style={{ ...codeStyle, minWidth: 42 }}>{o.value}</span>
                    {o.label && o.label !== o.value
                      ? <span className="odc-select-opt-label" style={{ color: 'var(--mud-palette-text-secondary)' }}>{o.label}</span> : null}
                  </button>
                </li>
              );
            })}
            {!shown.length ? (
              <li style={{
                listStyle: 'none', padding: 12, textAlign: 'center',
                font: 'var(--fw-regular) var(--fs-caption)/1.4 var(--font-sans)',
                color: 'var(--mud-palette-text-secondary)',
              }}>No currency matches “{query.trim()}”</li>
            ) : null}
          </ul>
        </div>
      ) : null}
    </div>
  );

  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  if (FieldShell) {
    return (
      <FieldShell label={label} htmlFor={fieldId} required={required} optional={optional}
        help={help} error={error} className={className}>
        {control}
      </FieldShell>
    );
  }
  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {label ? (
        <label className="odc-field-label" htmlFor={fieldId}>
          {label}
          {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
          {optional ? <span className="odc-field-opt">Optional</span> : null}
        </label>
      ) : null}
      {control}
      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
