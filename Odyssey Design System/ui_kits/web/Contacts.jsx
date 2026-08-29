/* Contacts — v5 "Extended Contacts".
   A base Contact record (DisplayName override + computed fallback,
   audit timestamps) discriminated by Type into a Person or Organization
   sub-record, plus three independently-managed contact collections
   (Addresses / Emails / Phone numbers), each row carrying a Label + a single
   Primary. Mirrors the Extended Contacts spec (v5).

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
const ADDRESS_LABELS = [
  { value: 'Home',    label: 'Home',    icon: 'home' },
  { value: 'Work',    label: 'Work',    icon: 'work' },
  { value: 'Billing', label: 'Billing', icon: 'receipt_long' },
  { value: 'Other',   label: 'Other',   icon: 'category' },
];
const EMAIL_LABELS = [
  { value: 'Home',  label: 'Home',  icon: 'home' },
  { value: 'Work',  label: 'Work',  icon: 'work' },
  { value: 'Other', label: 'Other', icon: 'category' },
];
const PHONE_LABELS = [
  { value: 'Home',   label: 'Home',   icon: 'home' },
  { value: 'Work',   label: 'Work',   icon: 'work' },
  { value: 'Mobile', label: 'Mobile', icon: 'smartphone' },
  { value: 'Other',  label: 'Other',  icon: 'category' },
];
const labelMeta = (labels, v) => labels.find(l => l.value === v) || labels[labels.length - 1];

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

/* Atoms not bridged to the kit globals — read straight off the DS namespace. */
const { Menu: DSMenu, Toast: DSToast, ToastStack: DSToastStack } = window.OdysseyDesignSystem_d5aa51 || {};

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
const ADR_TYPE = { Home: 'home', Work: 'work', Billing: 'billing', Other: 'other' };
const TEL_TYPE = { Home: 'home', Work: 'work', Mobile: 'cell', Other: 'other' };
const EMAIL_TYPE = { Home: 'home', Work: 'work', Other: 'other' };
const vcParam = (type, pref) => `${type ? ';TYPE=' + type : ''}${pref ? ';PREF=1' : ''}`;

