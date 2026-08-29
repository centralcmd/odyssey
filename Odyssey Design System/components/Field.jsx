/**
 * Odyssey DS — Field
 * A labelled text input: label (+ optional required mark), the control,
 * and a helper / error line. Maps to a MudTextField. Styled by .odc-field.
 * Pass `error` (string) to flip the field to its error state and show the
 * message in the help slot. `icon` renders a leading Material Icons glyph.
 *
 * Composes `FieldShell` (read off the DS namespace at render) for the label /
 * optional-marker / helper-error chrome, so that treatment stays identical
 * across every field. Falls back to inline chrome if the shell isn't loaded.
 *
 * Controlled: pass `value` + `onChange(value, event)` — the next string value
 * first (consistent with every other Odyssey control), the native event
 * second if you need it.
 *
 * Reach for `TextInputField` instead when the control has to point
 * `aria-labelledby` / `aria-describedby` at elements it doesn't own — a
 * `SettingRow` title and description, a table header, an inline edit row —
 * since those attributes reach this component's inner input only through the
 * MudBlazor attribute splat. `SearchField` for filter and page-search boxes.
 */
export function Field({
  label,
  value,
  onChange,
  placeholder,
  type = 'text',
  multiline = false,
  rows = 3,
  icon,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  clearable = false,
  className = '',
  id,
  ...rest
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const msg = error || help;
  const [revealed, setRevealed] = React.useState(false);
  const isPassword = type === 'password';
  const inputType = isPassword && revealed ? 'text' : type;
  const showClear = clearable && value && !isPassword;
  const showReveal = isPassword && !multiline;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;
  const control = (
    <div className={`odc-input-wrap${showClear ? ' has-clear' : ''}`}>
      {icon ? <span className="material-icons odc-input-icon" aria-hidden="true">{icon}</span> : null}
      {multiline ? (
        <textarea
          id={fieldId}
          className={`odc-input odc-input-multiline${icon ? ' has-icon' : ''}`}
          rows={rows}
          value={value}
          placeholder={placeholder}
          disabled={disabled}
          required={required}
          aria-invalid={error ? true : undefined}
          aria-describedby={msg ? helpId : undefined}
          onChange={(e) => onChange && onChange(e.target.value, e)}
          {...rest}
        />
      ) : (
      <input
        id={fieldId}
        className={`odc-input${icon ? ' has-icon' : ''}${showClear || showReveal ? ' has-clear' : ''}`}
        type={inputType}
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={msg ? helpId : undefined}
        onChange={(e) => onChange && onChange(e.target.value, e)}
        {...rest}
      />
      )}
      {!multiline && showClear ? (
        <button type="button" className="odc-input-clear" aria-label="Clear" onClick={() => onChange && onChange('')}>
          <span className="material-icons" aria-hidden="true">close</span>
        </button>
      ) : null}
      {showReveal ? (
        <button type="button" className="odc-input-clear odc-input-reveal" aria-label={revealed ? 'Hide password' : 'Show password'} aria-pressed={revealed} onClick={() => setRevealed((v) => !v)}>
          <span className="material-icons" aria-hidden="true">{revealed ? 'visibility_off' : 'visibility'}</span>
        </button>
      ) : null}
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
  // Fallback: inline chrome if the shell hasn't loaded.
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
