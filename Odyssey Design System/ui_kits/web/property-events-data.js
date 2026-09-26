/* property-events-data.js — PropertyEvent registry, matrix, seed and helpers.
   Frontend half of *Property Events — Backend (Draft v2)*, issue #167.

   Property events share ONE table with contract events (TPH, discriminator
   OwnerKind). Nothing here depends on that: every read and write is scoped to
   a property route, and a contract event id on it is a 404.

   ORDINALS START AT 100, disjoint from ContractEventType (0–99), and are a
   wire contract. The registry is a mirror of the DS PROPERTY_EVENT_TYPES
   (components/PropertyEventTypeSelect.jsx) — keep them in lockstep. Reading
   order is the registry's: type-specific members sit beside the universal
   ones, system-only members after, `Other` LAST because the unknown-ordinal
   fallback is positional.

   Two halves, one chronology, exactly as on contracts:
     'user'   — written through the dialog.
     'system' — staged by PropertyService in the same save as the change that
                caused it: acquired date set / cleared, disposed date set /
                cleared, archived / restored. Attributed to the person who
                acted. Editable and deletable like any row.

   NO ESTIMATE EVENTS (Non-Goal 1). Estimates sit behind
   properties.estimates.read and the log is read under properties.read, so an
   automatic "estimate added" row would leak what that claim withholds. A user
   may still write a Valued event by hand. NO DETAIL-CHANGE EVENTS (Non-Goal 2)
   — address, registration, VIN or make/model never enter a generated string. */
