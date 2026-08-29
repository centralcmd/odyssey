/**
 * Odyssey DS — ContactTypeSelect
 * A single-select pre-wired to the ContactType vocabulary: the six enum
 * members, each rendered with its Material icon in its category color. A thin,
 * domain-typed wrapper over the base `Select` — every Select prop (label, help,
 * error, required, disabled, placeholder, id, className) passes straight through.
 *
 * Value is the enum key — 'Person' | 'Organization' (v5: the six-value
 * taxonomy collapsed to these two; ordinals Person=1, Organization=2).
 * Controlled: pass `value` + `onChange(key, event)`.
 *
 * `CONTACT_TYPES` (exported here) is the canonical registry — name · icon ·
 * color · soft tint — and the design system's single source of truth for the
 * consumable layer. It mirrors `OdysseyData.contactTypes` (the kit seed)
 * and the C# `ContactType` enum; keep all three in lockstep.
 *
 * Bundle components can't import each other, so this reads the base Select off
 * the DS namespace at render time (the same way the kit consumes every atom).
 */

export const CONTACT_TYPES = [
  { key: 'Person',       label: 'Person',       icon: 'person',         color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Organization', label: 'Organization', icon: 'corporate_fare', color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
];

export function ContactTypeSelect({ value, onChange, label = 'Type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || CONTACT_TYPES} {...rest} />;
}
