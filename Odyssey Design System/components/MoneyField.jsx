/**
 * Odyssey DS — MoneyField
 * The canonical control for editing a money value: an amount and its ISO
 * currency code as ONE control. The amount is typed on the left, the currency
 * code sits on the right inside the same box — no separate field, no symbol.
 *
 * Why ISO codes, not symbols: Odyssey is multi-currency and several currencies
 * share a glyph ($, kr). The code is unambiguous, and it matches how amounts
 * are rendered read-only across the app ("kr 12 400").
 *
 * Currency editing is optional:
 *   • `currencyOptions` + `onCurrencyChange` → the code becomes a picker
 *     (popover listbox, same chrome as Select).
 *   • `currencyEditable={false}`, or no options / no handler → the code renders
 *     as static text (the account's currency, a locked base currency, …).
 *
 * A leading `sign` (− / +) and a `tone` of "income" / "expense" let a signed
 * amount read as one control, with the direction owned by the form. Pass
 * `direction` + `onDirectionChange` instead and that leading segment becomes a
 * BUTTON that flips expense ↔ income — direction, amount and currency in one
 * control, so a form needs no separate segmented toggle. For a plain signed
 * amount (no income/expense meaning) pass `signEditable` and the segment toggles
 * the value's own minus — picked, never typed.
 *
 * Controlled: `value` is a string so partial entries ("3.", "1,2") survive.
 * Invalid keystrokes are blocked as typed — letters and stray symbols are
 * dropped, and a second decimal separator (or a non-leading minus) is rejected
 * outright, so nothing is silently rewritten later. A leading minus IS accepted
 * (refunds, corrections, negative adjustments); pass `allowNegative={false}` on
 * fields where a negative is meaningless. Parse on submit. Shares the label /
 * help / error chrome with Field via FieldShell.
 */
