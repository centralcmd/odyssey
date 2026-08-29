
/**
 * Odyssey DS — TaxStatementFileTypeSelect
 * A single-select pre-wired to the TaxStatementFileType vocabulary — the kind of
 * document attached to a *tax statement*: TaxReturn · TaxAssessment ·
 * SupportingDocument · Other. Each option renders with its Material icon in its
 * category color. A thin, domain-typed wrapper over the base `Select`; every
 * Select prop (label, help, error, required, disabled, placeholder, id,
 * className) passes straight through.
 *
 * Value is the enum key. Controlled: pass `value` + `onChange(key, event)`.
 *
 * `TAX_STATEMENT_FILE_TYPES` (exported here) is the canonical registry — name ·
 * icon · color · soft tint · enumValue — and the consumable layer's source of
 * truth for tax-statement file types. It mirrors `OdysseyData.taxStatementFileTypes`
 * and the C# `TaxStatementFileType` enum (field `FileType` on `TaxStatementFile`);
 * keep them in lockstep. For files attached to an *account* use
 * AccountFileTypeSelect; to a *transaction*, TransactionFileTypeSelect.
 *
 * Bundle components can't import each other, so this reads the base Select off
 * the DS namespace at render time (the same way the kit consumes every atom).
 */

export const TAX_STATEMENT_FILE_TYPES = [
  { key: 'TaxReturn',          label: 'Tax return',          enumValue: 0, icon: 'assignment',        color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'TaxAssessment',      label: 'Tax assessment',      enumValue: 1, icon: 'fact_check',        color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'SupportingDocument', label: 'Supporting document', enumValue: 2, icon: 'attach_file',       color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)' },
  { key: 'Other',              label: 'Other',               enumValue: 3, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function TaxStatementFileTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || TAX_STATEMENT_FILE_TYPES} {...rest} />;
}
