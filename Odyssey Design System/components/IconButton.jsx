/**
 * Odyssey DS — IconButton
 * The bare icon-only button used for the modal close, table row actions, and
 * the Menu trigger — promoted to a typed component so consumers get the
 * focus ring, disabled handling, and required accessible name for free.
 * Maps to a MudIconButton.
 *
 * `ariaLabel` is REQUIRED (the button has no visible text). Renders as an
 * <a> when `href` is set. `danger` tints it for destructive actions; `size`
 * is sm (28) / md (36, default) / lg (44 — the 44px min touch target).
 */
export function IconButton({
  icon,
  ariaLabel,
  size = 'md',
  danger = false,
  href,
  type = 'button',
  disabled = false,
  onClick,
  ...rest
}) {
  const cls = `odc-iconbtn${size !== 'md' ? ' ' + size : ''}${danger ? ' danger' : ''}`;
  const glyph = <span className="material-icons" aria-hidden="true">{icon}</span>;
  if (href && !disabled) {
    return (
      <a className={cls} href={href} aria-label={ariaLabel} onClick={onClick} {...rest}>
        {glyph}
      </a>
    );
  }
  return (
    <button
      type={type}
      className={cls}
      aria-label={ariaLabel}
      disabled={disabled}
      onClick={onClick}
      {...rest}
    >
      {glyph}
    </button>
  );
}
