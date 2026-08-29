/**
 * Odyssey DS — InsurancePolicyTypeSelect
 * A single-select pre-wired to the InsurancePolicyType vocabulary: each enum
 * member rendered with its Material icon in its category color. A thin,
 * domain-typed wrapper over the base `Select` — every Select prop (label, help,
 * error, required, disabled, placeholder, id, className) passes straight through.
 *
 * Value is the enum key — 'Home' | 'Contents' | 'Building' | 'Vehicle' |
 * 'Travel' | 'Life' | 'Health' | 'Accident' | 'Liability' | 'Pet' | 'Property' |
 * 'Other'. Controlled: pass `value` + `onChange(key, event)`.
 *
 * `INSURANCE_POLICY_TYPES` (exported here) is the canonical registry — name ·
 * icon · color · soft tint — the single source of truth on the consumable layer;
 * it mirrors `OdysseyData.insurancePolicyTypes` and the C# `InsurancePolicyType`
 * enum. Bundle components can't import each other, so this reads the base Select
 * off the DS namespace at render time.
 */

export const INSURANCE_POLICY_TYPES = [
  { key: 'Home',      label: 'Home',         icon: 'house',             color: 'oklch(0.72 0.14 255)', soft: 'oklch(0.72 0.14 255 / 0.16)' },
  { key: 'Contents',  label: 'Contents',     icon: 'chair',             color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'Building',  label: 'Building',     icon: 'apartment',         color: 'oklch(0.76 0.13 225)', soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Vehicle',   label: 'Vehicle',      icon: 'directions_car',    color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)' },
  { key: 'Travel',    label: 'Travel',       icon: 'flight',            color: 'oklch(0.77 0.13 205)', soft: 'oklch(0.77 0.13 205 / 0.16)' },
  { key: 'Life',      label: 'Life',         icon: 'favorite',          color: 'oklch(0.72 0.16 8)',   soft: 'oklch(0.72 0.16 8 / 0.16)' },
  { key: 'Health',    label: 'Health',       icon: 'health_and_safety', color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Accident',  label: 'Accident',     icon: 'personal_injury',   color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)' },
  { key: 'Liability', label: 'Liability',    icon: 'gavel',             color: 'oklch(0.72 0.15 265)', soft: 'oklch(0.72 0.15 265 / 0.16)' },
  { key: 'Pet',       label: 'Pet',          icon: 'pets',              color: 'oklch(0.79 0.14 78)',  soft: 'oklch(0.79 0.14 78 / 0.16)' },
  { key: 'Property',  label: 'Property',     icon: 'home_work',         color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'Other',     label: 'Other',        icon: 'shield',            color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function InsurancePolicyTypeSelect({ value, onChange, label = 'Policy type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || INSURANCE_POLICY_TYPES} {...rest} />;
}
