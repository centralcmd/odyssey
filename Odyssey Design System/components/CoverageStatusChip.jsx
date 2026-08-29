/**
 * Odyssey DS — CoverageStatusChip
 * The read display of an insurance policy's DERIVED coverage status as a chip —
 * the Insurance feature's sibling of AccountStatusChip. Status is computed, never
 * stored (see the Insurance spec §5), and is one of: Active · ExpiringSoon ·
 * Lapsed · Upcoming · NoCoverage — plus Archived, the terminal lifecycle state
 * (an archived policy reads as Archived, mirroring the Contracts status chip).
 *
 * Accessibility: the status **meaning lives in the visible text label**, never in
 * colour or the glyph alone — the leading dot/icon is `aria-hidden`. Tone follows
 * the finance vocabulary: Active = income (mint), ExpiringSoon = pending (amber),
 * Lapsed/"Expired" = expense (coral), Upcoming = info (sea), NoCoverage /
 * Archived = neutral outline. Icons for the states shared with Contracts (Active,
 * Upcoming, the ended state, Archived) match conStatusMeta so a status reads
 * identically across both pages.
 *
 * `detail` is an optional muted trailing segment inside the chip — e.g.
 * "12 days left" / "ended Jun 1". Pass `showIcon` to lead with the status glyph
 * instead of the status dot. Styled by .odc-chip (shared pill) + .odc-coverage-detail.
 */

export const COVERAGE_STATUSES = [
  { key: 'Active',       label: 'Active',        tone: 'income',  dot: true,  icon: 'task_alt' },
  { key: 'ExpiringSoon', label: 'Expiring soon', tone: 'pending', dot: true, icon: 'hourglass_bottom' },
  { key: 'Lapsed',       label: 'Expired',       tone: 'expense', dot: true, icon: 'event_busy' },
  { key: 'Upcoming',     label: 'Upcoming',      tone: 'info',    dot: true,  icon: 'schedule' },
  { key: 'NoCoverage',   label: 'No coverage',   tone: 'outline', dot: true,  icon: 'remove_moderator' },
  { key: 'Archived',     label: 'Archived',      tone: 'outline', dot: true,  icon: 'inventory_2' },
];

const BY_KEY = Object.fromEntries(COVERAGE_STATUSES.map((s) => [s.key, s]));

export function CoverageStatusChip({
  status = 'NoCoverage',
  detail,
  showIcon = false,
  size = 'md',
  className = '',
  style,
}) {
  const meta = BY_KEY[status] || BY_KEY.NoCoverage;
  const sz = size === 'sm' ? ' sm' : '';
  return (
    <span className={`odc-chip ${meta.tone}${sz}${className ? ' ' + className : ''}`} style={style}>
      {showIcon
        ? <span className="material-icons" aria-hidden="true">{meta.icon}</span>
        : meta.dot ? <span className="odc-chip-dot" aria-hidden="true" /> : null}
      {meta.label}
      {detail ? <span className="odc-coverage-detail">{detail}</span> : null}
    </span>
  );
}
