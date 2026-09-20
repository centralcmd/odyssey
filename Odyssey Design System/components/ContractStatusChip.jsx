/**
 * Odyssey DS — ContractStatusChip
 * A contract's DERIVED lifecycle status as ONE chip: **Archived** (retired,
 * hidden from the default list), **Draft** (recorded but not yet marked ready
 * for signature), **Ready** (ready for signature, nobody has signed yet),
 * **Upcoming** (its term has not begun), **Expired** (its term has run out),
 * **Paused** (temporarily suspended — still listed, still editable, not
 * costing anything), or **Active**.
 *
 * A contract has exactly one status, computed server-side per request from the
 * dates and the four stamps; precedence is
 * **Archived → Draft/Ready → Upcoming → Expired → Paused → Active**. Paused
 * *replaces* Active and nothing else: a terminal fact outranks a temporary
 * one, so a paused contract whose term has since run out reads Expired. The
 * superseded stamp is never lost — the record body carries a tile per stored
 * stamp (ready / signed / paused / archived), which is where the full history
 * lives.
 *
 * The signature layer sits ABOVE the date chain, so an unsigned contract reads
 * Draft/Ready whatever its dates say: a term nobody has agreed to is not
 * Upcoming, and a negotiation that stalled is abandoned, not Expired. Draft
 * and Ready contracts are excluded from the run rate and the upcoming charges
 * for the same reason — see `contractStatusIsUnsigned`.
 *
 * **Display order is NOT the wire ordinal.** `ContractStatus` appends
 * (`Draft = 5`, `Ready = 6`), so sorting on the ordinal would put the two
 * earliest states last. Sort on `CONTRACT_STATUS_RANK` instead —
 * **Draft → Ready → Upcoming → Active → Paused → Expired → Archived**.
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
  { key: 'Draft',    label: 'Draft',    tone: 'outline', dot: true, icon: 'edit_note' },
  { key: 'Ready',    label: 'Ready',    tone: 'pending', dot: true, icon: 'draw' },
  { key: 'Upcoming', label: 'Upcoming', tone: 'info',    dot: true, icon: 'schedule' },
  { key: 'Expired',  label: 'Expired',  tone: 'expense', dot: true, icon: 'event_busy' },
  { key: 'Paused',   label: 'Paused',   tone: 'pending', dot: true, icon: 'pause_circle' },
  { key: 'Active',   label: 'Active',   tone: 'income',  dot: true, icon: 'task_alt' },
];

/** Lifecycle reading order — what a Status sort uses. NOT the wire ordinal. */
export const CONTRACT_STATUS_RANK = ['Draft', 'Ready', 'Upcoming', 'Active', 'Paused', 'Expired', 'Archived'];

/** Rank for a member; an unknown member sorts last rather than first. */
export function contractStatusRank(key) {
  const i = CONTRACT_STATUS_RANK.indexOf(key);
  return i < 0 ? CONTRACT_STATUS_RANK.length : i;
}

/** The two states that are on file but not in force — excluded from the money. */
export function contractStatusIsUnsigned(key) { return key === 'Draft' || key === 'Ready'; }

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
