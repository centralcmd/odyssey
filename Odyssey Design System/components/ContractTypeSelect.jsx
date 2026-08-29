/**
 * Odyssey DS — ContractTypeSelect
 * A single-select pre-wired to the ContractType vocabulary: each enum member
 * rendered with its Material icon in its category color. Delegates to the shared
 * `TypeSelect` (read off the DS namespace), so it shares one look with every
 * other type picker — colored glyph, label, far-right check. Falls back to the
 * base Select until the bundle carries TypeSelect.
 *
 * Value is the enum key — 'Employment' | 'Service' | 'Rental' | 'Other'.
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
  { key: 'Other',      label: 'Other',      enumValue: 3, icon: 'description',         color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function ContractTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || CONTRACT_TYPES} {...rest} />;
}
