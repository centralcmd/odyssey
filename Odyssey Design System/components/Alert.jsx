/**
 * Odyssey DS — Alert
 * Block-level notification (vs. the inline Chip). severity sets color +
 * icon: info · success · warning · error. Keep copy terse, sentence case.
 * Styled by .odc-alert.
 */
const ALERT_ICON = { info: 'info', success: 'check_circle', warning: 'warning', error: 'error' };

export function Alert({ severity = 'info', children }) {
  return (
    <div className={`odc-alert ${severity}`} role={severity === 'error' ? 'alert' : 'status'}>
      <span className="material-icons" aria-hidden="true">{ALERT_ICON[severity] || 'info'}</span>
      <div className="odc-alert-body">{children}</div>
    </div>
  );
}
