/**
 * Odyssey DS — SubscriptionStatusChip
 * The read display of a subscription's lifecycle state as ONE chip: **Paused**
 * (temporarily not billing, still visible), **Ended** (its term has lapsed —
 * DERIVED from `endDate`, never stored), **Archived** (retired and hidden), or
 * **Active** (only when `showActive`).
 *
 * A subscription has exactly one lifecycle state. The states are ordered rather
 * than orthogonal: only an ended subscription can be archived, so Archived
 * implies Ended, and Ended makes a pause moot. Precedence is therefore
 * Archived → Ended → Paused → Active, and the chip renders the first that
 * applies. Superseded timestamps are not lost — the record body carries a tile
 * per stored flag (paused / archived), which is where the full history lives.
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
  // Exactly one state, by precedence: Archived → Ended → Paused → Active.
  const state = archived ? BY_KEY.Archived : ended ? BY_KEY.Ended : paused ? BY_KEY.Paused : null;
  if (!state && !showActive) return null;
  const states = [state || BY_KEY.Active];
  return (
    <span
      className={`odc-substatus${className ? ' ' + className : ''}`}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', ...style }}>
      {states.map((meta) => <StateChip key={meta.key} meta={meta} showIcon={showIcon} size={size} />)}
    </span>
  );
}
