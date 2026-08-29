/**
 * Odyssey DS — TransactionFileTypeMultiSelect
 * A checkbox-list filter pre-wired to the TransactionFileType vocabulary (Receipt ·
 * Invoice · Other): each row carries its Material icon in its category color, with a
 * count badge on the trigger. A thin wrapper over `MultiSelect` — `value` (array of
 * enum keys) + `onChange` pass straight through, as do `icon` and `align`.
 *
 * Defaults: trigger label "Any type", trigger glyph `receipt_long`. The registry is
 * the same canonical `TRANSACTION_FILE_TYPES` exported by TransactionFileTypeSelect;
 * read off the DS namespace at render time (bundle components can't import each other).
 */

export function TransactionFileTypeMultiSelect({
  value = [],
  onChange,
  label = 'Any type',
  icon = 'receipt_long',
  align,
  types,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.TRANSACTION_FILE_TYPES || [];
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
