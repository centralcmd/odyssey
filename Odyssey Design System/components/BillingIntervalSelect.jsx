/**
 * Odyssey DS — BillingIntervalSelect
 * A single-select pre-wired to the Subscriptions `BillingInterval` vocabulary:
 * each enum member rendered with its Material icon in its category color. A thin,
 * domain-typed wrapper over the shared `TypeSelect` (read off the DS namespace),
 * so it shares one look with every other type picker — colored glyph, label,
 * far-right check. Falls back to the base `Select` until the bundle carries
 * TypeSelect. Every Select prop (label, help, error, required, disabled,
 * placeholder, id, className) passes straight through.
 *
 * Value is the enum key — 'Daily' | 'Weekly' | 'Monthly' | 'Yearly'. The order
 * is the enum's numeric order (Daily < Weekly < Monthly < Yearly), which is also
 * how the Subscriptions list sorts by "Frequency". Controlled: pass `value` +
 * `onChange(key, event)`. Default selection is Monthly (the DTO default).
 *
 * `BILLING_INTERVALS` (exported here) is the canonical registry — key · label ·
 * icon · color · soft tint — the single source of truth on the consumable layer;
 * it mirrors `OdysseyData.billingIntervals` and the C# `BillingInterval` enum.
 */

export const BILLING_INTERVALS = [
  { key: 'Daily',   label: 'Daily',   enumValue: 0, icon: 'today',          color: 'oklch(0.79 0.13 205)', soft: 'oklch(0.79 0.13 205 / 0.16)' },
  { key: 'Weekly',  label: 'Weekly',  enumValue: 1, icon: 'view_week',      color: 'oklch(0.78 0.14 168)', soft: 'oklch(0.78 0.14 168 / 0.16)' },
  { key: 'Monthly', label: 'Monthly', enumValue: 2, icon: 'calendar_month', color: 'oklch(0.72 0.14 255)', soft: 'oklch(0.72 0.14 255 / 0.16)' },
  { key: 'Yearly',  label: 'Yearly',  enumValue: 3, icon: 'event_repeat',   color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
];

export function BillingIntervalSelect({ value, onChange, label = 'Billing interval', placeholder = 'Select interval…', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} placeholder={placeholder} types={types || BILLING_INTERVALS} {...rest} />;
}
