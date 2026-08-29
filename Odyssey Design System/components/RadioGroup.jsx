/**
 * Odyssey DS — RadioGroup
 * Single choice from a small, mutually-exclusive set — transaction
 * direction (Money in / Money out), status, document type. Maps to a
 * MudRadioGroup + MudRadio. Native <input type="radio"> under styled dots,
 * so arrow-key navigation and form submission work for free.
 *
 * Controlled: pass `value` + `onChange(value, event)`.
 *
 * a11y: every group needs a name. Prefer the visible `label` (renders the
 * standard field label, wired via aria-labelledby); fall back to `ariaLabel`
 * only when the surrounding UI already names the choice visually.
 */
export function RadioGroup({
  name,
  value,
  onChange,
  options = [],
  row = false,
  disabled = false,
  label,
  ariaLabel,
}) {
  const autoName = React.useId();
  const groupName = name || autoName;
  const labelId = `${autoName}-label`;
  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));

  const group = (
    <div
      className={`odc-radiogroup${row ? ' row' : ''}`}
      role="radiogroup"
      aria-labelledby={label ? labelId : undefined}
      aria-label={label ? undefined : ariaLabel}
    >
      {opts.map((o) => (
        <label className="odc-radio" key={o.value}>
          <input
            type="radio"
            name={groupName}
            value={o.value}
            checked={value === o.value}
            disabled={disabled || o.disabled}
            onChange={(e) => onChange && onChange(o.value, e)}
          />
          <span className="odc-radio-dot" aria-hidden="true" />
          <span className="odc-radio-label">{o.label}</span>
        </label>
      ))}
    </div>
  );

  if (!label) return group;
  return (
    <div className="odc-field">
      <span className="odc-field-label" id={labelId}>{label}</span>
      {group}
    </div>
  );
}
