/**
 * Odyssey DS — SeverityIcon
 * The signal glyph shared by Alert blocks and the PageHeader problem-rollup
 * toggle. severity: warning | error | info. Renders in `currentColor` so the
 * parent supplies the tint (amber / coral / sea).
 *
 * The embedded Material Icons subset ships no warning triangle, so warning is
 * drawn as an inline SVG; error / info reuse the font's outline glyphs
 * (error_outline / info_outline) so a signal reads identically to an Alert.
 * Self-contained — no other DS atoms involved.
 */
export function SeverityIcon({ severity = 'warning', size = 18, className = '', style = {} }) {
  if (severity === 'warning') {
    return (
      <svg className={className} width={size} height={size} viewBox="0 0 24 24"
        fill="currentColor" aria-hidden="true" style={{ flex: 'none', ...style }}>
        <path d="M11.13 3.66 1.73 19.5a1 1 0 0 0 .87 1.5h18.8a1 1 0 0 0 .87-1.5L12.87 3.66a1 1 0 0 0-1.74 0Zm.87 4.59a1.05 1.05 0 0 1 1.05 1.13l-.33 4.7a.72.72 0 0 1-1.44 0l-.33-4.7A1.05 1.05 0 0 1 12 8.25Zm0 8.0a1.12 1.12 0 1 1 0 2.25 1.12 1.12 0 0 1 0-2.25Z" />
      </svg>
    );
  }
  return (
    <span className={`material-icons ${className}`.trim()} aria-hidden="true"
      style={{ fontSize: size, flex: 'none', ...style }}>
      {severity === 'error' ? 'error_outline' : 'info_outline'}
    </span>
  );
}
