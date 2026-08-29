/**
 * Odyssey DS — NoteField
 * A labelled multi-line text input with a live character counter — the
 * canonical "note / description / comment" field. Consolidates the textarea +
 * counter that was hand-rolled across every create/edit dialog (the `.field` +
 * `.atm-textarea` + `.trm-charcount` / `.est-charcount` pattern in AddTermModal,
 * AddEstimateModal, AddRenewalModal, AddInsurancePolicyModal, AddContractModal,
 * the New-transaction extra-data field…).
 *
 * Built on the same `.odc-field` shell as `Field`: the label row carries the
 * optional/required marker on the left and the `0/512` counter on the right;
 * the counter turns red once `value.length` reaches `maxLength`. Pass `error`
 * to flip to the error state and replace the helper. For a single-line value
 * use `Field`; for a money/numeric value use `AmountField`.
 *
 * Controlled: pass `value` + `onChange(value, event)` — the next string value
 * first, the native event second.
 */
export function NoteField({
  label,
  value = '',
  onChange,
  placeholder,
  maxLength,
  rows = 3,
  showCount = true,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  autoFocus = false,
  className = '',
  id,
  ...rest
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const msg = error || help;
  const len = (value || '').length;
  const counted = showCount && typeof maxLength === 'number';
  const over = counted && len >= maxLength;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const counter = counted ? (
    <span className={`odc-field-count${over ? ' over' : ''}`} aria-hidden="true">{len}/{maxLength}</span>
  ) : null;
  const control = (
    <textarea
      id={fieldId}
      className="odc-input odc-input-multiline"
      rows={rows}
      value={value}
      placeholder={placeholder}
      maxLength={maxLength}
      disabled={disabled}
      autoFocus={autoFocus}
      required={required}
      aria-invalid={error ? true : undefined}
      aria-describedby={msg ? helpId : undefined}
      onChange={(e) => onChange && onChange(e.target.value, e)}
      {...rest}
    />
  );
  if (FieldShell) {
    return (
      <FieldShell label={label} htmlFor={fieldId} required={required} optional={optional}
        help={help} error={error} aside={counter} className={className}>
        {control}
      </FieldShell>
    );
  }
  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {(label || counter) ? (
        <div className="odc-field-head">
          {label ? (
            <label className="odc-field-label" htmlFor={fieldId}>
              {label}
              {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
              {optional ? <span className="odc-field-opt">Optional</span> : null}
            </label>
          ) : <span />}
          {counter}
        </div>
      ) : null}
      {control}
      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
