/**
 * Odyssey DS — AccountTypeSelect
 * The typed Account-type picker: the chosen type's colored glyph + label in a
 * select trigger, opening a popover with the types split into Assets and
 * Liabilities — exactly how the registry groups them. Value is the AccountType
 * enum key (e.g. 'CheckingAccount').
 *
 * `ACCOUNT_TYPES` (exported here) is the canonical registry — key · label ·
 * group · Material icon · oklch color + soft tint — and the design system's
 * single source of truth for the consumable layer. It mirrors
 * `OdysseyData.accountTypes` (the kit seed) and the C# `AccountType` enum;
 * keep all three in lockstep. Completes the registry-picker family alongside
 * ContactTypeSelect and the two FileType pickers.
 *
 * Delegates to the shared `TypeSelect` (read off the DS namespace) — passing
 * `ACCOUNT_TYPES` + the Assets/Liabilities `groups` — so it shares one look with
 * every other type picker. Falls back to the base Select until the bundle
 * carries TypeSelect.
 */

export const ACCOUNT_TYPES = [
  // ---- Assets ----
  { key: 'Cash',              label: 'Cash',            group: 'asset',     icon: 'payments',               color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'CheckingAccount',   label: 'Checking',        group: 'asset',     icon: 'account_balance',        color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)' },
  { key: 'SavingsAccount',    label: 'Savings',         group: 'asset',     icon: 'savings',                color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'InvestmentAccount', label: 'Investment',      group: 'asset',     icon: 'trending_up',            color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'PensionAccount',    label: 'Pension',         group: 'asset',     icon: 'elderly',                color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'Property',          label: 'Property',        group: 'asset',     icon: 'home',                   color: 'oklch(0.72 0.14 255)', soft: 'oklch(0.72 0.14 255 / 0.16)' },
  { key: 'Vehicle',           label: 'Vehicle',         group: 'asset',     icon: 'directions_car',         color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)' },
  { key: 'OtherAsset',        label: 'Other asset',     group: 'asset',     icon: 'category',               color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
  // ---- Liabilities ----
  { key: 'CreditCard',        label: 'Credit card',     group: 'liability', icon: 'credit_card',            color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
  { key: 'Mortgage',          label: 'Mortgage',        group: 'liability', icon: 'home_work',              color: 'oklch(0.77 0.14 55)',  soft: 'oklch(0.77 0.14 55 / 0.16)' },
  { key: 'StudentLoan',       label: 'Student loan',    group: 'liability', icon: 'school',                 color: 'oklch(0.79 0.14 78)',  soft: 'oklch(0.79 0.14 78 / 0.16)' },
  { key: 'PersonalLoan',      label: 'Personal loan',   group: 'liability', icon: 'account_balance_wallet', color: 'oklch(0.72 0.16 8)',   soft: 'oklch(0.72 0.16 8 / 0.16)' },
  { key: 'CarLoan',           label: 'Car loan',        group: 'liability', icon: 'directions_car',         color: 'oklch(0.75 0.15 38)',  soft: 'oklch(0.75 0.15 38 / 0.16)' },
  { key: 'TaxDebt',           label: 'Tax debt',        group: 'liability', icon: 'receipt_long',           color: 'oklch(0.71 0.17 352)', soft: 'oklch(0.71 0.17 352 / 0.16)' },
  { key: 'OtherLiability',    label: 'Other liability', group: 'liability', icon: 'category',               color: 'oklch(0.66 0.03 30)',  soft: 'oklch(0.66 0.03 30 / 0.16)' },
];

export function AccountTypeSelect({ value, onChange, label = 'Account type', placeholder = 'Choose a type…', error, help, types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return (
    <RegistrySelect value={value} onChange={onChange} label={label} placeholder={placeholder} error={error} help={help}
      types={types || ACCOUNT_TYPES} groups={ACCOUNT_TYPE_GROUPS} {...rest} />
  );
}

export const ACCOUNT_TYPE_GROUPS = [
  { key: 'asset', label: 'Assets' },
  { key: 'liability', label: 'Liabilities' },
];
