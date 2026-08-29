/**
 * Odyssey DS — Badge
 * A small count / status indicator, distinct from Chip (which carries a label).
 * tone = error (default, notifications) · primary · neutral. `dot` renders a
 * bare 8px dot with no count. Styled by .odc-badge.
 */
export function Badge({ tone = 'error', dot = false, max = 99, count, children }) {
  if (dot) {
    return <span className={`odc-badge ${tone} dot`} aria-hidden="true" />;
  }
  const content = count != null && count > max ? `${max}+` : (count != null ? count : children);
  return <span className={`odc-badge ${tone}`}>{content}</span>;
}
