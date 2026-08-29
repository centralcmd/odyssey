/**
 * Odyssey DS — TypeSelect
 * The one shared "pick a type" control behind every registry-backed single
 * select (account type, policy type, contact type, the file-type pickers).
 * It renders the base Select's themed trigger + popover — so the affordance,
 * positioning and field chrome match the rest of the app — with a registry row
 * layout: a colored category glyph, the label, and the selected check pinned to
 * the far right. Optional `groups` split the list into labelled sections with
 * separators (e.g. Assets / Liabilities).
 *
 * Don't use this directly in product code — reach for the domain-typed wrapper
 * (AccountTypeSelect, InsurancePolicyTypeSelect, …), each of which feeds its
 * canonical registry in. This is the shared engine they delegate to.
 *
 * Each `types` entry: `{ key|value, label, icon, color|iconColor, group? }`.
 * Controlled: pass `value` + `onChange(key, event)`. Composes `FieldShell` for
 * the label / helper / error chrome.
 *
 * Keyboard: same listbox pattern as Select — ↑/↓ on the trigger opens and
 * focuses the selected option; ↑/↓/Home/End rove, typeahead jumps, Esc/Tab
 * close (Esc restores trigger focus and never reaches a Modal underneath).
 */
export function TypeSelect({
  value,
  onChange,
  types = [],
  groups,
  label,
  placeholder = 'Select type…',
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  className = '',
  id,
  ...rest
}) {
  const { useState, useRef, useEffect } = React;
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;

  const norm = (t) => ({ key: t.key != null ? t.key : t.value, label: t.label, icon: t.icon, color: t.color || t.iconColor, group: t.group });
  const opts = types.map(norm);
  const sel = opts.find((o) => o.key === value) || null;

  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const wrapRef = useRef(null);
  const btnRef = useRef(null);
  const listRef = useRef(null);
  const typeahead = useRef({ buf: '', t: 0 });
  const listId = `${fieldId}-listbox`;

  const openMenu = () => {
    if (btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      const gap = 6;
      const margin = 12;
      const spaceBelow = window.innerHeight - r.bottom - gap - margin;
      const spaceAbove = r.top - gap - margin;
      const flipUp = spaceBelow < 220 && spaceAbove > spaceBelow;
      const maxHeight = Math.max(160, Math.min(360, flipUp ? spaceAbove : spaceBelow));
      setPos(
        flipUp
          ? { bottom: window.innerHeight - r.top + gap, left: r.left, width: r.width, maxHeight }
          : { top: r.bottom + gap, left: r.left, width: r.width, maxHeight },
      );
    }
    setOpen(true);
  };
  const toggle = () => (open ? setOpen(false) : openMenu());

  const close = (restore) => {
    setOpen(false);
    if (restore && btnRef.current) btnRef.current.focus();
  };

  const optButtons = () =>
    listRef.current ? Array.from(listRef.current.querySelectorAll('.odc-select-opt:not([disabled])')) : [];

  const focusAt = (idx) => {
    const btns = optButtons();
    if (!btns.length) return;
    btns[Math.min(Math.max(idx, 0), btns.length - 1)].focus();
  };

  // On open, move focus to the selected option (or the first).
  useEffect(() => {
    if (!open) return;
    const btns = optButtons();
    if (!btns.length) return;
    const idx = btns.findIndex((b) => b.classList.contains('selected'));
    (btns[idx >= 0 ? idx : 0]).focus();
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
    // Capture + stopPropagation: Esc closes only this popover, not a Modal below.
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(true); } };
    const onResize = () => setOpen(false);
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

  const pick = (o, e) => { if (onChange) onChange(o.key, e); close(true); };

  const runTypeahead = (ch) => {
    const now = Date.now();
    const ta = typeahead.current;
    ta.buf = (now - ta.t < 600 ? ta.buf : '') + ch.toLowerCase();
    ta.t = now;
    const btns = optButtons();
    const cur = btns.indexOf(document.activeElement);
    for (let step = 1; step <= btns.length; step += 1) {
      const i = (cur + step) % btns.length;
      if (btns[i].textContent.trim().toLowerCase().startsWith(ta.buf)) { focusAt(i); return; }
    }
  };

  const onTriggerKey = (e) => {
    if (disabled) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) openMenu();
    }
  };

  const onListKey = (e) => {
    const btns = optButtons();
    const idx = btns.indexOf(document.activeElement);
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(idx + 1); break;
      case 'ArrowUp': e.preventDefault(); focusAt(idx - 1); break;
      case 'Home': e.preventDefault(); focusAt(0); break;
      case 'End': e.preventDefault(); focusAt(btns.length - 1); break;
      case 'Escape': e.preventDefault(); e.stopPropagation(); close(true); break;
      case 'Tab': close(false); break;
      default:
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && e.key !== ' ') {
          e.preventDefault();
          runTypeahead(e.key);
        }
        break;
    }
  };

  const optionRow = (o) => {
    const on = o.key === value;
    return (
      <li key={o.key}>
        <button type="button" role="option" aria-selected={on} tabIndex={-1}
          className={`odc-select-opt${on ? ' selected' : ''}`}
          onClick={(e) => pick(o, e)}>
          {o.icon ? (
            <span className="material-icons odc-opt-icon" style={o.color ? { color: o.color } : undefined} aria-hidden="true">{o.icon}</span>
          ) : null}
          <span className="odc-select-opt-label">{o.label}</span>
          {on ? (
            <span className="odc-typesel-check" aria-hidden="true"><span className="material-icons">check</span></span>
          ) : null}
        </button>
      </li>
    );
  };

  const renderItems = () => {
    if (!groups || !groups.length) return opts.map(optionRow);
    const out = [];
    groups.forEach((g, gi) => {
      const items = opts.filter((o) => o.group === (g.key != null ? g.key : g));
      if (!items.length) return;
      if (gi > 0 && out.length) out.push(<li key={`sep-${gi}`} className="odc-typesel-sep" role="presentation" />);
      out.push(<li key={`grp-${gi}`} className="odc-typesel-group" role="presentation">{g.label != null ? g.label : g}</li>);
      items.forEach((o) => out.push(optionRow(o)));
    });
    return out;
  };

  const control = (
    <div className="odc-select" ref={wrapRef}>
      <button type="button" id={fieldId} ref={btnRef} disabled={disabled}
        className={`odc-select-trigger${open ? ' open' : ''}${sel ? '' : ' placeholder'}`}
        aria-haspopup="listbox" aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={(error || help) ? helpId : undefined}
        onClick={toggle} onKeyDown={onTriggerKey} {...rest}>
        <span className="odc-select-trigger-main">
          {sel && sel.icon ? (
            <span className="material-icons odc-opt-icon" style={sel.color ? { color: sel.color } : undefined} aria-hidden="true">{sel.icon}</span>
          ) : null}
          <span className="odc-select-val">{sel ? sel.label : placeholder}</span>
        </span>
        <span className="material-icons odc-select-chev" aria-hidden="true">expand_more</span>
      </button>
      {open && pos ? (
        <ul className="odc-select-pop" id={listId} ref={listRef} role="listbox"
          aria-label={typeof label === 'string' ? label : undefined}
          style={{ top: pos.top, bottom: pos.bottom, left: pos.left, minWidth: pos.width, maxHeight: pos.maxHeight }} onKeyDown={onListKey}>
          {renderItems()}
        </ul>
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
      {(error || help) ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{error || help}</div> : null}
    </div>
  );
}
