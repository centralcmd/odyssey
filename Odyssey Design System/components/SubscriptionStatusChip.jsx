/**
 * Odyssey DS — SubscriptionStatusChip
 * The read display of a subscription's lifecycle states as chips: **Paused**
 * (temporarily not billing, still visible), **Ended** (its term has lapsed —
 * DERIVED from `endDate`, never stored), and **Archived** (hidden/retired).
 * Paused and Archived are orthogonal stored flags; Ended is derived. This
 * renders one chip per active state — Ended supersedes Paused (a pause is moot
 * once the term is over), Archived stacks after — and an "Active" chip when none
 * is set (only when `showActive`).
 *
 * This mirrors CoverageStatusChip's contract: the state **meaning lives in the
 * visible text label**, never colour or glyph alone (the leading dot/icon is
 * `aria-hidden`). Tone follows the finance vocabulary — Paused = pending
 * (amber), Archived = neutral outline, Active = income (mint).
 *
 * Props: `paused` / `archived` (boolean, or a truthy timestamp), `showActive`
 * (render the Active chip when neither state is set — default false, so a plain
 * active row shows nothing), `showIcon` (lead with the glyph instead of the
 * dot), `size` ('sm' | 'md'). Styled by the shared `.odc-chip`.
 */

export const SUBSCRIPTION_STATES = [
  { key: 'Paused',   label: 'Paused',   tone: 'pending', dot: true, icon: 'pause_circle' },
  { key: 'Ended',    label: 'Ended',    tone: 'expense', dot: true, icon: 'event_busy' },
  { key: 'Archived', label: 'Archived', tone: 'outline', dot: true,  icon: 'inventory_2' },
  { key: 'Active',   label: 'Active',   tone: 'income',  dot: true,  icon: 'autorenew' },
];

const BY_KEY = Object.fromEntries(SUBSCRIPTION_STATES.map((s) => [s.key, s]));

function StateChip({ meta, showIcon, size }) {
  return (
    <span className={`odc-chip ${meta.tone}${size === 'sm' ? ' sm' : ''}`}>
      {showIcon
        ? <span className="material-icons" aria-hidden="true">{meta.icon}</span>
        : meta.dot ? <span className="odc-chip-dot" aria-hidden="true" /> : null}
      {meta.label}
    </span>
  );
}

export function SubscriptionStatusChip({
  paused = false,
  ended = false,
  archived = false,
  showActive = false,
  showIcon = false,
  size = 'md',
  className = '',
  style,
}) {
  const states = [];
  if (ended) states.push(BY_KEY.Ended);
  else if (paused) states.push(BY_KEY.Paused);
  if (archived) states.push(BY_KEY.Archived);
  if (!states.length) {
    if (!showActive) return null;
    states.push(BY_KEY.Active);
  }
  return (
    <span
      className={`odc-substatus${className ? ' ' + className : ''}`}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', ...style }}>
      {states.map((meta) => <StateChip key={meta.key} meta={meta} showIcon={showIcon} size={size} />)}
    </span>
  );
}
