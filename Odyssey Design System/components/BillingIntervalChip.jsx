/**
 * Odyssey DS — BillingIntervalChip
 * The read display of a subscription's billing cadence: the interval as a
 * colored-glyph type chip plus the **derived billing anchor** as a muted
 * trailing segment — e.g. "Monthly · day 15", "Yearly · 15 Jan", "Weekly · Wed",
 * "Daily" (no anchor). It is Subscriptions' sibling of AccountTypeChip.
 *
 * The per-cycle anchor is DERIVED here from `firstBillingDate` + `interval`, and
 * is never stored (see the Subscriptions spec): day-of-month for Monthly,
 * month+day for Yearly, day-of-week for Weekly, none for Daily. The date is
 * parsed as UTC so the derived day/weekday never drifts by a timezone.
 *
 * Accessibility: the cadence + anchor are conveyed entirely in **visible text**;
 * the leading glyph is `aria-hidden`. Styled by the shared `.odc-typechip`.
 *
 * Props: `interval` (BillingInterval key), `count` (the "every N" multiplier,
 * int ≥ 1 — count 1 shows the plain label "Monthly", count > 1 shows "Every N
 * months"), `firstBillingDate` (ISO YYYY-MM-DD — optional; without it only the
 * cadence shows), `anchor` (override the derived string), `size` ('sm' | 'md').
 * The registry is `BILLING_INTERVALS`, read off the DS namespace at render time
 * with a defensive fallback.
 */

const SUB_UNIT_NOUN = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };

/** Cadence label honouring the "every N" multiplier. */
export function billingIntervalLabel(interval, count, fallbackLabel) {
  const n = Math.round(Number(count));
  const every = Number.isFinite(n) && n > 0 ? n : 1;
  if (every <= 1) return fallbackLabel || interval;
  return `Every ${every} ${SUB_UNIT_NOUN[interval] || 'cycle'}s`;
}

const BILLING_INTERVAL_FALLBACK = [
  { key: 'Daily',   label: 'Daily',   icon: 'today',          color: 'oklch(0.79 0.13 205)' },
  { key: 'Weekly',  label: 'Weekly',  icon: 'view_week',      color: 'oklch(0.78 0.14 168)' },
  { key: 'Monthly', label: 'Monthly', icon: 'calendar_month', color: 'oklch(0.72 0.14 255)' },
  { key: 'Yearly',  label: 'Yearly',  icon: 'event_repeat',   color: 'oklch(0.72 0.16 295)' },
];

const WD_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MON_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Derived per-cycle billing position for an interval, from an ISO first-billing date. */
export function billingAnchorLabel(interval, firstBillingDate) {
  if (!firstBillingDate) return null;
  const [y, m, d] = String(firstBillingDate).slice(0, 10).split('-').map(Number);
  if (!y || !m || !d) return null;
  switch (interval) {
    case 'Monthly': return `day ${d}`;
    case 'Yearly':  return `${d} ${MON_SHORT[m - 1]}`;
    case 'Weekly':  return WD_SHORT[new Date(Date.UTC(y, m - 1, d)).getUTCDay()];
    default:        return null; // Daily needs no anchor
  }
}

export function BillingIntervalChip({
  interval = 'Monthly',
  count = 1,
  firstBillingDate,
  anchor,
  size = 'md',
  className = '',
  style,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const registry = NS.BILLING_INTERVALS || BILLING_INTERVAL_FALLBACK;
  const meta = registry.find((t) => t.key === interval) || registry[0];
  const label = billingIntervalLabel(interval, count, meta.label);
  const derived = anchor != null ? anchor : billingAnchorLabel(interval, firstBillingDate);
  return (
    <span className={`odc-typechip${size === 'sm' ? ' sm' : ''}${className ? ' ' + className : ''}`} style={style}>
      <span className="material-icons odc-typechip-ic" style={{ color: meta.color }} aria-hidden="true">{meta.icon}</span>
      <span className="odc-typechip-name">{label}</span>
      {derived ? <span className="odc-typechip-group">{derived}</span> : null}
    </span>
  );
}
