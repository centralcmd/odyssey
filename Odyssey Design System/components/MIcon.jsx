/**
 * Odyssey DS — MIcon
 * A Material Icons glyph. Pass the ligature name (e.g. "account_balance_wallet").
 * Renders the system icon font (.material-icons); inherits currentColor.
 */
export function MIcon({ name, size, className = '', style }) {
  return (
    <span
      className={`material-icons ${className}`.trim()}
      style={{ ...(size ? { fontSize: size } : {}), ...(style || {}) }}
      aria-hidden="true"
    >
      {name}
    </span>
  );
}
