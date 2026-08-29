/**
 * Odyssey DS — StatTile
 * A single headline figure with an overline label and optional delta.
 * Numbers render tabular monospace. deltaDir tints the delta up (income)
 * or down (expense). `valueClass` tints the figure itself (income / expense)
 * — for a balance that flips sign. Styled by .odc-stat.
 */
export function StatTile({ overline, value, delta, deltaDir, valueClass = '', className = '' }) {
  return (
    <div className={`odc-stat${className ? ' ' + className : ''}`}>
      {overline ? <div className="odc-stat-overline">{overline}</div> : null}
      <div className={`odc-stat-num${valueClass ? ' ' + valueClass : ''}`}>{value}</div>
      {delta ? <div className={`odc-stat-delta ${deltaDir || ''}`.trim()}>{delta}</div> : null}
    </div>
  );
}
