/**
 * Odyssey DS — AccountStatusChip
 * The read display of an account's **status** as a chip — the third sibling of
 * `AccountTypeChip` and `CustodianChip`, so the account detail's metadata grid
 * reads as one coherent chip family. Same pill shell as those two, but the
 * leading accent is a tone-colored **dot** (status is a state, not a category
 * with an icon) followed by the status label and an optional muted detail
 * segment — the date context (e.g. "since Mar 14, 2021" / "on Mar 10, 2021").
 *
 * `label` is the status word (Open / Closed / Archived). `tone` maps to the
 * dot color — income (open) · pending (closed) · outline/neutral (archived).
 * `detail` is the muted trailing segment. `size` sm / md (default). Styled by
 * .odc-typechip (shared shell) + .odc-typechip-dot.
 */

const STATUS_DOT_COLOR = {
  income: 'var(--finance-income)',
  pending: 'var(--finance-pending)',
  expense: 'var(--finance-expense)',
  error: 'var(--finance-expense)',
  warning: 'var(--finance-pending)',
  info: 'var(--sea-400)',
  outline: 'var(--mud-palette-text-secondary)',
  neutral: 'var(--mud-palette-text-secondary)',
};

export function AccountStatusChip({ label, tone = 'neutral', detail, size = 'md', className = '', style }) {
  if (!label) return null;
  const sz = size === 'sm' ? ' sm' : '';
  const dotColor = STATUS_DOT_COLOR[tone] || STATUS_DOT_COLOR.neutral;

  return (
    <span className={`odc-typechip${sz}${className ? ' ' + className : ''}`} style={style}>
      <span className="odc-typechip-dot" style={{ background: dotColor }} aria-hidden="true"></span>
      <span className="odc-typechip-name">{label}</span>
      {detail ? <span className="odc-typechip-group">{detail}</span> : null}
    </span>
  );
}
