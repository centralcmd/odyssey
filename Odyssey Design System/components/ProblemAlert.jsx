/**
 * Odyssey DS — ProblemAlert
 * The fix-it block for a data condition that needs the user's attention — the
 * expanded-detail half of the problem/signal system (the PageHeader `signal`
 * rollup + a row severity Chip are the other two). A severity icon + title on
 * one row (with an optional fix CTA pinned right), then the detail below.
 *
 * `severity`: warning (amber) · error (coral) · info (sea). Pass `actionLabel`
 * + `onAction` for the fix (routes to where it's resolved). Self-contained —
 * the severity glyph is inlined (matches SeverityIcon). Styled by .odc-problem.
 */
function ProblemGlyph({ severity, size = 20 }) {
  if (severity === 'warning') {
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="currentColor"
        aria-hidden="true" style={{ flex: 'none' }}>
        <path d="M11.13 3.66 1.73 19.5a1 1 0 0 0 .87 1.5h18.8a1 1 0 0 0 .87-1.5L12.87 3.66a1 1 0 0 0-1.74 0Zm.87 4.59a1.05 1.05 0 0 1 1.05 1.13l-.33 4.7a.72.72 0 0 1-1.44 0l-.33-4.7A1.05 1.05 0 0 1 12 8.25Zm0 8.0a1.12 1.12 0 1 1 0 2.25 1.12 1.12 0 0 1 0-2.25Z" />
      </svg>
    );
  }
  return (
    <span className="material-icons" aria-hidden="true" style={{ fontSize: size, flex: 'none' }}>
      {severity === 'error' ? 'error_outline' : 'info_outline'}
    </span>
  );
}

export function ProblemAlert({
  severity = 'warning',
  title,
  detail,
  actionLabel,
  actionIcon = 'arrow_forward',
  onAction,
  className = '',
  children,
}) {
  return (
    <div className={`odc-problem ${severity}${className ? ' ' + className : ''}`}
      role={severity === 'error' ? 'alert' : 'status'}>
      <div className="odc-problem-head">
        <ProblemGlyph severity={severity} size={20} />
        {title ? <div className="odc-problem-title">{title}</div> : null}
        {actionLabel ? (
          <button type="button" className="odc-problem-cta" onClick={onAction}>
            {actionLabel}
            <span className="material-icons" aria-hidden="true">{actionIcon}</span>
          </button>
        ) : null}
      </div>
      {detail ? <p className="odc-problem-detail">{detail}</p> : null}
      {children}
    </div>
  );
}
