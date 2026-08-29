/**
 * Odyssey DS — ContactTypeMultiSelect
 * The ledger-header filter pre-wired to the ContactType vocabulary: a
 * checkbox-list popover whose rows each carry the type's Material icon in its
 * category color, with a count badge on the trigger. A thin, domain-typed
 * wrapper over the base `MultiSelect` — `value` (array of enum keys) + `onChange`
 * pass straight through, as do `icon` and `align`.
 *
 * Defaults: trigger label "Any type", trigger glyph `store`. Pairs with the
 * Contacts page type filter. The type registry is the same canonical
 * `CONTACT_TYPES` exported by ContactTypeSelect; read off the DS
 * namespace at render time (bundle components can't import each other).
 */

export function ContactTypeMultiSelect({
  value = [],
  onChange,
  label = 'Any type',
  icon = 'store',
  align,
  types,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.CONTACT_TYPES || [];
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
