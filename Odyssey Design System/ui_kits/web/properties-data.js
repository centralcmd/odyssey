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

  /* ---- Property documents (*Property Documents — Backend, Draft v2*) ----
     DOCUMENT_CONTENT_TYPES mirrors the ONE server declaration
     (DocumentContentTypes.Allowed) that contract and property attach both name.
     It is checked against the file's SERVER-RECORDED content type at attach. */
  D.DOCUMENT_CONTENT_TYPES = ['application/pdf', 'image/png', 'image/jpeg', 'image/webp'];
  D.DOCUMENT_CONTENT_TYPE_LABEL = 'PDF, PNG, JPEG or WebP';

  /* Issuers the demo documents name. Appended to the shared contact pool. */
  [
    { id: 'c40', name: 'Golden Gate Appraisal Co.', normalizedName: 'GOLDEN GATE APPRAISAL CO.', type: 'Organization', description: 'Residential appraiser — refinance and broker valuations.', archived: null },
    { id: 'c41', name: 'Kartverket', normalizedName: 'KARTVERKET', type: 'Organization', description: 'Norwegian land registry — issues deeds and registry extracts.', archived: null },
    { id: 'c42', name: 'Bay Roofing', normalizedName: 'BAY ROOFING', type: 'Organization', description: 'Roofing contractor — installed the 2016 roof.', archived: null },
    { id: 'c43', name: 'California DMV', normalizedName: 'CALIFORNIA DMV', type: 'Organization', description: 'Vehicle and vessel registration.', archived: null },
  ].forEach(c => { if (!(D.contactById || {})[c.id]) { D.contacts.push(c); if (D.contactById) D.contactById[c.id] = c; } });

  /* Stored files (FileMetadata) the property documents reference, plus a few
     the Files store holds that are attached nowhere yet — the "From Files"
     picker lists this pool and the contract library together. */
  const fm = (id, name, contentType, size, uploaded) => ({ id, name, contentType, size, uploaded, uploadedByName: 'Owner Demo' });
  D.propertyFileLibrary = [
    fm('fm-maple-deed',     'maple_st_grant_deed_2018.pdf',     'application/pdf', '1.2 MB', '2018-09-12'),
    fm('fm-maple-purchase', 'maple_st_purchase_agreement.pdf',  'application/pdf', '2.4 MB', '2018-09-12'),
    fm('fm-maple-apprais',  'refinance_appraisal_2021.pdf',     'application/pdf', '860 KB', '2021-05-20'),
    fm('fm-maple-roof',     'roof_warranty_bay_roofing.pdf',    'application/pdf', '140 KB', '2016-07-02'),
    fm('fm-maple-plan',     'maple_floor_plan.png',             'image/png',       '2.1 MB', '2019-03-11'),
    fm('fm-maple-retro',    'seismic_retrofit_invoice.jpg',     'image/jpeg',      '1.4 MB', '2021-10-04'),
    fm('fm-maple-tax',      'sf_property_tax_2026.pdf',         'application/pdf', '98 KB',  '2026-02-10'),
    fm('fm-stor-deed',      'skjote_storgata_14.pdf',           'application/pdf', '420 KB', '2019-06-10'),
    fm('fm-stor-tilstand',  'tilstandsrapport_2019.pdf',        'application/pdf', '3.1 MB', '2019-05-02'),
    fm('fm-out-reg',        'outback_registration_card.jpg',    'image/jpeg',      '980 KB', '2026-03-02'),
    fm('fm-out-ins',        'meridian_auto_id_card_2026.pdf',   'application/pdf', '64 KB',  '2026-01-05'),
    fm('fm-out-service',    'subaru_36k_service.pdf',           'application/pdf', '210 KB', '2026-06-21'),
    fm('fm-wren-reg',       'sea_wren_vessel_registration.pdf', 'application/pdf', '120 KB', '2025-04-18'),
    fm('fm-wren-survey',    'hull_survey_photo.webp',           'image/webp',      '1.7 MB', '2025-04-15'),
    fm('fm-home-inv',       'home_inventory_export.html',       'text/html',       '36 KB',  '2026-08-30'),
    fm('fm-cabin-sheet',    'cabin_shared_costs.xlsx',          'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', '44 KB', '2026-07-19'),
  ];
  D.fileLibraryPool = () => [...D.propertyFileLibrary, ...(D.contractFileLibrary || [])];
  D.fileLibraryById = () => Object.fromEntries(D.fileLibraryPool().map(f => [f.id, f]));

  /* PropertyFile link rows, per property, oldest attachment first (the list
     order). Type + validity live on the LINK, never on FileMetadata. */
  const pf = (pid, n, fileMetadataId, kind, attachedAtUtc, v) => ({ id: `pf-${pid}-${n}`, propertyId: pid, fileMetadataId, kind,
    attachedByUserId: 'u-owner', attachedByName: 'Owner Demo', attachedAtUtc,
    validFrom: null, validTo: null, issuedAt: null, issuedBy: null, ...(v || {}) });
  D.propertyFileSeed = {
    'p-maple': [
      pf('p-maple', 1, 'fm-maple-deed', 'Deed', '2026-01-10T09:05:00Z', { validFrom: '2018-09-05', issuedAt: '2018-09-05' }),
      pf('p-maple', 2, 'fm-maple-purchase', 'PurchaseAgreement', '2026-01-10T09:06:00Z', { issuedAt: '2018-08-14' }),
      pf('p-maple', 3, 'fm-maple-apprais', 'Valuation', '2026-01-10T09:08:00Z', { issuedAt: '2021-05-14', issuedBy: 'c40' }),
      pf('p-maple', 4, 'fm-maple-roof', 'Warranty', '2026-01-11T18:20:00Z', { validFrom: '2016-06-30', validTo: '2036-06-30', issuedAt: '2016-06-30', issuedBy: 'c42' }),
      pf('p-maple', 5, 'fm-maple-plan', 'Drawing', '2026-02-02T10:00:00Z'),
      pf('p-maple', 6, 'fm-maple-tax', 'Tax', '2026-02-10T08:30:00Z', { validFrom: '2025-07-01', validTo: '2026-06-30', issuedAt: '2026-02-01' }),
    ],
    'p-storgata': [
      pf('p-storgata', 1, 'fm-stor-deed', 'Deed', '2026-01-10T09:10:00Z', { validFrom: '2019-06-01', issuedAt: '2019-06-07', issuedBy: 'c41' }),
      pf('p-storgata', 2, 'fm-stor-tilstand', 'Inspection', '2026-01-10T09:12:00Z', { issuedAt: '2019-05-02' }),
    ],
    'p-outback': [
      pf('p-outback', 1, 'fm-out-reg', 'Registration', '2026-03-02T12:00:00Z', { validFrom: '2026-03-10', validTo: '2027-03-10', issuedBy: 'c43' }),
      pf('p-outback', 2, 'fm-out-ins', 'Insurance', '2026-03-02T12:02:00Z', { validFrom: '2026-01-01', validTo: '2026-12-31', issuedAt: '2025-12-18', issuedBy: 'c20' }),
      pf('p-outback', 3, 'fm-out-service', 'Maintenance', '2026-06-21T16:40:00Z', { issuedAt: '2026-06-21' }),
    ],
    'p-wren': [
      pf('p-wren', 1, 'fm-wren-reg', 'Registration', '2025-04-18T11:00:00Z', { validFrom: '2025-04-18', validTo: '2027-04-18', issuedBy: 'c43' }),
    ],
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
    propFileTypeInfo: (k) => (D.propertyFileTypeByKey || {})[k]
      || { key: k, label: k || 'Other', icon: 'insert_drive_file', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' },
    /* ExistingPropertyFile → FilesTable row. Name, size and content type come
       from the referenced FileMetadata; kind + validity from the link. */
    propFileRow: (pf) => {
      const meta = D.fileLibraryById()[pf.fileMetadataId] || {};
      return { id: pf.id, fileMetadataId: pf.fileMetadataId, name: pf.name || meta.name || pf.fileMetadataId,
        kind: pf.kind, size: pf.size || meta.size || '—', uploaded: meta.uploaded || (pf.attachedAtUtc || '').slice(0, 10),
        contentType: pf.contentType || meta.contentType,
        validFrom: pf.validFrom || null, validTo: pf.validTo || null, issuedAt: pf.issuedAt || null, issuedBy: pf.issuedBy || null };
    },
    propContentTypeAllowed: (ct) => D.DOCUMENT_CONTENT_TYPES.includes(ct),
    /* Content type the Files API would record for an upload — by extension in the kit. */
    propContentTypeFor: (name) => ({ pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', html: 'text/html', htm: 'text/html' })[(name.split('.').pop() || '').toLowerCase()] || 'application/octet-stream',
    propContentTypeShort: (ct) => ({ 'application/pdf': 'PDF', 'image/png': 'PNG', 'image/jpeg': 'JPEG', 'image/webp': 'WebP', 'text/html': 'HTML' })[ct] || (ct || '').split('/').pop().split('.').pop().toUpperCase(),
    /* Name → PropertyFileType guess for uploads; the user re-tags freely. */
    propGuessFileType: (name) => {
      const n = name.toLowerCase();
      const rules = [[/deed|skjøte|skjote|grunnbok|title/, 'Deed'], [/purchase|kjøpekontrakt|sale/, 'PurchaseAgreement'], [/apprais|valuation|takst|verdi/, 'Valuation'],
        [/inspect|tilstand|eu-kontroll|survey|smog/, 'Inspection'], [/regist|vognkort/, 'Registration'], [/insur|policy|forsikring/, 'Insurance'], [/warrant|guarantee|garanti/, 'Warranty'],
        [/receipt|invoice|kvittering|faktura/, 'Receipt'], [/service|maint|repair/, 'Maintenance'], [/tax|skatt/, 'Tax'], [/plan|drawing|tegning|site/, 'Drawing']];
      const hit = rules.find(([re]) => re.test(n));
      return hit ? hit[1] : 'Other';
    },
  });
})();
