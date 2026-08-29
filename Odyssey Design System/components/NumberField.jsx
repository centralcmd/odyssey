/**
 * Odyssey DS — NumberField
 * A labelled numeric input for plain quantities — counts, years, declared
 * figures — where there's no currency symbol to show (for money use
 * `AmountField`). Native `type="number"`, so it gets the platform stepper and
 * numeric keyboard. Consolidates the `ATS_NumField` / `TS_NumField` helpers that
 * were duplicated verbatim in the tax-statement dialogs.
 *
 * Emits a parsed **number, or `null`** when cleared — `onChange(value, event)` —
 * matching the figures it feeds (so it drops straight into the existing tax
 * draft state). Composes `FieldShell` for the label / helper / error chrome.
 *
 * `unit` renders a static unit inside the input's trailing edge (`%`, `MB`,
 * `days`). Prefer it to a helper line whenever the number is meaningless
 * without its unit: the unit stays visible while the helper slot is taken by an
 * error, and it is appended to `aria-describedby` so it is announced too.
 *
 * **Fractions are entered as whole percents.** A 0.0–1.0 stored value gets
 * `min={0} max={100} step={1} unit="%"`; the page multiplies by 100 for
 * display and divides by 100 on emit. Never expose the raw fraction with
 * `step={0.01}` — "0.62" gives the reader no clue it is a proportion, and the
 * two-decimal stepper is a fiddly target.
 */
export function NumberField({
  label,
  value,
  onChange,
  placeholder = '—',
  min,
  max,
  step,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  autoFocus = false,
  align = 'left',
  unit,
  className = '',
  id,
  ariaLabelledBy,
  ariaDescribedBy,
  ...rest
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const errId = `${helpId}-error`;
  const msg = error || help;
  // External aria-describedby (e.g. a row-owned hint) is APPENDED to the
  // internal help/error id(s) — never replacing them. When both help and error
  // render (two FieldShell nodes), the input points at both.
  const unitId = `${fieldId}-unit`;
  const describedBy = [
    ariaDescribedBy,
    unit ? unitId : null,
    help ? helpId : null,
    error ? (help ? errId : helpId) : null,
  ].filter(Boolean).join(' ') || undefined;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const handle = (e) => {
    if (!onChange) return;
    const raw = e.target.value;
    onChange(raw === '' ? null : parseFloat(raw), e);
  };
  const input = (
    <input
      id={fieldId}
      className={`odc-input${unit ? ' has-unit' : ''}`}
      type="number"
      value={value == null ? '' : value}
      placeholder={placeholder}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      autoFocus={autoFocus}
      required={required}
      style={align === 'right' ? { textAlign: 'right' } : undefined}
      aria-invalid={error ? true : undefined}
      aria-labelledby={ariaLabelledBy}
      aria-describedby={describedBy}
      onChange={handle}
      {...rest}
    />
  );
  const control = unit ? (
    <div className="odc-input-wrap">
      {input}
      <span className="odc-input-unit" id={unitId}>{unit}</span>
    </div>
  ) : input;
  if (FieldShell) {
    return (
      <FieldShell label={label} htmlFor={fieldId} required={required} optional={optional}
        help={help} error={error} className={className}>
        {control}
      </FieldShell>
    );
  }
  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {label ? (
        <label className="odc-field-label" htmlFor={fieldId}>
          {label}
          {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
          {optional ? <span className="odc-field-opt">Optional</span> : null}
        </label>
      ) : null}
      {control}
      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