const buildVCard = (c) => {
  const L = ['BEGIN:VCARD', 'VERSION:4.0'];
  L.push('UID:' + cpExternalUid(c));
  L.push('FN:' + vcEsc(resolvedName(c)));
  if (c.type === 'Person') {
    const p = c.person || {};
    L.push('KIND:individual');
    L.push(`N:${vcEsc(p.lastName)};${vcEsc(p.firstName)};;;`);
    if (p.title) L.push('TITLE:' + vcEsc(p.title));
    if (p.company) L.push('ORG:' + vcEsc(p.company));
    if (p.dateOfBirth) L.push('BDAY:' + p.dateOfBirth.replace(/-/g, ''));
    if (p.sex === 'Male' || p.sex === 'Female') L.push('GENDER:' + (p.sex === 'Male' ? 'M' : 'F'));
    if (p.relationshipType) L.push('X-ODYSSEY-RELATIONSHIP:' + vcEsc(p.relationshipType));
  } else {
    const o = c.org || {};
    L.push('KIND:org');
    L.push('ORG:' + vcEsc(o.legalName));
    if (o.website && /^https?:\/\//i.test(o.website)) L.push('URL:' + o.website);
    if (o.organizationNumber) L.push('X-ODYSSEY-ORG-NUMBER:' + vcEsc(o.organizationNumber));
  }
  (c.addresses || []).forEach((a) => {
    const street = [a.line1, a.line2].filter(Boolean).join(' ');
    const val = `;;${vcEsc(street)};${vcEsc(a.city)};${vcEsc(a.region)};${vcEsc(a.postalCode)};${vcEsc(a.countryCode)}`;
    L.push(`ADR${vcParam(ADR_TYPE[a.label], a.isPrimary)}:${val}`);
  });
  (c.emails || []).forEach((e) => L.push(`EMAIL${vcParam(EMAIL_TYPE[e.label], e.isPrimary)}:${vcEsc(e.value)}`));
  (c.phones || []).forEach((t) => L.push(`TEL${vcParam(TEL_TYPE[t.label], t.isPrimary)}:${vcEsc(t.value)}`));
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
      person: { firstName: first, lastName: last, dateOfBirth: null, sex: null, title: null, company: null },
      addresses: [], emails: [{ id: uid('e'), label: 'Home', isPrimary: true, value: `${first}.${last}`.toLowerCase() + '@example.com' }], phones: [],
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
    person: { firstName: 'Michael', lastName: 'Chen', dateOfBirth: '1990-04-12', sex: 'Male', title: 'Senior Engineer', company: 'Northwind Labs' },
    addresses: [{ id: 'a1', label: 'Home', isPrimary: true, line1: 'Thorvald Meyers gate 12', line2: 'Leil. 3B', city: 'Oslo', region: '', postalCode: '0555', countryCode: 'NO' }],
    emails: [{ id: 'e1', label: 'Home', isPrimary: true, value: 'michael.chen@example.com' }],
    phones: [
      { id: 'p1', label: 'Mobile', isPrimary: true, value: '+1 415 555 0147' },
      { id: 'p2', label: 'Home', isPrimary: false, value: '+1 415 555 0912' },
    ],
  },
  {
    id: 'c2', type: 'Organization', displayName: 'Lakeside PM', notes: 'Apartment landlord — monthly rent, billing goes to the SoMa office.',
    archived: null, createdAt: '2024-08-14T09:00:00Z', updatedAt: '2026-05-30T10:05:00Z',
    org: { legalName: 'Lakeside Property Management LLC', organizationNumber: '81-2233445', website: 'https://lakesidepm.example.com' },
    addresses: [
      { id: 'a2', label: 'Billing', isPrimary: true, line1: 'Storgata 55', line2: 'Etg. 14', city: 'Oslo', region: '', postalCode: '0184', countryCode: 'NO' },
      { id: 'a3', label: 'Work', isPrimary: false, line1: 'Strandveien 200', city: 'Bergen', region: '', postalCode: '5003', countryCode: 'NO' },
    ],
    emails: [{ id: 'e2', label: 'Work', isPrimary: true, value: 'billing@lakesidepm.example.com' }],
    phones: [{ id: 'p3', label: 'Work', isPrimary: true, value: '+1 510 555 0110' }],
  },
  {
    id: 'c3', type: 'Person', displayName: null, notes: 'Family physician — reimbursements for out-of-pocket visits.',
    archived: null, createdAt: '2025-01-20T09:00:00Z', updatedAt: '2026-04-11T08:40:00Z',
    person: { firstName: 'Priya', lastName: 'Nair', dateOfBirth: null, sex: 'Female', title: 'Physician', company: 'Bay Area Health Partners' },
    addresses: [], emails: [{ id: 'e3', label: 'Work', isPrimary: true, value: 'p.nair@bahp.example.org' }],
    phones: [{ id: 'p4', label: 'Work', isPrimary: true, value: '+1 650 555 0088' }],
  },
  {
    id: 'c4', type: 'Organization', displayName: null, notes: 'Employer — payroll direct deposit.',
    archived: null, createdAt: '2023-06-01T09:00:00Z', updatedAt: '2026-06-01T09:00:00Z',
    org: { legalName: 'Northwind Labs, Inc.', organizationNumber: '98-7654321', website: 'https://northwind.example.com' },
    addresses: [{ id: 'a4', label: 'Work', isPrimary: true, line1: 'Brobekkveien 80', city: 'Oslo', region: '', postalCode: '0598', countryCode: 'NO' }],
    emails: [{ id: 'e4', label: 'Work', isPrimary: true, value: 'payroll@northwind.example.com' }],
    phones: [],
  },
  {
    id: 'c5', type: 'Person', displayName: 'Sarah (agent)', notes: 'Letting agent for the Fell Street flat.',
    archived: null, createdAt: '2024-09-11T09:00:00Z', updatedAt: '2026-03-22T16:10:00Z',
    person: { firstName: 'Sarah', lastName: 'Whitfield', dateOfBirth: '1985-11-30', sex: 'Female', title: null, company: 'Lakeside Property Management LLC' },
    addresses: [], emails: [{ id: 'e5', label: 'Work', isPrimary: true, value: 'sarah.w@lakesidepm.example.com' }],
    phones: [{ id: 'p5', label: 'Mobile', isPrimary: true, value: '+1 415 555 0333' }],
  },
  {
    id: 'c6', type: 'Organization', displayName: null, notes: 'Home & contents insurer — issues the property policy.',
    archived: null, createdAt: '2024-02-19T09:00:00Z', updatedAt: '2026-02-19T09:00:00Z',
    org: { legalName: 'Pacific Home Insurance Co.', organizationNumber: '45-6677889', website: null },
    addresses: [{ id: 'a6', label: 'Other', isPrimary: true, line1: 'Markveien 35', line2: '12. etg.', city: 'Oslo', region: '', postalCode: '0554', countryCode: 'NO' }],
    emails: [{ id: 'e6', label: 'Other', isPrimary: true, value: 'claims@pacifichome.example.com' }],
    phones: [{ id: 'p6', label: 'Other', isPrimary: true, value: '+1 800 555 0199' }],
  },
  {
    id: 'c7', type: 'Person', displayName: null, notes: 'Dog walker — weekly, paid by transfer.',
    archived: null, createdAt: '2025-05-04T09:00:00Z', updatedAt: '2026-06-14T12:00:00Z',
    person: { firstName: 'Diego', lastName: 'Ramos', dateOfBirth: null, sex: null, title: null, company: null },
    addresses: [], emails: [], phones: [{ id: 'p7', label: 'Mobile', isPrimary: true, value: '+1 415 555 0270' }],
  },
  {
    id: 'c8', type: 'Organization', displayName: null, notes: 'Cancelled gym membership — kept for transaction history.',
    archived: '2025-03-02T09:00:00Z', createdAt: '2022-01-10T09:00:00Z', updatedAt: '2025-03-02T09:00:00Z',
    org: { legalName: 'FitZone Gym', organizationNumber: null, website: null },
    addresses: [], emails: [], phones: [],
  },
];

