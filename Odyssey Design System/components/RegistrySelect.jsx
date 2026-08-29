/**
 * Odyssey DS — RegistrySelect
 * The shared engine every domain single-select delegates to. Give it a registry
 * (`types`: an array of { key|value, label, icon, color } rows) and it renders the
 * themed `TypeSelect` popover — colored category glyph, label, far-right check,
 * optional `groups`. Falls back to the base `Select` if TypeSelect isn't on the
 * namespace yet. Every other prop (value, onChange, label, placeholder, help,
 * error, required, disabled, id, …) passes straight through.
 *
 * Don't reach for this in product code — use the domain wrapper (AccountFileType-
 * Select, InsurancePolicyTypeSelect, …), each of which feeds its canonical
 * registry in. Reads the base control off the DS namespace at render time (bundle
 * components can't import each other).
 */
export function RegistrySelect({ types = [], placeholder = 'Select type…', ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { TypeSelect, Select } = NS;
  if (TypeSelect) {
    return <TypeSelect types={types} placeholder={placeholder} {...rest} />;
  }
  if (Select) {
    const options = types.map((t) => ({
      value: t.key != null ? t.key : t.value,
      label: t.label,
      icon: t.icon,
      iconColor: t.color != null ? t.color : t.iconColor,
    }));
    return <Select options={options} placeholder={placeholder} {...rest} />;
  }
  return null;
}
