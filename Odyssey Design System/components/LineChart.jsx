/**
 * Odyssey DS — LineChart
 * The axis'd trend chart behind the Dashboard net-worth chart and the Tax
 * Statements overview. Unlike `Sparkline` (compact, axis-less), this is the
 * full card: a head (title · sub on the left, headline figure + optional delta
 * on the right) over an SVG line/area plot with horizontal gridlines and
 * value / category axis labels.
 *
 * Data is `series: { label, value }[]` (oldest → newest). Pass `format` for the
 * headline + axis numbers (e.g. a money formatter); `axisFormat` overrides the
 * y-axis ticks with a compact variant. `cumulative` plots the running total.
 * `showDelta` adds a latest-vs-first delta (mint up / coral down) with
 * `deltaSuffix` text (e.g. "all-time" or "vs 2024"). A single point renders as
 * a dot; an empty series renders the `emptyLabel`.
 *
 * Pure SVG + tokens (default stroke --chart-1) so it re-themes light/dark.
 * Styled by .odc-lc / .odc-line-svg in components.css.
 */
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
  ariaLabel,
  className = '',
  emptyLabel = 'No data yet.',
}) {
  const uid = React.useId();
  const fmtAxis = axisFormat || format;

  // Build the plotted points (oldest → newest), applying the running total in
  // cumulative mode.
  const pts = [];
  let acc = 0;
  for (const p of series) {
    if (p == null || p.value == null) continue;
    acc += p.value;
    pts.push({ label: p.label, value: cumulative ? acc : p.value });
  }

  const head = (
    <div className="odc-lc-head">
      <div>
        {title ? <div className="odc-lc-ttl">{title}</div> : null}
        {sub ? <div className="odc-lc-sub">{sub}</div> : null}
      </div>
      {(figure != null || pts.length > 0) && (
        <div className="odc-lc-figure">
          <div className="odc-lc-num">{figure != null ? figure : format(pts[pts.length - 1].value)}</div>
          {showDelta && pts.length > 1 && (() => {
            const delta = pts[pts.length - 1].value - pts[0].value;
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
  const yMin = Math.max(0, lo - span * 0.18), yMax = hi + span * 0.18;
  const sx = (i) => (single ? (x0 + x1) / 2 : x0 + (i * (x1 - x0)) / (pts.length - 1));
  const sy = (v) => yBot - ((v - yMin) / (yMax - yMin || 1)) * (yBot - yTop);

  const linePts = pts.map((p, i) => `${sx(i).toFixed(1)},${sy(p.value).toFixed(1)}`).join(' ');
  const areaPath =
    `M ${sx(0).toFixed(1)} ${yBot} ` +
    pts.map((p, i) => `L ${sx(i).toFixed(1)} ${sy(p.value).toFixed(1)}`).join(' ') +
    ` L ${sx(pts.length - 1).toFixed(1)} ${yBot} Z`;
  const gridVals = [yMax, yMin + (yMax - yMin) * 2 / 3, yMin + (yMax - yMin) / 3, yMin];
  const fillId = `odc-lc-fill-${uid.replace(/[^a-zA-Z0-9_-]/g, '')}`;

  return (
    <div className={`odc-lc${className ? ' ' + className : ''}`}>
      {head}
      <svg className="odc-line-svg" viewBox="0 0 1000 252" preserveAspectRatio="xMidYMid meet"
        role="img" aria-label={ariaLabel || (title ? `${title} — line chart` : 'Line chart')}>
        <g stroke="var(--chart-grid)" strokeWidth="1">
          {gridVals.map((v, i) => (
            <line key={i} x1={x0} y1={sy(v).toFixed(1)} x2={x1} y2={sy(v).toFixed(1)} />
          ))}
        </g>
        <g className="odc-lc-axis">
          {gridVals.map((v, i) => (
            <text key={i} x={x0 - 12} y={(sy(v) + 4).toFixed(1)} textAnchor="end">{fmtAxis(Math.round(v))}</text>
          ))}
        </g>
        <g className="odc-lc-axis">
          {pts.map((p, i) => (
            (i % xTickEvery === 0 || i === pts.length - 1) &&
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
        {area && !single && <path d={areaPath} fill={`url(#${fillId})`} />}
        {!single && (
          <polyline points={linePts} fill="none" stroke={color} strokeWidth="2.4"
            strokeLinejoin="round" strokeLinecap="round" />
        )}
        {pts.map((p, i) => (
          <circle key={i} cx={sx(i).toFixed(1)} cy={sy(p.value).toFixed(1)}
            r={i === pts.length - 1 ? 4 : 2.5} fill={color} />
        ))}
      </svg>
    </div>
  );
}