/* ================= small building blocks ================= */

/* Primary marker — always visible TEXT (never icon/colour alone; §10 a11y). */
const PrimaryBadge = () => <Chip tone="income" dot>Primary</Chip>;

const LabelChip = ({ labels, value }) => {
  const m = labelMeta(labels, value);
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

const CONTACT_KINDS = {
  address: { title: 'Addresses',     icon: 'home',  addTitle: 'Add address',      labels: ADDRESS_LABELS, avatar: 'location_on',     soft: 'oklch(0.77 0.14 55 / 0.15)',  fg: 'oklch(0.77 0.14 55)' },
  email:   { title: 'Emails',        icon: 'mail',  addTitle: 'Add email',        labels: EMAIL_LABELS,   avatar: 'alternate_email', soft: 'oklch(0.72 0.16 295 / 0.15)', fg: 'oklch(0.72 0.16 295)' },
  phone:   { title: 'Phone numbers', icon: 'call',  addTitle: 'Add phone number', labels: PHONE_LABELS,   avatar: 'call',            soft: 'oklch(0.78 0.13 200 / 0.15)', fg: 'oklch(0.78 0.13 200)' },
};

// Single-line summary for the chip value (full detail stays in copy / edit).
const addressSummary = (a) => [a.line1, [a.postalCode, a.city].filter(Boolean).join(' ').trim(), a.countryCode].filter(v => v && v.trim()).join(', ');
const chipValue = (kind, item) => kind === 'address' ? (addressSummary(item) || item.line1 || 'Address') : item.value;

const blankFor = (kind, defaultLabel) => {
  if (kind === 'address') return { label: defaultLabel, isPrimary: false, line1: '', line2: '', city: '', region: '', postalCode: '', countryCode: '' };
  return { label: defaultLabel, isPrimary: false, value: '' };
};

const ContactForm = ({ kind, item, onCommit, onCancel, isFirst, mode }) => {
  const { useState } = React;
  const cfg = CONTACT_KINDS[kind];
  const [d, setD] = useState(item);
  const [err, setErr] = useState({});
  const set = (k) => (v) => { setD(s => ({ ...s, [k]: v })); if (err[k]) setErr(e => ({ ...e, [k]: undefined })); };

  const validate = () => {
    const e = {};
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
        <Select label="Label" value={d.label} onChange={set('label')} options={cfg.labels} />
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
const ContactList = ({ c, onContacts, readOnly, addReq, onConsumeAdd, styleMode }) => {
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
  const setPrimary = (kind, id) => change(kind, applyPrimary(listOf(kind), id));
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
      <div className="cp-sub">
        <span className="cp-sub-label">Contact information</span>
        <span className="cp-sub-rule" />
        <span className="cp-sub-meta">{all.length} {all.length === 1 ? 'entry' : 'entries'}</span>
      </div>
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
                  <span>{labelMeta(cfg.labels, item.label).label}</span>
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
                  <LabelChip labels={cfg.labels} value={item.label} />
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
        <ContactForm mode="add" kind={adding} item={blankFor(adding, CONTACT_KINDS[adding].labels[0].value)} isFirst={listOf(adding).length === 0}
          onCommit={(data) => commitAdd(adding, data)} onCancel={() => setAdding(null)} />
      )}
      {!readOnly && editing && (
        <ContactForm mode="edit" kind={editing.kind} item={listOf(editing.kind).find(x => x.id === editing.id)} isFirst={listOf(editing.kind).length === 1}
          onCommit={(data) => commitEdit(editing.kind, editing.id, data)} onCancel={() => setEditing(null)} />
      )}
    </div>
  );
};

