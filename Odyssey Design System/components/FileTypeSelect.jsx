/**
 * Odyssey DS — FileTypeSelect
 * The one single-select for "what kind of document is this?". `kind` picks the
 * vocabulary — the entity the file is attached to:
 *   'account'      AccountFileType      (field `FileType` on ExistingAccountFile)
 *   'transaction'  TransactionFileType  (field `Type` on ExistingTransactionFile)
 *   'taxStatement' TaxStatementFileType (field `FileType` on TaxStatementFile)
 *   'property'     PropertyFileType     (field `FileType` on PropertyFile)
 * Each option renders its Material icon in its category color via RegistrySelect.
 * Every Select prop (label, help, error, required, disabled, placeholder, id,
 * className) passes straight through; `types` overrides / subsets the registry.
 *
 * Value is the enum key. Controlled: pass `value` + `onChange(key, event)`.
 *
 * The four registries below are the consumable layer's source of truth for file
 * types. They mirror `OdysseyData.*FileTypes` and the C# enums — keep them in
 * lockstep. Ordinals are a wire + persistence contract: never renumbered, never
 * reused. Keys shared across enums (PurchaseAgreement, Valuation, Warranty,
 * Registration, Tax, Receipt, Documentation, Other) share icon and color, so a
 * key reads the same on every surface.
 *
 * Replaces AccountFileTypeSelect · TransactionFileTypeSelect ·
 * TaxStatementFileTypeSelect · PropertyFileTypeSelect.
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

export const TRANSACTION_FILE_TYPES = [
  { key: 'Receipt',             label: 'Receipt',              enumValue: 0, icon: 'receipt_long',      color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Invoice',             label: 'Invoice',              enumValue: 1, icon: 'receipt',           color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)' },
  { key: 'CreditNote',          label: 'Credit note',          enumValue: 3, icon: 'assignment_return', color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
  { key: 'Quote',               label: 'Quote',                enumValue: 4, icon: 'format_quote',      color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'PaymentConfirmation', label: 'Payment confirmation', enumValue: 5, icon: 'price_check',       color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Documentation',       label: 'Documentation',        enumValue: 6, icon: 'menu_book',         color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)' },
  { key: 'Other',               label: 'Other',                enumValue: 2, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export const TAX_STATEMENT_FILE_TYPES = [
  { key: 'TaxReturn',          label: 'Tax return',          enumValue: 0, icon: 'assignment',        color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'TaxAssessment',      label: 'Tax assessment',      enumValue: 1, icon: 'fact_check',        color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'SupportingDocument', label: 'Supporting document', enumValue: 2, icon: 'attach_file',       color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)' },
  { key: 'Other',              label: 'Other',               enumValue: 3, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

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

/** kind → { types, filterIcon } — the filter glyph is the multi-select trigger default. */
export const FILE_TYPE_REGISTRIES = {
  account:      { types: ACCOUNT_FILE_TYPES,       filterIcon: 'folder' },
  transaction:  { types: TRANSACTION_FILE_TYPES,   filterIcon: 'receipt_long' },
  taxStatement: { types: TAX_STATEMENT_FILE_TYPES, filterIcon: 'request_quote' },
  property:     { types: PROPERTY_FILE_TYPES,      filterIcon: 'home_work' },
};

export function FileTypeSelect({ kind = 'account', value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  const reg = FILE_TYPE_REGISTRIES[kind] || FILE_TYPE_REGISTRIES.account;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || reg.types} {...rest} />;
}