(function () {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  D.propertyEventTypes = [
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
  D.propertyEventTypeByKey = Object.fromEntries(D.propertyEventTypes.map(t => [t.key, t]));
  D.propertyEventSystemOnly = D.propertyEventTypes.filter(t => t.systemOnly).map(t => t.key);
  const common = D.propertyEventTypes.filter(t => t.scope === 'common').map(t => t.key);
  D.propertyEventTypeMatrix = {
    RealEstate: D.propertyEventTypes.filter(t => t.scope === 'RealEstate').map(t => t.key).concat(common),
    Vehicle: D.propertyEventTypes.filter(t => t.scope === 'Vehicle').map(t => t.key).concat(common),
  };

  /* Attribution labels — the shared resolver table contract events use. */
  D.cevUsers = D.cevUsers || {};
  if (!D.cevUsers['u-owner']) D.cevUsers['u-owner'] = 'Owner Demo';

  /* §8.5 — "d MMMM yyyy", invariant culture. */
  const longDate = (iso) => {
    const d = new Date(iso.length === 10 ? iso + 'T00:00:00Z' : iso);
    return d.getUTCDate() + ' ' + d.toLocaleDateString('en-GB', { month: 'long', timeZone: 'UTC' }) + ' ' + d.getUTCFullYear();
  };
  const nowIso = () => new Date().toISOString().slice(0, 19) + 'Z';
  const minNow = (date) => { const t = date + 'T00:00:00Z'; return new Date(t) > new Date() ? nowIso() : t; };

  /* The catalogue — closed inputs: the transition kind and its date. Never a
     name, address, registration, VIN, make/model or estimate figure. */
  const CATALOGUE = {
    Archived:               (d) => ['Property archived', 'Archived on ' + d + '.'],
    Unarchived:             (d) => ['Property restored from the archive', 'Restored on ' + d + '.'],
    Acquired:               (d) => ['Property acquired', 'Acquired on ' + d + '.'],
    AcquisitionDateCleared: (d) => ['Acquired date cleared', 'Cleared on ' + d + '.'],
    Disposed:               (d) => ['Property disposed of', 'Disposed of on ' + d + '.'],
    DisposalReversed:       (d) => ['Disposal reversed', 'Reversed on ' + d + '.'],
  };
  let seq = 0;
  const sysRow = (propertyId, type, occurredAt, dateForText, userId, createdAtUtc) => {
    const [title, description] = CATALOGUE[type](longDate(dateForText));
    return { id: 'pev-sys-' + propertyId + '-' + (++seq), propertyId, source: 'system', type, title, description, notes: null,
      occurredAt, createdByUserId: userId, createdAtUtc: createdAtUtc || nowIso() };
  };

  /* ---- Seed ---- */
  const u = (id, pid, type, title, description, occurredAt, by, createdAtUtc, notes) =>
    ({ id, propertyId: pid, source: 'user', type, title, description: description || null, notes: notes || null, occurredAt, createdByUserId: by, createdAtUtc: createdAtUtc || occurredAt });
  const created = '2026-01-10T09:00:00Z';
  D.propertyEventSeed = {
    'p-maple': [
      /* Staged when the record was created with an acquired date — the event
         predates the "Property added" marker, and sorts below it. */
      sysRow('p-maple', 'Acquired', '2018-09-05T00:00:00Z', '2018-09-05', 'u-jane', created),
      u('pev-m1', 'p-maple', 'Renovation', 'Seismic retrofit completed', 'Foundation bolting and cripple-wall bracing. Permit signed off by the city inspector.', '2021-10-04T15:00:00Z', 'u-jane', '2026-01-10T09:20:00Z'),
      u('pev-m2', 'p-maple', 'TaxAssessed', 'Assessment notice for 2026–27', 'Assessed value up 2%, the statutory cap.', '2026-02-01T10:00:00Z', 'u-sam', '2026-02-10T08:31:00Z'),
      u('pev-m3', 'p-maple', 'Damage', 'Storm damage to the roof', 'Two sections of flashing lifted on the north side. Water stain in the back bedroom ceiling.', '2026-07-14T16:30:00Z', 'u-jane', '2026-07-14T19:02:00Z', 'Photos are in the shared album — claim ref pending.'),
      u('pev-m4', 'p-maple', 'Maintenance', 'Exterior repainted', 'South and west walls, trim included.', '2026-08-03T12:00:00Z', null),
      u('pev-m5', 'p-maple', 'Repair', 'Gutter and flashing repaired', 'Bay Roofing replaced the lifted flashing and re-hung the rear gutter.', '2026-09-12T11:00:00Z', 'u-jane', '2026-09-12T17:44:00Z', 'Ask about the 10-year workmanship guarantee in writing.'),
    ],
    'p-storgata': [
      sysRow('p-storgata', 'Acquired', '2019-06-01T00:00:00Z', '2019-06-01', 'u-sam', created),
      u('pev-s1', 'p-storgata', 'TenancyStarted', 'Let from 1 August', 'Three-year tenancy, deposit in a blocked account.', '2024-08-01T00:00:00Z', 'u-sam', '2024-08-02T09:00:00Z'),
      u('pev-s2', 'p-storgata', 'Inspection', 'Annual walk-through with the tenant', 'Bathroom sealant needs redoing. Nothing else noted.', '2025-09-15T17:00:00Z', 'u-sam'),
      u('pev-s3', 'p-storgata', 'Valued', 'Broker valuation', 'Valuation obtained for the refinance.', '2026-01-01T10:00:00Z', 'u-mira'),
    ],
    /* p-cabin deliberately has none — no acquired date, so nothing staged: the empty state. */
    'p-ridge': [
      sysRow('p-ridge', 'Acquired', '2017-04-18T00:00:00Z', '2017-04-18', 'u-jane', created),
      u('pev-r1', 'p-ridge', 'Other', 'Building plans shelved', 'Architect’s fee paid to date. Drawings kept.', '2025-06-20T10:00:00Z', 'u-jane'),
      sysRow('p-ridge', 'Archived', '2025-10-01T09:00:00Z', '2025-10-01', 'u-jane', '2025-10-01T09:00:00Z'),
    ],
    'p-outback': [
      sysRow('p-outback', 'Acquired', '2022-03-14T00:00:00Z', '2022-03-14', 'u-jane', created),
      u('pev-o1', 'p-outback', 'TyreChange', 'Winter tyres on', 'Studded, front left worn to 5 mm.', '2025-10-28T09:00:00Z', 'u-sam', '2025-10-28T09:12:44Z', 'Next change mid-April.'),
      u('pev-o2', 'p-outback', 'InsuranceChanged', 'Policy renewed with Meridian', 'Comprehensive, excess unchanged.', '2026-01-01T00:00:00Z', 'u-jane', '2026-01-05T08:10:00Z'),
      u('pev-o3', 'p-outback', 'Registered', 'Registration renewed', null, '2026-03-02T12:00:00Z', 'u-jane'),
      u('pev-o4', 'p-outback', 'PeriodicInspection', 'Smog check passed', null, '2026-03-08T10:30:00Z', 'u-jane'),
      u('pev-o5', 'p-outback', 'TyreChange', 'Summer tyres on', 'Winter set stored at the dealer.', '2026-04-12T08:00:00Z', 'u-sam'),
      u('pev-o6', 'p-outback', 'Serviced', '36,000-mile service', 'Oil, filters and brake fluid. Rear pads at 40%.', '2026-06-21T14:00:00Z', 'u-sam', '2026-06-21T16:40:00Z'),
    ],
    'p-wren': [
      sysRow('p-wren', 'Acquired', '2020-05-02T00:00:00Z', '2020-05-02', 'u-mira', created),
      u('pev-w1', 'p-wren', 'Inspection', 'Hull survey', 'Out of the water at the boatyard. Keel bolts sound.', '2025-04-15T09:00:00Z', 'u-mira'),
      u('pev-w2', 'p-wren', 'Registered', 'Vessel registration renewed', null, '2025-04-18T11:00:00Z', 'u-mira'),
      u('pev-w3', 'p-wren', 'Maintenance', 'Antifouling and new zinc', null, '2026-04-20T10:00:00Z', 'u-mira'),
    ],
    'p-civic': [
      sysRow('p-civic', 'Acquired', '2012-08-01T00:00:00Z', '2012-08-01', 'u-jane', created),
      u('pev-c1', 'p-civic', 'Damage', 'Rear bumper dented in a car park', 'Repaired under the other driver’s insurance.', '2019-11-04T18:00:00Z', 'u-jane'),
      sysRow('p-civic', 'Disposed', '2022-03-12T00:00:00Z', '2022-03-12', 'u-jane', created),
    ],
  };

  Object.assign(H, {
    pevTypeInfo(key) {
      return D.propertyEventTypeByKey[key]
        || { key, label: key || 'Other', icon: 'more_horiz', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)', unknown: true };
    },
    pevIsSystem(ev) { return !!ev && ev.source === 'system'; },
    pevAutoClause(key) { const t = D.propertyEventTypeByKey[key]; return (t && t.auto) || 'the application made this change'; },
    /* 'legal' | 'systemOnly' | 'illegal' — the 422 keyed `type`. */
    pevLegality(propertyType, key) {
      const cell = D.propertyEventTypeMatrix[propertyType];
      if (cell && !cell.includes(key)) return 'illegal';
      return D.propertyEventSystemOnly.includes(key) ? 'systemOnly' : 'legal';
    },
    pevFor(propertyId) { return (D.propertyEventSeed[propertyId] || []).slice(); },

    /* The detector (§8.5). Runs against an all-null "before" on create. A
       re-date of a non-null value writes nothing. Returns rows to stage in the
       same save as the property change. */
    pevTransitions(before, after, userId) {
      const b = before || {}, rows = [], now = nowIso(), today = now.slice(0, 10);
      const pid = after.id;
      if (!b.archived && after.archived) rows.push(sysRow(pid, 'Archived', after.archived.slice(0, 19) + 'Z', after.archived, userId, now));
      if (b.archived && !after.archived) rows.push(sysRow(pid, 'Unarchived', now, today, userId, now));
      if (!b.acquiredDate && after.acquiredDate) rows.push(sysRow(pid, 'Acquired', minNow(after.acquiredDate), after.acquiredDate, userId, now));
      if (b.acquiredDate && !after.acquiredDate) rows.push(sysRow(pid, 'AcquisitionDateCleared', now, today, userId, now));
      if (!b.disposedDate && after.disposedDate) rows.push(sysRow(pid, 'Disposed', minNow(after.disposedDate), after.disposedDate, userId, now));
      if (b.disposedDate && !after.disposedDate) rows.push(sysRow(pid, 'DisposalReversed', now, today, userId, now));
      return rows;
    },
  });
})();
