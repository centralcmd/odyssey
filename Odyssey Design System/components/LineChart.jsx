/**
 * Odyssey DS — LineChart
 * The axis'd trend chart behind the Dashboard net-worth chart and the Tax
 * Statements overview. Unlike `Sparkline` (compact, axis-less), this is the
 * full card: a head (title · sub on the left, headline figure + optional delta
 * on the right) over an SVG line/area plot with horizontal gridlines and
 * value / category axis labels.
 *
 * Data is `series: { label, value, kind? }[]` (oldest → newest). Pass `format`
 * for the headline + axis numbers (e.g. a money formatter); `axisFormat`
 * overrides the y-axis ticks with a compact variant. `cumulative` plots the
 * running total. `showDelta` adds a latest-vs-first delta (mint up / coral
 * down) with `deltaSuffix` text. A single point renders as a dot; an empty
 * series renders the `emptyLabel`.
 *
 * Reconstructed series (net-worth history) need three things a plain trend
 * card doesn't:
 *   • **Negatives.** The value domain drops below zero when it has to. The
 *     area then anchors at `sy(0)` instead of the plot floor and a labelled
 *     zero gridline is drawn. A domain that never goes negative keeps the
 *     old floor-anchored fill and the old four gridlines exactly.
 *   • **Point kinds**, not a flag: `normal` (measured) · `partial`
 *     (understated — a contributing account had no exchange rate) ·
 *     `revalued` (a real step, an estimate took effect). Shape and stroke
 *     carry the meaning, never fill colour: filled dot · hollow dot with
 *     dashed adjoining segments · filled dot with a vertical tick. A partial
 *     **endpoint** suppresses the delta; a revalued one does not.
 *   • **A text equivalent** — `textEquivalent` renders a visually-hidden
 *     table of every point and its state. Opt-in, so existing consumers'
 *     assistive-tech output is unchanged.
 *
 * `sub` is a node, so a consumer can hand it note sentences rather than a
 * single caption string. `xTickEvery="auto"` picks a stride that never leaves
 * two adjacent labels at the tail.
 *
 * For a value that HOLDS between dated changes — a price, a rate, a term — use
 * `StepChart` instead: same card, real time axis, staircase, today marker.
 *
 * Pure SVG + tokens (default stroke --chart-1) so it re-themes light/dark.
 * Styled by .odc-lc / .odc-line-svg in components.css.
 */
const LINE_CHART_KINDS = {
  normal: { label: 'Measured', desc: 'Measured' },
  partial: { label: 'Understated', desc: 'Understated — an account had no exchange rate for this period' },
  revalued: { label: 'Revalued', desc: 'Revalued — an estimate took effect in this period' },
};

/** §3.5 stride rule: start at ceil(n / 8), then step up until the tail label
 *  and the stride's last label are not adjacent. Exported shape is the
 *  `xTickEvery="auto"` mode; the maths lives here so it is testable. */
function lineChartTickEvery(n) {
  if (n <= 1) return 1;
  let every = Math.max(1, Math.ceil(n / 8));
  let guard = 0;
  while ((n - 1) % every === 1 && guard++ < n) every += 1;
  return every;
}

