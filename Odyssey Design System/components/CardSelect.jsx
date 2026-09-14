/**
 * Odyssey DS — CardSelect
 * Single-select picker drawn as a row of tappable cards: icon over label,
 * centred, tinted in an accent when selected. The shape used for the "what
 * kind of thing is this" question at the top of a create dialog — contract
 * party kind, insurance policy role, account term kind — where the options
 * are few, equally weighted and want an icon to be told apart.
 *
 * Use SegmentedControl for dense 2–3 option switches, RadioGroup when each
 * option needs a sentence of explanation. Maps to a MudToggleGroup of cards.
 *
 * Controlled: pass `value` + `onChange(value)`. Proper radiogroup semantics —
 * roving tabindex, ←/→ (and ↑/↓) move and select, Home/End jump.
 *
 * Accent: `accent` (icon), `accentLine` (border, defaults to accent) and
 * `accentSoft` (selected background) tint the whole group (pass a module accent,
 * e.g. var(--ins-accent)); an option may carry its own `color` / `soft` to
 * override it — the account-term kinds each have their own colour.
 */
export function CardSelect({
  options = [], value, onChange, ariaLabel,
  accent, accentLine, accentSoft, columns = 'fit', maxItemWidth, center = false,
}) {
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
    if (next && !next.disabled) { e.preventDefault(); select(next.value); }
  };

  const track = typeof columns === 'number'
    ? `repeat(${columns}, minmax(0, ${maxItemWidth ? maxItemWidth + 'px' : '1fr'}))`
    : `repeat(auto-fit, minmax(0, ${maxItemWidth ? maxItemWidth + 'px' : '1fr'}))`;

  const groupStyle = { gridTemplateColumns: track };
  if (accent) groupStyle['--odc-cardsel-accent'] = accent;
  if (accentLine || accent) groupStyle['--odc-cardsel-line'] = accentLine || accent;
  if (accentSoft) groupStyle['--odc-cardsel-soft'] = accentSoft;

  return (
    <div
      className={`odc-cardsel${center ? ' center' : ''}`}
      role="radiogroup"
      aria-label={ariaLabel}
      style={groupStyle}
      ref={ref}
      onKeyDown={onKey}
    >
      {items.map((o) => {
        const active = o.value === value;
        const style = {};
        if (o.color) { style['--odc-cardsel-accent'] = o.color; if (!o.line) style['--odc-cardsel-line'] = o.color; }
        if (o.line) style['--odc-cardsel-line'] = o.line;
        if (o.soft) style['--odc-cardsel-soft'] = o.soft;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            data-val={o.value}
            tabIndex={active ? 0 : -1}
            disabled={o.disabled}
            style={style}
            className="odc-cardsel-opt"
            onClick={() => onChange && onChange(o.value)}
          >
            {o.icon ? <span className="material-icons" aria-hidden="true">{o.icon}</span> : null}
            <span className="odc-cardsel-lab">{o.label}</span>
            {o.sub ? <span className="odc-cardsel-sub">{o.sub}</span> : null}
          </button>
        );
      })}
    </div>
  );
}
