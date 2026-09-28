/**
 * Odyssey DS — FileTypeMultiSelect
 * The Files-list filter for document type: a checkbox-list popover whose rows
 * each carry the type's Material icon in its category color, with a count badge
 * on the trigger. `kind` picks the vocabulary (account · transaction ·
 * taxStatement · property) from FILE_TYPE_REGISTRIES, exported by FileTypeSelect
 * and read off the DS namespace at render time (bundle components can't import
 * each other).
 *
 * Defaults: trigger label "Any type"; trigger glyph per kind (folder ·
 * receipt_long · request_quote · home_work). `value` (array of enum keys),
 * `onChange`, `icon`, `align` and `types` pass straight through.
 *
 * Replaces AccountFileTypeMultiSelect · TransactionFileTypeMultiSelect ·
 * TaxStatementFileTypeMultiSelect · PropertyFileTypeMultiSelect.
 */
export function FileTypeMultiSelect({ kind = 'account', value = [], onChange, label = 'Any type', icon, align, types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  if (!RegistryMultiSelect) return null;
  const regs = NS.FILE_TYPE_REGISTRIES || {};
  const reg = regs[kind] || regs.account || { types: [], filterIcon: 'folder' };
  return (
    <RegistryMultiSelect
      value={value}
      onChange={onChange}
      label={label}
      icon={icon || reg.filterIcon}
      align={align}
      types={types || reg.types}
      {...rest}
    />
  );
}
