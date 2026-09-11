/* Contacts — v6 "Aliases & lifecycle dates" (on top of v5 "Extended Contacts").
   A base Contact record (DisplayName override + computed fallback,
   audit timestamps) discriminated by Type into a Person or Organization
   sub-record, plus three independently-managed contact collections
   (Addresses / Emails / Phone numbers), each row carrying a Label + a single
   Primary.

   v6 adds a fourth child collection — ALIASES, the alternative names a contact
   is actually known by, each with an optional FREE-TEXT label ("maiden name",
   "nickname", "trading as") — and four optional lifecycle scalars: a Person's
   MiddleName + DateOfDeath, an Organization's EstablishedDate + DissolvedDate.
   Aliases are names, not contact methods, so they get their own section ABOVE
   Contact information, rendered by the DS `ContactAliases` component. Alias
   values and MiddleName are SEARCHED; an alias LABEL is metadata and is not.
   Recording a death or a dissolution changes no state: the contact keeps its
   links and stays selectable everywhere, and only reads as historical (a card
   chip in text, a picker suffix).

   NOTE: this kit screen keeps its own local type registry + seed so the two
   downstream previews (page + New-contact dialog) render the new shape
   without disturbing the shared 6-value OdysseyData registry other cards use.
   In the app, ContactType is trimmed to Person=1 | Organization=2. */

