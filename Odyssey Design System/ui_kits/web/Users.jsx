/* =============================================================
   Users — admin-only user administration page.
   Implements the "Frontend User Administration Specification":
     • List / search / role + enabled filters
     • Inline-expanding row (Budgets pattern) → detail view, then an
       Edit panel that swaps in (toggle), exactly like a budget expands
       into edit mode.
     • Editable: emailConfirmed, enabled (PATCH /api/users/{id}) and
       role (PUT /api/users/{id}/role). Everything else is read-only.
     • Last-enabled-Admin protection surfaced as a blocking 409 guard.
     • Confirmation step for disruptive changes (disable / role↔Admin /
       mark email unconfirmed).
     • Read-only Roles & permissions reference (accordion).

   Reuses kit atoms: PageHeader, Card, Field, Select, Switch, Button,
   Avatar, ActionMenu, Collapsible, Alert. Styling in admin.css.
   ============================================================= */

/* ---------- 1. Permission catalog (GET /api/users/permissions) ----------
   The real claim catalog, lifted from the codebase
   (Odyssey.Application.Context/Authorization/PermissionClaims.cs) — every
   claim the system defines, grouped by category in the order AllClaims
   declares them. */
const UA_CATEGORIES = [
  { cat: 'accounts',            icon: 'account_balance_wallet', actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'accounts.terms',      icon: 'percent',                actions: ['read', 'write'] },
  { cat: 'accounts.estimates',  icon: 'query_stats',            actions: ['read', 'write'] },
  { cat: 'budgets',             icon: 'pie_chart',              actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'transactions',        icon: 'receipt_long',           actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'transactions.tags',   icon: 'local_offer',            actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'taxes',               icon: 'request_quote',          actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'insurance',           icon: 'shield',                 actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'contracts',           icon: 'handshake',              actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'subscriptions',       icon: 'subscriptions',          actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'contacts',      icon: 'store',                  actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'currencies',          icon: 'attach_money',           actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'exchangerates',       icon: 'currency_exchange',      actions: ['create', 'read', 'delete'] },
  { cat: 'files',               icon: 'folder',                 actions: ['create', 'read', 'update', 'delete', 'export-all'] },
  { cat: 'data',                icon: 'download',               actions: ['export'] },
  { cat: 'file-analysis',       icon: 'document_scanner',       actions: ['create', 'read', 'import', 'audit'] },
  { cat: 'journal',             icon: 'book',                   actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'journal.tags',        icon: 'local_offer',            actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'tasks',               icon: 'checklist',              actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'tasks.tags',          icon: 'local_offer',            actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'user-preferences',    icon: 'tune',                   actions: ['create', 'read', 'update', 'delete'] },
  { cat: 'users',               icon: 'manage_accounts',        actions: ['manage', 'read', 'update', 'delete'] },
];
const UA_PERMISSIONS = UA_CATEGORIES.flatMap(c =>
  c.actions.map(a => ({ value: `${c.cat}.${a}`, category: c.cat, action: a })));
const UA_ALL = UA_PERMISSIONS.map(p => p.value);
const pick = (...cats) => UA_ALL.filter(v => cats.some(c =>
  typeof c === 'string' ? v === c : v.startsWith(c.cat + '.') && c.acts.includes(v.split('.').pop())));

/* ---------- 2. Roles (GET /api/users/roles) ----------
   The four seeded roles from RoleDefinitions.cs, each carrying the exact
   claim set PermissionClaims.cs grants it: Admin = AllClaims, Owner =
   everything except user administration, User = day-to-day finance work,
   Guest = read-only (plus personal preferences). */
