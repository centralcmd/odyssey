/**
 * Odyssey DS — Tabs
 * A horizontal tab strip with an underline active marker. This is the tablist
 * only — it renders the tab buttons and the ARIA wiring; **you render the
 * panel** and swap its content on `onChange`. Give a tab a `panelId` (→
 * aria-controls) and `tabId` (so your panel can point back with
 * aria-labelledby). Controlled: pass `tabs` as [{value,label}] (or strings),
 * the active `value`, and `onChange`. Styled by .odc-tabs / .odc-tab.
 *
 * Keyboard (WAI-ARIA tabs pattern, automatic activation): only the active tab
 * is in the tab order (roving tabindex); ←/→ (and ↑/↓) move to and select the
 * adjacent tab, Home/End jump to the ends. Give a tab a `panelId` to wire
 * aria-controls to its panel, and a `tabId` so the panel can point back with
 * aria-labelledby.
 */
export function Tabs({ tabs = [], value, onChange }) {
  const items = tabs.map((t) => (typeof t === 'string' ? { value: t, label: t } : t));
  const ref = React.useRef(null);

  const select = (v) => {
    if (onChange) onChange(v);
    // focus the newly-active tab after the roving tabindex updates
    requestAnimationFrame(() => {
      const el = ref.current && ref.current.querySelector(`[data-val="${(window.CSS && CSS.escape ? CSS.escape(v) : v)}"]`);
      if (el) el.focus();
    });
  };

  const onKey = (e) => {
    const idx = items.findIndex((t) => t.value === value);
    if (idx < 0 || !items.length) return;
    let next = null;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = items[(idx + 1) % items.length];
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = items[(idx - 1 + items.length) % items.length];
    else if (e.key === 'Home') next = items[0];
    else if (e.key === 'End') next = items[items.length - 1];
    if (next) {
      e.preventDefault();
      select(next.value);
    }
  };

  return (
    <div className="odc-tabs" role="tablist" ref={ref} onKeyDown={onKey}>
      {items.map((t) => {
        const active = t.value === value;
        return (
          <button
            key={t.value}
            type="button"
            role="tab"
            data-val={t.value}
            id={t.tabId || undefined}
            aria-selected={active}
            aria-controls={t.panelId || undefined}
            tabIndex={active ? 0 : -1}
            className={`odc-tab${active ? ' active' : ''}`}
            onClick={() => onChange && onChange(t.value)}
          >
            {t.label}
          </button>
        );
      })}
    </div>
  );
}
