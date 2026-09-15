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
 *
 * THE TOTAL IS PART OF THE TILE, and on by default: a distribution whose sum a
 * reader has to add up in their head is a table, not a summary. `total` takes
 * three forms:
 *   true   (default) — sum the rows' own counts, when every count is a number
 *                      (or a numeric string). A tile whose counts are nodes —
 *                      money, a pair of figures — cannot be summed safely, so
 *                      nothing is rendered rather than a wrong figure.
 *   a value          — any number or node to show as the total, for exactly
 *                      that case (a net, a figure the caller computed, a sum
 *                      that is not the rows' arithmetic).
 *   false            — no total row, for a distribution whose sum means
 *                      nothing (overlapping buckets, percentages, a slice).
 * The row is ruled off above and its label is muted, so it reads as the tile's
 * conclusion rather than another category. Styled by .odc-breakdown-*.
 */
export function BreakdownTile({
  label,
  rows = [],
  empty = 'Nothing to show.',
  total = true,
  totalLabel = 'Total',
  totalIcon = 'functions',
  className = '',
  style,
  ...rest
}) {
  const numeric = (v) => {
    if (typeof v === 'number') return Number.isFinite(v) ? v : null;
    if (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v))) return Number(v);
    return null;
  };
  let totalValue = null;
  if (total !== false && total != null && rows.length) {
    if (total === true) {
      const nums = rows.map((r) => numeric(r.count));
      totalValue = nums.every((n) => n != null) ? nums.reduce((a, b) => a + b, 0) : null;
    } else {
      totalValue = total;
    }
  }
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
          {totalValue != null ? (
            <div className="odc-breakdown-row odc-breakdown-total">
              {totalIcon ? <span className="material-icons" aria-hidden="true">{totalIcon}</span> : null}
              <span className="odc-breakdown-label">{totalLabel}</span>
              <span className="odc-breakdown-n">{totalValue}</span>
            </div>
          ) : null}
        </div>
      ) : (
        <div className="odc-breakdown-empty">{empty}</div>
      )}
    </div>
  );
}
