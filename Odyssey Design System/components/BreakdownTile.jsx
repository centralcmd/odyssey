/**
 * Odyssey DS — BreakdownTile
 * A labelled summary tile that lists a small distribution as icon · label ·
 * count rows — "By type", "By status", "By currency", and the like. The generic
 * form of the Contracts overview's by-type breakdown, so any page can drop one
 * (or several, fed different data) into a summary grid.
 *
 * Pass `label` (the overline caption) and `rows` — each `{ icon, iconColor,
 * label, count, key? }`. The count is right-aligned in tabular monospace; the
 * icon tints to `iconColor`. When `rows` is empty the `empty` message shows
 * instead. Sits on the recessed well background like the other summary tiles.
 * Styled by .odc-breakdown-*.
 */
export function BreakdownTile({ label, rows = [], empty = 'Nothing to show.', className = '', style, ...rest }) {
  return (
    <div className={`odc-breakdown${className ? ' ' + className : ''}`} style={style} {...rest}>
      {label ? <span className="odc-breakdown-ov">{label}</span> : null}
      {rows.length ? (
        <div className="odc-breakdown-rows">
          {rows.map((r, i) => (
            <div className="odc-breakdown-row" key={r.key != null ? r.key : i}>
              {r.icon ? (
                <span className="material-icons" aria-hidden="true" style={r.iconColor ? { color: r.iconColor } : undefined}>{r.icon}</span>
              ) : null}
              <span className="odc-breakdown-label">{r.label}</span>
              <span className="odc-breakdown-n">{r.count}</span>
            </div>
          ))}
        </div>
      ) : (
        <div className="odc-breakdown-empty">{empty}</div>
      )}
    </div>
  );
}
