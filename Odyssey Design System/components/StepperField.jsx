/**
 * Odyssey DS — StepperField
 * ---------------------------------------------------------------------------
 * A compact integer entry paired with a trailing **unit** that auto-pluralizes
 * with the value — "every 1 week" → "every 2 weeks", "after 10 occurrences".
 * This is the "number + unit" pattern that was hand-assembled in the calendar
 * recurrence builder (`.cal-interval` = a bare `NumberField` + a `.cal-interval-unit`
 * span, repeated for the occurrence count), lifted into one control so the unit
 * label, pluralization and compact sizing stay consistent wherever a quantity
 * is entered with its unit.
 *
 * It is the count sibling of `AmountField` (whose adornment sits *inside* the
 * box for currency/%): here the unit reads as a separate word *beside* a small
 * number box, which is what a "3 weeks" quantity wants.
 *
 * Emits a parsed number, or `null` when cleared — `onChange(value)`. Give the
 * unit in its **singular** form; pass `unitPlural` when it isn't a simple `+s`.
 *
 *   <StepperField label="Repeat every" value={n} onChange={setN} unit="week" />
 */
export function StepperField({
  label,
  value,
  onChange,
  unit,
  unitPlural,
  min = 1,
  max,
  step = 1,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  autoFocus = false,
  className = '',
  id,
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;

  const n = value == null || value === '' ? null : Number(value);
  const unitLabel = unit ? (n === 1 ? unit : (unitPlural || unit + 's')) : null;

  const control = (
    <div className={`odc-stepper${disabled ? ' disabled' : ''}${error ? ' error' : ''}`}>
      <input
        id={fieldId}
        className="odc-input odc-stepper-num"
        type="number"
        value={value == null ? '' : value}
        min={min}
        max={max}
        step={step}
        disabled={disabled}
        autoFocus={autoFocus}
        aria-invalid={error ? true : undefined}
        onChange={(e) => { if (onChange) onChange(e.target.value === '' ? null : parseFloat(e.target.value), e); }}
      />
      {unitLabel ? <span className="odc-stepper-unit">{unitLabel}</span> : null}
    </div>
  );

  if (label != null || help != null || error != null) {
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
        {label ? <label className="odc-field-label" htmlFor={fieldId}>{label}{required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}{optional ? <span className="odc-field-opt">Optional</span> : null}</label> : null}
        {control}
        {msg ? <div className="odc-field-help" role={error ? 'alert' : undefined}>{msg}</div> : null}
      </div>
    );
  }
  return control;
}
