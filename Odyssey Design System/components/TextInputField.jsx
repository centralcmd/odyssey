/**
 * Odyssey DS — TextInputField
 * A labelled single-line text input built on a native `<input type="text">`
 * inside `FieldShell` — the same shape as `NumberField`, so a text setting and
 * a numeric setting read identically in a row.
 *
 * Use `TextInputField` when the control must point `aria-labelledby` /
 * `aria-describedby` at elements it does not own — a `SettingRow` title and
 * description, a table cell header, an inline-edit row. Because the input is
 * native and rendered by this component, those attributes land on the input
 * itself rather than travelling through a wrapper's attribute splat.
 *
 * Use `Field` for ordinary dialog/form entry (it maps to a MudTextField and
 * carries the leading-icon, clearable, multiline and password affordances).
 * Use `SearchField` for filter and page-search boxes.
 *
 * Emits `onChange(value, event)` — the next string first, the native event
 * second. `maxLength` also renders a live counter in the shell's `aside` slot
 * so the ceiling is visible before it is hit.
 */
export function TextInputField({
  label,
  value = '',
  onChange,
  placeholder,
  maxLength,
  showCount = false,
  inputMode,
  autoComplete,
  spellCheck,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  autoFocus = false,
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
  // External aria-describedby (e.g. a row-owned description) is APPENDED to the
  // internal help/error id(s) — never replacing them. When both help and error
  // render (two FieldShell nodes), the input points at both.
  const describedBy = [
    ariaDescribedBy,
    help ? helpId : null,
    error ? (help ? errId : helpId) : null,
  ].filter(Boolean).join(' ') || undefined;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const handle = (e) => { if (onChange) onChange(e.target.value, e); };
  const control = (
    <input
      id={fieldId}
      className="odc-input"
      type="text"
      value={value == null ? '' : value}
      placeholder={placeholder}
      maxLength={maxLength}
      inputMode={inputMode}
      autoComplete={autoComplete}
      spellCheck={spellCheck}
      disabled={disabled}
      autoFocus={autoFocus}
      required={required}
      aria-invalid={error ? true : undefined}
      aria-labelledby={ariaLabelledBy}
      aria-describedby={describedBy}
      onChange={handle}
      {...rest}
    />
  );
  const counter = (showCount && maxLength)
    ? <span className="odc-field-count">{(value || '').length}/{maxLength}</span>
    : null;
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
