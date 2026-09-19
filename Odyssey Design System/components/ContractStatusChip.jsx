/**
 * Odyssey DS — ContractStatusChip
 * A contract's DERIVED lifecycle status as ONE chip: **Archived** (retired,
 * hidden from the default list), **Upcoming** (its term has not begun),
 * **Expired** (its term has run out), **Paused** (temporarily suspended —
 * still listed, still editable, not costing anything), or **Active**.
 *
 * A contract has exactly one status, computed server-side per request from the
 * dates and the two stamps; precedence is
 * **Archived → Upcoming → Expired → Paused → Active**. Paused *replaces*
 * Active and nothing else: a terminal fact outranks a temporary one, so a
 * paused contract whose term has since run out reads Expired. The superseded
 * stamp is never lost — the record body carries a tile per stored stamp
 * (paused / archived), which is where the full history lives.
 *
 * Mirrors SubscriptionStatusChip's contract: the state **meaning lives in the
 * visible text label**, never colour or glyph alone (the leading dot/icon is
 * `aria-hidden`). Tone follows the finance vocabulary — Active = income
 * (mint), Upcoming = info (sea), Expired = expense (coral), Paused = pending
 * (amber), Archived = neutral outline.
 *
 * **Unknown members fail neutrally.** `ContractStatus` is a server enum that
 * appends: a client older than the deployment can be handed a member it has
 * never heard of. It renders as an unstyled outline chip carrying the member's
 * own name — never resolved into Active, which would report a *wrong* state
 * rather than a degraded one.
 */

export const CONTRACT_STATES = [
  { key: 'Archived', label: 'Archived', tone: 'outline', dot: true, icon: 'inventory_2' },
  { key: 'Upcoming', label: 'Upcoming', tone: 'info',    dot: true, icon: 'schedule' },
  { key: 'Expired',  label: 'Expired',  tone: 'expense', dot: true, icon: 'event_busy' },
  { key: 'Paused',   label: 'Paused',   tone: 'pending', dot: true, icon: 'pause_circle' },
  { key: 'Active',   label: 'Active',   tone: 'income',  dot: true, icon: 'task_alt' },
];

const BY_KEY = Object.fromEntries(CONTRACT_STATES.map((s) => [s.key, s]));

/** Resolve a ContractStatus member to its display row. Unknown ⇒ neutral. */
export function contractStatusMeta(key) {
  if (BY_KEY[key]) return BY_KEY[key];
  return { key: String(key == null ? 'Unknown' : key), label: String(key == null ? 'Unknown' : key),
    tone: 'outline', dot: true, icon: 'help', unknown: true };
}

export function ContractStatusChip({ status = 'Active', showIcon = false, size = 'md', className = '', style }) {
  const meta = contractStatusMeta(status);
  return (
    <span
      className={`odc-constatus${className ? ' ' + className : ''}`}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', ...style }}>
      <span className={`odc-chip ${meta.tone}${size === 'sm' ? ' sm' : ''}`}>
        {showIcon
          ? <span className="material-icons" aria-hidden="true">{meta.icon}</span>
          : meta.dot ? <span className="odc-chip-dot" aria-hidden="true" /> : null}
        {meta.label}
      </span>
    </span>
  );
}
