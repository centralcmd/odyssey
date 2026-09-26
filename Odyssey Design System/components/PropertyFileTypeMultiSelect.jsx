
/**
 * Odyssey DS — PropertyFileTypeMultiSelect
 * A checkbox-list filter pre-wired to the PropertyFileType vocabulary: each row
 * carries its Material icon in its category color, with a count badge on the
 * trigger. A thin wrapper over `RegistryMultiSelect` — `value` (array of enum
 * keys) + `onChange` pass straight through, as do `icon` and `align`.
 *
 * Defaults: trigger label "Any type", trigger glyph `home_work`. The registry is
 * the canonical `PROPERTY_FILE_TYPES` exported by PropertyFileTypeSelect, read
 * off the DS namespace at render time (bundle components can't import each other).
 */

export function PropertyFileTypeMultiSelect({
  value = [],
  onChange,
  label = 'Any type',
  icon = 'home_work',
  align,
  types,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.PROPERTY_FILE_TYPES || [];
  if (!RegistryMultiSelect) return null;
  return (
    <RegistryMultiSelect value={value} onChange={onChange} label={label} icon={icon} align={align} types={registry} {...rest} />
  );
}
