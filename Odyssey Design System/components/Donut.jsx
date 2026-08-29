/**
 * Odyssey DS — Donut
 * The allocation panel — the canonical data-viz used across the product:
 * Accounts ("Asset allocation" / "Liabilities"), Budgets ("Planned income" /
 * "Planned expenses"), and the standalone "expense by tag" breakdown. One
 * component, one stylesheet (.odc-donut-* in components.css) — the kit pages
 * and the preview card render the same classes.
 *
 * Layout: a ring whose hole holds only a muted **watermark icon** (never a
 * number — large sums would overflow), beside (or above) a legend ("ledger")
 * of slice rows — swatch · name on the left, **percent then amount** in two
 * right-aligned columns — closed by a **total row** where the sum lives.
 *  - layout="row" (default) → ring left, legend right (the single-donut look).
 *  - layout="stack"       → ring above legend (use when two panels sit side by
 *                           side in <Donut.Pair>, where row panels are too wide).
 * Slices draw largest-first by caller order, zero-values dropped, with a small
 * gap so same-family hues read apart. Colors default to the categorical
 * --chart-1…6 palette. Pass `format` to render money (e.g. H.money).
 */
const ODC_DONUT_PALETTE = [
  'var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)',
  'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)',
];

function odcDonutSlices(items, total, colors, size, thickness, gap) {
  const r = (size - thickness) / 2;
  const C = 2 * Math.PI * r;
  const GAP = items.length > 1 ? gap : 0;
  let acc = 0;
  const slices = items.map((it, i) => {
    const frac = total > 0 ? it.value / total : 0;
    const dash = Math.max(frac * C - GAP, 1);
    const seg = { ...it, color: it.color || colors[i % colors.length], dash, off: -acc * C, pct: frac };
    acc += frac;
    return seg;
  });
  return { r, C, slices };
}

export function Donut({
  data = [],
  title,
  sub,
  centerIcon,
  totalLabel = 'Total',
  format = (v) => v,
  layout = 'row',
  size = 200,
  thickness = 26,
  colors = ODC_DONUT_PALETTE,
  gap = 7,
  trackColor = 'var(--mud-palette-divider-light)',
  ariaLabel,
}) {
  const items = data.filter((d) => d.value > 0);
  const total = items.reduce((s, d) => s + d.value, 0);
  const { r, C, slices } = odcDonutSlices(items, total, colors, size, thickness, gap);

  return (
    <div className="odc-donut-panel">
      {title || sub ? (
        <div className="odc-chart-head">
          <div>
            {title ? <div className="odc-chart-ttl">{title}</div> : null}
            {sub ? <div className="odc-chart-sub">{sub}</div> : null}
          </div>
        </div>
      ) : null}
      <div className={`odc-donut-body${layout === 'stack' ? ' stack' : ''}`}>
        <div className="odc-donut-ring" style={{ width: size, height: size }}>
          <svg viewBox={`0 0 ${size} ${size}`} width={size} height={size} role="img"
            aria-label={ariaLabel || (title ? `${title} — donut chart` : undefined)}
            aria-hidden={ariaLabel || title ? undefined : true}>
            <circle cx={size / 2} cy={size / 2} r={r} stroke={trackColor} strokeWidth={thickness} fill="none" />
            {slices.map((s, i) => (
              <circle
                key={i}
                cx={size / 2}
                cy={size / 2}
                r={r}
                fill="none"
                stroke={s.color}
                strokeWidth={thickness}
                strokeDasharray={`${s.dash.toFixed(1)} ${(C - s.dash).toFixed(1)}`}
                strokeDashoffset={s.off.toFixed(1)}
                strokeLinecap="butt"
              />
            ))}
          </svg>
          {centerIcon ? (
            <div className="odc-donut-center" aria-hidden="true">
              <span className="material-icons odc-donut-center-ic">{centerIcon}</span>
            </div>
          ) : null}
        </div>
        <DonutLegend data={data} colors={colors} totalLabel={totalLabel} format={format} />
      </div>
    </div>
  );
}

/**
 * DonutLegend — the "ledger": one row per slice (swatch · name · percent ·
 * amount), closed by the total row where the sum lives. Pass the same `data` +
 * `colors` as the Donut so swatch colors line up by order. Used standalone when
 * the ring and ledger are laid out separately.
 */
export function DonutLegend({
  data = [],
  colors = ODC_DONUT_PALETTE,
  totalLabel = 'Total',
  format = (v) => v,
  showTotal = true,
}) {
  const items = data.filter((d) => d.value > 0);
  const total = items.reduce((s, d) => s + d.value, 0);
  return (
    <div className="odc-donut-legend">
      {items.map((it, i) => {
        const pct = total > 0 ? it.value / total : 0;
        return (
          <div className="odc-legend-row" key={i}>
            <div className="odc-legend-main">
              <span className="odc-legend-swatch" style={{ background: it.color || colors[i % colors.length] }} />
              <span className="odc-legend-name">{it.label}</span>
            </div>
            <div className="odc-legend-figs">
              <span className="odc-legend-pct">{Math.round(pct * 100)}%</span>
              <span className="odc-legend-amt">{format(it.value, it)}</span>
            </div>
          </div>
        );
      })}
      {showTotal ? (
        <div className="odc-donut-total">
          <span className="odc-donut-total-lab">{totalLabel}</span>
          <div className="odc-legend-figs">
            <span className="odc-donut-total-pct">100%</span>
            <span className="odc-donut-total-amt">{format(total)}</span>
          </div>
        </div>
      ) : null}
    </div>
  );
}

/** Donut.Pair — two panels side by side with a hairline divider (assets/liabilities). */
Donut.Pair = function DonutPair({ children }) {
  const kids = React.Children.toArray(children);
  return (
    <div className="odc-donut-pair">
      {kids.map((child, i) => (
        <React.Fragment key={i}>
          {i > 0 ? <div className="odc-donut-pair-div" /> : null}
          {child}
        </React.Fragment>
      ))}
    </div>
  );
};