/* ---- Type registry (trimmed to the two v5 values) ---- */
const CP_TYPES = [
  { key: 'Person',       label: 'Person',       icon: 'person',         color: 'oklch(0.80 0.15 150)', soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Organization', label: 'Organization', icon: 'corporate_fare', color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
];
const CP_TYPE_BY_KEY = Object.fromEntries(CP_TYPES.map(t => [t.key, t]));
const CP_TYPE_OPTIONS = CP_TYPES.map(t => ({ value: t.key, label: t.label }));
const CP_STATUS_OPTIONS = [
  { value: 'active',   label: 'Active' },
  { value: 'archived', label: 'Archived' },
];
const cpTone = (type) => { const m = CP_TYPE_BY_KEY[type] || CP_TYPE_BY_KEY.Person; return { bg: m.soft, fg: m.color }; };

/* ---- Sub-vocabularies (new OdsTypeRegistries entries) ---- */
const SEX_OPTIONS = [
  { value: 'Male',   label: 'Male' },
  { value: 'Female', label: 'Female' },
];
/* ---- Contact-method labels: one scope map, read off the DS namespace ----
   The three label enums now carry an organization vocabulary, and every member
   is scoped to the contact types it is valid for. ONE invariant: a contact
   method's label is valid for its contact's type — the picker offers only valid
   labels, the write path refuses the rest with a 422, and import + the
   type-switch remap CLAMP to Other rather than dropping a record.
   The map is the DS `ContactLabelScope`; a second copy in the kit is precisely
   the divergence the spec forbids. */
const CP_DS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
const CP_SCOPE = CP_DS.ContactLabelScope;
const labelsFor = (kind, type) => (CP_SCOPE ? CP_SCOPE.labelsFor(kind, type) : []);
const defaultLabel = (kind, type) => (CP_SCOPE ? CP_SCOPE.defaultFor(kind, type) : '');
const isLabelValid = (kind, key, type) => (CP_SCOPE ? CP_SCOPE.isValidFor(kind, key, type) : true);
const clampLabel = (kind, key, type) => (CP_SCOPE ? CP_SCOPE.clamp(kind, key, type) : key);
// Keyed `Other` fallback — organization members are appended AFTER Other, so a
// positional last-entry fallback would render an undefined ordinal as Branch /
// Claims / Direct: plausible, specific and wrong.
const labelMeta = (kind, key) => (CP_SCOPE ? CP_SCOPE.metaFor(kind, key) : { key, label: key, icon: 'category' });
// The 422 a contact-method write returns when the label is out of scope. Named
// here because the client surfaces the server's own message verbatim.
const labelScopeMessage = (kind, key, type) =>
  `Label '${key}' is not valid for ${type === 'Organization' ? 'an Organization' : 'a Person'} contact. `
  + `Valid labels: ${labelsFor(kind, type).map((l) => l.key).join(', ')}.`;

/* ---- Resolution + formatting ---- */
const resolvedName = (c) => {
  if (c.displayName && c.displayName.trim()) return c.displayName.trim();
  if (c.type === 'Person') return `${(c.person && c.person.firstName) || ''} ${(c.person && c.person.lastName) || ''}`.trim();
  return (c.org && c.org.legalName) || '';
};
// Norwegian address format: street line(s), then "<postal code> <city>", then country.
const addressLines = (a) => {
  const cityLine = [a.postalCode, a.city].filter(Boolean).join(' ').trim();
  return [a.line1, a.line2, cityLine, a.countryCode].filter(v => v && v.trim());
};
const uid = (p) => `${p}-${Math.random().toString(36).slice(2, 8)}`;
const cpToday = () => new Date().toISOString().slice(0, 10);

/* Atoms not bridged to the kit globals — read straight off the DS namespace. */
const { Menu: DSMenu, Toast: DSToast, ToastStack: DSToastStack, ContactAliases: DSContactAliases } = window.OdysseyDesignSystem_d5aa51 || {};

/* ---- Lifecycle state (deceased / dissolved) ----
   The meaning is always the chip's visible TEXT; the date makes it specific.
   Recording either date removes no capability. */
const cpLifecycle = (c) => {
  const H = window.OdysseyHelpers;
  const d = c.type === 'Person' ? (c.person || {}).dateOfDeath : (c.org || {}).dissolvedDate;
  if (!d) return null;
  const word = c.type === 'Person' ? 'Deceased' : 'Dissolved';
  return { word, date: d, text: `${word} ${H ? H.dateLong(d) : d}` };
};

/* ================= vCard (RFC 6350 v4.0) export + import sim (spec §6/§9) =================
   Export is real — each row serializes to an RFC-shaped VCARD block with §3.3
   escaping and §3.2 (75-octet) line folding, downloaded as text/vcard. Import
   is a simulated parse (the DS FileUpload abstracts away the raw bytes, exactly
   like the ICS precedent) that creates/updates by UID match and returns a
   VCardImportResult the page applies + surfaces. */
const cpExternalUid = (c) => c.externalUid || `urn:uuid:${c.id}`;
const vcEsc = (s) => String(s == null ? '' : s).replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;');
const vcFold = (line) => {
  if (line.length <= 75) return line;
  let out = line.slice(0, 75), rest = line.slice(75);
  while (rest.length) { out += '\r\n ' + rest.slice(0, 74); rest = rest.slice(74); }
  return out;
};
const vcRev = (iso) => { try { return new Date(iso).toISOString().replace(/[-:]/g, '').replace(/\.\d+/, ''); } catch (e) { return ''; } };
/* The nearest standard TYPE token for every label, and the labels that have
   none of their own — those and only those also emit X-ODYSSEY-LABEL, so an
   export carrying no organization label is byte-identical to the pre-change
   output. RFC 6350 dropped 3.0's `postal` ADR type, which is why Postal maps to
   `home` plus the extension. */
const VC_STD_TYPE = {
  address: { Home: 'home', Work: 'work', Billing: 'billing', Other: 'other', Postal: 'home', Visiting: 'work', Registered: 'work', Branch: 'work' },
  email:   { Home: 'home', Work: 'work', Other: 'other', General: 'work', Support: 'work', Sales: 'work', Billing: 'work', Claims: 'work' },
  phone:   { Home: 'home', Work: 'work', Mobile: 'cell', Other: 'other', Switchboard: 'voice,work', Support: 'voice,work', Sales: 'voice,work', Billing: 'voice,work', Claims: 'voice,work', Emergency: 'voice,work', Direct: 'voice,work' },
};
const VC_EXT_LABELS = {
  address: ['Postal', 'Visiting', 'Registered', 'Branch'],
  email: ['General', 'Support', 'Sales', 'Billing', 'Claims'],
  phone: ['Switchboard', 'Support', 'Sales', 'Billing', 'Claims', 'Emergency', 'Direct'],
};
// The X-ODYSSEY-LABEL value is always an enum member name from a closed set, so
// nothing user-supplied reaches the vCard parameter grammar.
const vcParam = (kind, label, pref) => {
  const token = (VC_STD_TYPE[kind] || {})[label];
  const ext = (VC_EXT_LABELS[kind] || []).indexOf(label) >= 0;
  return `${token ? ';TYPE=' + token : ''}${pref ? ';PREF=1' : ''}${ext ? ';X-ODYSSEY-LABEL=' + label : ''}`;
};

const buildVCard = (c) => {
  const L = ['BEGIN:VCARD', 'VERSION:4.0'];
  L.push('UID:' + cpExternalUid(c));
  L.push('FN:' + vcEsc(resolvedName(c)));
  if (c.type === 'Person') {
    const p = c.person || {};
    L.push('KIND:individual');
    L.push(`N:${vcEsc(p.lastName)};${vcEsc(p.firstName)};${vcEsc(p.middleName)};;`);
    if (p.title) L.push('TITLE:' + vcEsc(p.title));
    if (p.company) L.push('ORG:' + vcEsc(p.company));
    if (p.dateOfBirth) L.push('BDAY:' + p.dateOfBirth.replace(/-/g, ''));
    if (p.dateOfDeath) L.push('DEATHDATE:' + p.dateOfDeath.replace(/-/g, ''));
    if (p.sex === 'Male' || p.sex === 'Female') L.push('GENDER:' + (p.sex === 'Male' ? 'M' : 'F'));
  } else {
    const o = c.org || {};
    L.push('KIND:org');
    L.push('ORG:' + vcEsc(o.legalName));
    if (o.website && /^https?:\/\//i.test(o.website)) L.push('URL:' + o.website);
    if (o.organizationNumber) L.push('X-ODYSSEY-ORG-NUMBER:' + vcEsc(o.organizationNumber));
    if (o.establishedDate) L.push('X-ODYSSEY-ESTABLISHED:' + o.establishedDate.replace(/-/g, ''));
    if (o.dissolvedDate) L.push('X-ODYSSEY-DISSOLVED:' + o.dissolvedDate.replace(/-/g, ''));
  }
  /* Aliases: one GROUPED pair per alias, in API order — the label is a grouped
     PROPERTY, never a parameter. RFC 6350's quoted param-value excludes DQUOTE,
     so free text in a parameter has no in-band escape and would let `"` / `;`
     / `:` inject parameters downstream address books honour. A property value
     is TEXT, which the existing escaper round-trips losslessly. A contact with
     no aliases emits nothing, so its card is byte-identical to v5's. */
  (c.aliases || []).forEach((a, i) => {
    L.push(`item${i + 1}.NICKNAME:${vcEsc(a.value)}`);
    if (a.label) L.push(`item${i + 1}.X-ODYSSEY-ALIAS-LABEL:${vcEsc(a.label)}`);
  });
  (c.addresses || []).forEach((a) => {
    const street = [a.line1, a.line2].filter(Boolean).join(' ');
    const val = `;;${vcEsc(street)};${vcEsc(a.city)};${vcEsc(a.region)};${vcEsc(a.postalCode)};${vcEsc(a.countryCode)}`;
    L.push(`ADR${vcParam('address', a.label, a.isPrimary)}:${val}`);
  });
  (c.emails || []).forEach((e) => L.push(`EMAIL${vcParam('email', e.label, e.isPrimary)}:${vcEsc(e.value)}`));
  (c.phones || []).forEach((t) => L.push(`TEL${vcParam('phone', t.label, t.isPrimary)}:${vcEsc(t.value)}`));
  if (c.notes) L.push('NOTE:' + vcEsc(c.notes));
  if (c.updatedAt) L.push('REV:' + vcRev(c.updatedAt));
  L.push('END:VCARD');
  return L.map(vcFold).join('\r\n');
};
const buildVCardFile = (list) => list.map(buildVCard).join('\r\n') + '\r\n';

const vcSlug = (name) => (name || 'contact').trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 60) || 'contact';
const vcDateStamp = () => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`; };
const vcDownload = (text, filename) => {
  const blob = new Blob([text], { type: 'text/vcard;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename; document.body.appendChild(a); a.click();
  a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
};

/* ---- import simulation (see note above) ---- */
const VC_FIRST = ['Jordan', 'Riley', 'Casey', 'Avery', 'Morgan', 'Quinn', 'Skyler', 'Rowan'];
const VC_LAST = ['Blake', 'Ellis', 'Harper', 'Nguyen', 'Okafor', 'Santos', 'Vega', 'Walsh'];
let __vcSeq = 0;
const vcMakeCreated = (n) => {
  const now = new Date().toISOString();
  return Array.from({ length: n }, (_, i) => {
    const first = VC_FIRST[(__vcSeq + i) % VC_FIRST.length];
    const last = VC_LAST[(__vcSeq * 3 + i) % VC_LAST.length];
    return {
      id: uid('cp'), externalUid: `urn:uuid:imported-${Date.now()}-${i}`, type: 'Person', displayName: null,
      notes: 'Imported from vCard.', archived: null, createdAt: now, updatedAt: now,
      person: { firstName: first, lastName: last, middleName: null, dateOfBirth: null, dateOfDeath: null, sex: null, title: null, company: null },
      aliases: [],
      // Every imported row's label goes through the clamp before it is written:
      // an out-of-scope label becomes Other, never a dropped property.
      addresses: [], emails: [{ id: uid('e'), label: clampLabel('email', 'Home', 'Person'), isPrimary: true, value: `${first}.${last}`.toLowerCase() + '@example.com' }], phones: [],
    };
  });
};
const simulateImport = (file, rows, outcome) => {
  if (outcome === 'rejected') return { rejected: 'This file has more than the 5,000-contact limit (MaxVCardEntries). Split it into smaller files and import each.' };
  // A file whose name looks like an Odyssey export round-trips: every entry
  // matches an existing UID, so all update and none are created (idempotent).
  if (/^odyssey-contacts/i.test(file.name || '')) {
    const ids = rows.map((r) => r.id);
    return { result: { createdCount: 0, updatedCount: ids.length, skipped: [] }, createdRows: [], updatedIds: ids };
  }
  const created = vcMakeCreated(6); __vcSeq += 6;
  const updatedIds = rows.slice(0, 2).map((r) => r.id);
  const skipped = outcome === 'clean' ? [] : [
    { reason: 'Missing a usable name (no FN, N, or ORG)', count: 3, sampleNames: ['(no name)', 'vCard entry 14', 'vCard entry 31'] },
    { reason: 'Email address is not valid', count: 2, sampleNames: ['Taylor Reed', 'Harbor Dental Group'] },
    { reason: 'Name exceeds 128 characters', count: 1, sampleNames: ['Aaaaaaaaaaaaaaaaaaaaaaaaaaaa…'] },
    { reason: 'External ID already in use by another contact', count: 1, sampleNames: ['Michael Chen'] },
  ];
  return { result: { createdCount: created.length, updatedCount: updatedIds.length, skipped }, createdRows: created, updatedIds };
};

/* ---- Seed (new shape) ---- */
const CP_SEED = [
  {
    id: 'c1', type: 'Person', displayName: null, notes: 'Shares the flat — splits rent and utilities.',
    archived: null, createdAt: '2024-11-02T09:00:00Z', updatedAt: '2026-06-18T14:22:00Z',
    person: { firstName: 'Michael', lastName: 'Chen', middleName: 'Wei', dateOfBirth: '1990-04-12', dateOfDeath: null, sex: 'Male', title: 'Senior Engineer', company: 'Northwind Labs' },
    aliases: [
      { id: 'al1', value: 'Mike', label: 'nickname' },
      { id: 'al2', value: 'M. W. Chen', label: null },
    ],
    addresses: [
      { id: 'a1', label: 'Home', isPrimary: true, line1: 'Thorvald Meyers gate 12', line2: 'Leil. 3B', city: 'Oslo', region: '', postalCode: '0555', countryCode: 'NO' },
      { id: 'a1b', label: 'Postal', isPrimary: false, line1: 'Postboks 234 Sentrum', city: 'Oslo', region: '', postalCode: '0103', countryCode: 'NO' },
    ],
    emails: [{ id: 'e1', label: 'Home', isPrimary: true, value: 'michael.chen@fastmail.com' }],
    phones: [
      { id: 'p1', label: 'Mobile', isPrimary: true, value: '+1 415 555 0147' },
      { id: 'p2', label: 'Home', isPrimary: false, value: '+1 415 555 0912' },
    ],
  },
  {
    id: 'c2', type: 'Organization', displayName: 'Lakeside PM', notes: 'Apartment landlord — monthly rent, billing goes to the SoMa office.',
    archived: null, createdAt: '2024-08-14T09:00:00Z', updatedAt: '2026-05-30T10:05:00Z',
    org: { legalName: 'Lakeside Property Management LLC', organizationNumber: '81-2233445', website: 'https://lakesidepm.example.com', establishedDate: '2009-03-02', dissolvedDate: null },
    aliases: [{ id: 'al3', value: 'Lakeside Lettings', label: 'trading as' }],
    addresses: [
      { id: 'a2', label: 'Billing', isPrimary: true, line1: 'Storgata 55', line2: 'Etg. 14', city: 'Oslo', region: '', postalCode: '0184', countryCode: 'NO' },
      { id: 'a3', label: 'Branch', isPrimary: false, line1: 'Strandveien 200', city: 'Bergen', region: '', postalCode: '5003', countryCode: 'NO' },
    ],
    emails: [{ id: 'e2', label: 'Billing', isPrimary: true, value: 'billing@lakesidepm.example.com' }],
    phones: [{ id: 'p3', label: 'Switchboard', isPrimary: true, value: '+1 510 555 0110' }],
  },
  {
    id: 'c3', type: 'Person', displayName: null, notes: 'Family physician — reimbursements for out-of-pocket visits.',
    archived: null, createdAt: '2025-01-20T09:00:00Z', updatedAt: '2026-04-11T08:40:00Z',
    person: { firstName: 'Priya', lastName: 'Nair', middleName: null, dateOfBirth: '1962-07-19', dateOfDeath: '2024-03-11', sex: 'Female', title: 'Physician', company: 'Bay Area Health Partners' },
    aliases: [{ id: 'al4', value: 'Priya Menon', label: 'maiden name' }],
    addresses: [], emails: [{ id: 'e3', label: 'Work', isPrimary: true, value: 'p.nair@bahp.example.org' }],
    phones: [{ id: 'p4', label: 'Work', isPrimary: true, value: '+1 650 555 0088' }],
  },
  {
    id: 'c4', type: 'Organization', displayName: null, notes: 'Employer — payroll direct deposit.',
    archived: null, createdAt: '2023-06-01T09:00:00Z', updatedAt: '2026-06-01T09:00:00Z',
    org: { legalName: 'Northwind Labs, Inc.', organizationNumber: '98-7654321', website: 'https://northwind.example.com', establishedDate: '1998-11-04', dissolvedDate: null },
    aliases: [{ id: 'al5', value: 'Northwind', label: null }],
    addresses: [{ id: 'a4', label: 'Visiting', isPrimary: true, line1: 'Brobekkveien 80', city: 'Oslo', region: '', postalCode: '0598', countryCode: 'NO' }],
    emails: [{ id: 'e4', label: 'Billing', isPrimary: true, value: 'payroll@northwind.example.com' }],
    phones: [],
  },
  {
    id: 'c5', type: 'Person', displayName: 'Sarah (agent)', notes: 'Letting agent for the Fell Street flat.',
    archived: null, createdAt: '2024-09-11T09:00:00Z', updatedAt: '2026-03-22T16:10:00Z',
    person: { firstName: 'Sarah', lastName: 'Whitfield', middleName: null, dateOfBirth: '1985-11-30', dateOfDeath: null, sex: 'Female', title: null, company: 'Lakeside Property Management LLC' },
    aliases: [{ id: 'al6', value: 'Sarah Boyd', label: 'maiden name' }],
    addresses: [], emails: [{ id: 'e5', label: 'Work', isPrimary: true, value: 'sarah.w@lakesidepm.example.com' }],
    phones: [{ id: 'p5', label: 'Mobile', isPrimary: true, value: '+1 415 555 0333' }],
  },
  {
    id: 'c6', type: 'Organization', displayName: null, notes: 'Home & contents insurer — issues the property policy.',
    archived: null, createdAt: '2024-02-19T09:00:00Z', updatedAt: '2026-02-19T09:00:00Z',
    org: { legalName: 'Pacific Home Insurance Co.', organizationNumber: '45-6677889', website: null, establishedDate: '1974-06-01', dissolvedDate: '2023-06-30' },
    aliases: [
      { id: 'al7', value: 'Pacific Home Assurance', label: 'pre-merger name' },
      { id: 'al8', value: 'PHI', label: null },
    ],
    addresses: [{ id: 'a6', label: 'Registered', isPrimary: true, line1: 'Markveien 35', line2: '12. etg.', city: 'Oslo', region: '', postalCode: '0554', countryCode: 'NO' }],
    emails: [{ id: 'e6', label: 'Claims', isPrimary: true, value: 'claims@pacifichome.example.com' }],
    phones: [
      { id: 'p6', label: 'Switchboard', isPrimary: true, value: '+1 800 555 0199' },
      { id: 'p6b', label: 'Claims', isPrimary: false, value: '+1 800 555 0177' },
      { id: 'p6c', label: 'Emergency', isPrimary: false, value: '+1 800 555 0100' },
    ],
  },
  {
    id: 'c7', type: 'Person', displayName: null, notes: 'Dog walker — weekly, paid by transfer.',
    archived: null, createdAt: '2025-05-04T09:00:00Z', updatedAt: '2026-06-14T12:00:00Z',
    person: { firstName: 'Diego', lastName: 'Ramos', middleName: null, dateOfBirth: null, dateOfDeath: null, sex: null, title: null, company: null },
    aliases: [],
    addresses: [], emails: [], phones: [{ id: 'p7', label: 'Mobile', isPrimary: true, value: '+1 415 555 0270' }],
  },
  {
    id: 'c8', type: 'Organization', displayName: null, notes: 'Cancelled gym membership — kept for transaction history.',
    archived: '2025-03-02T09:00:00Z', createdAt: '2022-01-10T09:00:00Z', updatedAt: '2025-03-02T09:00:00Z',
    org: { legalName: 'FitZone Gym', organizationNumber: null, website: null, establishedDate: null, dissolvedDate: null },
    aliases: [],
    addresses: [], emails: [], phones: [],
  },
];

/* ================= small building blocks ================= */

/* Primary marker — always visible TEXT (never icon/colour alone; §10 a11y). */
const PrimaryBadge = () => <Chip tone="income" dot>Primary</Chip>;

const LabelChip = ({ kind, value }) => {
  const m = labelMeta(kind, value);
  return <Chip tone="outline" icon={m.icon}>{m.label}</Chip>;
};

/* A themed vertical section header used inside the detail panel. */
const cpSectionHead = (icon, title, count) => (
  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
    <MIcon name={icon} size={18} />
    <span style={{ font: '600 13px/1.2 var(--font-sans)', letterSpacing: '.01em' }}>{title}</span>
    <span style={{ font: '500 12px/1 var(--font-sans)', color: 'var(--mud-palette-text-secondary)' }}>{count > 0 ? count : ''}</span>
  </div>
);

/* ================= contact collection ================= */
/* One config-driven manager for Addresses / Emails / Phones: list rows with a
   Label chip + Primary badge + row actions (Edit, Set as primary, Delete) and
   an Add affordance opening an inline form. Local state (mockup): the same
   arbitration the service enforces (§9) runs here — setting one primary clears
   the siblings; the collection is the sole owner of the flag. */

/* The label vocabulary is no longer part of this config — it depends on the
   parent contact's type as well as the kind, so it is resolved per render
   through ContactLabelScope. */
const CONTACT_KINDS = {
  address: { title: 'Addresses',     icon: 'home',  addTitle: 'Add address',      avatar: 'location_on',     soft: 'oklch(0.77 0.14 55 / 0.15)',  fg: 'oklch(0.77 0.14 55)' },
  email:   { title: 'Emails',        icon: 'mail',  addTitle: 'Add email',        avatar: 'alternate_email', soft: 'oklch(0.72 0.16 295 / 0.15)', fg: 'oklch(0.72 0.16 295)' },
  phone:   { title: 'Phone numbers', icon: 'call',  addTitle: 'Add phone number', avatar: 'call',            soft: 'oklch(0.78 0.13 200 / 0.15)', fg: 'oklch(0.78 0.13 200)' },
};

// Single-line summary for the chip value (full detail stays in copy / edit).
const addressSummary = (a) => [a.line1, [a.postalCode, a.city].filter(Boolean).join(' ').trim(), a.countryCode].filter(v => v && v.trim()).join(', ');
const chipValue = (kind, item) => kind === 'address' ? (addressSummary(item) || item.line1 || 'Address') : item.value;

const blankFor = (kind, defaultLabel) => {
  if (kind === 'address') return { label: defaultLabel, isPrimary: false, line1: '', line2: '', city: '', region: '', postalCode: '', countryCode: '' };
  return { label: defaultLabel, isPrimary: false, value: '' };
};

const ContactForm = ({ kind, contactType, item, onCommit, onCancel, isFirst, mode }) => {
  const { useState } = React;
  const cfg = CONTACT_KINDS[kind];
  const [d, setD] = useState(item);
  const [err, setErr] = useState({});
  const set = (k) => (v) => { setD(s => ({ ...s, [k]: v })); if (err[k]) setErr(e => ({ ...e, [k]: undefined })); };
  const offered = labelsFor(kind, contactType);

  const validate = () => {
    const e = {};
    // Set membership, NOT non-emptiness. A label outside the offered set renders
    // the trigger's placeholder while the draft still holds it, so a merely
    // non-empty check would let an invisible value through to a server 422 on a
    // field the user never touched. Required-style wording for the same reason.
    if (!isLabelValid(kind, d.label, contactType)) e.label = 'Label is required.';
    if (kind === 'address') {
      if (!d.line1.trim()) e.line1 = 'Line 1 is required.';
      if (!d.city.trim()) e.city = 'City is required.';
      if (!/^[A-Za-z]{2}$/.test(d.countryCode.trim())) e.countryCode = 'Two-letter country code.';
    } else if (kind === 'email') {
      if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(d.value.trim())) e.value = 'Enter a valid email address.';
    } else {
      if (!/^[+\d][\d\s()\-]{5,}$/.test(d.value.trim())) e.value = 'Enter a valid phone number.';
    }
    setErr(e);
    return Object.keys(e).length === 0;
  };
  const commit = () => {
    if (!validate()) return;
    const clean = kind === 'address'
      ? { ...d, countryCode: d.countryCode.trim().toUpperCase() }
      : { ...d, value: d.value.trim() };
    onCommit(clean);
  };

  return (
    <Modal
      title={mode === 'edit' ? `Edit ${KIND_NOUN[kind]}` : `New ${KIND_NOUN[kind]}`}
      icon={CONTACT_KINDS[kind].avatar}
      onClose={onCancel}
      footer={<React.Fragment>
        <Button variant="text" onClick={onCancel}>Cancel</Button>
        <Button variant="filled" color="primary" icon={mode === 'edit' ? 'check' : 'add'} onClick={commit}>{mode === 'edit' ? 'Save changes' : `Create ${KIND_NOUN[kind]}`}</Button>
      </React.Fragment>}>
      <div style={{ display: 'grid', gap: 12 }}>
        <ContactMethodLabelSelect kind={kind} contactType={contactType} value={d.label} onChange={set('label')}
          required error={err.label}
          helper={err.label ? undefined : `${offered.length} offered for ${contactType === 'Organization' ? 'an organization' : 'a person'}`} />
        {kind === 'address' && (
          <React.Fragment>
            <Field label="Line 1" value={d.line1} onChange={set('line1')} error={err.line1} placeholder="Street name and number" maxLength={256} />
            <Field label="Line 2" value={d.line2} onChange={set('line2')} placeholder="Apartment, floor, etc. (optional)" maxLength={256} />
            <div style={{ display: 'grid', gridTemplateColumns: '140px 1fr', gap: 12 }}>
              <Field label="Postal code" value={d.postalCode} onChange={set('postalCode')} placeholder="0554" maxLength={32} />
              <Field label="City" value={d.city} onChange={set('city')} error={err.city} placeholder="Oslo" maxLength={128} />
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '140px 1fr', gap: 12 }}>
              <Field label="Country code" value={d.countryCode} onChange={set('countryCode')} error={err.countryCode} placeholder="NO" helper="Two letters" maxLength={2} />
              <div />
            </div>
          </React.Fragment>
        )}
        {kind === 'email' && (
          <Field label="Email address" value={d.value} onChange={set('value')} error={err.value} placeholder="name@example.com" maxLength={256} />
        )}
        {kind === 'phone' && (
          <Field label="Phone number" value={d.value} onChange={set('value')} error={err.value} placeholder="+47 22 00 00 00" helper="International format recommended" maxLength={32} />
        )}
      </div>
      <label style={cpStyles.primaryToggle}>
        <Switch checked={d.isPrimary} onChange={set('isPrimary')} disabled={isFirst} />
        <span>{isFirst ? 'Primary (first record)' : 'Set as primary'}</span>
      </label>
    </Modal>
  );
};

const ContactValue = ({ kind, item }) => {
  if (kind === 'address') {
    const lines = addressLines(item);
    return <div style={{ display: 'flex', flexDirection: 'column', gap: 1 }}>{lines.map((l, i) => (
      <span key={i} style={{ font: i === 0 ? '500 13px/1.4 var(--font-sans)' : '400 12.5px/1.45 var(--font-sans)', color: i === 0 ? 'var(--mud-palette-text-primary)' : 'var(--mud-palette-text-secondary)' }}>{l}</span>
    ))}</div>;
  }
  return <span style={{ font: '500 13px/1.4 var(--font-mono, ui-monospace)', letterSpacing: '.01em' }}>{item.value}</span>;
};

const contactCopyText = (kind, item) => kind === 'address' ? addressLines(item).join('\n') : item.value;

const COLL_OF = { address: 'addresses', email: 'emails', phone: 'phones' };
const KIND_NOUN = { address: 'address', email: 'email', phone: 'phone number' };
const KIND_TITLE = { address: 'Address', email: 'Email', phone: 'Phone number' };

/* One flat list across all three collections — the kind avatar makes each row
   self-describing, so no per-kind section headers. Adding is driven from the
   contact's action menu (addReq); primary arbitration stays per-kind. */
const ContactList = ({ c, onContacts, readOnly, addReq, onConsumeAdd, styleMode, bare, onProblem }) => {
  const { useState, useRef, useEffect } = React;
  const [editing, setEditing] = useState(null); // {kind,id}
  const [adding, setAdding] = useState(null);    // kind
  const [copiedId, setCopiedId] = useState(null);
  const copyTimer = useRef(null);

  useEffect(() => {
    if (addReq && !readOnly) { setEditing(null); setAdding(addReq.kind); onConsumeAdd(); }
  }, [addReq && addReq.nonce]);

  const listOf = (kind) => c[COLL_OF[kind]] || [];
  const change = (kind, next) => onContacts(c.id, COLL_OF[kind], next);
  const applyPrimary = (list, id) => list.map(x => ({ ...x, isPrimary: x.id === id }));
  const commitEdit = (kind, id, data) => {
    let next = listOf(kind).map(x => x.id === id ? { ...x, ...data } : x);
    if (data.isPrimary) next = applyPrimary(next, id);
    else if (!next.some(x => x.isPrimary) && next.length) next = applyPrimary(next, next[0].id);
    change(kind, next); setEditing(null);
  };
  const commitAdd = (kind, data) => {
    const id = uid(kind[0]);
    let next = [...listOf(kind), { ...data, id }];
    if (data.isPrimary || next.length === 1) next = applyPrimary(next, id);
    change(kind, next); setAdding(null);
  };
  const setPrimary = (kind, id) => {
    const row = listOf(kind).find(x => x.id === id);
    // "Set as primary" rebuilds the whole row from what is stored and re-PUTs
    // it, bypassing the picker — so a stored label that is out of scope for the
    // contact's type replays and the server refuses it with a 422. The user sees
    // the server's own message; nothing is lost, and editing the label once
    // clears it.
    if (row && !isLabelValid(kind, row.label, c.type)) {
      onProblem && onProblem(`Update failed: ${labelScopeMessage(kind, row.label, c.type)}`);
      return;
    }
    change(kind, applyPrimary(listOf(kind), id));
  };
  const remove = (kind, id) => {
    let next = listOf(kind).filter(x => x.id !== id);
    if (next.length && !next.some(x => x.isPrimary)) next = applyPrimary(next, next[0].id);
    change(kind, next);
  };
  const copy = (kind, item) => {
    const text = contactCopyText(kind, item);
    const done = () => { setCopiedId(item.id); clearTimeout(copyTimer.current); copyTimer.current = setTimeout(() => setCopiedId(null), 1400); };
    const fallback = () => {
      try {
        const ta = document.createElement('textarea');
        ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
        document.body.appendChild(ta); ta.select();
        document.execCommand('copy'); document.body.removeChild(ta);
      } catch (e) {}
      done();
    };
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(done, fallback);
    else fallback();
  };

  const all = ['address', 'email', 'phone'].flatMap(kind => listOf(kind).map(item => ({ kind, item })));
  const cards = styleMode !== 'rows';

  return (
    <div>
      {!bare && (
        <div className="cp-sub">
          <span className="cp-sub-label">Contact information</span>
          <span className="cp-sub-rule" />
          <span className="cp-sub-meta">{all.length} {all.length === 1 ? 'entry' : 'entries'}</span>
        </div>
      )}
      {all.length === 0 && (
        <div style={cpStyles.emptyRow}>
          <span style={{ color: 'var(--mud-palette-text-secondary)', font: '400 13px/1.4 var(--font-sans)' }}>
            {readOnly ? 'No contact details.' : 'No contact details yet — use the ⋯ menu to add an address, email, or phone number.'}
          </span>
        </div>
      )}
      <div className={cards ? 'cp-tile-grid' : undefined} style={cards ? undefined : cpStyles.rowWrap}>
        {all.map(({ kind, item }) => {
          const cfg = CONTACT_KINDS[kind];
          const menuItems = [
            { icon: copiedId === item.id ? 'check' : 'content_copy', label: copiedId === item.id ? 'Copied' : `Copy ${KIND_NOUN[kind]}`, onClick: () => copy(kind, item) },
            ...(!readOnly && !item.isPrimary ? [{ icon: 'star', label: 'Set as primary', onClick: () => setPrimary(kind, item.id) }] : []),
            ...(!readOnly ? [{ icon: 'edit', label: 'Edit', onClick: () => { setAdding(null); setEditing({ kind, id: item.id }); } }] : []),
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(item.id); } },
            ...(!readOnly ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: () => remove(kind, item.id) }] : []),
          ];
          if (cards) {
            const valueEl = chipValue(kind, item);
            const span = kind === 'address' ? { gridColumn: '1 / -1' } : kind === 'email' ? { gridColumn: 'span 2' } : { gridColumn: 'span 1' };
            return (
              <div key={item.id} className="cp-tile cp-contact-row" style={span} title={kind === 'address' ? addressLines(item).join(', ') : item.value}>
                <span className="cp-tile-menu"><ActionMenu items={menuItems} /></span>
                <div className="cp-tile-top">
                  <span className="cp-tile-ic" style={{ background: cfg.soft, color: cfg.fg }}><MIcon name={cfg.avatar} size={16} /></span>
                  <span className="cp-tile-kind">{KIND_TITLE[kind]}</span>
                </div>
                <div className="cp-tile-value" style={{ color: cfg.fg }}>{valueEl}</div>
                <div className="cp-tile-foot">
                  <span>{labelMeta(kind, item.label).label}</span>
                  {item.isPrimary && <React.Fragment><span className="cp-tile-sep">·</span><span className="cp-tile-primary">Primary</span></React.Fragment>}
                </div>
              </div>
            );
          }
          return (
            <div key={item.id} className="cp-contact-row" style={cpStyles.row}>
              <Avatar icon={cfg.avatar} tone={{ bg: cfg.soft, fg: cfg.fg }} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', marginBottom: 4 }}>
                  <LabelChip kind={kind} value={item.label} />
                  {item.isPrimary && <PrimaryBadge />}
                </div>
                <ContactValue kind={kind} item={item} />
              </div>
              <span className="cp-row-menu"><ActionMenu items={menuItems} /></span>
            </div>
          );
        })}
      </div>
      {!readOnly && adding && (
        <ContactForm mode="add" kind={adding} contactType={c.type} item={blankFor(adding, defaultLabel(adding, c.type))} isFirst={listOf(adding).length === 0}
          onCommit={(data) => commitAdd(adding, data)} onCancel={() => setAdding(null)} />
      )}
      {!readOnly && editing && (
        <ContactForm mode="edit" kind={editing.kind} contactType={c.type} item={listOf(editing.kind).find(x => x.id === editing.id)} isFirst={listOf(editing.kind).length === 1}
          onCommit={(data) => commitEdit(editing.kind, editing.id, data)} onCancel={() => setEditing(null)} />
      )}
    </div>
  );
};

/* ================= expanded body · DS RecordCard tiles =================
   The Contacts rollout of the DS record-card pattern. The body carries the
   record's FULL field set as InfoTiles — including what the collapsed header
   already shows: at tile scale each value arrives with its own label, so the
   header's meta line and a labelled Job title tile read as two different
   things. Person and Organization contribute their own field sets (they are
   mutually exclusive); everything else is the base Contact. Notes is the wide
   content tile, so a long note wraps across the grid instead of squeezing into
   a column. */
const CpTiles = ({ c }) => {
  const H = window.OdysseyHelpers;
  const meta = CP_TYPE_BY_KEY[c.type] || CP_TYPE_BY_KEY.Person;
  const status = H.archivedStatus(c);
  const isPerson = c.type === 'Person';
  const p = c.person || {}, o = c.org || {};
  const website = o.website && /^https?:\/\//i.test(o.website) ? o.website : null;
  return (
    <InfoTileGrid dense>
      <InfoTile icon="badge" label="Display name" value={resolvedName(c) || '—'} valueVariant="text"
        foot={c.displayName ? 'override' : 'computed from the name fields'} />
      <InfoTile icon={meta.icon} iconColor={meta.color} iconSoft={meta.soft}
        label="Type" value={meta.label} valueVariant="text" foot="fixed after creation" />
      {isPerson ? (
        <React.Fragment>
          <InfoTile icon="person" label="First name" value={p.firstName || '—'} valueVariant="text" />
          {/* A middle name is stored, shown and searchable — but it is NOT part of
              the "First Last" fallback, so no contact's display name or sort
              position shifts. No value renders no tile. */}
          {p.middleName ? <InfoTile icon="person" label="Middle name" value={p.middleName} valueVariant="text" /> : null}
          <InfoTile icon="person" label="Last name" value={p.lastName || '—'} valueVariant="text" />
          {p.dateOfBirth ? (
            <InfoTile icon="cake" label="Date of birth" value={H.dateLong(p.dateOfBirth)} valueVariant="sm" />
          ) : null}
          {p.dateOfDeath ? (
            <InfoTile icon="event_busy" label="Date of death" value={H.dateLong(p.dateOfDeath)} valueVariant="sm"
              foot="the record stays live — nothing is archived" />
          ) : null}
          {p.sex ? <InfoTile icon="wc" label="Sex" value={p.sex} valueVariant="text" /> : null}
          {p.title ? <InfoTile icon="work" label="Job title" value={p.title} valueVariant="text" /> : null}
          {p.company ? (
            <InfoTile icon="corporate_fare" label="Company" value={p.company} valueVariant="text"
              foot="free text — not a linked contact" />
          ) : null}
        </React.Fragment>
      ) : (
        <React.Fragment>
          <InfoTile icon="corporate_fare" label="Legal name" value={o.legalName || '—'} valueVariant="text" />
          {o.organizationNumber ? (
            <InfoTile icon="pin" label="Organization number" value={o.organizationNumber} />
          ) : null}
          {o.website ? (
            <InfoTile icon="link" label="Website" valueVariant="text"
              value={website ? <a href={website} target="_blank" rel="noopener noreferrer">{o.website}</a> : o.website} />
          ) : null}
          {/* Either date stands alone: the founding date of an old institution is
              frequently unknown, and a dissolved body may have no recorded one. */}
          {o.establishedDate ? (
            <InfoTile icon="foundation" label="Established" value={H.dateLong(o.establishedDate)} valueVariant="sm" />
          ) : null}
          {o.dissolvedDate ? (
            <InfoTile icon="event_busy" label="Dissolved" value={H.dateLong(o.dissolvedDate)} valueVariant="sm"
              foot="reads as historical — still linkable" />
          ) : null}
        </React.Fragment>
      )}
      <InfoTile icon={c.archived ? 'inventory_2' : 'task_alt'} label="Status" value={status.label} valueVariant="text"
        className={c.archived ? 'muted' : 'tone-income'}
        foot={c.archived ? `since ${H.dateTime(c.archived)}` : 'in the default list'} />
      <InfoTile icon="schedule" label="Created" value={H.dateTime(c.createdAt)} valueVariant="sm" />
      <InfoTile icon="update" label="Updated" value={H.dateTime(c.updatedAt)} valueVariant="sm"
        foot="bumped by any address, email, phone or alias change" />
    </InfoTileGrid>
  );
};

/* One contact record (DS RecordCard). The list owns ONE openId, so opening a
   card closes its siblings. */
const CpRecordCard = ({ c, open, onToggle, onSave, onDelete, onContacts, onAliases, onExportRow, contactStyle, onProblem, onAnnounce, perms = {}, aliasCap }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const [showEdit, setShowEdit] = useState(false);
  const [addReq, setAddReq] = useState(null); // {kind, nonce}
  const meta = CP_TYPE_BY_KEY[c.type] || CP_TYPE_BY_KEY.Person;
  const status = H.archivedStatus(c);
  const isPerson = c.type === 'Person';
  const p = c.person || {}, o = c.org || {};
  const primaryOf = (list) => (list || []).find((x) => x.isPrimary) || (list || [])[0] || null;
  const email = primaryOf(c.emails), phone = primaryOf(c.phones), addr = primaryOf(c.addresses);
  const role = isPerson
    ? [p.title, p.company].filter(Boolean).join(' · ')
    : (o.organizationNumber || (o.website || ''));
  const entries = contactCount(c);
  const aliases = c.aliases || [];
  const life = cpLifecycle(c);
  const canUpdate = perms.update !== false;
  const canCreate = perms.create !== false;
  const canDelete = perms.delete !== false;
  const requestAdd = (kind) => { if (!open) onToggle(true); setAddReq({ kind, nonce: Date.now() }); };
  /* Alias writes are ordinary child-collection writes: each one bumps the
     parent UpdatedAt, exactly as an address / email / phone change does. */
  const addAlias = (value, label) => onAliases(c.id, [...aliases, { id: uid('al'), value, label }]);
  const editAlias = (id, value, label) => onAliases(c.id, aliases.map((a) => (a.id === id ? { ...a, value, label } : a)));
  const deleteAlias = (id) => onAliases(c.id, aliases.filter((a) => a.id !== id));

  if (!RecordCard || !InfoTileGrid || !InfoTile) return null;

  return (
    <div>
      <RecordCard
        icon={meta.icon}
        accent={meta.color}
        accentSoft={meta.soft}
        name={resolvedName(c)}
        chips={<React.Fragment>
          <Chip tone={status.tone} dot>{status.label}</Chip>
          {/* Deceased / dissolved sits AFTER the status chip and says so in text
              — the card's Name is a string, so the marker cannot be folded into
              the accessible heading. */}
          {life ? <Chip tone="outline" icon="event_busy">{life.text}</Chip> : null}
        </React.Fragment>}
        meta={[
          <span className="row gap-1" style={{ alignItems: 'center' }}><MIcon name={meta.icon} size={14} /><span>{meta.label}</span></span>,
          role ? <span className="row gap-1" style={{ alignItems: 'center' }}><MIcon name={isPerson ? 'work' : 'pin'} size={14} /><span>{role}</span></span> : null,
          email ? <span className="row gap-1" style={{ alignItems: 'center' }}><MIcon name="mail" size={14} /><span className="mono">{email.value}</span></span> : null,
          !email && phone ? <span className="row gap-1" style={{ alignItems: 'center' }}><MIcon name="call" size={14} /><span className="mono">{phone.value}</span></span> : null,
          !email && !phone && addr ? <span className="row gap-1" style={{ alignItems: 'center' }}><MIcon name="location_on" size={14} /><span>{addressLines(addr).join(', ')}</span></span> : null,
        ]}
        counts={[
          { icon: 'location_on', value: (c.addresses || []).length, label: 'Addresses' },
          { icon: 'mail', value: (c.emails || []).length, label: 'Emails' },
          { icon: 'call', value: (c.phones || []).length, label: 'Phone numbers' },
        ].filter((k) => k.value > 0)}
        dimmed={!!c.archived}
        open={open}
        onToggle={onToggle}
        actions={<ActionMenu items={[
          { icon: 'edit', label: 'Edit contact', onClick: () => setShowEdit(true) },
          { icon: 'download', label: 'Export vCard', onClick: () => onExportRow && onExportRow(c) },
          ...(c.archived ? [] : [
            { divider: true },
            ...(canCreate ? [{ icon: 'badge', label: 'New alias', onClick: () => requestAdd('alias') }] : []),
            { icon: 'add_location_alt', label: 'New address', onClick: () => requestAdd('address') },
            { icon: 'alternate_email', label: 'New email', onClick: () => requestAdd('email') },
            { icon: 'add_call', label: 'New phone number', onClick: () => requestAdd('phone') },
          ]),
          { divider: true },
          { icon: c.archived ? 'unarchive' : 'inventory_2', label: c.archived ? 'Restore' : 'Archive',
            onClick: () => onSave(c.id, { archived: c.archived ? null : new Date().toISOString() }) },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(c.id); } },
          { divider: true },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(c.id) },
        ]} />}
        details={<CpTiles c={c} />}
        content={(
          <InfoTileGrid>
            <InfoTile icon="sticky_note_2" label="Notes" wide value={c.notes || 'No notes'}
              valueVariant="text" className={c.notes ? undefined : 'muted'} />
          </InfoTileGrid>
        )}
      >
        <SectionDivider label="Aliases" meta={`${aliases.length} ${aliases.length === 1 ? 'alias' : 'aliases'}`} />
        {DSContactAliases ? (
          <DSContactAliases
            aliases={aliases}
            canCreate={canCreate} canUpdate={canUpdate} canDelete={canDelete}
            archived={!!c.archived}
            cap={aliasCap}
            addRequest={addReq && addReq.kind === 'alias' ? addReq : null}
            onConsumeAddRequest={() => setAddReq(null)}
            onAdd={addAlias} onEdit={editAlias} onDelete={deleteAlias}
            onAnnounce={onAnnounce} />
        ) : (
          /* Bundle-lag fallback (same pattern as SeverityIcon / DateField): the
             alias tiles read, but adding and editing live in the DS component. */
          <div className="odc-aliases">
            {aliases.length === 0
              ? <p className="odc-aliases-empty">No aliases yet — use the ⋯ menu to add one.</p>
              : <div className="odc-alias-grid">{aliases.map((a) => (
                <div key={a.id} className="odc-alias-tile">
                  <div className="odc-alias-top">
                    <span className="odc-alias-ic" aria-hidden="true"><MIcon name="badge" size={15} /></span>
                    <span className="odc-alias-kind">Alias</span>
                  </div>
                  <div className="odc-alias-value" title={a.value}>{a.value}</div>
                  {a.label ? <div className="odc-alias-foot">{a.label}</div> : null}
                </div>
              ))}</div>}
          </div>
        )}
        <SectionDivider label="Contact information" meta={`${entries} ${entries === 1 ? 'entry' : 'entries'}`} />
        <ContactList c={c} onContacts={onContacts} readOnly={!!c.archived} bare onProblem={onProblem}
          addReq={addReq && addReq.kind !== 'alias' ? addReq : null} onConsumeAdd={() => setAddReq(null)} styleMode={contactStyle} />
      </RecordCard>
      {showEdit && <AddContactModal contact={c} onClose={() => setShowEdit(false)}
        onSave={(id, patch) => { onSave(id, patch); setShowEdit(false); }} />}
    </div>
  );
};

const contactCount = (c) => (c.addresses || []).length + (c.emails || []).length + (c.phones || []).length;

/* ================= shared Person / Organization field sets ================= */
const PersonFields = ({ d, set, err }) => (
  <React.Fragment>
    <FormRow>
      <Field label="First name" value={d.firstName} onChange={set('firstName')} error={err.firstName} required autoFocus maxLength={128} />
      <Field label="Last name" value={d.lastName} onChange={set('lastName')} error={err.lastName} required maxLength={128} />
    </FormRow>
    {/* Stored and searchable, but deliberately outside the "First Last"
        display-name fallback — so adding one shifts no name and no sort. */}
    <Field label="Middle name" value={d.middleName} onChange={set('middleName')} placeholder="As it appears on a passport or bank record" helper="Optional · not part of the display name" maxLength={128} />
    {/* Both dates are DateFields, not bare pickers: the birth/death pair is
        checked from BOTH sides, so each one needs its own error channel. */}
    <FormRow>
      <DateField label="Date of birth" value={d.dateOfBirth || null} onChange={set('dateOfBirth')}
        max={cpToday()} error={err.dateOfBirth} optional help={err.dateOfBirth ? undefined : 'Cannot be in the future'} />
      <DateField label="Date of death" value={d.dateOfDeath || null} onChange={set('dateOfDeath')}
        max={cpToday()} error={err.dateOfDeath} optional
        help={err.dateOfDeath ? undefined : 'Recording it archives nothing'} />
    </FormRow>
    <FormRow>
      <Select label="Sex" value={d.sex} onChange={set('sex')} options={SEX_OPTIONS} helper="Optional" placeholder="Unspecified" />
      <Field label="Job title" value={d.title} onChange={set('title')} placeholder="e.g. Senior Engineer" helper="Optional" maxLength={128} />
    </FormRow>
    <Field label="Company" value={d.company} onChange={set('company')} placeholder="Employer name (optional)" helper="A free-text note — not linked to another contact" maxLength={256} />
  </React.Fragment>
);

const OrgFields = ({ d, set, err }) => (
  <React.Fragment>
    <Field label="Legal name" value={d.legalName} onChange={set('legalName')} error={err.legalName} required autoFocus placeholder="e.g. Lakeside Property Management LLC" maxLength={256} />
    <FormRow>
      <Field label="Organization number" value={d.organizationNumber} onChange={set('organizationNumber')} placeholder="Optional" maxLength={64} />
      <Field label="Website" value={d.website} onChange={set('website')} error={err.website} placeholder="https://example.com" helper="http/https only" maxLength={2048} />
    </FormRow>
    {/* "Dissolved", the term a business register publishes (No. oppløst) — not
        "closed". Either date may stand without the other. */}
    <FormRow>
      <DateField label="Established" value={d.establishedDate || null} onChange={set('establishedDate')}
        max={cpToday()} error={err.establishedDate} optional help={err.establishedDate ? undefined : 'Cannot be in the future'} />
      <DateField label="Dissolved" value={d.dissolvedDate || null} onChange={set('dissolvedDate')}
        max={cpToday()} error={err.dissolvedDate} optional
        help={err.dissolvedDate ? undefined : 'Reads as historical — still linkable'} />
    </FormRow>
  </React.Fragment>
);

const displayNameHint = (type) => type === 'Person'
  ? 'Defaults to "First Last" if left blank'
  : 'Defaults to the legal name if left blank';

/* Contact record editing reuses AddContactModal in edit mode
   (row Edit → setEditCp); there is no inline edit panel. */

/* Sorting is client-side over the card list (the DS rollout has no column
   headers); the SortSelect in the page header owns the field + direction. */
const cpSortVal = (c, key) => {
  switch (key) {
    case 'name': return resolvedName(c).toLowerCase();
    case 'type': return c.type;
    case 'status': return c.archived ? 1 : 0;
    default: return 0;
  }
};

/* ================= New / Edit contact dialog ================= */
const AddContactModal = ({ onClose, onCreate, contact, onSave }) => {
  const { useState } = React;
  const isEdit = !!contact;
  const cp = contact || {};
  const [type, setType] = useState(cp.type || 'Person');
  const [displayName, setDisplayName] = useState(cp.displayName || '');
  const [draft, setDraft] = useState({
    firstName: (cp.person && cp.person.firstName) || '', lastName: (cp.person && cp.person.lastName) || '',
    middleName: (cp.person && cp.person.middleName) || '',
    dateOfBirth: (cp.person && cp.person.dateOfBirth) || '', dateOfDeath: (cp.person && cp.person.dateOfDeath) || '',
    sex: (cp.person && cp.person.sex) || '',
    title: (cp.person && cp.person.title) || '', company: (cp.person && cp.person.company) || '',
    legalName: (cp.org && cp.org.legalName) || '', organizationNumber: (cp.org && cp.org.organizationNumber) || '',
    website: (cp.org && cp.org.website) || '',
    establishedDate: (cp.org && cp.org.establishedDate) || '', dissolvedDate: (cp.org && cp.org.dissolvedDate) || '',
    notes: cp.notes || '',
  });
  const [err, setErr] = useState({});
  const set = (k) => (v) => { setDraft(s => ({ ...s, [k]: v })); if (err[k]) setErr(e => ({ ...e, [k]: undefined })); };

  // Switching Type discards the previously-visible field set (§3 — the two are
  // mutually exclusive; stale hidden values are never retained or submitted).
  const changeType = (t) => {
    if (t === type) return;
    setType(t);
    setErr({});
    // Notes belong to the contact itself, not to either field set — kept across a Type switch.
    setDraft(s => ({ firstName: '', lastName: '', middleName: '', dateOfBirth: '', dateOfDeath: '', sex: '', title: '', company: '', legalName: '', organizationNumber: '', website: '', establishedDate: '', dissolvedDate: '', notes: s.notes }));
  };

  const submit = () => {
    const e = {};
    const today = cpToday();
    if (type === 'Person') {
      if (!draft.firstName.trim()) e.firstName = 'Required.';
      if (!draft.lastName.trim()) e.lastName = 'Required.';
      if (draft.dateOfBirth && draft.dateOfBirth > today) e.dateOfBirth = 'Cannot be in the future.';
      if (draft.dateOfDeath && draft.dateOfDeath > today) e.dateOfDeath = 'Cannot be in the future.';
      // The pair is checked from BOTH sides, so the invariant cannot be broken
      // by editing either field: the error renders on the field just changed.
      if (draft.dateOfBirth && draft.dateOfDeath && draft.dateOfDeath < draft.dateOfBirth) {
        e.dateOfDeath = 'Cannot be before the date of birth.';
      }
    } else {
      if (!draft.legalName.trim()) e.legalName = 'Legal name is required for an organization.';
      if (draft.website && !/^https?:\/\//i.test(draft.website.trim())) e.website = 'Must start with http:// or https://';
      if (draft.establishedDate && draft.establishedDate > today) e.establishedDate = 'Cannot be in the future.';
      if (draft.dissolvedDate && draft.dissolvedDate > today) e.dissolvedDate = 'Cannot be in the future.';
      if (draft.establishedDate && draft.dissolvedDate && draft.dissolvedDate < draft.establishedDate) {
        e.dissolvedDate = 'Cannot be before the established date.';
      }
    }
    if (Object.keys(e).length) { setErr(e); return; }
    const personPatch = () => ({
      firstName: draft.firstName.trim(), lastName: draft.lastName.trim(),
      middleName: draft.middleName.trim() || null,
      dateOfBirth: draft.dateOfBirth || null, dateOfDeath: draft.dateOfDeath || null,
      sex: draft.sex || null, title: draft.title.trim() || null, company: draft.company.trim() || null,
    });
    const orgPatch = () => ({
      legalName: draft.legalName.trim(), organizationNumber: draft.organizationNumber.trim() || null,
      website: draft.website.trim() || null,
      establishedDate: draft.establishedDate || null, dissolvedDate: draft.dissolvedDate || null,
    });
    if (isEdit) {
      const patch = { displayName: displayName.trim() || null, notes: draft.notes.trim() || null };
      if (type === 'Person') patch.person = personPatch();
      else patch.org = orgPatch();
      onSave && onSave(cp.id, patch);
      return;
    }
    // Aliases are NOT part of the create dialog: they are added from the
    // expanded card once the contact exists, exactly as addresses, emails and
    // phone numbers are. So the create DTO carries an empty list.
    const dto = { type, displayName: displayName.trim() || null, notes: draft.notes.trim() || null, archived: null, aliases: [], addresses: [], emails: [], phones: [] };
    if (type === 'Person') dto.person = personPatch();
    else dto.org = orgPatch();
    onCreate && onCreate(dto);
  };

  const typeMeta = CP_TYPE_BY_KEY[type] || CP_TYPE_BY_KEY.Person;

  return (
    <Modal
      title={isEdit ? 'Edit contact' : 'New contact'}
      subtitle="A person or organization that money moves to or from."
      icon={isEdit ? 'edit' : 'store'}
      onClose={onClose}
      footer={<React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon={isEdit ? 'check' : 'add'} onClick={submit}>{isEdit ? 'Save changes' : 'Create contact'}</Button>
      </React.Fragment>}>
      {isEdit
        ? <FieldShell label="Type" helper="Type can’t be changed after creation."><div style={cpStyles.typeLock}><Chip tone="outline" icon={typeMeta.icon}>{typeMeta.label}</Chip></div></FieldShell>
        : <ContactTypeSelect label="Type" value={type} onChange={changeType} types={CP_TYPES} helper="Choose Person or Organization — the matching fields appear below." />}
      <div style={cpStyles.dialogFields}>
        {type === 'Person'
          ? <PersonFields d={draft} set={set} err={err} />
          : <OrgFields d={draft} set={set} err={err} />}
        <Field label="Display name" value={displayName} onChange={setDisplayName} placeholder="Optional override" helper={displayNameHint(type)} maxLength={128} />
        <NoteField label="Notes" optional maxLength={1024} value={draft.notes} onChange={set('notes')}
          placeholder={type === 'Person' ? 'How you know them, what they invoice for…' : 'What this organization is to you, billing quirks…'} />
      </div>
    </Modal>
  );
};

/* ================= page ================= */
const Contacts = ({ tweaks = {} }) => {
  const { useState, useEffect, useMemo } = React;
  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [typeFilter, setTypeFilter] = useState([]);
  const [statusFilter, setStatusFilter] = useState([]);
  const [adding, setAdding] = useState(false);
  const [rows, setRows] = useState(CP_SEED);
  const [sort, setSort] = useState({ key: 'name', dir: 'asc' });
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  // The list owns ONE openId — opening a record closes its siblings.
  const [openId, setOpenId] = useState('c1');
  const [importOpen, setImportOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [toast, setToast] = useState(null);
  const canImport = tweaks.cpCanImport !== false; // requires contacts.create AND .update
  const pushToast = (severity, message) => setToast({ severity, message, k: Date.now() });

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  // Residual state (§9): a row whose STORED label is invalid for its contact's
  // type. Unreachable through the UI — it takes a concurrent type switch on the
  // API (the contact PUT carries no concurrency token), a rollback then
  // roll-forward, or a hand-edited row. Toggle it to see the picker fall back to
  // its placeholder and "Set as primary" refuse with the server's 422 message.
  const residual = !!tweaks.cpResidualLabel;
  useEffect(() => {
    setRows(prev => prev.map(c => c.id === 'c2'
      ? { ...c, phones: (c.phones || []).map(p => p.id === 'p3' ? { ...p, label: residual ? 'Home' : 'Switchboard' } : p) }
      : c));
  }, [residual]);

  const touch = (c) => ({ ...c, updatedAt: new Date().toISOString() });
  const createCp = (dto) => {
    const now = new Date().toISOString();
    setRows(prev => [{ id: uid('cp'), createdAt: now, updatedAt: now, ...dto }, ...prev]);
    setAdding(false);
  };
  const onSave = (id, patch) => setRows(prev => prev.map(c => c.id === id ? touch({ ...c, ...patch }) : c));
  // A contact named as an insurer, an insured contact or a beneficiary on any
  // policy cannot be deleted by the ordinary route — the delete is refused and
  // the dialog carries the supported detach path (see ContactLinksBlockedModal).
  const [blocked, setBlocked] = useState(null);
  const removeRow = (id) => setRows(prev => prev.filter(c => c.id !== id));
  const onDelete = (id) => {
    const linking = (window.OdysseyHelpers.insPoliciesLinkingContact || (() => []))(id);
    if (linking.length) { setBlocked({ contact: rows.find(c => c.id === id) || { id, name: 'This contact' }, blocking: linking }); return; }
    removeRow(id);
  };
  // Any child (address/email/phone) mutation bumps the parent UpdatedAt (§9).
  const onContacts = (id, coll, value) => setRows(prev => prev.map(c => c.id === id ? touch({ ...c, [coll]: value }) : c));
  // Aliases are the fourth child collection, on the same rule.
  const onAliases = (id, value) => setRows(prev => prev.map(c => c.id === id ? touch({ ...c, aliases: value }) : c));
  // ONE polite live region for the whole list — alias outcomes, and the two
  // failure paths with neither a dialog to hold open nor a field to focus (a
  // failed delete, and a 404 that closes the dialog), route through it. An
  // aria-atomic region will not re-announce an identical string, so each
  // message carries an invisible nonce.
  const [announce, setAnnounce] = useState('');
  const announceNonce = React.useRef(0);
  const say = (msg) => { announceNonce.current += 1; setAnnounce(`${msg}${'\u200B'.repeat((announceNonce.current % 4) + 1)}`); };
  const perms = {
    create: tweaks.cpCanCreate !== false,
    update: tweaks.cpCanUpdate !== false,
    delete: tweaks.cpCanDelete !== false,
  };

  const filtered = useMemo(() => rows.filter(c => {
    const st = c.archived ? 'archived' : 'active';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (typeFilter.length && !typeFilter.includes(c.type)) return false;
    if (debouncedQ) {
      // Alias VALUES and the middle name are searched alongside the resolved
      // and normalized name and the notes. An alias LABEL is metadata and is
      // NOT searched — which is why the placeholder does not promise it.
      const aliasHay = (c.aliases || []).map(a => a.value).join(' ');
      const middle = (c.person && c.person.middleName) || '';
      const hay = `${resolvedName(c)} ${window.OdysseyHelpers.normalizeName(resolvedName(c))} ${middle} ${aliasHay} ${c.notes || ''}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [rows, typeFilter, statusFilter, debouncedQ]);

  const totalCount = filtered.length;
  const sortedRows = useMemo(() => {
    const dir = sort.dir === 'desc' ? -1 : 1;
    return [...filtered].sort((a, b) => {
      const va = cpSortVal(a, sort.key), vb = cpSortVal(b, sort.key);
      return va < vb ? -dir : va > vb ? dir : 0;
    });
  }, [filtered, sort]);

  const activeCount = rows.filter(c => !c.archived).length;
  const archivedCount = rows.length - activeCount;
  const hasFilters = !!(debouncedQ || typeFilter.length || statusFilter.length);
  const clearFilters = () => { setQ(''); setTypeFilter([]); setStatusFilter([]); };

  // Per-row export (spec §7.1) — requires only contacts.read; no cap.
  const exportRow = (c) => {
    const fname = vcSlug(resolvedName(c)) + '.vcf';
    vcDownload(buildVCard(c), fname);
    pushToast('success', `Exported ${fname}`);
  };
  // Page-level export (spec §7.2): scope 'all' ignores filters, 'filtered' uses
  // the current search/type/status set. _exporting guards re-entrant clicks (§3).
  const doExport = (scope) => {
    if (exporting) return;
    if (tweaks.cpExportCap) { pushToast('error', 'Too many contacts matched — narrow your filters and try again.'); return; }
    setExporting(true);
    setTimeout(() => {
      const set = scope === 'filtered' ? filtered : rows;
      const stamp = vcDateStamp();
      const fname = scope === 'filtered' ? `odyssey-contacts-filtered-${stamp}.vcf` : `odyssey-contacts-${stamp}.vcf`;
      vcDownload(buildVCardFile(set), fname);
      setExporting(false);
      pushToast('success', `Exported ${set.length} ${set.length === 1 ? 'contact' : 'contacts'}.`);
    }, 700);
  };
  // Import (spec §7.3): apply created rows + touch updated rows, then hand the
  // VCardImportResult back to the dialog to render its summary.
  const runImport = (file) => {
    const sim = simulateImport(file, rows, tweaks.cpImportOutcome || 'skips');
    if (sim.rejected) return { rejected: sim.rejected };
    const now = new Date().toISOString();
    const upd = new Set(sim.updatedIds || []);
    setRows(prev => [...(sim.createdRows || []), ...prev.map(c => upd.has(c.id) ? { ...c, updatedAt: now } : c)]);
    return { result: sim.result };
  };

  return (
    <div className="col gap-6">
      <div className="odc-sr-only" role="status" aria-live="polite">{announce}</div>
      <PageHeader
        title="Contacts"
        icon="groups"
        sub={`${activeCount} active · ${archivedCount} archived`}
        overview={(
          <div className="odc-summary-grid">
            <BreakdownTile label="By type" empty="No contacts."
              rows={odcTypeRows(rows.filter(c => !c.archived), CP_TYPES, (c) => c.type)} />
            <BreakdownTile label="By status" empty="No contacts."
              rows={odcStatusRows(rows, [
                { key: 'active', label: 'Active', tone: 'income', icon: 'task_alt' },
                { key: 'archived', label: 'Archived', tone: 'outline', icon: 'inventory_2' },
              ], (c) => (c.archived ? 'archived' : 'active'))} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search name, alias or notes…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 180 }}>
              <MultiSelect allLabel="Any type" value={typeFilter} onChange={setTypeFilter} options={CP_TYPE_OPTIONS} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter} options={CP_STATUS_OPTIONS} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[{ key: 'name', label: 'Name', type: 'text' }, { key: 'type', label: 'Type', type: 'status' }]} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Contacts per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        )}
        primary={{ label: 'New contact', icon: 'add', onClick: () => setAdding(true) }}
        menu={[
          { icon: 'download', label: 'Export all as vCard', onClick: () => doExport('all') },
          { icon: 'filter_list', label: `Export filtered (${totalCount}) as vCard`, onClick: () => doExport('filtered') },
          ...(canImport ? [{ divider: true }, { icon: 'upload_file', label: 'Import from vCard…', onClick: () => setImportOpen(true) }] : []),
        ]}
      />

      {importOpen && <ContactImportModal onClose={() => setImportOpen(false)} onImport={runImport} />}

      {adding && <AddContactModal onClose={() => setAdding(false)} onCreate={createCp} />}

      {rows.length === 0 ? (
        <EmptyState icon="store" mutedIcon
          title="No contacts yet"
          desc="Add the people and organizations money moves to and from."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>New contact</Button>} />
      ) : (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(c) => c.id}
            noun="contacts"
            renderItem={(c) => (
              <CpRecordCard c={c}
                open={openId === c.id}
                onToggle={(o) => setOpenId(o ? c.id : null)}
                onSave={onSave} onDelete={onDelete} onContacts={onContacts} onAliases={onAliases} onExportRow={exportRow}
                perms={perms} aliasCap={tweaks.cpAliasCap || 32} onAnnounce={say}
                onProblem={(m) => pushToast('error', m)} />
            )}
            empty={(
              <EmptyState icon="store" mutedIcon
                title="No contacts match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everything.' : 'Add the people and organizations money moves to and from.'}
                action={hasFilters
                  ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button>
                  : <Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>New contact</Button>} />
            )}
            trailing={(
              <AddRow title="New contact" sub="A person or organization that money moves to or from."
                onClick={() => setAdding(true)} />
            )}
          />
        </div>
      )}
      {toast && DSToast && DSToastStack && (
        <DSToastStack>
          <DSToast key={toast.k} severity={toast.severity} duration={4200} onClose={() => setToast(null)} message={toast.message} />
        </DSToastStack>
      )}
      {blocked && window.ContactLinksBlockedModal && (
        <window.ContactLinksBlockedModal
          contact={blocked.contact}
          blocking={blocked.blocking}
          canReadInsurance={tweaks.cpCanReadInsurance !== false}
          canUpdateInsurance={tweaks.cpCanUpdateInsurance !== false}
          onClose={() => setBlocked(null)}
          onDetachAndDelete={(id) => removeRow(id)} />
      )}
    </div>
  );
};

