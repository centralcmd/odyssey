/**
 * Odyssey DS — DateRangePicker
 * A compact, inline two-field date-range control for filter/search bars: a
 * leading icon + short caption, a start and end `DatePicker` joined by an
 * en-dash, and a clear button that appears once either end is set. It reads as
 * one pill-shaped input sitting next to `Select` / `MultiSelect` in a toolbar
 * — the "Taken From – To" range in the photo library, or any "between two
 * dates" filter (due, created, effective).
 *
 * Controlled: pass `value={{ from, to }}` (ISO `YYYY-MM-DD` strings, either null
 * for open-ended) + `onChange(next)`, which fires the whole `{ from, to }` on
 * every edit (and `{ from: null, to: null }` on clear). By default the range
 * stays ordered — the start field caps at `to` and the end field floors at
 * `from` — so a crossed range can't be selected; pass `clamp={false}` to allow
 * independent ends. `min` / `max` bound both ends.
 *
 * Each end is a full `DatePicker`, so it keeps the body-portaled calendar,
 * keyboard grid and flip-above behaviour — the wrapper only supplies the shared
 * shell, caption and clear affordance.
 */
export function DateRangePicker({
  value,
  onChange,
  label,
  icon = 'event',
  fromPlaceholder = 'From',
  toPlaceholder = 'To',
  min,
  max,
  clamp = true,
  align = 'start',
  ariaLabel = 'Filter by date range',
  id,
  className = '',
  style,
}) {
  const autoId = React.useId();
  const rootId = id || autoId;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const DatePicker = NS.DatePicker;

  const from = (value && value.from) || null;
  const to = (value && value.to) || null;
  const emit = (next) => { if (onChange) onChange(next); };

  // Ordered range: start can't exceed `to`, end can't precede `from`.
  const fromMax = clamp && to ? to : max;
  const toMin = clamp && from ? from : min;

  const control = DatePicker ? (
    <React.Fragment>
      <DatePicker id={`${rootId}-from`} value={from} placeholder={fromPlaceholder}
        onChange={(v) => emit({ from: v || null, to })} min={min} max={fromMax} align={align} />
      <span className="odc-dpr-dash" aria-hidden="true">–</span>
      <DatePicker id={`${rootId}-to`} value={to} placeholder={toPlaceholder}
        onChange={(v) => emit({ from, to: v || null })} min={toMin} max={max} align={align} />
    </React.Fragment>
  ) : (
    // DatePicker should always be in the bundle; fall back to native inputs if a
    // partial build lags a turn behind, so the range still works.
    <React.Fragment>
      <input className="odc-input" type="date" value={from || ''} aria-label={fromPlaceholder}
        min={min} max={fromMax} onChange={(e) => emit({ from: e.target.value || null, to })} />
      <span className="odc-dpr-dash" aria-hidden="true">–</span>
      <input className="odc-input" type="date" value={to || ''} aria-label={toPlaceholder}
        min={toMin} max={max} onChange={(e) => emit({ from, to: e.target.value || null })} />
    </React.Fragment>
  );

  return (
    <div className={`odc-dpr${className ? ' ' + className : ''}`} role="group" aria-label={ariaLabel} style={style}>
      {icon ? <span className="material-icons odc-dpr-ic" aria-hidden="true">{icon}</span> : null}
      {label ? <span className="odc-dpr-lab">{label}</span> : null}
      {control}
      {(from || to)
        ? (
          <button type="button" className="odc-dpr-clear" aria-label="Clear date range"
            onClick={() => emit({ from: null, to: null })}>
            <span className="material-icons" aria-hidden="true">close</span>
          </button>
        )
        : null}
    </div>
  );
}
