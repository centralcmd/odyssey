/**
 * Odyssey DS — AccountFileTypeMultiSelect
 * The Files-page filter pre-wired to the AccountFileType vocabulary: a checkbox-
 * list popover whose rows each carry the type's Material icon in its category
 * color, with a count badge on the trigger. A thin wrapper over `MultiSelect` —
 * `value` (array of enum keys) + `onChange` pass straight through, as do `icon`
 * and `align`.
 *
 * Defaults: trigger label "Any type", trigger glyph `folder`. The registry is the
 * same canonical `ACCOUNT_FILE_TYPES` exported by AccountFileTypeSelect; read off
 * the DS namespace at render time (bundle components can't import each other).
 */

export function AccountFileTypeMultiSelect({
  value = [],
  onChange,
  label = 'Any type',
  icon = 'folder',
  align,
  types,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.ACCOUNT_FILE_TYPES || [];
  if (!RegistryMultiSelect) return null;
  return (
    <RegistryMultiSelect
      value={value}
      onChange={onChange}
      label={label}
      icon={icon}
      align={align}
      types={registry}
      {...rest}
    />
  );
}
