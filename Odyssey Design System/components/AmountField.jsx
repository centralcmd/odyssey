/**
 * Odyssey DS — AmountField
 * A money / numeric input with a fixed adornment (currency symbol, %, unit).
 * Consolidates the hand-rolled money inputs scattered across the kit
 * (`.trm-value`, `.est-value`, `.atm-amount`, the local `MoneyField` in
 * AddRenewalModal) into one labelled control with consistent error + helper
 * states, built on the same `.odc-field` shell as `Field`.
 *
 * The adornment sits inside the box: `prefix` on the left (e.g. "$"), `suffix`
 * on the right (e.g. "%"). The numeric text is monospaced + tabular so digits
 * line up. Two sizes: default (data-entry rows) and `lg` (a hero amount input,
 * patterned on the Estimates dialog).
 *
 * Controlled: pass `value` (string) + `onChange(value, event)`. Input is kept
 * as a string so partial entries ("3.", "1,2") aren't clobbered; characters are
 * sanitized to digits, separators and (optionally) a leading minus — parse on
 * submit. Set `allowNegative` for rates/deltas that can go below zero.
 */
export function AmountField({
  label,
  value = '',
  onChange,
  prefix,
  suffix,
  placeholder = '0.00',
  size = 'md',
  align = 'left',
  allowNegative = false,
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
  const re = allowNegative ? /[^0-9.,\-]/g : /[^0-9.,]/g;
  const handle = (e) => {
    if (!onChange) return;
    const next = e.target.value.replace(re, '');
    onChange(next, e);
  };
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const control = (
    <div className={`odc-amount${size === 'lg' ? ' lg' : ''}${error ? ' error' : ''}${disabled ? ' disabled' : ''}`}>
      {prefix ? <span className="odc-amount-adorn pre" aria-hidden="true">{prefix}</span> : null}
      <input
        id={fieldId}
        className="odc-amount-input"
        inputMode="decimal"
        type="text"
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        autoFocus={autoFocus}
        style={align === 'right' ? { textAlign: 'right' } : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={msg ? helpId : undefined}
        onChange={handle}
        {...rest}
      />
      {suffix ? <span className="odc-amount-adorn suf" aria-hidden="true">{suffix}</span> : null}
    </div>
  );
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