const UA_ROLES = [
  // Admin = AllClaims — every claim the system defines.
  { name: 'Admin', permissions: UA_ALL.slice() },
  // Owner = every claim EXCEPT user administration (users.*) and the three
  // admin-only claims: data.export, files.export-all, file-analysis.audit.
  // Matches OwnerClaims in the backend PermissionClaims.cs (74 claims).
  { name: 'Owner', permissions: UA_ALL.filter(v =>
    !v.startsWith('users.') && v !== 'data.export' && v !== 'files.export-all' && v !== 'file-analysis.audit') },
  // User = day-to-day finance work + full Journal/Tasks module. Mirrors UserClaims.
  { name: 'User', permissions: [
    'accounts.read', 'accounts.terms.read', 'accounts.estimates.read',
    'budgets.read',
    'transactions.create', 'transactions.read', 'transactions.update', 'transactions.delete',
    'transactions.tags.read',
    'taxes.read', 'insurance.read', 'contracts.read', 'subscriptions.read',
    'contacts.read', 'currencies.read', 'exchangerates.read',
    'user-preferences.create', 'user-preferences.read', 'user-preferences.update', 'user-preferences.delete',
    'files.create', 'files.read', 'files.update', 'files.delete',
    'file-analysis.create', 'file-analysis.read', 'file-analysis.import',
    'journal.create', 'journal.read', 'journal.update', 'journal.delete',
    'journal.tags.create', 'journal.tags.read', 'journal.tags.update', 'journal.tags.delete',
    'tasks.create', 'tasks.read', 'tasks.update', 'tasks.delete',
    'tasks.tags.create', 'tasks.tags.read', 'tasks.tags.update', 'tasks.tags.delete',
  ] },
  // Guest = read-only finance data + personal preferences. Mirrors GuestClaims
  // (no Journal/Tasks module, no file-analysis, no insurance/contracts/subscriptions).
  { name: 'Guest', permissions: [
    'accounts.read', 'accounts.terms.read', 'accounts.estimates.read',
    'budgets.read', 'transactions.read', 'transactions.tags.read',
    'contacts.read', 'currencies.read', 'exchangerates.read',
    'user-preferences.create', 'user-preferences.read', 'user-preferences.update', 'user-preferences.delete',
    'files.read', 'taxes.read',
  ] },
];
const UA_ROLE_BY = Object.fromEntries(UA_ROLES.map(r => [r.name, r]));
const UA_ROLE_META = {
  Admin: { icon: 'shield',        cls: 'admin', desc: 'Full system access, including user administration — every claim the system defines.' },
  Owner: { icon: 'verified_user', cls: 'owner', desc: 'Full control of finance data and settings, but cannot administer users.' },
  User:  { icon: 'how_to_reg',    cls: 'user',  desc: 'Day-to-day access: transactions, files, journal and tasks in full; reads the rest.' },
  Guest: { icon: 'visibility',    cls: '',      desc: 'Read-only access to finance data, plus personal preferences.' },
};
const UA_ROLE_OPTIONS = UA_ROLES.map(r => ({ value: r.name, label: r.name }));

/* ---------- 3. Users dataset (GET /api/users → UsersPage.items) ----------
   Seed: [first, last, role, enabled?, confirmed?, createdNull?, nick?]. The first
   three rows set up the last-enabled-Admin scenario (one enabled Admin). The
   name column renders the shared resolver's RESOLVED LABEL, not a raw column:
   DisplayName ?? FirstName ?? (admin holds users.read ? email : "Unknown user").
   `nick` seeds a DisplayName that overrides the first name for a few rows. */
