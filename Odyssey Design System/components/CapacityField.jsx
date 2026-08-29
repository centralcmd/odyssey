/**
 * Odyssey DS — CapacityField
 * A capacity limit control: a right-aligned numeric input paired with a
 * "No limit" switch. The control behind the count caps on the System settings
 * import/export groups, where a limit is either a finite number or explicitly
 * "no limit" (unbounded). Composes the DS `NumberField` and `Switch`.
 *
 * Tri-state, page-owned draft: the caller keeps BOTH `unlimited` and `value`.
 * Toggling "No limit" on disables the input but RETAINS the entered number
 * (so toggling back off is not a data-losing action); the number is simply not
 * sent while `unlimited` is true. Emits `onValueChange(number|null)` and
 * `onUnlimitedChange(bool)` separately.
 *
 * ## `variant`
 * `stacked` (default) is the SettingRow form: the input over a "No limit"
 * switch, right-aligned in the row's control column.
 *
 * `inline` is the SettingField form — one line inside a notched outline: the
 * value, then a pill at the trailing edge carrying the **inverse action**
 * ("No limit" when a number is set, "Set a limit" when it is unlimited). The
 * pill never repeats the words already showing as the value, so the pressed
 * state reads as a state rather than a stutter. There is no room for a switch
 * plus its own label inside the frame, and no second line to put them on.
 *
 * Labelling: the number input is labelled by the row title (`ariaLabelledBy`)
 * and described by the row description (`ariaDescribedBy`) — the hint text
 * lives in the row, never here. The switch carries its own composed
 * `aria-label` (e.g. "Maximum contacts per import — no limit") since it has no
 * visible text label of its own.
 */
export function CapacityField({
  value = null,
  onValueChange,
  unlimited = false,
  onUnlimitedChange,
  label,
  ariaLabelledBy,
  ariaDescribedBy,
  error,
  min = 1,
  max = 1000000,
  disabled = false,
  variant = 'stacked',
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const NumberField = NS.NumberField;
  const Switch = NS.Switch;
  const numDisabled = disabled || unlimited;
  if (variant === 'inline') {
    return (
      <div className={`odc-capacity inline${className ? ' ' + className : ''}`}>
        {unlimited ? (
          <span className="odc-capacity-value">No limit</span>
        ) : NumberField ? (
          <NumberField
            className="odc-capacity-num"
            value={value}
            min={min}
            max={max}
            step={1}
            align="right"
            disabled={disabled}
            error={error || undefined}
            ariaLabelledBy={ariaLabelledBy}
            ariaDescribedBy={ariaDescribedBy}
            onChange={(v) => onValueChange && onValueChange(v)}
          />
        ) : null}
        <button
          type="button"
          className="odc-capacity-pill"
          disabled={disabled}
          aria-pressed={unlimited}
          onClick={() => onUnlimitedChange && onUnlimitedChange(!unlimited)}
        >
          {unlimited ? 'Set a limit' : 'No limit'}
        </button>
      </div>
    );
  }
  return (
    <div className={`odc-capacity${className ? ' ' + className : ''}`}>
      {unlimited ? (
        <div className="odc-capacity-nolimit" aria-hidden={disabled ? undefined : 'false'}>No limit</div>
      ) : NumberField ? (
        <NumberField
          className="odc-capacity-num"
          value={value}
          min={min}
          max={max}
          step={1}
          align="right"
          disabled={disabled}
          error={error || undefined}
          ariaLabelledBy={ariaLabelledBy}
          ariaDescribedBy={ariaDescribedBy}
          onChange={(v) => onValueChange && onValueChange(v)}
        />
      ) : null}
      <label className={`odc-capacity-toggle${disabled ? ' disabled' : ''}`}>
        <span className="odc-capacity-toggle-lbl">No limit</span>
        {Switch ? (
          <Switch
            checked={unlimited}
            disabled={disabled}
            aria-label={`${label || 'Limit'} — no limit`}
            onChange={(c) => onUnlimitedChange && onUnlimitedChange(c)}
          />
        ) : null}
      </label>
    </div>
  );
}
