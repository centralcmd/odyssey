/**
 * Odyssey DS — PropertyEventTypeSelect
 * A single-select pre-wired to the PropertyEventType vocabulary — what happened
 * to a house, cabin, car or boat. Like ContractPartyRoleSelect, its option list
 * depends on ANOTHER field: pass `propertyType` ('RealEstate' | 'Vehicle') and
 * the control offers exactly the legal members for that type, the
 * type-specific ones first under their own heading.
 *
 * SYSTEM-ONLY MEMBERS (Archived · Restored · Acquired date cleared · Disposal
 * reversed) are never offered. They are rejected on POST and cannot be
 * introduced by PUT. A PUT on a row that already carries one may keep it, so
 * pass that key as `keepType` and it is offered alone under "Recorded
 * automatically", so the edit can keep it.
 *
 * `PROPERTY_EVENT_TYPES` is the canonical registry (key · label · enumValue ·
 * icon · color · soft · desc · auto). Ordinals start at 100, disjoint from
 * ContractEventType (0–99): one shared table, a stored Type always names its
 * enum. `Other` is ordinal 108 but reads LAST — the unknown-ordinal fallback is
 * positional. `PROPERTY_EVENT_TYPE_MATRIX` is the client half of the server's
 * `PropertyEventTypeMatrix`: 17 legal cells per property type, 34 of 42.
 */

export const PROPERTY_EVENT_TYPES = [
  { key: 'Acquired',               label: 'Acquired',              enumValue: 100, icon: 'key',              color: 'oklch(0.79 0.14 145)', soft: 'oklch(0.79 0.14 145 / 0.16)', scope: 'common', auto: 'the acquired date was set', desc: 'Bought, inherited or received.' },
  { key: 'Disposed',               label: 'Disposed of',           enumValue: 101, icon: 'output',           color: 'oklch(0.74 0.13 25)',  soft: 'oklch(0.74 0.13 25 / 0.16)',  scope: 'common', auto: 'the disposed date was set', desc: 'Sold, scrapped or written off.' },
  { key: 'Valued',                 label: 'Valued',                enumValue: 102, icon: 'price_check',      color: 'oklch(0.78 0.13 110)', soft: 'oklch(0.78 0.13 110 / 0.16)', scope: 'common', desc: 'An appraisal or valuation was obtained.' },
  { key: 'Maintenance',            label: 'Maintenance',           enumValue: 103, icon: 'build',            color: 'oklch(0.80 0.14 95)',  soft: 'oklch(0.80 0.14 95 / 0.16)',  scope: 'common', desc: 'Routine upkeep.' },
  { key: 'Repair',                 label: 'Repair',                enumValue: 104, icon: 'handyman',         color: 'oklch(0.79 0.14 60)',  soft: 'oklch(0.79 0.14 60 / 0.16)',  scope: 'common', desc: 'A defect was fixed.' },
  { key: 'Damage',                 label: 'Damage',                enumValue: 105, icon: 'report',           color: 'oklch(0.72 0.15 20)',  soft: 'oklch(0.72 0.15 20 / 0.16)',  scope: 'common', desc: 'An incident, accident, storm or water damage.' },
  { key: 'Inspection',             label: 'Inspection',            enumValue: 106, icon: 'troubleshoot',     color: 'oklch(0.78 0.12 180)', soft: 'oklch(0.78 0.12 180 / 0.16)', scope: 'common', desc: 'A general inspection or survey.' },
  { key: 'InsuranceChanged',       label: 'Insurance changed',     enumValue: 107, icon: 'shield',           color: 'oklch(0.74 0.15 30)',  soft: 'oklch(0.74 0.15 30 / 0.16)',  scope: 'common', desc: 'A policy was taken out, renewed or changed.' },
  { key: 'Renovation',             label: 'Renovation',            enumValue: 113, icon: 'format_paint',     color: 'oklch(0.76 0.14 320)', soft: 'oklch(0.76 0.14 320 / 0.16)', scope: 'RealEstate', desc: 'A renovation or extension.' },
  { key: 'TaxAssessed',            label: 'Tax assessed',          enumValue: 114, icon: 'request_quote',    color: 'oklch(0.75 0.16 330)', soft: 'oklch(0.75 0.16 330 / 0.16)', scope: 'RealEstate', desc: 'A property-tax assessment.' },
  { key: 'TenancyStarted',         label: 'Tenancy started',       enumValue: 115, icon: 'vpn_key',          color: 'oklch(0.79 0.13 55)',  soft: 'oklch(0.79 0.13 55 / 0.16)',  scope: 'RealEstate', desc: 'Let to a tenant.' },
  { key: 'TenancyEnded',           label: 'Tenancy ended',         enumValue: 116, icon: 'key_off',          color: 'oklch(0.72 0.10 40)',  soft: 'oklch(0.72 0.10 40 / 0.16)',  scope: 'RealEstate', desc: 'A tenancy came to an end.' },
  { key: 'Serviced',               label: 'Serviced',              enumValue: 117, icon: 'car_repair',       color: 'oklch(0.77 0.13 205)', soft: 'oklch(0.77 0.13 205 / 0.16)', scope: 'Vehicle', desc: 'A workshop service.' },
  { key: 'TyreChange',             label: 'Tyre change',           enumValue: 118, icon: 'tire_repair',      color: 'oklch(0.76 0.10 240)', soft: 'oklch(0.76 0.10 240 / 0.16)', scope: 'Vehicle', desc: 'Seasonal or replacement tyres.' },
  { key: 'PeriodicInspection',     label: 'Periodic inspection',   enumValue: 119, icon: 'fact_check',       color: 'oklch(0.78 0.13 170)', soft: 'oklch(0.78 0.13 170 / 0.16)', scope: 'Vehicle', desc: 'The statutory roadworthiness test (e.g. EU-kontroll).' },
  { key: 'Registered',             label: 'Registration',          enumValue: 120, icon: 'app_registration', color: 'oklch(0.74 0.15 310)', soft: 'oklch(0.74 0.15 310 / 0.16)', scope: 'Vehicle', desc: 'Registered, re-registered or deregistered.' },
  { key: 'Archived',               label: 'Archived',              enumValue: 109, icon: 'inventory_2',      color: 'oklch(0.75 0.06 250)', soft: 'oklch(0.75 0.06 250 / 0.16)', scope: 'common', systemOnly: true, auto: 'the property was archived', desc: 'The property was archived.' },
  { key: 'Unarchived',             label: 'Restored',              enumValue: 110, icon: 'unarchive',        color: 'oklch(0.78 0.12 185)', soft: 'oklch(0.78 0.12 185 / 0.16)', scope: 'common', systemOnly: true, auto: 'the property was restored from the archive', desc: 'The property was restored from the archive.' },
  { key: 'AcquisitionDateCleared', label: 'Acquired date cleared', enumValue: 111, icon: 'event_busy',       color: 'oklch(0.74 0.08 150)', soft: 'oklch(0.74 0.08 150 / 0.16)', scope: 'common', systemOnly: true, auto: 'the acquired date was removed', desc: 'The acquired date was removed.' },
  { key: 'DisposalReversed',       label: 'Disposal reversed',     enumValue: 112, icon: 'undo',             color: 'oklch(0.76 0.10 60)',  soft: 'oklch(0.76 0.10 60 / 0.16)',  scope: 'common', systemOnly: true, auto: 'the disposed date was removed', desc: 'The disposed date was removed.' },
  { key: 'Other',                  label: 'Other',                 enumValue: 108, icon: 'more_horiz',       color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)', scope: 'common', desc: 'Anything the named types do not cover. The title carries it.' },
];

