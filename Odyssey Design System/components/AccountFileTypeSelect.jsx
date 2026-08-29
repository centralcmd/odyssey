/**
 * Odyssey DS — AccountFileTypeSelect
 * A single-select pre-wired to the AccountFileType vocabulary — the kind of
 * document attached to an *account*: Message · Statement · Contract · Tax ·
 * Documentation · InsurancePolicy · LoanAgreement · RepaymentSchedule ·
 * PurchaseAgreement · Valuation · Warranty · Registration · Prospectus · Other.
 * Each option renders with its Material icon in its category color. A thin,
 * domain-typed wrapper over the base `Select`; every Select prop (label, help,
 * error, required, disabled, placeholder, id, className) passes straight through.
 *
 * Value is the enum key. Controlled: pass `value` + `onChange(key, event)`.
 *
 * `ACCOUNT_FILE_TYPES` (exported here) is the canonical registry — name · icon ·
 * color · soft tint · enumValue — and the consumable layer's source of truth for
 * account file types. It mirrors `OdysseyData.accountFileTypes` and the C#
 * `AccountFileType` enum (field `FileType` on `ExistingAccountFile`); keep them
 * in lockstep. For files attached to a *transaction*, use TransactionFileTypeSelect.
 *
 * Bundle components can't import each other, so this reads the base Select off
 * the DS namespace at render time (the same way the kit consumes every atom).
 */

export const ACCOUNT_FILE_TYPES = [
  { key: 'Message',           label: 'Message',             enumValue: 1,  icon: 'mail',              color: 'oklch(0.76 0.13 225)',  soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Statement',         label: 'Statement',           enumValue: 2,  icon: 'description',       color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)' },
  { key: 'Contract',          label: 'Contract',            enumValue: 3,  icon: 'history_edu',       color: 'oklch(0.72 0.16 295)',  soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'Tax',               label: 'Tax',                 enumValue: 4,  icon: 'request_quote',     color: 'oklch(0.75 0.16 330)',  soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'Documentation',     label: 'Documentation',       enumValue: 5,  icon: 'menu_book',         color: 'oklch(0.77 0.14 110)',  soft: 'oklch(0.77 0.14 110 / 0.16)' },
  { key: 'InsurancePolicy',   label: 'Insurance policy',    enumValue: 6,  icon: 'shield',            color: 'oklch(0.74 0.15 30)',   soft: 'oklch(0.74 0.15 30 / 0.16)' },
  { key: 'LoanAgreement',     label: 'Loan agreement',      enumValue: 7,  icon: 'gavel',             color: 'oklch(0.72 0.15 265)',  soft: 'oklch(0.72 0.15 265 / 0.16)' },
  { key: 'RepaymentSchedule', label: 'Repayment schedule',  enumValue: 8,  icon: 'event_repeat',      color: 'oklch(0.78 0.14 160)',  soft: 'oklch(0.78 0.14 160 / 0.16)' },
  { key: 'PurchaseAgreement', label: 'Purchase agreement',  enumValue: 9,  icon: 'sell',              color: 'oklch(0.79 0.14 60)',   soft: 'oklch(0.79 0.14 60 / 0.16)' },
  { key: 'Valuation',         label: 'Valuation',           enumValue: 10, icon: 'price_check',       color: 'oklch(0.80 0.15 140)',  soft: 'oklch(0.80 0.15 140 / 0.16)' },
  { key: 'Warranty',          label: 'Warranty',            enumValue: 11, icon: 'verified',          color: 'oklch(0.77 0.13 205)',  soft: 'oklch(0.77 0.13 205 / 0.16)' },
  { key: 'Registration',      label: 'Registration',        enumValue: 12, icon: 'app_registration',  color: 'oklch(0.74 0.15 310)',  soft: 'oklch(0.74 0.15 310 / 0.16)' },
  { key: 'Prospectus',        label: 'Prospectus',          enumValue: 13, icon: 'auto_stories',      color: 'oklch(0.78 0.14 95)',   soft: 'oklch(0.78 0.14 95 / 0.16)' },
  { key: 'Other',             label: 'Other',               enumValue: 0,  icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function AccountFileTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || ACCOUNT_FILE_TYPES} {...rest} />;
}
