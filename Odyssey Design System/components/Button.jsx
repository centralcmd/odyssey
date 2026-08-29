/**
 * Odyssey DS — Button
 * MudBlazor-aligned button. variant = filled (primary CTA) / outlined
 * (secondary) / text (tertiary, nav) / danger (destructive). Verb-first
 * labels. Styled by components.css (.odc-btn) — token-driven, theme-aware.
 *
 * Pass `loading` for the busy state (label hides, spinner overlays). For an
 * icon-only button (no children), pass `ariaLabel` so it's announced to AT.
 *
 * `badge` puts a count pill on the button — for a pending quantity the action
 * is about to commit (unsaved changes, queued uploads). Always pass
 * `badgeLabel` to name what is being counted; the pill itself is decorative,
 * and "Save changes, 3" tells a screen-reader user nothing.
 */
export function Button({
  variant = 'filled',
  icon,
  iconRight,
  full = false,
  loading = false,
  disabled = false,
  badge,
  badgeLabel,
  ariaLabel,
  type = 'button',
  className = '',
  onClick,
  children,
}) {
  const cls = `odc-btn ${variant}${full ? ' full' : ''}${loading ? ' loading' : ''}${className ? ' ' + className : ''}`;
  return (
    <button
      type={type}
      className={cls}
      disabled={disabled || loading}
      aria-label={ariaLabel}
      aria-busy={loading || undefined}
      onClick={onClick}
    >
      {icon ? <span className="material-icons" aria-hidden="true">{icon}</span> : null}
      {children ? <span>{children}</span> : null}
      {badge ? <span className="odc-btn-badge" aria-hidden="true">{badge}</span> : null}
      {badge ? <span className="odc-sr-only">{`, ${badge} ${badgeLabel || 'pending'}`}</span> : null}
      {iconRight ? <span className="material-icons" aria-hidden="true">{iconRight}</span> : null}
      {loading ? <span className="odc-btn-spin" aria-hidden="true" /> : null}
    </button>
  );
}