export const PROPERTY_EVENT_SYSTEM_ONLY = ['Archived', 'Unarchived', 'AcquisitionDateCleared', 'DisposalReversed'];

const common = PROPERTY_EVENT_TYPES.filter((t) => t.scope === 'common').map((t) => t.key);
const scoped = (s) => PROPERTY_EVENT_TYPES.filter((t) => t.scope === s).map((t) => t.key);

/** Legal members per PropertyType — 13 universal + 4 type-specific each. */
export const PROPERTY_EVENT_TYPE_MATRIX = {
  RealEstate: scoped('RealEstate').concat(common),
  Vehicle: scoped('Vehicle').concat(common),
};

/** 'legal' | 'systemOnly' | 'illegal' for one (property type, event type) cell. */
export function propertyEventTypeLegality(propertyType, key) {
  const cell = PROPERTY_EVENT_TYPE_MATRIX[propertyType];
  if (cell && cell.indexOf(key) === -1) return 'illegal';
  if (PROPERTY_EVENT_SYSTEM_ONLY.indexOf(key) !== -1) return 'systemOnly';
  return 'legal';
}

/** The pickable registry rows for a property type, each tagged `group`. */
export function propertyEventTypesFor(propertyType, keepType, types) {
  const all = types || PROPERTY_EVENT_TYPES;
  const out = [];
  all.forEach((t) => {
    const legality = propertyEventTypeLegality(propertyType, t.key);
    if (legality === 'illegal') return;
    if (legality === 'systemOnly') {
      if (t.key === keepType) out.push({ ...t, group: 'system' });
      return;
    }
    out.push({ ...t, group: propertyType && t.scope === propertyType ? 'specific' : 'common' });
  });
  const rank = { specific: 0, common: 1, system: 2 };
  return out.sort((a, b) => rank[a.group] - rank[b.group]);
}

export function PropertyEventTypeSelect({ value, onChange, label = 'Type', propertyType, keepType, types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  const list = propertyEventTypesFor(propertyType, keepType, types);
  const groups = propertyType ? [
    { key: 'specific', label: propertyType === 'Vehicle' ? 'For vehicles' : 'For real estate' },
    { key: 'common', label: 'Any property' },
    ...(keepType ? [{ key: 'system', label: 'Recorded automatically' }] : []),
  ] : undefined;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={list} groups={groups} {...rest} />;
}
