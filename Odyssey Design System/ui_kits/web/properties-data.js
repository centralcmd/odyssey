/* Properties seed + registries — the frontend half of *Property — Backend (Draft v8)*.
   A Property is a standalone record (not an account) with a fixed subtype
   (RealEstate = 0, Vehicle = 1), one detail sub-object matching it, an
   effective-dated estimate history (PropertyEstimates, sibling of
   AccountEstimates) and a smart-tag set (PropertySmartTags, sibling of
   ContractSmartTags). Ordinals are a wire contract: never renumbered. */
(() => {
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;

  /* PropertyType. Colours reuse the Property / Vehicle account-type hues so a
     house reads the same whichever record it is on during the side-by-side v1. */
  D.propertyTypes = [
    { key: 'RealEstate', enumValue: 0, label: 'Real estate', icon: 'home_work',      color: 'oklch(0.72 0.14 255)', soft: 'oklch(0.72 0.14 255 / 0.16)', estimateType: 'Property', detailsKey: 'realEstateDetails' },
    { key: 'Vehicle',    enumValue: 1, label: 'Vehicle',     icon: 'directions_car', color: 'oklch(0.78 0.14 170)', soft: 'oklch(0.78 0.14 170 / 0.16)', estimateType: 'Vehicle',  detailsKey: 'vehicleDetails' },
  ];
  D.realEstateKinds = [
    { key: 'House', enumValue: 0, label: 'House', icon: 'house' },
    { key: 'Apartment', enumValue: 1, label: 'Apartment', icon: 'apartment' },
    { key: 'Cabin', enumValue: 2, label: 'Cabin', icon: 'cabin' },
    { key: 'Plot', enumValue: 3, label: 'Plot', icon: 'landscape' },
    { key: 'Commercial', enumValue: 4, label: 'Commercial', icon: 'storefront' },
    { key: 'Other', enumValue: 5, label: 'Other', icon: 'home_work' },
  ];
  D.vehicleKinds = [
    { key: 'Car', enumValue: 0, label: 'Car', icon: 'directions_car' },
    { key: 'Motorcycle', enumValue: 1, label: 'Motorcycle', icon: 'two_wheeler' },
    { key: 'Boat', enumValue: 2, label: 'Boat', icon: 'sailing' },
    { key: 'Trailer', enumValue: 3, label: 'Trailer', icon: 'rv_hookup' },
    { key: 'Other', enumValue: 4, label: 'Other', icon: 'commute' },
  ];
  /* PropertyStatus — DERIVED, never stored: Archived > Disposed (on/before now) > Owned. */
  D.propertyStatuses = [
    { key: 'Owned', label: 'Owned', icon: 'check_circle', tone: 'income', dot: true },
    { key: 'Disposed', label: 'Disposed', icon: 'output', tone: 'outline', dot: true },
    { key: 'Archived', label: 'Archived', icon: 'inventory_2', tone: 'outline', dot: false },
  ];

  /* PropertyMaxSmartTagsPerProperty — shipped default; the page reads it from
     GET /api/property-limits, never as a constant. Ceiling = MaxFilterArrayLength. */
  D.PROPERTY_MAX_SMART_TAGS_PER_PROPERTY = 20;
  D.PROPERTY_MAX_SMART_TAGS_CEILING = 50;

  /* Tags and ledger rows the demo properties watch. Appended to the shared pool. */
  const newTags = [
    { id: 't20', name: 'Home maintenance', description: 'Repairs, trades and materials for the house', archived: null },
    { id: 't21', name: 'Property tax', description: 'Municipal property tax and fees', archived: null },
    { id: 't22', name: 'Fuel', description: 'Petrol and charging', archived: null },
    { id: 't23', name: 'Car service', description: 'Servicing, tyres and inspections', archived: null },
    { id: 't24', name: 'Mooring', description: 'Marina berth and boat upkeep', archived: null },
  ];
  newTags.forEach(t => { if (!D.tagById[t.id]) { D.tags.push(t); D.tagById[t.id] = t; } });
  const tx = (id, date, desc, tags, amount, icon) => ({ id, date, desc, account: '1', tags, currency: 'USD', amount, status: 'Approved', icon, dir: amount < 0 ? 'expense' : 'income' });
  [
    tx('xp1', '2026-09-12', 'Bay Roofing · Gutter repair', ['t20'], -840.00, 'roofing'),
    tx('xp2', '2026-08-03', 'Home Depot · Paint & fixings', ['t20'], -212.35, 'handyman'),
    tx('xp3', '2026-04-10', 'SF Treasurer · Property tax (2nd inst.)', ['t21'], -4310.00, 'account_balance'),
    tx('xp4', '2026-09-18', 'Chevron · Fuel', ['t22'], -64.10, 'local_gas_station'),
    tx('xp5', '2026-09-02', 'Shell · Fuel', ['t22'], -58.72, 'local_gas_station'),
    tx('xp6', '2026-06-21', 'Subaru of SF · 36k service', ['t23'], -389.00, 'car_repair'),
    tx('xp7', '2026-09-01', 'Sausalito Marina · Berth Sep', ['t24'], -310.00, 'anchor'),
    tx('xp8', '2026-08-01', 'Sausalito Marina · Berth Aug', ['t24'], -310.00, 'anchor'),
  ].forEach(t => { if (!D.transactions.some(x => x.id === t.id)) D.transactions.push(t); });

  const re = (o) => ({ kind: 'House', addressLine: null, postalCode: null, city: null, countryCode: null, cadastralNumber: null, livingAreaSqm: null, plotAreaSqm: null, buildYear: null, ...o });
  const ve = (o) => ({ kind: 'Car', registrationNumber: null, vin: null, make: null, model: null, modelYear: null, firstRegisteredDate: null, ...o });
  const base = { acquiredDate: null, disposedDate: null, notes: null, archived: null, realEstateDetails: null, vehicleDetails: null, createdAt: '2026-01-10T09:00:00Z', updatedAt: '2026-01-10T09:00:00Z' };

  D.properties = [
    { ...base, id: 'p-maple', name: 'Maple St Residence', description: 'Primary residence', type: 'RealEstate', currencyCode: 'USD', acquiredDate: '2018-09-05',
      notes: 'Seismic retrofit done 2021. Roof last replaced 2016.',
      realEstateDetails: re({ kind: 'House', addressLine: '1482 Maple St', postalCode: '94110', city: 'San Francisco', countryCode: 'US', cadastralNumber: 'APN 3612-044', livingAreaSqm: 186, plotAreaSqm: 412, buildYear: 1928 }) },
    { ...base, id: 'p-storgata', name: 'Storgata 14', description: 'Inherited apartment, let to a tenant', type: 'RealEstate', currencyCode: 'NOK', acquiredDate: '2019-06-01',
      realEstateDetails: re({ kind: 'Apartment', addressLine: 'Storgata 14', postalCode: '0155', city: 'Oslo', countryCode: 'NO', cadastralNumber: '208/451', livingAreaSqm: 72.5, buildYear: 1968 }) },
    { ...base, id: 'p-cabin', name: 'Lakeview cabin', description: 'Weekend cabin, shared with siblings', type: 'RealEstate', currencyCode: 'USD',
      notes: 'Acquisition date unknown — passed down in the family.',
      realEstateDetails: re({ kind: 'Cabin', addressLine: 'Lot 7, North Shore Rd', postalCode: '96143', city: 'Kings Beach', countryCode: 'US', livingAreaSqm: 58, plotAreaSqm: 2100, buildYear: 1974 }) },
    { ...base, id: 'p-ridge', name: 'Ridge lot', description: 'Undeveloped plot — plans shelved', type: 'RealEstate', currencyCode: 'USD', acquiredDate: '2017-04-18', archived: '2025-10-01T09:00:00Z',
      realEstateDetails: re({ kind: 'Plot', city: 'Sonoma', countryCode: 'US', cadastralNumber: 'APN 128-220-017', plotAreaSqm: 8100 }) },
    { ...base, id: 'p-outback', name: 'Subaru Outback', description: 'Family car', type: 'Vehicle', currencyCode: 'USD', acquiredDate: '2022-03-14',
      vehicleDetails: ve({ kind: 'Car', registrationNumber: '8XKR214', vin: '4S4BTGND5N3123456', make: 'Subaru', model: 'Outback Limited', modelYear: 2022, firstRegisteredDate: '2022-03-10' }) },
    { ...base, id: 'p-wren', name: 'Sea Wren', description: 'Daysailer, moored at Sausalito', type: 'Vehicle', currencyCode: 'USD', acquiredDate: '2020-05-02',
      vehicleDetails: ve({ kind: 'Boat', registrationNumber: 'CF4417KP', make: 'Catalina', model: '22 Sport', modelYear: 2004 }) },
    { ...base, id: 'p-civic', name: 'Honda Civic', description: 'First car — sold privately', type: 'Vehicle', currencyCode: 'USD', acquiredDate: '2012-08-01', disposedDate: '2022-03-12',
      vehicleDetails: ve({ kind: 'Car', registrationNumber: '6ABC123', vin: '19XFB2F59BE012345', make: 'Honda', model: 'Civic LX', modelYear: 2011, firstRegisteredDate: '2011-06-20' }) },
  ];

  const es = (pid, n, value, cur, from, note) => ({ id: `pe-${pid}-${n}`, propertyId: pid, value, currencyCode: cur, effectiveFrom: from, note: note || null, createdAtUtc: from + 'T09:00:00Z' });
  D.propertyEstimates = {
    'p-maple': [es('p-maple', 1, 540000, 'USD', '2018-09-05', 'Purchase price at closing'), es('p-maple', 2, 612000, 'USD', '2021-05-14', 'Refinance appraisal'), es('p-maple', 3, 668000, 'USD', '2023-08-01', 'Comparable sales'), es('p-maple', 4, 705000, 'USD', '2025-11-20', 'Broker valuation')],
    'p-storgata': [es('p-storgata', 1, 3900000, 'NOK', '2019-06-01', 'Probate valuation'), es('p-storgata', 2, 4600000, 'NOK', '2023-02-01'), es('p-storgata', 3, 4950000, 'NOK', '2026-01-01', 'Eiendomsverdi estimate')],
    'p-cabin': [],
    'p-ridge': [es('p-ridge', 1, 95000, 'USD', '2017-04-18', 'Purchase price')],
    'p-outback': [es('p-outback', 1, 38500, 'USD', '2022-03-14', 'Purchase price'), es('p-outback', 2, 31200, 'USD', '2024-03-01', 'KBB private-party'), es('p-outback', 3, 26800, 'USD', '2026-03-01', 'KBB private-party')],
    'p-wren': [es('p-wren', 1, 14000, 'USD', '2020-05-02', 'Purchase price'), es('p-wren', 2, 12500, 'USD', '2025-04-15')],
    'p-civic': [es('p-civic', 1, 17000, 'USD', '2012-08-01', 'Purchase price'), es('p-civic', 2, 5200, 'USD', '2021-09-01')],
  };
  /* Composite key (PropertyId, TransactionTagId); oldest association first. */
  D.propertySmartTagSeed = {
    'p-maple': ['t20', 't21', 't7'],
    'p-storgata': ['t21'],
    'p-outback': ['t22', 't23'],
    'p-wren': ['t24'],
  };

  const byKey = (arr) => Object.fromEntries(arr.map(x => [x.key, x]));
  const TY = byKey(D.propertyTypes), RK = byKey(D.realEstateKinds), VK = byKey(D.vehicleKinds), ST = byKey(D.propertyStatuses);
  const today = () => new Date().toISOString().slice(0, 10);

  Object.assign(H, {
    propToday: today,
    propTypeInfo: (k) => TY[k] || { key: k, label: 'Unrecognised type', icon: 'help', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)', estimateType: 'OtherAsset' },
    propKindInfo: (p) => {
      const d = p.type === 'Vehicle' ? p.vehicleDetails : p.realEstateDetails;
      const reg = p.type === 'Vehicle' ? VK : RK;
      return (d && reg[d.kind]) || { label: 'Other', icon: (TY[p.type] || {}).icon || 'category' };
    },
    propDetails: (p) => (p.type === 'Vehicle' ? p.vehicleDetails : p.realEstateDetails) || {},
    propStatusMeta: (k) => ST[k] || ST.Owned,
    /* The server's projection: Archived when Archived is set, else Disposed when
       DisposedDate is set and on or before now, else Owned. */
    propStatus: (p, asOf) => {
      if (p.archived) return 'Archived';
      if (p.disposedDate && p.disposedDate <= (asOf || today())) return 'Disposed';
      return 'Owned';
    },
    /* The in-force estimate: greatest EffectiveFrom on/before the cutoff,
       tie-broken by CreatedAtUtc — the one rule EstimateEffectiveDating owns. */
    propCurrentEstimate: (list, asOf) => {
      const cut = asOf || today();
      let cur = null;
      for (const e of list || []) {
        if (e.effectiveFrom > cut) continue;
        if (!cur || e.effectiveFrom > cur.effectiveFrom || (e.effectiveFrom === cur.effectiveFrom && e.createdAtUtc > cur.createdAtUtc)) cur = e;
      }
      return cur;
    },
    /* `search` covers Name, Description, Notes and the subtype identity fields. */
    propSearchHay: (p) => {
      const d = H.propDetails(p);
      return [p.name, p.description, p.notes, d.addressLine, d.city, d.cadastralNumber, d.registrationNumber, d.vin, d.make, d.model]
        .filter(Boolean).join(' ').toLowerCase();
    },
    /* Service normalisation, previewed live in the dialog. */
    propNormPlate: (v) => (v || '').replace(/\s+/g, '').toUpperCase(),
    propAddressText: (d) => [d.addressLine, [d.postalCode, d.city].filter(Boolean).join(' '), d.countryCode].filter(Boolean).join(', '),
    propArea: (n) => (n == null ? null : `${Number(n).toLocaleString('en-US', { maximumFractionDigits: 2 })} m²`),
  });
})();
