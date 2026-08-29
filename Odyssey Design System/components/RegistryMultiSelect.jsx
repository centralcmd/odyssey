/**
 * Odyssey DS — RegistryMultiSelect
 * The shared engine every domain checkbox-list filter delegates to. Give it a
 * registry (`types`: an array of { key, label, icon, color } rows) and it maps
 * each row to a `MultiSelect` option (icon in its category color) and renders the
 * trigger + count badge. Every other prop (value, onChange, label, icon, align, …)
 * passes straight through.
 *
 * Don't reach for this in product code — use the domain wrapper (AccountFileType-
 * MultiSelect, ContactTypeMultiSelect, …), each of which feeds its canonical
 * registry in. Reads `MultiSelect` off the DS namespace at render time (bundle
 * components can't import each other).
 */
export function RegistryMultiSelect({ types = [], ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { MultiSelect } = NS;
  if (!MultiSelect) return null;
  const options = types.map((t) => ({
    value: t.key, label: t.label, icon: t.icon, iconColor: t.color,
  }));
  return <MultiSelect options={options} {...rest} />;
}