export function LineChart({
  series = [],
  color = 'var(--chart-1)',
  cumulative = false,
  title,
  sub,
  format = (n) => n.toLocaleString(),
  axisFormat,
  showDelta = false,
  deltaSuffix,
  figure,
  xTickEvery = 1,
  area = true,
  markLegend = true,
  textEquivalent = false,
  textEquivalentLabel = 'Period',
  ariaLabel,
  className = '',
  emptyLabel = 'No data yet.',
}) {
  const uid = React.useId();
  const fmtAxis = axisFormat || format;

  // Build the plotted points (oldest → newest), applying the running total in
  // cumulative mode. Partial and revalued points are plotted and marked, never
  // dropped — nulling one would move the delta's endpoints.
  const pts = [];
  let acc = 0;
  for (const p of series) {
    if (p == null || p.value == null) continue;
    acc += p.value;
    pts.push({
      label: p.label,
      value: cumulative ? acc : p.value,
      kind: p.kind === 'partial' || p.kind === 'revalued' ? p.kind : 'normal',
      assets: p.assets,
      liabilities: p.liabilities,
    });
  }

  const first = pts[0], last = pts[pts.length - 1];
  // V15 — an understated endpoint makes the difference meaningless. A revalued
  // endpoint is a real movement and does not suppress it.
  const deltaOk = showDelta && pts.length > 1 &&
    first.kind !== 'partial' && last.kind !== 'partial';

  const head = (
    <div className="odc-lc-head">
      <div>
        {title ? <div className="odc-lc-ttl">{title}</div> : null}
        {sub ? <div className="odc-lc-sub">{sub}</div> : null}
      </div>
      {(figure != null || pts.length > 0) && (
        <div className="odc-lc-figure">
          <div className="odc-lc-num">{figure != null ? figure : format(last.value)}</div>
          {deltaOk && (() => {
            const delta = last.value - first.value;
            return (
              <div className={`odc-lc-delta ${delta >= 0 ? 'income' : 'expense'}`}>
                {delta >= 0 ? '+' : '−'}{format(Math.abs(delta))}{deltaSuffix ? ` ${deltaSuffix}` : ''}
              </div>
            );
          })()}
        </div>
      )}
    </div>
  );

  if (pts.length === 0) {
    return (
      <div className={`odc-lc${className ? ' ' + className : ''}`}>
        {head}
        <div className="odc-lc-empty">{emptyLabel}</div>
      </div>
    );
  }

  const x0 = 64, x1 = 968, yTop = 28, yBot = 212;
  const single = pts.length === 1;
  const vals = pts.map((p) => p.value);
  const lo = Math.min(...vals), hi = Math.max(...vals);
  const span = hi - lo || Math.abs(hi) || 1;
  const pad = span * 0.18;
  // The floor clamp at 0 is kept for series that never go negative, so every
  // existing consumer renders byte-identically; a negative low drops the floor.
  const yMin = lo < 0 ? lo - pad : Math.max(0, lo - pad);
  const yMax = hi + pad;
  const straddles = yMin < 0 && yMax > 0;
  const sx = (i) => (single ? (x0 + x1) / 2 : x0 + (i * (x1 - x0)) / (pts.length - 1));
  const sy = (v) => yBot - ((v - yMin) / (yMax - yMin || 1)) * (yBot - yTop);
  const baseline = straddles ? sy(0) : yBot;

  const linePts = pts.map((p, i) => `${sx(i).toFixed(1)},${sy(p.value).toFixed(1)}`).join(' ');
  const areaPath =
    `M ${sx(0).toFixed(1)} ${baseline.toFixed(1)} ` +
    pts.map((p, i) => `L ${sx(i).toFixed(1)} ${sy(p.value).toFixed(1)}`).join(' ') +
    ` L ${sx(pts.length - 1).toFixed(1)} ${baseline.toFixed(1)} Z`;

  // Four evenly-spaced values, as before — unless the domain straddles zero,
  // in which case zero is a gridline of its own and each half is halved.
  const gridVals = straddles
    ? [yMax, yMax / 2, 0, yMin / 2, yMin]
    : [yMax, yMin + (yMax - yMin) * 2 / 3, yMin + (yMax - yMin) / 3, yMin];

  const every = xTickEvery === 'auto' ? lineChartTickEvery(pts.length) : (xTickEvery || 1);
  const anyMarked = pts.some((p) => p.kind !== 'normal');
  const fillId = `odc-lc-fill-${uid.replace(/[^a-zA-Z0-9_-]/g, '')}`;

  const segments = [];
  for (let i = 1; i < pts.length; i++) {
    const understated = pts[i - 1].kind === 'partial' || pts[i].kind === 'partial';
    segments.push({ i, understated });
  }

  return (
    <div className={`odc-lc${className ? ' ' + className : ''}`}>
      {head}
      <svg className="odc-line-svg" viewBox="0 0 1000 252" preserveAspectRatio="xMidYMid meet"
        role="img" aria-label={ariaLabel || (title ? `${title} — line chart` : 'Line chart')}>
        <g stroke="var(--chart-grid)" strokeWidth="1">
          {gridVals.map((v, i) => (
            v === 0 ? null :
            <line key={i} x1={x0} y1={sy(v).toFixed(1)} x2={x1} y2={sy(v).toFixed(1)} />
          ))}
        </g>
        <g className="odc-lc-axis">
          {gridVals.map((v, i) => (
            <text key={i} x={x0 - 12} y={(sy(v) + 4).toFixed(1)} textAnchor="end"
              className={v === 0 ? 'odc-lc-axis-zero' : undefined}>{fmtAxis(Math.abs(yMax) < 10 ? v : Math.round(v))}</text>
          ))}
        </g>
        <g className="odc-lc-axis">
          {pts.map((p, i) => (
            (i % every === 0 || i === pts.length - 1) &&
            <text key={i} x={sx(i).toFixed(1)} y={yBot + 26} textAnchor="middle">{p.label}</text>
          ))}
        </g>
        {area && !single && (
          <defs>
            <linearGradient id={fillId} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity="0.26" />
              <stop offset="100%" stopColor={color} stopOpacity="0" />
            </linearGradient>
          </defs>
        )}
        {/* A straddling domain fills flat — a vertical gradient across a band
            that crosses zero would read as a second signal. */}
        {area && !single && (
          straddles
            ? <path d={areaPath} fill={color} fillOpacity="0.14" />
            : <path d={areaPath} fill={`url(#${fillId})`} />
        )}
        {straddles && (
          <line className="odc-lc-zero" x1={x0} y1={sy(0).toFixed(1)} x2={x1} y2={sy(0).toFixed(1)} />
        )}
        {!single && !anyMarked && (
          <polyline points={linePts} fill="none" stroke={color} strokeWidth="2.4"
            strokeLinejoin="round" strokeLinecap="round" />
        )}
        {!single && anyMarked && segments.map((s) => (
          <line key={s.i} x1={sx(s.i - 1).toFixed(1)} y1={sy(pts[s.i - 1].value).toFixed(1)}
            x2={sx(s.i).toFixed(1)} y2={sy(pts[s.i].value).toFixed(1)}
            stroke={color} strokeWidth="2.4" strokeLinecap="round"
            strokeDasharray={s.understated ? '2 5' : undefined} />
        ))}
        {pts.map((p, i) => {
          const cx = +sx(i).toFixed(1), cy = +sy(p.value).toFixed(1);
          const r = i === pts.length - 1 ? 4 : 2.5;
          if (p.kind === 'partial') {
            return (
              <circle key={i} cx={cx} cy={cy} r={r + 1.4} fill="var(--mud-palette-surface)"
                stroke={color} strokeWidth="1.8" />
            );
          }
          if (p.kind === 'revalued') {
            return (
              <g key={i}>
                <line x1={cx} y1={cy - 8} x2={cx} y2={cy + 8} stroke={color} strokeWidth="1.6" />
                <circle cx={cx} cy={cy} r={r} fill={color} />
              </g>
            );
          }
          return <circle key={i} cx={cx} cy={cy} r={r} fill={color} />;
        })}
      </svg>

      {markLegend && anyMarked && (
        <div className="odc-lc-marks">
          {pts.some((p) => p.kind === 'partial') && (
            <span className="odc-lc-mark">
              <svg width="26" height="10" viewBox="0 0 26 10" aria-hidden="true">
                <line x1="0" y1="5" x2="26" y2="5" stroke={color} strokeWidth="2" strokeDasharray="2 5" />
                <circle cx="13" cy="5" r="3.6" fill="var(--mud-palette-surface)" stroke={color} strokeWidth="1.6" />
              </svg>
              Understated
            </span>
          )}
          {pts.some((p) => p.kind === 'revalued') && (
            <span className="odc-lc-mark">
              <svg width="26" height="10" viewBox="0 0 26 10" aria-hidden="true">
                <line x1="0" y1="5" x2="26" y2="5" stroke={color} strokeWidth="2" />
                <line x1="13" y1="0" x2="13" y2="10" stroke={color} strokeWidth="1.6" />
                <circle cx="13" cy="5" r="2.6" fill={color} />
              </svg>
              Revalued
            </span>
          )}
        </div>
      )}

      {textEquivalent && (
        <table className="odc-sr-only">
          {title ? <caption>{title}</caption> : null}
          <thead>
            <tr>
              <th scope="col">{textEquivalentLabel}</th>
              <th scope="col">Value</th>
              <th scope="col">State</th>
            </tr>
          </thead>
          <tbody>
            {pts.map((p, i) => (
              <tr key={i}>
                <th scope="row">{p.label}</th>
                <td>{format(p.value)}</td>
                <td>{LINE_CHART_KINDS[p.kind].desc}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
