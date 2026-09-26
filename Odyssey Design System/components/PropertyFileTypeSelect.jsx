
/**
 * Odyssey DS — PropertyFileTypeSelect
 * A single-select pre-wired to the PropertyFileType vocabulary — the kind of
 * document attached to a *property* (house, cabin, car, boat): Deed ·
 * PurchaseAgreement · Valuation · Inspection · Registration · Insurance ·
 * Warranty · Receipt · Maintenance · Tax · Drawing · Other. Each option renders
 * with its Material icon in its category color. A thin, domain-typed wrapper
 * over the base `RegistrySelect`; every Select prop passes straight through.
 *
 * Value is the enum key. Controlled: pass `value` + `onChange(key, event)`.
 *
 * `PROPERTY_FILE_TYPES` (exported here) is the canonical registry — name · icon ·
 * color · soft tint · enumValue — mirroring `OdysseyData.propertyFileTypes` and
 * the C# `PropertyFileType` enum (field `FileType` on `PropertyFile`). Ordinals
 * are a wire and persistence contract: never renumbered, never reused. `Other`
 * is ordinal 0 (the AccountFileType shape) and sorts last. Keys shared with
 * another file enum (PurchaseAgreement, Valuation, Warranty, Registration, Tax,
 * Receipt, Other) reuse that enum's icon and color, so a key reads the same on
 * every surface.
 */

export const PROPERTY_FILE_TYPES = [
  { key: 'Deed',              label: 'Deed',               enumValue: 1,  icon: 'workspace_premium', color: 'oklch(0.74 0.15 290)', soft: 'oklch(0.74 0.15 290 / 0.16)' },
  { key: 'PurchaseAgreement', label: 'Purchase agreement', enumValue: 2,  icon: 'sell',              color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)' },
  { key: 'Valuation',         label: 'Valuation',          enumValue: 3,  icon: 'price_check',       color: 'oklch(0.80 0.15 140)', soft: 'oklch(0.80 0.15 140 / 0.16)' },
  { key: 'Inspection',        label: 'Inspection',         enumValue: 4,  icon: 'troubleshoot',      color: 'oklch(0.78 0.12 180)', soft: 'oklch(0.78 0.12 180 / 0.16)' },
  { key: 'Registration',      label: 'Registration',       enumValue: 5,  icon: 'app_registration',  color: 'oklch(0.74 0.15 310)', soft: 'oklch(0.74 0.15 310 / 0.16)' },
  { key: 'Insurance',         label: 'Insurance',          enumValue: 6,  icon: 'shield',            color: 'oklch(0.74 0.15 30)',  soft: 'oklch(0.74 0.15 30 / 0.16)' },
  { key: 'Warranty',          label: 'Warranty',           enumValue: 7,  icon: 'verified',          color: 'oklch(0.77 0.13 205)', soft: 'oklch(0.77 0.13 205 / 0.16)' },
  { key: 'Receipt',           label: 'Receipt',            enumValue: 8,  icon: 'receipt_long',      color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Maintenance',       label: 'Maintenance',        enumValue: 9,  icon: 'build',             color: 'oklch(0.80 0.14 95)',  soft: 'oklch(0.80 0.14 95 / 0.16)' },
  { key: 'Tax',               label: 'Tax',                enumValue: 10, icon: 'request_quote',     color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'Drawing',           label: 'Drawing',            enumValue: 11, icon: 'architecture',      color: 'oklch(0.76 0.12 240)', soft: 'oklch(0.76 0.12 240 / 0.16)' },
  { key: 'Other',             label: 'Other',              enumValue: 0,  icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function PropertyFileTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || PROPERTY_FILE_TYPES} {...rest} />;
}
