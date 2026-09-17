/**
 * Odyssey DS — RowActions
 * The hover-revealed cluster of icon buttons that sits at the end of a table
 * row or list item (edit / delete / anything else). Promoted from the
 * near-identical `.trm-rowbtns` / `.est-rowbtns` / `.ua-row-actions` blocks
 * that each surface had reimplemented.
 *
 * Reveal: hidden until the containing <tr> (or any `.odc-rowactions-host`
 * element) is hovered or focused within — and always visible on touch
 * devices, where there is no hover. `reveal={false}` pins it visible.
 *
 * Bundle components can't import each other, so IconButton is read off the DS
 * namespace at render time (the same way the kit consumes every atom).
 */
export function RowActions({
  actions = [],
  size = 'sm',
  reveal = true,
  className = '',
  children,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { IconButton } = NS;
  if (!IconButton) return null;
  const cls = `odc-rowactions${reveal ? ' reveal' : ''}${className ? ' ' + className : ''}`;
  return (
    <span className={cls} {...rest}>
      {actions.map((a, i) => (
        <IconButton
          key={a.key || a.icon || i}
          icon={a.icon}
          ariaLabel={a.label}
          danger={!!a.danger}
          disabled={!!a.disabled}
          size={a.size || size}
          onClick={a.onClick}
        />
      ))}
      {children}
    </span>
  );
}
