
/**
 * Odyssey DS — TaxStatementFileTypeMultiSelect
 * A checkbox-list filter pre-wired to the TaxStatementFileType vocabulary
 * (TaxReturn · TaxAssessment · SupportingDocument · Other): each row carries its
 * Material icon in its category color, with a count badge on the trigger. A thin
 * wrapper over `MultiSelect` — `value` (array of enum keys) + `onChange` pass
 * straight through, as do `icon` and `align`.
 *
 * Defaults: trigger label "Any type", trigger glyph `request_quote`. The registry
 * is the same canonical `TAX_STATEMENT_FILE_TYPES` exported by
 * TaxStatementFileTypeSelect; read off the DS namespace at render time (bundle
 * components can't import each other).
 */

export function TaxStatementFileTypeMultiSelect({
  value = [],
  onChange,
  label = 'Any type',
  icon = 'request_quote',
  align,
  types,
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.TAX_STATEMENT_FILE_TYPES || [];
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
