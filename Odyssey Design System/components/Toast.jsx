/**
 * Odyssey DS — Toast (Snackbar)
 * Terse, transient confirmation — bottom-right, matching the system's quiet
 * success voice ("Saved." · "Approved 3 transactions."). Maps to MudSnackbar.
 * Presentational + self-dismissing: render it from your own queue inside a
 * <ToastStack>. Pass `duration` (ms) to auto-close; `severity` tints the
 * leading icon (default = no icon, just text). Errors get role="alert".
 *
 * a11y: the live-region container mounts first and the message text fills in
 * a frame later, so screen readers reliably announce it (a region inserted
 * *with* its content is often skipped). Auto-dismiss pauses while the toast
 * is hovered or holds focus (WCAG 2.2.1), and action-bearing toasts get a
 * minimum 8s so the action stays reachable.
 */
export function Toast({
  message,
  severity = 'default',
  action,
  onClose,
  duration,
  icon,
}) {
  // Fill the live region one frame after mount so AT announces it.
  const [live, setLive] = React.useState(false);
  React.useEffect(() => {
    const raf = requestAnimationFrame(() => setLive(true));
    return () => cancelAnimationFrame(raf);
  }, []);

  // Auto-dismiss with pause-on-hover/focus. `remaining` survives pauses.
  const [paused, setPaused] = React.useState(false);
  const remaining = React.useRef(
    duration ? (action ? Math.max(duration, 8000) : duration) : null,
  );
  React.useEffect(() => {
    if (remaining.current == null || !onClose || paused) return undefined;
    const startedAt = Date.now();
    const t = setTimeout(onClose, remaining.current);
    return () => {
      clearTimeout(t);
      remaining.current = Math.max(250, remaining.current - (Date.now() - startedAt));
    };
  }, [paused, onClose]);

  const glyph =
    icon ||
    { success: 'check_circle', warning: 'warning', error: 'error', info: 'info' }[severity];

  return (
    <div
      className={`odc-toast ${severity}`}
      role={severity === 'error' ? 'alert' : 'status'}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocusCapture={() => setPaused(true)}
      onBlurCapture={() => setPaused(false)}
    >
      {glyph ? <span className="material-icons odc-toast-ic" aria-hidden="true">{glyph}</span> : null}
      <div className="odc-toast-body">{live ? message : null}</div>
      {action ? (
        <button type="button" className="odc-toast-action" onClick={action.onClick}>{action.label}</button>
      ) : null}
      {onClose ? (
        <button type="button" className="odc-toast-close" aria-label="Dismiss" onClick={onClose}>
          <span className="material-icons" aria-hidden="true">close</span>
        </button>
      ) : null}
    </div>
  );
}

/**
 * ToastStack — fixed positioner for live toasts. Defaults to bottom-right
 * (the system's toast corner). Render your active <Toast>s as children.
 *
 * No live region here: each Toast announces itself (role="status", or
 * role="alert" for errors), so wrapping them in another aria-live region
 * would double-announce on some screen readers. The stack is just a labelled
 * region for landmark navigation.
 */
export function ToastStack({ align = 'end', children }) {
  return (
    <div className={`odc-toast-stack ${align}`} role="region" aria-label="Notifications">
      {children}
    </div>
  );
}
