/**
 * Odyssey DS — Checkbox
 * Multi-select boolean — row-select in the Analyze-file candidate table,
 * batch-edit grids, settings lists. Maps to a MudCheckBox. A native
 * <input type="checkbox"> under a styled box; supports an indeterminate
 * (mixed) state for "select all" headers.
 *
 * Controlled: pass `checked` + `onChange(next, event)`.
 */
export function Checkbox({
  checked = false,
  indeterminate = false,
  onChange,
  label,
  disabled = false,
  id,
  ...rest
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const ref = React.useRef(null);
  const mixed = indeterminate && !checked;

  React.useEffect(() => {
    if (ref.current) ref.current.indeterminate = mixed;
  }, [mixed]);

  return (
    <label className="odc-check" htmlFor={fieldId}>
      <input
        ref={ref}
        type="checkbox"
        id={fieldId}
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange && onChange(e.target.checked, e)}
        {...rest}
      />
      <span className="odc-check-box" aria-hidden="true">
        <span className="material-icons">{mixed ? 'remove' : 'check'}</span>
      </span>
      {label ? <span className="odc-check-label">{label}</span> : null}
    </label>
  );
}
