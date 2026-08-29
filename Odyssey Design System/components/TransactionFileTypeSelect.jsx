/**
 * Odyssey DS — TransactionFileTypeSelect
 * A single-select pre-wired to the TransactionFileType vocabulary — the kind of
 * document attached to a *transaction*: Receipt · Invoice · CreditNote · Quote ·
 * PaymentConfirmation · Documentation · Other. Each option
 * renders with its Material icon in its category color. A thin, domain-typed
 * wrapper over the base `Select`; every Select prop passes straight through.
 *
 * Value is the enum key. Controlled: pass `value` + `onChange(key, event)`.
 *
 * `TRANSACTION_FILE_TYPES` (exported here) is the canonical registry and the
 * consumable layer's source of truth for transaction file types. It mirrors
 * `OdysseyData.transactionFileTypes` and the C# `TransactionFileType` enum
 * (field `Type` on `ExistingTransactionFile`); keep them in lockstep. For files
 * attached to an *account*, use AccountFileTypeSelect.
 *
 * Bundle components can't import each other, so this reads the base Select off
 * the DS namespace at render time (the same way the kit consumes every atom).
 */

export const TRANSACTION_FILE_TYPES = [
  { key: 'Receipt',             label: 'Receipt',              enumValue: 0, icon: 'receipt_long',      color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Invoice',             label: 'Invoice',              enumValue: 1, icon: 'receipt',           color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)' },
  { key: 'CreditNote',          label: 'Credit note',          enumValue: 3, icon: 'assignment_return', color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
  { key: 'Quote',               label: 'Quote',                enumValue: 4, icon: 'format_quote',      color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'PaymentConfirmation', label: 'Payment confirmation', enumValue: 5, icon: 'price_check',       color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Documentation',       label: 'Documentation',        enumValue: 6, icon: 'menu_book',         color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)' },
  { key: 'Other',               label: 'Other',                enumValue: 2, icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function TransactionFileTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || TRANSACTION_FILE_TYPES} {...rest} />;
}
