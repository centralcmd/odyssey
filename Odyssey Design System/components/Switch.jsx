/**
 * Odyssey DS — Switch
 * Binary on/off toggle. The Preferences "Dark mode" control and any instant
 * setting. Maps to a MudSwitch. A real <input type="checkbox" role="switch">
 * under a styled track, so it's keyboard- and form-native.
 *
 * Controlled: pass `checked` + `onChange(next, event)`.
 */
export function Switch({
  checked = false,
  onChange,
  label,
  disabled = false,
  id,
  ...rest
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  return (
    <label className={`odc-switch${disabled ? ' disabled' : ''}`} htmlFor={fieldId}>
      <input
        type="checkbox"
        role="switch"
        id={fieldId}
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange && onChange(e.target.checked, e)}
        {...rest}
      />
      <span className="odc-switch-track" aria-hidden="true">
        <span className="odc-switch-thumb" />
      </span>
      {label ? <span className="odc-switch-label">{label}</span> : null}
    </label>
  );
}
