/**
 * Odyssey DS — BudgetCategoryTypeSelect
 * A single-select pre-wired to the BudgetCategoryType vocabulary — the two
 * directions a budget line can take: Expense (money out) and Income (money in).
 * Each renders with its Material icon in its category color, delegating to the
 * shared `TypeSelect` so it shares one look with every other type picker —
 * colored glyph, label, far-right check. Falls back to the base Select until
 * the bundle carries TypeSelect.
 *
 * Value is the enum key — 'Expense' | 'Income'. Controlled: pass `value` +
 * `onChange(key, event)`. Every wrapper prop (label, help, error, required,
 * disabled, placeholder, id, className) passes through.
 *
 * `BUDGET_CATEGORY_TYPES` (exported here) is the canonical registry — key ·
 * label · enumValue · icon · color · soft tint — the consumable layer's source
 * of truth. It mirrors the C# `BudgetCategoryType` enum (Expense = 0, Income = 1);
 * keep both in lockstep. Colors share the categorical band with the other type
 * registries: Expense reads as a debit (warm red), Income as a credit (green).
 */

export const BUDGET_CATEGORY_TYPES = [
  { key: 'Expense', label: 'Expense', enumValue: 0, icon: 'trending_down', color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
  { key: 'Income',  label: 'Income',  enumValue: 1, icon: 'trending_up',   color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
];

export function BudgetCategoryTypeSelect({ value, onChange, label = 'Category', placeholder = 'Select category…', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} placeholder={placeholder} types={types || BUDGET_CATEGORY_TYPES} {...rest} />;
}
