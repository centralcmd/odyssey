/**
 * Odyssey DS — CardHeader
 * The titled header row at the top of a card: a `title` on the left and an
 * optional `action` cluster (buttons, a menu, a chip) on the right, over a
 * bottom divider. Pass `children` instead of `title` to render a custom heading
 * node. Sits above a `CardBody`. Styled by .odc-card-header.
 */
export function CardHeader({ title, action, className = '', children, ...rest }) {
  return (
    <div className={`odc-card-header${className ? ' ' + className : ''}`} {...rest}>
      {children ? children : <div className="odc-card-header-ttl">{title}</div>}
      {action || null}
    </div>
  );
}
