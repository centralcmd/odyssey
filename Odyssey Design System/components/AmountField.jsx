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
 *
 * A DIRECTION LEAD, the same one MoneyField carries: `direction` +
 * `onDirectionChange` (with `directionOptions` naming the two states) turn the
 * left edge into a button that flips between them, showing each option's short
 * word where a sign would be. For a record that stores a direction rather than
 * a sign — a percentage-unit fee that is money in or money out — so the value
 * and its direction stay ONE control, exactly as on the amount path.
 */
export function AmountField({
  label,
  value = '',
  onChange,
  prefix,
  suffix,
  direction,
  onDirectionChange,
  directionOptions,
  tone,
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
  // The two states the lead flips between — the finance pair by default, so a
  // caller passing only `direction` still gets a sensible − / +.
  const DIR_DEFAULT = [
    { value: 'expense', label: 'Expense', sign: '−', tone: 'expense' },
    { value: 'income', label: 'Income', sign: '+', tone: 'income' },
  ];
  const dirMode = !!(direction && onDirectionChange);
  const dirOpts = (directionOptions && directionOptions.length === 2) ? directionOptions : DIR_DEFAULT;
  const dirIdx = Math.max(0, dirOpts.findIndex((o) => o.value === direction));
  const dirOpt = dirOpts[dirIdx] || dirOpts[0];
  const dirNext = dirOpts[(dirIdx + 1) % dirOpts.length];
  const dirTone = tone || (dirMode ? dirOpt.tone : undefined);
  const control = (
    <div className={`odc-amount${size === 'lg' ? ' lg' : ''}${dirTone ? ` tone-${dirTone}` : ''}${error ? ' error' : ''}${disabled ? ' disabled' : ''}`}>
      {dirMode ? (
        <button
          type="button"
          className="odc-money-sign btn"
          disabled={disabled}
          aria-label={`${dirOpt.label} — switch to ${dirNext.label}`}
          title={`${dirOpt.label} — click to switch`}
          onClick={(e) => onDirectionChange(dirNext.value, e)}
        >
          {dirOpt.icon
            ? <span className="material-icons odc-money-dir-ic" aria-hidden="true">{dirOpt.icon}</span>
            : <span className="odc-money-dir-word" aria-hidden="true">{dirOpt.short || dirOpt.sign}</span>}
        </button>
      ) : null}
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
        </label>
      ) : null}
      {control}
      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