/* ================= detail (read + contact management) ================= */
const CpDetail = ({ c, onContacts, addReq, onConsumeAdd, contactStyle }) => {
  const H = window.OdysseyHelpers;
  const meta = CP_TYPE_BY_KEY[c.type] || CP_TYPE_BY_KEY.Person;
  const status = H.archivedStatus(c);
  const isPerson = c.type === 'Person';
  const p = c.person || {}, o = c.org || {};
  const website = o.website && /^https?:\/\//i.test(o.website) ? o.website : null;

  return (
    <div className="acct-detail">
      <div className="meta-grid">
        <MetaTile label="Display name" value={resolvedName(c) || '—'} />
        <MetaTile label="Type" value={<Chip tone="outline" icon={meta.icon}>{meta.label}</Chip>} />
        {isPerson ? (
          <React.Fragment>
            <MetaTile label="First name" value={p.firstName || '—'} />
            <MetaTile label="Last name" value={p.lastName || '—'} />
            <MetaTile label="Date of birth" value={p.dateOfBirth ? H.dateLong(p.dateOfBirth) : '—'} mono />
            <MetaTile label="Sex" value={p.sex || '—'} />
            <MetaTile label="Job title" value={p.title || '—'} />
            <MetaTile label="Company" value={p.company || '—'} />
          </React.Fragment>
        ) : (
          <React.Fragment>
            <MetaTile label="Legal name" value={o.legalName || '—'} />
            <MetaTile label="Organization number" value={o.organizationNumber || '—'} mono />
            <MetaTile label="Website" value={website
              ? <a href={website} target="_blank" rel="noopener noreferrer">{o.website}</a>
              : (o.website || '—')} />
          </React.Fragment>
        )}
        <MetaTile label="Status" value={<Chip tone={status.tone} dot>{status.label}</Chip>} />
        <MetaTile label="Notes" value={c.notes || '—'} />
        <MetaTile label="Created" value={H.dateTime(c.createdAt)} mono />
        <MetaTile label="Updated" value={H.dateTime(c.updatedAt)} mono />
      </div>

      <div style={cpStyles.contactWrap}>
        <ContactList c={c} onContacts={onContacts} readOnly={!!c.archived} addReq={addReq} onConsumeAdd={onConsumeAdd} styleMode={contactStyle} />
      </div>
    </div>
  );
};

