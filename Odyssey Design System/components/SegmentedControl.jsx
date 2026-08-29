/**
 * Odyssey DS — SegmentedControl
 * A compact single-select toggle — the transaction direction switch (Money in
 * / Money out), dense view switches. A visual sibling of RadioGroup; use this
 * when there are 2–3 short options that benefit from a button-bar form. Maps to
 * a MudToggleGroup.
 *
 * Controlled: pass `value` + `onChange(value)`. Proper radiogroup semantics —
 * roving tabindex, ←/→ (and ↑/↓) move and select, Home/End jump. An option may
 * carry an `icon` and a `tone` (income / expense) that tints it when selected.
 */
export function SegmentedControl({ options = [], value, onChange, full = false, ariaLabel }) {
  const items = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const ref = React.useRef(null);

  const select = (v) => {
    if (onChange) onChange(v);
    requestAnimationFrame(() => {
      const el = ref.current && ref.current.querySelector(`[data-val="${(window.CSS && CSS.escape ? CSS.escape(v) : v)}"]`);
      if (el) el.focus();
    });
  };

  const onKey = (e) => {
    const idx = items.findIndex((o) => o.value === value);
    if (idx < 0 || !items.length) return;
    let next = null;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = items[(idx + 1) % items.length];
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = items[(idx - 1 + items.length) % items.length];
    else if (e.key === 'Home') next = items[0];
    else if (e.key === 'End') next = items[items.length - 1];
    if (next) { e.preventDefault(); select(next.value); }
  };

  return (
    <div
      className={`odc-seg${full ? ' full' : ''}`}
      role="radiogroup"
      aria-label={ariaLabel}
      ref={ref}
      onKeyDown={onKey}
    >
      {items.map((o) => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            data-val={o.value}
            tabIndex={active ? 0 : -1}
            disabled={o.disabled}
            className={`odc-seg-btn${o.tone ? ' ' + o.tone : ''}`}
            onClick={() => onChange && onChange(o.value)}
          >
            {o.icon ? <span className="material-icons" aria-hidden="true">{o.icon}</span> : null}
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
