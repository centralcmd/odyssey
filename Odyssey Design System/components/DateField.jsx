/**
 * Odyssey DS — DateField
 * The labelled single-date field: the calendar `DatePicker` wrapped in the
 * shared `FieldShell` chrome (label row with the required `*` / muted "Optional"
 * marker, and the helper / error line below). This is the date sibling of
 * `Field`, `AmountField`, `NumberField` and `NoteField` — so a date entry reads
 * and aligns identically to every other labelled control in a form, instead of
 * a bare picker or a hand-rolled `.field` + `.label` + calendar assembly.
 *
 * Controlled: pass `value` (ISO `YYYY-MM-DD` string | null) + `onChange(iso)`
 * (fires null on clear). `min` / `max` (ISO) disable out-of-range days. The
 * calendar keeps `DatePicker`'s full keyboard grid, body-portaled popover, and
 * flip-above behaviour — it just gains a label, helper and error line.
 *
 *   <DateField label="Statement date" value={date} onChange={setDate}
 *              optional help="Leave blank if unknown" />
 */
export function DateField({
  label,
  value,
  onChange,
  placeholder = 'Select date',
  min,
  max,
  align = 'start',
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  full = true,
  className = '',
  id,
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const DatePicker = NS.DatePicker;

  const control = DatePicker ? (
    <DatePicker
      id={fieldId}
      value={value}
      onChange={onChange}
      placeholder={placeholder}
      min={min}
      max={max}
      align={align}
      full={full}
      disabled={disabled}
    />
  ) : (
    // DatePicker should always be present in the bundle; render an accessible
    // stub if a partial build lags a turn behind, so the field still labels.
    <input id={fieldId} className="odc-input" type="date" value={value || ''} disabled={disabled}
      min={min} max={max}
      onChange={(e) => onChange && onChange(e.target.value || null)} />
  );

  if (FieldShell) {
    return (
      <FieldShell label={label} htmlFor={fieldId} required={required} optional={optional}
        help={help} error={error} className={className}>
        {control}
      </FieldShell>
    );
  }

  const msg = error || help;
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
