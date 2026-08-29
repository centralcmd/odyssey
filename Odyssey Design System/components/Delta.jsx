/**
 * Odyssey DS — Delta
 * One indicator for the three "change / difference" encodings the product uses,
 * so they read consistently:
 *   • mode="variance"    — a reconciliation result: 0 reads reconciled (mint ✓),
 *                          non-zero a discrepancy (amber), null "unavailable"
 *                          (disabled). Backs the Tax Statements variance cells.
 *   • mode="directional" — a period-over-period change: ↑/↓/– arrow + magnitude.
 *                          Pass `neutral` to mute the color when direction isn't
 *                          good-or-bad (rate changes). Backs the Account-terms delta.
 *   • mode="signed"      — a signed amount: +/− + magnitude, mint up / coral down.
 *                          Backs the LineChart / StatTile head deltas.
 *
 * `format(n)` renders the magnitude (e.g. a money/percent formatter); the sign
 * and glyph are added by the component. Tabular monospace, inline. Styled by
 * .odc-delta in components.css.
 */
export function Delta({
  value,
  format = (n) => Math.abs(n).toLocaleString(),
  mode = 'signed',
  neutral = false,
  zeroFormat,
  naLabel = 'Unavailable',
  suffix,
  className = '',
}) {
  const cls = (m) => `odc-delta ${m}${className ? ' ' + className : ''}`;
  const tail = suffix ? <span className="odc-delta-suffix"> {suffix}</span> : null;

  if (value == null) return <span className={cls('na')}>{naLabel}{tail}</span>;

  const mag = format(Math.abs(value));

  if (mode === 'variance') {
    if (Math.round(value) === 0) {
      return (
        <span className={cls('reconciled')}>
          <span className="material-icons" aria-hidden="true">check_circle</span>
          <span className="sr-only">reconciled, </span>
          {zeroFormat ? zeroFormat() : format(0)}{tail}
        </span>
      );
    }
    return (
      <span className={cls('diff')}>
        <span aria-hidden="true">{value < 0 ? '−' : '+'}</span>
        <span className="sr-only">variance, {value < 0 ? 'minus' : 'plus'} </span>
        {mag}{tail}
      </span>
    );
  }

  if (mode === 'directional') {
    const dir = value > 0 ? 'up' : value < 0 ? 'down' : 'flat';
    const glyph = dir === 'up' ? 'arrow_upward' : dir === 'down' ? 'arrow_downward' : 'remove';
    return (
      <span className={cls(neutral ? 'flat' : dir)}>
        <span className="material-icons" aria-hidden="true">{glyph}</span>
        {/* The arrow is aria-hidden and color is not a signal — give AT the
            direction as text. */}
        <span className="sr-only">{dir === 'up' ? 'up ' : dir === 'down' ? 'down ' : 'no change '}</span>
        {mag}{tail}
      </span>
    );
  }

  // signed
  return (
    <span className={cls(value >= 0 ? 'up' : 'down')}>
      <span aria-hidden="true">{value >= 0 ? '+' : '−'}</span>
      <span className="sr-only">{value >= 0 ? 'up ' : 'down '}</span>
      {mag}{tail}
    </span>
  );
}
