/**
 * Odyssey DS — Chip
 * Compact status / category pill. tone sets the semantic color:
 * income · expense · pending · info · tag · warning · error, the neutral
 * `outline` (bordered, transparent), or `default` (neutral fill).
 * Optional leading dot (status) or Material icon. Styled by .odc-chip.
 */
export function Chip({ tone = 'default', icon, dot = false, className = '', children }) {
  return (
    <span className={`odc-chip ${tone}${className ? ' ' + className : ''}`.trim()}>
      {dot ? <span className="odc-chip-dot" aria-hidden="true" /> : null}
      {icon ? <span className="material-icons" aria-hidden="true">{icon}</span> : null}
      {children}
    </span>
  );
}
