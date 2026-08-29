/**
 * Odyssey DS — Drawer & NavItem
 * The app's only chrome surface: a 240px left rail holding the brand lockup,
 * the primary nav, and a footer group (Preferences / User Account / About).
 * There is no top app bar — the drawer carries everything. Maps to MudDrawer +
 * MudNavMenu / MudNavLink.
 *
 * `Drawer` is the container: pass `brand` (the lockup node) and `footer` (the
 * footer nav group); nav items are its children. `NavItem` is one row — an
 * icon + label with an `active` state (tide tint + brand text), rendered as an
 * <a> when `href` is set, otherwise a <button>. `badge` shows a trailing count.
 * Group children with `Drawer.Section` (an uppercase label).
 */
export function Drawer({ brand, footer, children, ariaLabel = 'Primary' }) {
  return (
    <nav className="odc-drawer" aria-label={ariaLabel}>
      {brand ? <div className="odc-drawer-brand">{brand}</div> : null}
      <div className="odc-drawer-nav">{children}</div>
      {footer ? <div className="odc-drawer-foot">{footer}</div> : null}
    </nav>
  );
}

function DrawerSection({ children }) {
  return <div className="odc-drawer-section">{children}</div>;
}
Drawer.Section = DrawerSection;

export function NavItem({ icon, label, active = false, href, onClick, badge, ariaLabel }) {
  const cls = `odc-navitem${active ? ' active' : ''}`;
  const inner = (
    <>
      {icon ? <span className="material-icons" aria-hidden="true">{icon}</span> : null}
      <span className="odc-navitem-label">{label}</span>
      {badge != null ? <span className="odc-badge neutral">{badge}</span> : null}
    </>
  );
  if (href) {
    return (
      <a className={cls} href={href} aria-current={active ? 'page' : undefined} aria-label={ariaLabel} onClick={onClick}>
        {inner}
      </a>
    );
  }
  return (
    <button type="button" className={cls} aria-current={active ? 'page' : undefined} aria-label={ariaLabel} onClick={onClick}>
      {inner}
    </button>
  );
}