export function MoneyField({
  label,
  value = '',
  onChange,
  currency,
  onCurrencyChange,
  currencyOptions = [],
  currencyEditable = true,
  currencyPlaceholder = '—',
  currencySearchThreshold = 8,
  placeholder = '0.00',
  size = 'md',
  align = 'left',
  sign,
  tone,
  direction,
  onDirectionChange,
  signEditable = false,
  allowNegative = true,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  currencyDisabled = false,
  autoFocus = false,
  className = '',
  id,
  ...rest
}) {
  const { useState, useRef, useEffect } = React;
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const listId = `${fieldId}-currency-listbox`;
  const msg = error || help;
  const re = allowNegative ? /[^0-9.,\-\s]/g : /[^0-9.,\s]/g;
  // Disallowed characters never make it in: a second decimal separator, or a
  // minus that isn't leading, rejects that keystroke outright (the value stays
  // as it was) rather than being rewritten elsewhere in the string.
  const sanitize = (raw) => {
    const s = raw.replace(re, '');
    if ((s.match(/[.,]/g) || []).length > 1) return null;
    if (allowNegative ? /(?!^)-/.test(s) : /-/.test(s)) return null;
    return s;
  };

  const opts = (currencyOptions || []).map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const editable = currencyEditable && !disabled && !currencyDisabled && opts.length > 0 && !!onCurrencyChange;

  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const [query, setQuery] = useState('');
  const btnRef = useRef(null);
  const listRef = useRef(null);
  const searchRef = useRef(null);
  // A short list is faster to scan than to type into; a long one (the full ISO
  // registry) needs the filter.
  const searchable = opts.length > (currencySearchThreshold || 0);
  const q = query.trim().toLowerCase();
  const shown = q
    ? opts.filter((o) => o.value.toLowerCase().startsWith(q)
      || o.value.toLowerCase().includes(q)
      || String(o.label || '').toLowerCase().includes(q))
    : opts;

  const openMenu = () => {
    setQuery('');
    const el = btnRef.current;
    if (el) {
      const r = el.getBoundingClientRect();
      const gap = 6;
      const spaceBelow = window.innerHeight - r.bottom - gap - 12;
      const spaceAbove = r.top - gap - 12;
      const flipUp = spaceBelow < 200 && spaceAbove > spaceBelow;
      const maxHeight = Math.max(160, Math.min(360, flipUp ? spaceAbove : spaceBelow));
      // Anchor against clientWidth, not innerWidth — a fixed-position popover is
      // laid out inside the viewport minus any vertical scrollbar.
      const right = document.documentElement.clientWidth - r.right;
      setPos(flipUp
        ? { bottom: window.innerHeight - r.top + gap, right, maxHeight }
        : { top: r.bottom + gap, right, maxHeight });
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
    const idx = opts.findIndex((o) => o.value === currency);
    (btns[idx >= 0 ? idx : 0] || btns[0]).focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => {
    if (!open) return undefined;
    const onDoc = (e) => {
      if (btnRef.current && btnRef.current.contains(e.target)) return;
      if (listRef.current && listRef.current.contains(e.target)) return;
      setOpen(false);
    };
    const onScroll = () => setOpen(false);
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
      case 'Enter':
        e.preventDefault();
        if (shown.length) { onCurrencyChange(shown[0].value, e); close(true); }
        break;
      case 'Escape': e.preventDefault(); e.stopPropagation(); close(true); break;
      case 'Tab': close(false); break;
      default: break;
    }
  };
  const onTriggerKey = (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); if (!open) openMenu(); }
  };

  const handle = (e) => {
    if (!onChange) return;
    const next = sanitize(e.target.value);
    // React won't re-render on a rejected keystroke (the value prop is
    // unchanged), so the character would linger in the DOM — put it back.
    if (next === null) { e.target.value = signMode ? magnitude : value; return; }
    if (next !== e.target.value) e.target.value = next;
    if (signMode) { setSigned(negative, next.replace(/^\s*-/, ''), e); return; }
    onChange(next, e);
  };

  const code = currency || currencyPlaceholder;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;

  const dirMode = !!(direction && onDirectionChange);
  // Generic signed amount: the leading segment toggles the value's own sign, so
  // the minus is picked, never typed. The input shows the magnitude; `value`
  // stays signed for the form.
  const signMode = !dirMode && signEditable && !!onChange;
  const negative = /^\s*-/.test(String(value || ''));
  const magnitude = signMode ? String(value || '').replace(/^\s*-/, '') : value;
  const setSigned = (neg, mag, e) => onChange((neg ? '-' : '') + mag, e);
  const flipSign = (e) => setSigned(!negative, String(value || '').replace(/^\s*-/, ''), e);
  const dirTone = tone || (dirMode ? direction : undefined);
  const dirSign = sign || (direction ? (direction === 'expense' ? '−' : '+') : (signMode ? (negative ? '−' : '+') : undefined));
  const flipDir = (e) => onDirectionChange(direction === 'expense' ? 'income' : 'expense', e);
  // Typing a sign in the amount sets the direction / sign rather than the value.
  const onAmountKey = (e) => {
    if (dirMode) {
      if (e.key === '-' || e.key === '−') { e.preventDefault(); onDirectionChange('expense', e); }
      else if (e.key === '+') { e.preventDefault(); onDirectionChange('income', e); }
      return;
    }
    if (!signMode) return;
    if (e.key === '-' || e.key === '−') { e.preventDefault(); setSigned(true, magnitude, e); }
    else if (e.key === '+') { e.preventDefault(); setSigned(false, magnitude, e); }
  };

  const control = (
    <div className={`odc-money${size === 'lg' ? ' lg' : ''}${dirTone ? ` tone-${dirTone}` : ''}${error ? ' error' : ''}${disabled ? ' disabled' : ''}`}>
      {dirMode || signMode ? (
        <button
          type="button"
          className="odc-money-sign btn"
          disabled={disabled}
          aria-label={dirMode
            ? `${direction === 'expense' ? 'Expense' : 'Income'} — switch to ${direction === 'expense' ? 'income' : 'expense'}`
            : `${negative ? 'Negative' : 'Positive'} — switch to ${negative ? 'positive' : 'negative'}`}
          title={dirMode
            ? `${direction === 'expense' ? 'Expense' : 'Income'} — click to switch`
            : `${negative ? 'Negative' : 'Positive'} — click to switch`}
          onClick={dirMode ? flipDir : flipSign}
        >
          <span className="odc-money-dir-sign" aria-hidden="true">{dirSign}</span>
        </button>
      ) : dirSign ? (
        <span className="odc-money-sign" aria-hidden="true">{dirSign}</span>
      ) : null}
      <input
        id={fieldId}
        className="odc-money-input"
        inputMode="decimal"
        type="text"
        value={signMode ? magnitude : value}
        placeholder={placeholder}
        disabled={disabled}
        autoFocus={autoFocus}
        style={align === 'right' ? { textAlign: 'right' } : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={msg ? helpId : undefined}
        onChange={handle}
        onKeyDown={onAmountKey}
        {...rest}
      />
      {editable ? (
        <button
          type="button"
          ref={btnRef}
          className={`odc-money-cur btn${open ? ' open' : ''}`}
          aria-haspopup="listbox"
          aria-expanded={open}
          aria-controls={open ? listId : undefined}
          aria-label={`Currency${currency ? `: ${currency}` : ''}`}
          onClick={() => (open ? setOpen(false) : openMenu())}
          onKeyDown={onTriggerKey}
        >
          <span className="odc-money-code">{code}</span>
          <span className="material-icons odc-money-chev" aria-hidden="true">expand_more</span>
        </button>
      ) : (
        <span className="odc-money-cur">
          <span className="odc-money-code">{code}</span>
        </span>
      )}
      {open && pos ? (
        <div
          className="odc-select-pop odc-money-pop"
          ref={listRef}
          style={{
            top: pos.top, bottom: pos.bottom, right: pos.right, maxHeight: pos.maxHeight,
            minWidth: 240, display: 'flex', flexDirection: 'column', padding: 0, overflow: 'hidden',
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
          <ul id={listId} role="listbox" aria-label="Currency" className="odc-money-list"
            style={{ flex: 1, minHeight: 0, overflowY: 'auto', margin: 0, padding: 4, listStyle: 'none' }}>
            {shown.map((o) => {
              const on = o.value === currency;
              return (
                <li key={o.value} style={{ listStyle: 'none', margin: 0, padding: 0 }}>
                  <button
                    type="button"
                    role="option"
                    aria-selected={on}
                    tabIndex={-1}
                    className={`odc-select-opt${on ? ' selected' : ''}`}
                    onClick={(e) => { onCurrencyChange(o.value, e); close(true); }}
                  >
                    <span className="odc-select-tick">
                      {on ? <span className="material-icons" aria-hidden="true">check</span> : null}
                    </span>
                    <span className="odc-money-opt-code">{o.value}</span>
                    {o.label && o.label !== o.value
                      ? <span className="odc-select-opt-label">{o.label}</span> : null}
                  </button>
                </li>
              );
            })}
            {!shown.length ? (
              <li className="odc-money-empty" style={{
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
