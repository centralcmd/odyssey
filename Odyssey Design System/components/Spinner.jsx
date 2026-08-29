/**
 * Odyssey DS — Spinner & ProgressBar
 * The two progress primitives. `Spinner` is the circular indeterminate
 * indicator (the file-analysis "analyzing" state, page-level loads) — the
 * only sanctioned continuous motion in the system; maps to
 * MudProgressCircular Indeterminate. `ProgressBar` is the determinate fill
 * (budget planned-vs-actual bars, upload progress); maps to MudProgressLinear.
 */
export function Spinner({ size = 'md', ariaLabel = 'Loading', className = '', ...rest }) {
  const cls = `odc-spinner${size !== 'md' ? ' ' + size : ''}${className ? ' ' + className : ''}`;
  return <span className={cls} role="status" aria-label={ariaLabel} {...rest} />;
}

/**
 * ProgressBar — determinate fill. `value` 0–100 (clamped). `tone` colors the
 * fill: default (brand) · income · expense · pending. `tall` is the 10px
 * variant. Reports value to AT via role="progressbar".
 */
export function ProgressBar({ value = 0, tone, tall = false, ariaLabel, className = '' }) {
  const pct = Math.max(0, Math.min(100, value));
  const cls = `odc-progress${tone ? ' ' + tone : ''}${tall ? ' tall' : ''}${className ? ' ' + className : ''}`;
  return (
    <div
      className={cls}
      role="progressbar"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={ariaLabel}
    >
      <div className="odc-progress-fill" style={{ width: `${pct}%` }} />
    </div>
  );
}
