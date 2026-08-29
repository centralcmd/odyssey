/**
 * Odyssey DS — PolicyFileTypeMultiSelect
 * The policy/renewal document filter pre-wired to the PolicyFileType vocabulary:
 * a checkbox-list popover whose rows each carry the type's Material icon in its
 * category color, with a count badge on the trigger. A thin wrapper over
 * `MultiSelect` — `value` (array of enum keys) + `onChange` pass straight through.
 *
 * Defaults: trigger label "Any type", trigger glyph `shield`. The registry is the
 * canonical `POLICY_FILE_TYPES` exported by PolicyFileTypeSelect; read off the DS
 * namespace at render time (bundle components can't import each other).
 */

export function PolicyFileTypeMultiSelect({
  value = [],
  onChange,
  label = 'Any type',
  icon = 'shield',
  align,
  types,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.POLICY_FILE_TYPES || [];
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
