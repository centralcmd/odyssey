/**
 * Odyssey DS — ContractTypeSelect
 * A single-select pre-wired to the ContractType vocabulary: each enum member
 * rendered with its Material icon in its category color. Delegates to the shared
 * `TypeSelect` (read off the DS namespace), so it shares one look with every
 * other type picker — colored glyph, label, far-right check. Falls back to the
 * base Select until the bundle carries TypeSelect.
 *
 * Value is the enum key — 'Employment' | 'Service' | 'Rental' | 'Loan' | 'Deposit' | 'Other'.
 * The registry is in READING order, which is not ordinal order, and `Other` must
 * stay the trailing entry: it is the documented fallback for an out-of-range
 * value, so a reorder that displaces it makes stale rows render as whatever
 * ends the list.
 * Controlled: pass `value` + `onChange(key, event)`. Every wrapper prop (label,
 * help, error, required, disabled, placeholder, id, className) passes through.
 *
 * `CONTRACT_TYPES` (exported here) is the canonical registry — key · label ·
 * enumValue · icon · color · soft tint — the consumable layer's source of truth
 * for contract types. It mirrors `OdysseyData.contractTypes` and the C#
 * `ContractType` enum; keep all three in lockstep. For documents that attach to
 * a contract, use the (kit-side) contract file-type picker.
 */

export const CONTRACT_TYPES = [
  { key: 'Employment', label: 'Employment', enumValue: 0, icon: 'work',                color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Service',    label: 'Service',    enumValue: 1, icon: 'home_repair_service', color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)' },
  { key: 'Rental',     label: 'Rental',     enumValue: 2, icon: 'cottage',             color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)' },
  { key: 'Insurance',  label: 'Insurance',  enumValue: 4, icon: 'shield',              color: 'oklch(0.75 0.14 290)', soft: 'oklch(0.75 0.14 290 / 0.16)' },
  { key: 'Subscription', label: 'Subscription', enumValue: 5, icon: 'autorenew',       color: 'oklch(0.76 0.14 320)', soft: 'oklch(0.76 0.14 320 / 0.16)' },
  { key: 'Purchase',   label: 'Purchase',   enumValue: 6, icon: 'shopping_bag',        color: 'oklch(0.78 0.14 140)', soft: 'oklch(0.78 0.14 140 / 0.16)' },
  { key: 'Loan',       label: 'Loan',       enumValue: 8, icon: 'account_balance',     color: 'oklch(0.77 0.13 100)', soft: 'oklch(0.77 0.13 100 / 0.16)' },
  { key: 'Deposit',    label: 'Deposit',    enumValue: 9, icon: 'lock_clock',          color: 'oklch(0.76 0.13 258)', soft: 'oklch(0.76 0.13 258 / 0.16)' },
  { key: 'Membership', label: 'Membership', enumValue: 7, icon: 'card_membership',     color: 'oklch(0.77 0.13 20)',  soft: 'oklch(0.77 0.13 20 / 0.16)' },
  { key: 'Other',      label: 'Other',      enumValue: 3, icon: 'description',         color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function ContractTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || CONTRACT_TYPES} {...rest} />;
}
