/**
 * Odyssey DS — PageSizeSelect
 * The toolbar MIRROR of the footer Pager's rows-per-page control. Mount it in a
 * list page's search/toolbar region (right, after the filters) and bind it to
 * the SAME `pageSize` state the footer `Pager` reads — the two stay in sync
 * because they read and write one value. It is additive: the footer selector is
 * the canonical, always-present home; this appears only where a search bar
 * exists to host it.
 *
 * Reads "Show 25 ▾" by default (pass `prefix` to change the verb, or "" for a
 * bare value). Presets are 25 · 100 · 1000 · All — "All" fetches every matching
 * row (the client virtualizes them). The menu is fixed-positioned (escapes the
 * toolbar/header overflow) and opens DOWNWARD; the footer copy opens upward.
 * 40px tall to line up with SearchField / MultiSelect on the toolbar row.
 *
 * Controlled: pass `value` (number | 'all') + `onChange(next)`.
 */
export function PageSizeSelect({
  value = 25,
  options = [25, 100, 1000, 'all'],
  onChange,
  prefix = 'Show',
  suffix = '',
  label = 'Rows per page',
  disabled = false,
  className = '',
  id,
}) {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(false);
  const [menuStyle, setMenuStyle] = useState(null);
  const triggerRef = useRef(null);
  const menuRef = useRef(null);
  // Fixed-positioned menu (escapes the toolbar/header overflow); opens DOWNWARD
  // since the toolbar sits at the top of the list.
  const place = () => {
    const t = triggerRef.current;
    if (!t) return;
    const r = t.getBoundingClientRect();
    const w = Math.max(150, Math.round(r.width));
    setMenuStyle({
      position: 'fixed',
      top: `${Math.round(r.bottom + 6)}px`,
      left: `${Math.max(8, Math.round(r.right - w))}px`,
      minWidth: `${w}px`,
    });
  };
  useEffect(() => {
    if (!open) return undefined;
    const onDoc = (e) => {
      if (triggerRef.current && triggerRef.current.contains(e.target)) return;
      if (menuRef.current && menuRef.current.contains(e.target)) return;
      setOpen(false);
    };
    const onKey = (e) => { if (e.key === 'Escape') setOpen(false); };
    const onReflow = () => setOpen(false);
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', onReflow, true);
    window.addEventListener('resize', onReflow);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', onReflow, true);
      window.removeEventListener('resize', onReflow);
    };
  }, [open]);
  const toggle = () => { if (open) setOpen(false); else { place(); setOpen(true); } };
  const fmt = (v) => (v === 'all' ? 'All' : Number(v).toLocaleString());
  return (
    <div className={`odc-rpp mirror${className ? ' ' + className : ''}`} id={id}>
      <button
        ref={triggerRef}
        type="button"
        className="odc-rpp-trigger"
        aria-haspopup="listbox"
        aria-expanded={open ? 'true' : 'false'}
        aria-label={`${label}: ${fmt(value)}`}
        disabled={disabled || undefined}
        onClick={toggle}
      >
        {prefix ? <span className="odc-rpp-prefix">{prefix}</span> : null}
        <b>{fmt(value)}</b>
        {suffix ? <span className="odc-rpp-prefix">{suffix}</span> : null}
        <span className="material-icons odc-rpp-chev" aria-hidden="true">{open ? 'expand_less' : 'expand_more'}</span>
      </button>
      {open ? (
        <ul ref={menuRef} className="odc-rpp-menu" role="listbox" style={menuStyle}>
          {options.map((o) => (
            <li key={String(o)} role="option" aria-selected={o === value ? 'true' : 'false'}>
              <button
                type="button"
                className={`odc-rpp-opt${o === value ? ' sel' : ''}`}
                onClick={() => { if (onChange) onChange(o); setOpen(false); }}
              >
                <span>{fmt(o)}</span>
                {o === value ? <span className="material-icons" aria-hidden="true">check</span> : <span aria-hidden="true" />}
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
