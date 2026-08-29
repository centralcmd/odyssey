/**
 * Odyssey DS — BillingIntervalMultiSelect
 * The Subscriptions-list **Interval** filter, pre-wired to the BillingInterval
 * vocabulary: a checkbox-list popover whose rows each carry the interval's
 * Material icon in its category color, with a count badge on the trigger. A thin
 * wrapper over `MultiSelect` — `value` (array of enum keys) + `onChange` pass
 * straight through, as do `icon` and `align`. It emits the selected keys, which
 * the page parses into the `Intervals[]` query param.
 *
 * Defaults: trigger label "Any interval", trigger glyph `autorenew`. The registry
 * is the canonical `BILLING_INTERVALS` exported by BillingIntervalSelect; read
 * off the DS namespace at render time (bundle components can't import each other).
 */

export function BillingIntervalMultiSelect({
  value = [],
  onChange,
  label = 'Any interval',
  icon = 'autorenew',
  align = 'start',
  types,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistryMultiSelect } = NS;
  const registry = types || NS.BILLING_INTERVALS || [];
  if (!RegistryMultiSelect) return null;
  return (
    <RegistryMultiSelect
      value={value}
      onChange={onChange}
      label={label}
      icon={icon}
      align={align}
      types={registry}
    />
  );
}
