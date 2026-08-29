/**
 * Odyssey DS — Select
 * Single-select with a fully-themed popover menu (not a native <select>), so
 * the option list matches the rest of the app in both modes and escapes any
 * overflow:hidden ancestor (cards, collapsibles, modals) via fixed
 * positioning. Maps to a MudSelect.
 *
 * Pass `options` as [{value, label}] (or plain strings). Controlled: pass
 * `value` + `onChange(value, event)` — the selected value first (consistent
 * with every other Odyssey control), the native event second. Shares the
 * field label + help + error chrome with Field.
 *
 * Keyboard (WAI-ARIA listbox pattern): Enter/Space/↑/↓ on the trigger opens
 * the list and moves focus to the selected (or first) option; ↑/↓ rove
 * through options, Home/End jump to the ends, typing jumps to the next option
 * starting with those letters, Enter/Space picks, Esc or Tab closes. Closing
 * returns focus to the trigger. Esc is stopped from propagating so a Select
 * inside a Modal doesn't close the Modal too.
 */
export function Select({
  label,
  prefix,
  value,
  onChange,
  options = [],
  placeholder = 'Select…',
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

  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const sel = opts.find((o) => o.value === value);

  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const wrapRef = useRef(null);
  const btnRef = useRef(null);
  const listRef = useRef(null);
  const typeahead = useRef({ buf: '', t: 0 });

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
  const close = (restore) => {
    setOpen(false);
    if (restore && btnRef.current) btnRef.current.focus();
  };
  const toggle = () => (open ? setOpen(false) : openMenu());

  const optButtons = () =>
    listRef.current ? Array.from(listRef.current.querySelectorAll('.odc-select-opt:not([disabled])')) : [];

  // On open, move focus to the selected option (or the first).
  useEffect(() => {
    if (!open) return;
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
    // Capture phase + stopPropagation: Esc closes only this popover, never a
    // Modal underneath (its Esc handler listens in the bubble phase).
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

  const pick = (o, e) => { if (onChange) onChange(o.value, e); close(true); };

  const focusAt = (idx) => {
    const btns = optButtons();
    if (!btns.length) return;
    const i = Math.min(Math.max(idx, 0), btns.length - 1);
    btns[i].focus();
  };

  const onTriggerKey = (e) => {
    if (disabled) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) openMenu();
    }
  };

  const runTypeahead = (ch) => {
    const now = Date.now();
    const ta = typeahead.current;
    ta.buf = (now - ta.t < 600 ? ta.buf : '') + ch.toLowerCase();
    ta.t = now;
    const btns = optButtons();
    const cur = btns.indexOf(document.activeElement);
    const labels = opts.map((o) => String(o.label).toLowerCase());
    for (let step = 1; step <= labels.length; step += 1) {
      const i = (cur + step) % labels.length;
      if (labels[i].startsWith(ta.buf)) { focusAt(i); return; }
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

  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {label ? (
        <label className="odc-field-label" htmlFor={fieldId}>
          {label}
          {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
          {optional ? <span className="odc-field-opt">Optional</span> : null}
        </label>
      ) : null}
      <div className="odc-select" ref={wrapRef}>
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
          onClick={toggle}
          onKeyDown={onTriggerKey}
        >
          <span className="odc-select-trigger-main">
            {prefix ? <span className="odc-select-prefix">{prefix}</span> : null}
            {sel && sel.icon ? (
              <span className="material-icons odc-opt-icon" style={sel.iconColor ? { color: sel.iconColor } : undefined} aria-hidden="true">{sel.icon}</span>
            ) : null}
            <span className="odc-select-val">{sel ? sel.label : placeholder}</span>
          </span>
          <span className="material-icons odc-select-chev" aria-hidden="true">expand_more</span>
        </button>
        {open && pos ? (
          <ul
            className="odc-select-pop"
            id={listId}
            ref={listRef}
            role="listbox"
            aria-label={typeof label === 'string' ? label : undefined}
            style={{ top: pos.top, bottom: pos.bottom, left: pos.left, minWidth: pos.width, maxHeight: pos.maxHeight }}
            onKeyDown={onListKey}
          >
            {opts.map((o) => {
              const on = o.value === value;
              return (
                <li key={o.value}>
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
                    {o.icon ? (
                      <span className="material-icons odc-opt-icon" style={o.iconColor ? { color: o.iconColor } : undefined} aria-hidden="true">{o.icon}</span>
                    ) : null}
                    <span className="odc-select-opt-label">{o.label}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        ) : null}
      </div>
      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