/* ---- local styles (tokens only — valid in both themes) ---- */
const cpStyles = {
  contactWrap: { marginTop: 20 },
  section: { background: 'var(--mud-palette-surface)', border: '1px solid var(--mud-palette-divider)', borderRadius: 12, padding: '14px 16px' },
  row: { display: 'flex', alignItems: 'center', gap: 12, padding: '10px 12px', borderRadius: 10, background: 'var(--mud-palette-background)', border: '1px solid var(--mud-palette-divider)' },
  rowActions: { display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0 },
  emptyRow: { padding: '6px 2px 10px' },
  chipWrap: { display: 'flex', flexWrap: 'wrap', gap: 9 },
  rowWrap: { display: 'flex', flexDirection: 'column', gap: 8 },
  addCaption: { display: 'flex', alignItems: 'center', gap: 6, margin: '0 0 8px', font: '600 12px/1 var(--font-sans)', letterSpacing: '.03em', textTransform: 'uppercase', color: 'var(--mud-palette-text-secondary)' },
  addBtn: { display: 'inline-flex', alignItems: 'center', gap: 6, marginTop: 10, padding: '7px 12px', border: '1px dashed var(--mud-palette-divider)', borderRadius: 9, background: 'transparent', color: 'var(--mud-palette-text-secondary)', font: '500 12.5px/1 var(--font-sans)', cursor: 'pointer' },
  form: { background: 'var(--mud-palette-background)', border: '1px solid var(--mud-palette-primary)', borderRadius: 10, padding: 14 },
  formGrid: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0,1fr))', gap: 12 },
  formFoot: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, marginTop: 14, flexWrap: 'wrap' },
  primaryToggle: { display: 'inline-flex', alignItems: 'center', gap: 8, font: '500 12.5px/1 var(--font-sans)', color: 'var(--mud-palette-text-secondary)', cursor: 'pointer' },
  dialogFields: { display: 'flex', flexDirection: 'column', gap: 12, marginTop: 4 },
  typeLock: { display: 'flex', alignItems: 'center', minHeight: 40 },
  countPill: { display: 'inline-flex', alignItems: 'center', gap: 3, font: '500 12.5px/1 var(--font-sans)' },
};

Object.assign(window, { Contacts, CpRecordCard, CpTiles, AddContactModal, CP_TYPES });
