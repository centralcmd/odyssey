/**
 * Odyssey DS — TodoStatusChip
 * The read display of a to-do task's kanban lifecycle as a chip:
 * **Backlog → Doing → Done**, plus a terminal **Archived**. One chip per call
 * (a task has exactly one status), the state **meaning carried in the visible
 * text label** — never colour or glyph alone (the leading dot/icon is
 * `aria-hidden`), matching SubscriptionStatusChip / CoverageStatusChip.
 *
 * Tone follows the shared vocabulary: Backlog = neutral outline (not started),
 * Doing = info/sea (in progress — a cool, non-finance accent), Done = income
 * (mint, the only "good/complete" green in the system), Archived = neutral
 * outline read quieter. Tones intentionally avoid the finance income/expense
 * pairing carrying money meaning — Done reuses mint only as a generic
 * completion signal, always with its text label.
 *
 * Props: `status` ('Backlog' | 'Doing' | 'Done' | 'Archived'), `showIcon`
 * (lead with the glyph instead of the status dot), `size` ('sm' | 'md').
 * Styled by the shared `.odc-chip`.
 */

export const TODO_STATUSES = [
  { key: 'Backlog',  label: 'Backlog',  tone: 'outline', value: 0, dot: true, icon: 'inbox' },
  { key: 'Doing',    label: 'Doing',    tone: 'info',    value: 1, dot: true, icon: 'timelapse' },
  { key: 'Done',     label: 'Done',     tone: 'income',  value: 2, dot: true, icon: 'check_circle' },
  { key: 'Archived', label: 'Archived', tone: 'outline', value: 3, dot: true, icon: 'inventory_2' },
];

const TODO_BY_KEY = Object.fromEntries(TODO_STATUSES.map((s) => [s.key, s]));

export function TodoStatusChip({
  status = 'Backlog',
  showIcon = false,
  size = 'md',
  className = '',
  style,
}) {
  const meta = TODO_BY_KEY[status] || TODO_BY_KEY.Backlog;
  const archived = meta.key === 'Archived';
  return (
    <span
      className={`odc-chip ${meta.tone}${size === 'sm' ? ' sm' : ''}${archived ? ' odc-todo-archived' : ''}${className ? ' ' + className : ''}`}
      style={style}>
      {showIcon
        ? <span className="material-icons" aria-hidden="true">{meta.icon}</span>
        : meta.dot ? <span className="odc-chip-dot" aria-hidden="true" /> : null}
      {meta.label}
    </span>
  );
}
