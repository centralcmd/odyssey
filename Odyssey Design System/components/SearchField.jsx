/**
 * Odyssey DS — SearchField
 * The canonical search / filter input. A thin, intent-typed wrapper over the
 * base `Field`, pre-set for one job: a leading `search` glyph, a clear (×)
 * button once there's a value, and `type="search"` for native semantics.
 *
 * Use it for every filter-bar and page search box so the affordance reads
 * identically everywhere. The kit ships three single-line text inputs and the
 * choice between them is by job, not by looks:
 *   • `SearchField`     — filtering or searching a collection.
 *   • `Field`           — labelled data entry in a form or dialog (a
 *                         MudTextField, with icon / clearable / multiline /
 *                         password affordances).
 *   • `TextInputField`  — a native input for when the control must be labelled
 *                         or described by elements it does not own (a
 *                         `SettingRow` title, a table header, an inline edit).
 * For a search-or-create input use `Combobox`.
 *
 * Controlled: pass `value` + `onChange(value, event)` — the next string value
 * first, the native event second. `Field` is read off the DS namespace at
 * render time (bundle components can't import each other).
 */
export function SearchField({
  value = '',
  onChange,
  placeholder = 'Search…',
  ariaLabel,
  icon = 'search',
  clearable = true,
  disabled = false,
  className = '',
  id,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Field = NS.Field;
  if (!Field) return null;
  // Search boxes carry no visible <label>, so they MUST name themselves for
  // assistive tech — a placeholder is not an accessible name. Default to the
  // placeholder text (or "Search"); callers can override with `ariaLabel`.
  const name = ariaLabel || placeholder || 'Search';
  return (
    <Field
      type="search"
      icon={icon}
      value={value}
      onChange={onChange}
      placeholder={placeholder}
      aria-label={name}
      clearable={clearable}
      disabled={disabled}
      className={`odc-searchfield${className ? ' ' + className : ''}`}
      id={id}
      {...rest}
    />
  );
}