/* ================= shared Person / Organization field sets ================= */
const PersonFields = ({ d, set, err }) => (
  <React.Fragment>
    <FormRow>
      <Field label="First name" value={d.firstName} onChange={set('firstName')} error={err.firstName} required autoFocus maxLength={128} />
      <Field label="Last name" value={d.lastName} onChange={set('lastName')} error={err.lastName} required maxLength={128} />
    </FormRow>
    <FormRow>
      <Field label="Date of birth" type="date" value={d.dateOfBirth} onChange={set('dateOfBirth')} helper="Optional · cannot be in the future" />
      <Select label="Sex" value={d.sex} onChange={set('sex')} options={SEX_OPTIONS} helper="Optional" placeholder="Unspecified" />
    </FormRow>
    <Field label="Job title" value={d.title} onChange={set('title')} placeholder="e.g. Senior Engineer" helper="Optional" maxLength={128} />
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
  </React.Fragment>
);

const displayNameHint = (type) => type === 'Person'
  ? 'Defaults to "First Last" if left blank'
  : 'Defaults to the legal name if left blank';

/* Contact record editing reuses AddContactModal in edit mode
   (row Edit → setEditCp); there is no inline edit panel. */

/* ================= table ================= */
const cpSortVal = (c, key) => {
  switch (key) {
    case 'name': return resolvedName(c).toLowerCase();
    case 'type': return c.type;
    case 'status': return c.archived ? 1 : 0;
    default: return 0;
  }
};
const contactCount = (c) => (c.addresses || []).length + (c.emails || []).length + (c.phones || []).length;

