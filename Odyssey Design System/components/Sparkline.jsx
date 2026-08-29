/**
 * Odyssey DS — Sparkline
 * A compact, axis-less trend line — the net-worth strip on the Dashboard /
 * stat tiles. Pure SVG computed from a `data` number[]; no dependency, no
 * axes, no labels. Uses the categorical chart tokens (default --chart-1) so
 * it re-themes for light/dark. Area fill is the stroke color at low opacity.
 *
 * Sizes to `width`×`height`; the path auto-scales to the data's min/max.
 * Give an `ariaLabel` summarizing the trend ("Net worth, up 18% over 6 months").
 */
export function Sparkline({
  data = [],
  width = 120,
  height = 36,
  stroke = 'var(--chart-1)',
  area = true,
  strokeWidth = 2,
  showDot = true,
  ariaLabel,
}) {
  const n = data.length;
  const pad = strokeWidth + 1;
  if (n < 2) {
    return <svg className="odc-sparkline" width={width} height={height} role="img" aria-label={ariaLabel} aria-hidden={ariaLabel ? undefined : true} />;
  }
  const min = Math.min(...data);
  const max = Math.max(...data);
  const span = max - min || 1;
  const x = (i) => pad + (i / (n - 1)) * (width - pad * 2);
  const y = (v) => pad + (1 - (v - min) / span) * (height - pad * 2);
  const pts = data.map((v, i) => `${x(i).toFixed(2)},${y(v).toFixed(2)}`);
  const line = `M ${pts.join(' L ')}`;
  const fillPath = `M ${x(0).toFixed(2)},${(height - pad).toFixed(2)} L ${pts.join(' L ')} L ${x(n - 1).toFixed(2)},${(height - pad).toFixed(2)} Z`;

  return (
    <svg
      className="odc-sparkline"
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={ariaLabel}
      aria-hidden={ariaLabel ? undefined : true}
      preserveAspectRatio="none"
    >
      {area ? <path d={fillPath} fill={stroke} fillOpacity="0.16" stroke="none" /> : null}
      <path
        d={line}
        fill="none"
        stroke={stroke}
        strokeWidth={strokeWidth}
        strokeLinejoin="round"
        strokeLinecap="round"
        vectorEffect="non-scaling-stroke"
      />
      {showDot ? (
        <circle cx={x(n - 1)} cy={y(data[n - 1])} r={strokeWidth + 0.5} fill={stroke} />
      ) : null}
    </svg>
  );
}