const UA_SEED = [
  ['Jane', 'Sato', 'Owner'],
  ['Marcus', 'Reed', 'Admin', true, true, false, 'Marc Reed'],  // the single ENABLED Admin → guard target (display-name override)
  ['Priya', 'Nair', 'Admin', false],           // disabled Admin (lockout)
  ['Diego', 'Marín', 'Owner'],
  ['Lena', 'Vogt', 'Owner'],
  ['Aisha', 'Khan', 'Owner', true, false],     // unconfirmed
  ['Tomás', 'Silva', 'Owner'],
  ['Noah', 'Bauer', 'User', true, true, false, 'Noah B.'],  // display-name override
  ['Mei', 'Chen', 'User'],
  ['Olek', 'Nowak', 'User', false],            // disabled user
  ['Sara', 'Lund', 'User'],
  ['Liam', "O'Brien", 'User', true, false],
  ['Yuki', 'Tanaka', 'User'],
  ['Ravi', 'Iyer', 'User'],
  ['Chloe', 'Dubois', 'User'],
  ['Ben', 'Foster', 'User'],
  ['Ingrid', 'Holm', 'User'],
  ['Carlos', 'Vega', 'User'],
  ['Fatima', 'Zahra', 'User'],
  ['Sam', 'Ellis', 'User', true, true, true],  // created date null
  ['Hana', 'Park', 'User'],
  ['Erik', 'Strand', 'User', false],
  ['Nadia', 'Rashid', 'Guest'],
  ['Owen', 'Clarke', 'Guest'],
  ['Zoe', 'Martin', 'Guest', true, false],
  ['Pablo', 'Romero', 'Guest'],
  ['Amara', 'Diallo', 'Guest'],
  ['Felix', 'Wagner', 'Guest'],
  ['Ivy', 'Larsen', 'Guest'],
  ['Hugo', 'Costa', 'Guest'],
  ['Lara', 'Petrova', 'Guest'],
  ['Theo', 'Adams', 'Guest', false],
  ['Maya', 'Joshi', 'Guest'],
  ['Jonas', 'Berg', 'Guest'],
  ['Nina', 'Falk', 'Guest', true, false],
  ['Caleb', 'Hughes', 'Guest'],
  ['Rosa', 'Ibarra', 'Guest'],
  ['Adam', 'Walsh', 'Guest'],
  ['Tara', 'Brennan', 'Guest'],
  ['Kofi', 'Mensah', 'Owner'],
  ['Elsa', 'Lindqvist', 'User'],
  ['Victor', 'Hale', 'User', false],
  ['Sana', 'Qureshi', 'Guest'],
  ['Bruno', 'Almeida', 'Guest'],
];
const uaGuid = (n) => {
  const seg = (mult, len) => ((n + 1) * mult >>> 0).toString(16).padStart(len, '0').slice(-len);
  return `${seg(2654435761, 8)}-${seg(40503, 4)}-4${seg(12347, 3)}-8${seg(6791, 3)}-${seg(2246822519, 8)}${seg(99991, 4)}`;
};
const UA_MIDDLES = ['Marie', 'Lee', 'Anne', 'James', 'Rose', 'Dev', 'Kai', 'Elin', 'Grace', 'Omar'];
const uaBorn = (n) => {
  const year = 1958 + ((n * 7) % 44);            // 1958..2001
  const month = (n * 5) % 12;                    // 0..11
  const day = 1 + ((n * 11) % 27);               // 1..27
  return `${year}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
};
const uaFullName = (u) => [u.firstName, u.middleName, u.lastName].filter(Boolean).join(' ');
const uaCreated = (n, isNull) => {
  if (isNull) return null;
  // Spread across ~3 years, oldest first, deterministic.
  const start = Date.UTC(2023, 0, 12, 9, 0, 0);
  const day = 86400000;
  const ts = start + n * 23 * day + (n % 7) * 3 * day;
  return new Date(ts).toISOString().replace('.000', '');
};
const UA_USERS_SEED = UA_SEED.map(([first, last, role, enabled = true, confirmed = true, cNull = false, nick = null], i) => ({
  id: uaGuid(i),
  userName: first.toLowerCase() + '.' + last.toLowerCase().replace(/[^a-z]/g, ''),
  firstName: first,
  middleName: (i % 3 === 0) ? UA_MIDDLES[i % UA_MIDDLES.length] : '',
  lastName: last,
  birthDate: uaBorn(i),
  sex: (i % 2 === 0) ? 'Female' : 'Male',
  // The resolver's resolved label: DisplayName ?? FirstName. (For an admin
  // caller — who holds users.read — the email is the final fallback when a
  // profile is incomplete; every seeded user here has a name.)
  displayName: nick || `${first} ${last}`,
  email: `${first.toLowerCase()}.${last.toLowerCase().replace(/[^a-z]/g, '')}@odyssey.app`,
  initials: (first[0] + last[0]).toUpperCase(),
  emailConfirmed: confirmed,
  twoFactorEnabled: confirmed && (i % 3 !== 1),
  enabled,
  lockoutEnd: enabled ? null : '2099-12-31T00:00:00+00:00',
  role,
  mustChangePassword: (i === 8 || i === 22),
  createdAtUtc: uaCreated(i, cNull),
}));

/* The current operator (holds users.update): the single enabled Admin, Marcus
   Reed. Self-targeting a reset is allowed but gates the operator's own session. */
const UA_SELF_ID = UA_USERS_SEED[1].id;
/* Toast atoms aren't bridged to the kit globals — read them off the DS namespace. */
const { Toast: UAToast, ToastStack: UAToastStack } = window.OdysseyDesignSystem_d5aa51 || {};

/* ---------- 4. Formatting helpers ---------- */
const uaDate = (iso) => {
  if (!iso) return null;
  const d = new Date(iso);
  if (isNaN(d)) return null;
  return `${String(d.getUTCDate()).padStart(2, '0')} ${['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'][d.getUTCMonth()]} ${d.getUTCFullYear()}`;
};
const uaRoleTone = (role) => {
  const t = { Admin: 'violet', Owner: 'tide', User: 'sea', Guest: 'ink' };
  const map = {
    violet: { bg: 'rgba(139,92,246,0.16)', fg: 'var(--violet-text)' },
    tide:   { bg: 'rgba(79,215,203,0.14)', fg: 'var(--tide-400)' },
    sea:    { bg: 'rgba(26,165,224,0.14)', fg: 'var(--sea-400)' },
    mint:   { bg: 'rgba(74,222,128,0.14)', fg: 'var(--mint-500)' },
    ink:    { bg: 'rgba(152,164,188,0.14)', fg: 'var(--ink-300)' },
  };
  return map[t[role]] || map.ink;
};

/* ---------- 4b. Sorting (mirrors MudTableSortLabel) ----------
   Click a header to sort; click again to flip direction. Roles sort by their
   privilege rank rather than alphabetically; booleans sort false→true; null
   created dates sort to the bottom in ascending order. */
const UA_ROLE_RANK = { Admin: 0, Owner: 1, User: 2, Guest: 3 };
const uaSortVal = (u, key) => {
  switch (key) {
    case 'displayName':    return u.displayName.toLowerCase();
    case 'fullName':       return uaFullName(u).toLowerCase();
    case 'birthDate':      return u.birthDate ? new Date(u.birthDate).getTime() : Infinity;
    case 'email':          return (u.email || '').toLowerCase();
    case 'role':           return UA_ROLE_RANK[u.role] ?? 99;
    case 'emailConfirmed': return u.emailConfirmed ? 1 : 0;
    case 'enabled':        return u.enabled ? 1 : 0;
    case 'createdAtUtc':   return u.createdAtUtc ? new Date(u.createdAtUtc).getTime() : -Infinity;
    default:               return 0;
  }
};
/* SortHeader now lives in /components (window.SortHeader, bridged by
   Components.jsx). It used to be defined inline here. */

/* ---------- 5. Small presentational atoms ---------- */
const RolePill = ({ role }) => {
  const m = UA_ROLE_META[role] || {};
  return <span className={`ua-role ${m.cls || ''}`}><MIcon name={m.icon || 'person'} size={14} />{role}</span>;
};
const EnabledBadge = ({ on }) => on
  ? <span className="ua-badge on"><span className="dot" style={{ width: 6, height: 6, borderRadius: 999, background: 'currentColor' }} />Enabled</span>
  : <span className="ua-badge off"><MIcon name="lock" size={14} />Disabled</span>;
const EmailBadge = ({ on }) => on
  ? <span className="ua-badge confirmed"><MIcon name="mark_email_read" size={14} />Confirmed</span>
  : <span className="ua-badge unconfirmed"><MIcon name="mark_email_unread" size={14} />Unconfirmed</span>;
const TwoFactorBadge = ({ on }) => on
  ? <span className="ua-badge confirmed"><MIcon name="verified_user" size={14} />2FA on</span>
  : <span className="ua-badge unconfirmed"><MIcon name="gpp_maybe" size={14} />2FA off</span>;

/* ---------- 6. Role → permission claim chips ---------- */
const ClaimChips = ({ role }) => {
  const perms = (UA_ROLE_BY[role] || {}).permissions || [];
  return (
    <div className="ua-claims">
      {perms.map(p => <span key={p} className={`ua-claim ${p.startsWith('users.') ? 'granted' : ''}`}>{p}</span>)}
    </div>
  );
};

/* ---------- 7. Expanded DETAIL (read view) ---------- */
const UserDetail = ({ u, canEdit, onEdit }) => (
  <div className="acct-detail">
    <div className="meta-grid">
      <MetaTile label="User ID" value={u.id} mono />
      <MetaTile label="Username" value={u.userName || '—'} mono />
      <MetaTile label="Email" value={u.email || '—'} />
      <MetaTile label="Full name" value={uaFullName(u) || '—'} />
      <MetaTile label="Display name" value={u.displayName || '—'} />
      <MetaTile label="Date of birth" value={uaDate(u.birthDate) || '—'} mono />
      <MetaTile label="Sex" value={u.sex || '—'} />
      <MetaTile label="Role" value={<RolePill role={u.role} />} />
      <MetaTile label="Account status" value={<EnabledBadge on={u.enabled} />} />
      {u.mustChangePassword && <MetaTile label="Password" value={<Chip tone="warning" icon="lock_reset">Reset pending</Chip>} />}
      <MetaTile label="Email status" value={<EmailBadge on={u.emailConfirmed} />} />
      <MetaTile label="Lockout ends" value={u.lockoutEnd ? (uaDate(u.lockoutEnd) || 'Indefinite') : '—'} mono />
      <MetaTile label="Created" value={uaDate(u.createdAtUtc) || 'Not recorded'} mono />
    </div>

    <Collapsible icon="key" title="Role permissions" count={(UA_ROLE_BY[u.role] || {}).permissions.length} defaultOpen={false}>
      <div style={{ padding: '12px 14px' }}>
        <div className="ua-cat-note" style={{ marginBottom: 12 }}>
          Permissions are granted by the <b style={{ color: 'var(--mud-palette-text-primary)' }}>{u.role}</b> role and are read-only here.
          Change access by assigning a different role.
        </div>
        <ClaimChips role={u.role} />
      </div>
    </Collapsible>

  </div>
);

/* ---------- 8. Expanded EDIT panel (swaps in for the detail) ---------- */
const UserEdit = ({ u, enabledAdminCount, onSave, onCancel }) => {
  const { useState } = React;
  const [draft, setDraft] = useState({ emailConfirmed: u.emailConfirmed, enabled: u.enabled, role: u.role });
  const [confirming, setConfirming] = useState(false);
  const [saving, setSaving] = useState(false);
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); setConfirming(false); };

  const changed = draft.emailConfirmed !== u.emailConfirmed || draft.enabled !== u.enabled || draft.role !== u.role;

  // Disruptive changes that warrant a confirmation step (spec §8.5).
  const willDisable   = u.enabled && !draft.enabled;
  const willUnconfirm = u.emailConfirmed && !draft.emailConfirmed;
  const touchesAdmin  = draft.role !== u.role && (draft.role === 'Admin' || u.role === 'Admin');
  const disruptive = willDisable || willUnconfirm || touchesAdmin;

  // Last-enabled-Admin protection (would return 409 from the backend).
  const isLastEnabledAdmin = u.role === 'Admin' && u.enabled && enabledAdminCount <= 1;
  const conflict = isLastEnabledAdmin && (
    (!draft.enabled && 'Disabling this account would leave the system with no enabled Admin.') ||
    (draft.role !== 'Admin' && 'Changing this role would leave the system with no enabled Admin.')
  );

  const confirmLines = [
    willDisable && 'Disable this account — the user will be signed out and locked out.',
    touchesAdmin && (draft.role === 'Admin' ? 'Grant Admin — full user-management access.' : 'Remove Admin access from this user.'),
    willUnconfirm && 'Mark the email address as unconfirmed.',
  ].filter(Boolean);

  const attempt = () => {
    if (!changed || conflict) return;
    if (disruptive && !confirming) { setConfirming(true); return; }
    commit();
  };
  const commit = () => {
    setSaving(true);
    // Simulates the PATCH (flags) + PUT (role) round-trip, then applies the
    // returned ExistingUser to the row.
    setTimeout(() => onSave({
      emailConfirmed: draft.emailConfirmed,
      enabled: draft.enabled,
      role: draft.role,
      lockoutEnd: draft.enabled ? null : (u.lockoutEnd || '2099-12-31T00:00:00+00:00'),
    }), 220);
  };

  return (
    <div className="acct-detail acct-edit">
      <div className="acct-edit-head">
        <MIcon name="edit" size={18} />
        <span>Edit user — {u.displayName}</span>
      </div>

      {/* Read-only identity (cannot be changed from this page) */}
      <div className="ua-locked-grid">
        <div className="ua-locked-field">
          <span className="ua-locked-label"><MIcon name="lock" size={13} />Username</span>
          <span className="ua-locked-value mono">{u.userName || '—'}</span>
        </div>
        <div className="ua-locked-field">
          <span className="ua-locked-label"><MIcon name="lock" size={13} />Email</span>
          <span className="ua-locked-value">{u.email || '—'}</span>
        </div>
      </div>

      {/* Editable flags */}
      <div className="ua-edit-section-label">Account flags</div>
      <div className="ua-edit-toggles">
        <div className="ua-toggle-row">
          <span className="ua-toggle-ic"><MIcon name="mark_email_read" size={20} /></span>
          <div className="ua-toggle-text">
            <div className="ua-toggle-ttl">Email confirmed</div>
            <div className="ua-toggle-desc">Marks the user's email address as verified.</div>
          </div>
          <Switch checked={draft.emailConfirmed} onChange={set('emailConfirmed')} />
        </div>
        <div className="ua-toggle-row">
          <span className="ua-toggle-ic"><MIcon name="check_circle" size={20} /></span>
          <div className="ua-toggle-text">
            <div className="ua-toggle-ttl">Account enabled</div>
            <div className="ua-toggle-desc">Disabling signs the user out and applies a backend lockout.</div>
          </div>
          <Switch checked={draft.enabled} onChange={set('enabled')} />
        </div>
      </div>

      {/* Role */}
      <div className="ua-edit-section-label">Role</div>
      <div style={{ maxWidth: 320 }}>
        <Select value={draft.role} onChange={set('role')} options={UA_ROLE_OPTIONS}
          helper={(UA_ROLE_META[draft.role] || {}).desc} />
      </div>

      {/* Blocking 409 guard */}
      {conflict && (
        <div className="ua-guard">
          <Alert severity="error">
            <b>Action blocked.</b> {conflict} The system must keep at least one enabled Admin.
          </Alert>
        </div>
      )}

      {/* Confirmation step for disruptive changes */}
      {confirming && !conflict && (
        <div className="ua-guard">
          <Alert severity="warning">
            <b>Confirm these changes</b>
            <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
              {confirmLines.map((l, i) => <li key={i} style={{ marginTop: 2 }}>{l}</li>)}
            </ul>
          </Alert>
        </div>
      )}

      <div className="acct-edit-actions">
        <Button variant="text" onClick={onCancel}>Cancel</Button>
        {confirming && !conflict ? (
          <Button variant="filled" color={willDisable ? 'error' : 'primary'} icon="check"
            onClick={commit} disabled={saving}>Yes, apply changes</Button>
        ) : (
          <Button variant="filled" color="primary" icon="save"
            onClick={attempt} disabled={!changed || !!conflict || saving}>
            {saving ? 'Saving…' : 'Save changes'}
          </Button>
        )}
      </div>
    </div>
  );
};

/* ---------- 9. The user row + its detail / edit panel are now the shared DS
   RecordTable, configured inline in the Users page below. ---------- */

/* ---------- 12. Page ---------- */
function Users({ resetOutcome = 'delivered' }) {
  const { useState, useEffect, useMemo } = React;
  // The current operator's claims. With users.update the page is in edit mode;
  // strip it (or users.read) and the page would fall back to read-only / 403.
  const canEdit = true; // operator holds users.update
  const canDelete = true; // operator holds users.delete (Admin) — gates the row Delete action

  const [users, setUsers] = useState(UA_USERS_SEED);
  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [roleFilter, setRoleFilter] = useState([]);
  const [enabledFilter, setEnabledFilter] = useState([]);
  // No email-status or 2FA filter: the ExistingUser contract exposes no two-factor
  // field and GET api/users filters by role + enabled only, so the real page can't.
  const [pendingDelete, setPendingDelete] = useState(null);
  const [deleting, setDeleting] = useState(false);
  // Shared sort (§6.12): one {key,dir} drives the toolbar SortSelect AND the
  // clickable column headers (retained — no discoverability regression). All
  // six historical keys are preserved.
  const [sort, setSort] = useState({ key: 'displayName', dir: 'asc' });
  // Server pagination: page (1-based) + rows-per-page (footer Pager owns it,
  // toolbar PageSizeSelect mirrors it).
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);
  // Admin-initiated "Send password reset" (spec §3): a confirmation modal, then
  // one of four honest outcomes surfaced as a toast. `resetOutcome` (a Tweak)
  // drives which outcome this prototype simulates.
  const [pendingReset, setPendingReset] = useState(null);
  const [sending, setSending] = useState(false);
  const [toast, setToast] = useState(null);
  const pushToast = (severity, message) => setToast({ severity, message, k: Date.now() });

  // Debounce the search ~300ms (spec §5.1).
  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  const filtered = useMemo(() => users.filter(u => {
    if (roleFilter.length && !roleFilter.includes(u.role)) return false;
    if (enabledFilter.length && !enabledFilter.includes(String(u.enabled))) return false;
    if (debouncedQ) {
      const n = debouncedQ.toLowerCase();
      if (!(`${u.userName} ${u.email} ${u.displayName}`.toLowerCase().includes(n))) return false;
    }
    return true;
  }), [users, roleFilter, enabledFilter, debouncedQ]);

  // Any search / filter / sort / size change returns to page 1 (server contract).
  useEffect(() => { setPage(1); }, [debouncedQ, roleFilter, enabledFilter, sort, pageSize]);
  const totalCount = filtered.length;
  const paged = useMemo(() => {
    if (pageSize === 'all') return filtered;
    const start = (page - 1) * pageSize;
    return filtered.slice(start, start + pageSize);
  }, [filtered, page, pageSize]);

  const enabledAdminCount = users.filter(u => u.role === 'Admin' && u.enabled).length;
  const enabledCount = users.filter(u => u.enabled).length;

  const resetIsSelf = !!pendingReset && pendingReset.id === UA_SELF_ID;
  const confirmReset = () => {
    if (sending) return;
    setSending(true);
    setTimeout(() => {
      const u = pendingReset;
      const outcome = resetOutcome || 'delivered';
      // A delivered / undelivered reset still applies the flag + revokes sessions;
      // no-email and throttled mutate nothing (spec §11).
      if (outcome === 'delivered' || outcome === 'undelivered') {
        setUsers(prev => prev.map(x => x.id === u.id ? { ...x, mustChangePassword: true } : x));
      }
      if (outcome === 'delivered')        pushToast('success', `Password reset link sent to ${u.email}.`);
      else if (outcome === 'undelivered') pushToast('warning', `Reset applied, but the email couldn’t be delivered. Ask ${u.displayName} to use Forgot password on the sign-in page.`);
      else if (outcome === 'no-email')    pushToast('error', `${u.displayName} has no confirmed email address, so a reset link can’t be sent.`);
      else if (outcome === 'throttled')   pushToast('warning', `Too many reset emails to ${u.email} recently. Try again in a little while.`);
      setSending(false);
      setPendingReset(null);
    }, 650);
  };

  const onSave = (id, patch) => setUsers(prev => prev.map(u => u.id === id ? { ...u, ...patch } : u));
  const clearFilters = () => { setQ(''); setRoleFilter([]); setEnabledFilter([]); };

  const hasFilters = !!debouncedQ || roleFilter.length > 0 || enabledFilter.length > 0;

  // Permanent delete (users.delete). The API guards self-deletion and the last
  // enabled Admin with a 409; the reproducible case in this seed is the
  // last-enabled-Admin block (Marcus Reed), surfaced inline in the dialog.
  const targetIsLastEnabledAdmin = !!pendingDelete && pendingDelete.role === 'Admin' && pendingDelete.enabled && enabledAdminCount <= 1;
  const confirmDelete = () => {
    setDeleting(true);
    setTimeout(() => {
      setUsers(prev => prev.filter(x => x.id !== pendingDelete.id));
      setDeleting(false);
      setPendingDelete(null);
    }, 300);
  };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Users"
        icon="manage_accounts"
        sub={(
          <span className="row gap-3" style={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <span>{users.length} users · {enabledCount} enabled</span>
          </span>
        )}
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search username or email…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 180 }}>
              <MultiSelect allLabel="All roles" value={roleFilter} onChange={setRoleFilter}
                options={UA_ROLE_OPTIONS} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="All statuses" value={enabledFilter} onChange={setEnabledFilter}
                options={[
                  { value: 'true', label: 'Enabled' },
                  { value: 'false', label: 'Disabled' },
                ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[
                { key: 'displayName',    label: 'Username',       type: 'text' },
                { key: 'fullName',       label: 'Full name',      type: 'text' },
                { key: 'birthDate',      label: 'Date of birth',  type: 'date' },
                { key: 'email',          label: 'Email',          type: 'text' },
                { key: 'role',           label: 'Role',           type: 'status' },
                { key: 'emailConfirmed', label: 'Email status',   type: 'status' },
                { key: 'enabled',        label: 'Account status', type: 'status' },
                { key: 'createdAtUtc',   label: 'Created',        type: 'date' },
              ]} />
            {hasFilters && (
              <Button variant="text" icon="close" onClick={clearFilters}>Clear</Button>
            )}
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
          </div>
        )}
      />

      <Card>
        <CardBody style={{ padding: 0 }}>
          <RecordTable
            rows={paged}
            ariaLabel="Users"
            rowKey={(u) => u.id}
            defaultSort={{ key: 'displayName', dir: 'asc' }}
            sort={sort}
            onSortChange={setSort}
            columns={[
              {
                key: 'displayName', header: 'User', sortable: true, sortType: 'text', sortValue: (u) => uaSortVal(u, 'displayName'),
                cell: (u, ctx) => (
                  <div className="ua-user-cell">
                    <Avatar initials={u.initials} tone={uaRoleTone(u.role)} />
                    <div>
                      <div className="ua-user-name" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>{u.displayName}{ctx.justSaved && <Chip tone="income" dot>Saved</Chip>}</div>
                      <div className="ua-user-id">@{u.userName || '—'}</div>
                    </div>
                  </div>
                ),
              },
              { key: 'email', header: 'Email', sortable: true, sortType: 'text', className: 'ua-email', sortValue: (u) => uaSortVal(u, 'email'), cell: (u) => u.email || <span className="muted">—</span> },
              { key: 'fullName', header: 'Full name', sortable: true, sortType: 'text', sortValue: (u) => uaSortVal(u, 'fullName'), cell: (u) => uaFullName(u) },
              { key: 'birthDate', header: 'Date of birth', sortable: true, sortType: 'date', className: 'muted mono', sortValue: (u) => uaSortVal(u, 'birthDate'), cell: (u) => uaDate(u.birthDate) || '—' },
              { key: 'role', header: 'Role', sortable: true, sortType: 'status', sortValue: (u) => uaSortVal(u, 'role'), cell: (u) => <RolePill role={u.role} /> },
              { key: 'emailConfirmed', header: 'Email', sortable: true, sortType: 'status', sortValue: (u) => uaSortVal(u, 'emailConfirmed'), cell: (u) => <EmailBadge on={u.emailConfirmed} /> },
              { key: 'enabled', header: 'Account', sortable: true, sortType: 'status', sortValue: (u) => uaSortVal(u, 'enabled'), cell: (u) => (
                <span style={{ display: 'inline-flex', flexDirection: 'column', alignItems: 'flex-start', gap: 4 }}>
                  <EnabledBadge on={u.enabled} />
                  {u.mustChangePassword && <Chip tone="warning" icon="lock_reset">Reset pending</Chip>}
                </span>
              ) },
              { key: 'createdAtUtc', header: 'Created', sortable: true, sortType: 'date', className: 'muted mono', sortValue: (u) => uaSortVal(u, 'createdAtUtc'), cell: (u) => uaDate(u.createdAtUtc) || '—' },
            ]}
            actions={(u, ctx) => [
              { icon: ctx.expanded ? 'close' : 'expand_more', label: ctx.expanded ? 'Collapse' : 'View details', onClick: ctx.toggle },
              ...(canEdit ? [{ icon: 'edit', label: 'Edit', onClick: ctx.startEdit }] : []),
              ...(canEdit ? [{ icon: 'lock_reset', label: 'Send password reset', onClick: () => setPendingReset(u) }] : []),
              { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(u.id); } },
              ...(canDelete ? [
                { divider: true },
                { icon: 'delete_forever', label: 'Delete', danger: true, onClick: () => setPendingDelete(u) },
              ] : []),
            ]}
            renderDetail={(u) => <UserDetail u={u} canEdit={canEdit} />}
            renderEdit={canEdit ? (u, { save, cancel }) => <UserEdit u={u} enabledAdminCount={enabledAdminCount} onSave={save} onCancel={cancel} /> : undefined}
            onSave={onSave}
            empty={(
              <EmptyState icon="manage_search" mutedIcon
                title="No users match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everyone.' : 'There are no users to show.'}
                action={hasFilters ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button> : undefined} />
            )}
          />
          {totalCount > 0 && (
            <Pager page={page} pageSize={pageSize} totalCount={totalCount}
              onPageChange={setPage} onPageSizeChange={setPageSize} />
          )}
        </CardBody>
      </Card>

      {pendingDelete && (
        <Modal
          title="Delete user"
          icon="delete_forever"
          iconTone="error"
          onClose={() => { if (!deleting) setPendingDelete(null); }}
          footer={(
            <>
              <Button variant="text" onClick={() => setPendingDelete(null)} disabled={deleting}>Cancel</Button>
              <Button variant="filled" className="danger" color="" icon="delete_forever"
                disabled={deleting || targetIsLastEnabledAdmin} onClick={confirmDelete}>
                {deleting ? 'Deleting\u2026' : 'Delete'}
              </Button>
            </>
          )}
        >
          <div className="col gap-3">
            <div style={{ font: '400 14px/1.65 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>
              Permanently delete <b>{pendingDelete.displayName}</b>{' '}
              (<span className="mono">{pendingDelete.email || pendingDelete.userName || pendingDelete.id}</span>)?
              This removes the account and cannot be undone.
            </div>
            {targetIsLastEnabledAdmin && (
              <Alert severity="error">
                <b>Action blocked.</b> This is the only enabled Admin. The system must keep at least one
                enabled Admin, so this account can't be deleted.
              </Alert>
            )}
          </div>
        </Modal>
      )}

      {pendingReset && (
        <Modal
          title="Send password reset"
          icon="lock_reset"
          iconTone="warning"
          onClose={() => { if (!sending) setPendingReset(null); }}
          footer={(
            <>
              <Button variant="text" onClick={() => setPendingReset(null)} disabled={sending}>Cancel</Button>
              <Button variant="filled" color="primary" icon="send" loading={sending} disabled={sending} onClick={confirmReset}>
                {sending ? 'Sending…' : 'Send reset link'}
              </Button>
            </>
          )}
        >
          <div className="col gap-3" style={{ font: '400 14px/1.65 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>
            <div>
              We’ll email <b>{pendingReset.email}</b> a link to choose a new password. They’ll be signed out of all
              devices immediately, and if they sign in with their current password they’ll have to change it before
              continuing. Their current password keeps working until they use the link.
            </div>
            {resetIsSelf && (
              <Alert severity="warning">
                <b>This is your own account.</b> You’ll be signed out everywhere else and asked to change your
                password before continuing.
              </Alert>
            )}
          </div>
        </Modal>
      )}

      {toast && UAToast && UAToastStack && (
        <UAToastStack>
          <UAToast key={toast.k} severity={toast.severity} duration={5200} onClose={() => setToast(null)} message={toast.message} />
        </UAToastStack>
      )}
    </div>
  );
}

// Export the page plus the role/permission reference data & atoms the
// dedicated Roles page (Roles.jsx) renders from.
Object.assign(window, { Users, UA_ROLES, UA_ROLE_META, UA_CATEGORIES, UA_PERMISSIONS, RolePill });