const ContactTable = ({ contacts, onSave, onDelete, onContacts, onExportRow, sort, onSortChange, empty, contactStyle }) => {
  const { useState } = React;
  const [addReq, setAddReq] = useState(null); // {id, kind, nonce}
  const requestAdd = (c, ctx, kind) => { if (!ctx.expanded) ctx.toggle(); setAddReq({ id: c.id, kind, nonce: Date.now() }); };
  const [editCp, setEditCp] = useState(null);
  return (
  <React.Fragment>
  <RecordTable
    rows={contacts}
    ariaLabel="Contacts"
    rowKey={(c) => c.id}
    defaultSort={{ key: 'name', dir: 'asc' }}
    sort={sort}
    onSortChange={onSortChange}
    leading={(c) => <Avatar icon={(CP_TYPE_BY_KEY[c.type] || CP_TYPE_BY_KEY.Person).icon} tone={cpTone(c.type)} />}
    columns={[
      {
        key: 'name', header: 'Name', sortable: true, sortType: 'text', sortValue: (c) => cpSortVal(c, 'name'),
        cell: (c, ctx) => (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
            {resolvedName(c)}
            {c.displayName && <MIcon name="badge" size={15} className="muted" title="Display name overridden" />}
            {ctx.justSaved && <Chip tone="income" dot>Saved</Chip>}
          </span>
        ),
      },
      {
        key: 'type', header: 'Type', sortable: true, sortType: 'status', sortValue: (c) => cpSortVal(c, 'type'),
        cell: (c) => { const m = CP_TYPE_BY_KEY[c.type] || CP_TYPE_BY_KEY.Person; return <Chip tone="outline" icon={m.icon}>{m.label}</Chip>; },
      },
      {
        key: 'contact', header: 'Contact', className: 'muted',
        cell: (c) => {
          const n = contactCount(c);
          if (!n) return <span className="muted">—</span>;
          return (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10, color: 'var(--mud-palette-text-secondary)' }}>
              {(c.addresses || []).length > 0 && <span style={cpStyles.countPill}><MIcon name="location_on" size={14} />{c.addresses.length}</span>}
              {(c.emails || []).length > 0 && <span style={cpStyles.countPill}><MIcon name="mail" size={14} />{c.emails.length}</span>}
              {(c.phones || []).length > 0 && <span style={cpStyles.countPill}><MIcon name="call" size={14} />{c.phones.length}</span>}
            </span>
          );
        },
      },
      {
        key: 'status', header: 'Status', sortable: true, sortType: 'status', sortValue: (c) => cpSortVal(c, 'status'),
        cell: (c) => { const s = window.OdysseyHelpers.archivedStatus(c); return <Chip tone={s.tone} dot>{s.label}</Chip>; },
      },
    ]}
    actions={(c, ctx) => [
      ...(ctx.editing ? [] : [{ icon: ctx.expanded ? 'close' : 'expand_more', label: ctx.expanded ? 'Collapse' : 'View details', onClick: ctx.toggle }]),
      { icon: 'edit', label: 'Edit', onClick: () => setEditCp(c) },
      { icon: 'download', label: 'Export vCard', onClick: () => onExportRow && onExportRow(c) },
      ...(c.archived ? [] : [
        { divider: true },
        { icon: 'add_location_alt', label: 'New address', onClick: () => requestAdd(c, ctx, 'address') },
        { icon: 'alternate_email', label: 'New email', onClick: () => requestAdd(c, ctx, 'email') },
        { icon: 'add_call', label: 'New phone number', onClick: () => requestAdd(c, ctx, 'phone') },
      ]),
      { divider: true },
      { icon: c.archived ? 'unarchive' : 'archive', label: c.archived ? 'Restore' : 'Archive', onClick: () => onSave(c.id, { archived: c.archived ? null : new Date().toISOString() }) },
      { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(c.id); } },
      { divider: true },
      { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove },
    ]}
    renderDetail={(c) => <CpDetail c={c} onContacts={onContacts} contactStyle={contactStyle} addReq={addReq && addReq.id === c.id ? addReq : null} onConsumeAdd={() => setAddReq(null)} />}
    onSave={onSave}
    onDelete={onDelete}
    empty={empty}
  />
  {editCp && <AddContactModal contact={editCp} onClose={() => setEditCp(null)} onSave={(id, patch) => { onSave(id, patch); setEditCp(null); }} />}
  </React.Fragment>
  );
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
    dateOfBirth: (cp.person && cp.person.dateOfBirth) || '', sex: (cp.person && cp.person.sex) || '',
    title: (cp.person && cp.person.title) || '', company: (cp.person && cp.person.company) || '',
    legalName: (cp.org && cp.org.legalName) || '', organizationNumber: (cp.org && cp.org.organizationNumber) || '',
    website: (cp.org && cp.org.website) || '',
  });
  const [err, setErr] = useState({});
  const set = (k) => (v) => { setDraft(s => ({ ...s, [k]: v })); if (err[k]) setErr(e => ({ ...e, [k]: undefined })); };

  // Switching Type discards the previously-visible field set (§3 — the two are
  // mutually exclusive; stale hidden values are never retained or submitted).
  const changeType = (t) => {
    if (t === type) return;
    setType(t);
    setErr({});
    setDraft({ firstName: '', lastName: '', dateOfBirth: '', sex: '', title: '', company: '', legalName: '', organizationNumber: '', website: '' });
  };

  const submit = () => {
    const e = {};
    if (type === 'Person') { if (!draft.firstName.trim()) e.firstName = 'Required.'; if (!draft.lastName.trim()) e.lastName = 'Required.'; }
    else { if (!draft.legalName.trim()) e.legalName = 'Legal name is required for an organization.'; if (draft.website && !/^https?:\/\//i.test(draft.website.trim())) e.website = 'Must start with http:// or https://'; }
    if (Object.keys(e).length) { setErr(e); return; }
    if (isEdit) {
      const patch = { displayName: displayName.trim() || null };
      if (type === 'Person') patch.person = { firstName: draft.firstName.trim(), lastName: draft.lastName.trim(), dateOfBirth: draft.dateOfBirth || null, sex: draft.sex || null, title: draft.title.trim() || null, company: draft.company.trim() || null };
      else patch.org = { legalName: draft.legalName.trim(), organizationNumber: draft.organizationNumber.trim() || null, website: draft.website.trim() || null };
      onSave && onSave(cp.id, patch);
      return;
    }
    const dto = { type, displayName: displayName.trim() || null, notes: undefined, archived: null, addresses: [], emails: [], phones: [] };
    if (type === 'Person') dto.person = { firstName: draft.firstName.trim(), lastName: draft.lastName.trim(), dateOfBirth: draft.dateOfBirth || null, sex: draft.sex || null, title: draft.title.trim() || null, company: draft.company.trim() || null };
    else dto.org = { legalName: draft.legalName.trim(), organizationNumber: draft.organizationNumber.trim() || null, website: draft.website.trim() || null };
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
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);
  const [importOpen, setImportOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [toast, setToast] = useState(null);
  const canImport = tweaks.cpCanImport !== false; // requires contacts.create AND .update
  const pushToast = (severity, message) => setToast({ severity, message, k: Date.now() });

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  const touch = (c) => ({ ...c, updatedAt: new Date().toISOString() });
  const createCp = (dto) => {
    const now = new Date().toISOString();
    setRows(prev => [{ id: uid('cp'), createdAt: now, updatedAt: now, ...dto }, ...prev]);
    setAdding(false);
  };
  const onSave = (id, patch) => setRows(prev => prev.map(c => c.id === id ? touch({ ...c, ...patch }) : c));
  const onDelete = (id) => setRows(prev => prev.filter(c => c.id !== id));
  // Any child (address/email/phone) mutation bumps the parent UpdatedAt (§9).
  const onContacts = (id, coll, value) => setRows(prev => prev.map(c => c.id === id ? touch({ ...c, [coll]: value }) : c));

  const filtered = useMemo(() => rows.filter(c => {
    const st = c.archived ? 'archived' : 'active';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (typeFilter.length && !typeFilter.includes(c.type)) return false;
    if (debouncedQ) {
      const hay = `${resolvedName(c)} ${window.OdysseyHelpers.normalizeName(resolvedName(c))} ${c.notes || ''}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [rows, typeFilter, statusFilter, debouncedQ]);

  useEffect(() => { setPage(1); }, [debouncedQ, typeFilter, statusFilter, sort, pageSize]);
  const totalCount = filtered.length;
  const paged = useMemo(() => {
    if (pageSize === 'all') return filtered;
    const start = (page - 1) * pageSize;
    return filtered.slice(start, start + pageSize);
  }, [filtered, page, pageSize]);

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
              <SearchField placeholder="Search name or notes…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 180 }}>
              <MultiSelect allLabel="Any type" value={typeFilter} onChange={setTypeFilter} options={CP_TYPE_OPTIONS} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter} options={CP_STATUS_OPTIONS} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[{ key: 'name', label: 'Name', type: 'text' }, { key: 'type', label: 'Type', type: 'status' }]} />
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
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

      <Card>
        <CardBody style={{ padding: 0 }}>
          <ContactTable
            contacts={paged}
            sort={sort}
            onSortChange={setSort}
            onSave={onSave}
            onDelete={onDelete}
            onContacts={onContacts}
            onExportRow={exportRow}
            empty={(
              <EmptyState icon="store" mutedIcon
                title="No contacts match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everything.' : 'Add the people and organizations money moves to and from.'}
                action={hasFilters
                  ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button>
                  : <Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>New contact</Button>} />
            )}
          />
          {totalCount > 0 && (
            <Pager page={page} pageSize={pageSize} totalCount={totalCount} onPageChange={setPage} onPageSizeChange={setPageSize} />
          )}
        </CardBody>
      </Card>
      {toast && DSToast && DSToastStack && (
        <DSToastStack>
          <DSToast key={toast.k} severity={toast.severity} duration={4200} onClose={() => setToast(null)} message={toast.message} />
        </DSToastStack>
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

Object.assign(window, { Contacts, ContactTable, AddContactModal, CpDetail, CP_TYPES });
