/* =============================================================
   Account.jsx — Self-service User Account area (/account).

   A single authenticated page with an in-page section tab bar that
   mirrors the spec's information architecture:

     Overview    — identity + security + access summary, recommendations
     Profile     — read-only identity, change email, resend confirmation
     Password    — change password with a live requirement checklist
     Two-factor  — authenticator 2FA flow + recovery codes  (AccountTwoFactor.jsx)
     Permissions — read-only claims & permissions for the current role

   Everything is built from the kit atoms (Card / Field / Switch / Chip /
   Alert / Button / MIcon) + the admin permission catalog. The current
   user is the seed Owner "Jane Sato". Styling lives in account.css.
   ============================================================= */

/* ---- The signed-in user (mirrors ApplicationUser + the Owner seed row).
   Identity/security fields only; the human name now lives on the UserProfile
   (see profile-fields.jsx / DEFAULT_PROFILE), resolved into the header below. */
const ACC_USER = {
  username: 'jane.sato',
  email: 'jane@odyssey.app',
  userId: 'a3f9c1d2-0e84-4b17-9c52-6b1d44e0a7f3',
  emailConfirmed: true,
  role: 'Owner',
  createdAt: '12 Jan 2023',
  lastPasswordChange: '14 Mar 2026',
};

/* ---- Owner permission set, grouped by domain (from the admin catalog) ---- */
const accOwnerPerms = () => {
  const roles = window.UA_ROLES || [];
  const owner = roles.find(r => r.name === 'Owner');
  return owner ? owner.permissions : [];
};

/* ---- Password policy ----
   The rule set + minimum length now live in ONE place — the shared
   PASSWORD_POLICY (DS: components/PasswordRules.jsx), which mirrors the server's
   IdentityOptions.Password gate (16 chars + four classes). This corrects the
   old local "6 characters" copy that had drifted from the real policy. */

/* ---- Small reusable bits ---- */
/* Card header: the icon badge + title + sub, folded into the top of a card
   (followed by a hairline divider). Replaces the old floating section header. */
const AccCardHead = ({ icon, tone, title, sub }) => (
  <div className="acc-cardhead bordered">
    <span className={`acc-ic ${tone || ''}`}><MIcon name={icon} /></span>
    <div className="acc-sec-titles">
      <div className="acc-sec-title">{title}</div>
      {sub && <div className="acc-sec-sub">{sub}</div>}
    </div>
  </div>
);
const AccLocked = ({ label, value, mono }) => (
  <div className="acc-locked">
    <span className="acc-locked-label"><MIcon name="lock" size={13} />{label}</span>
    <span className={`acc-locked-val ${mono ? 'mono' : ''}`}>{value}</span>
  </div>
);

/* =============================================================
   OVERVIEW
   ============================================================= */
const AccOverview = ({ user, profile, tfa, onGo }) => {
  const permCount = accOwnerPerms().length;
  const resolved = resolveProfileName(profile);
  return (
    <div className="acc-overview">
      {/* Summary cards */}
      <div className="acc-cards">
        <Card outlined><CardBody>
          <div className="acc-card-head"><MIcon name="badge" /><span className="acc-card-ttl">Identity</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Name</span><span className="acc-kv-val">{resolved || '—'}</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Email</span><span className="acc-kv-val mono">{user.email}</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Username</span><span className="acc-kv-val mono">{user.username}</span></div>
          <div className="acc-card-foot row gap-2"><Button variant="text" icon="badge" onClick={() => onGo('profile')}>Edit profile</Button><Button variant="text" icon="alternate_email" onClick={() => onGo('email')}>Manage email</Button></div>
        </CardBody></Card>

        <Card outlined><CardBody>
          <div className="acc-card-head"><MIcon name="security" /><span className="acc-card-ttl">Security</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Password</span><span className="acc-kv-val">Changed {user.lastPasswordChange}</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Two-factor</span><span className="acc-kv-val">{tfa.enabled ? 'On · authenticator' : 'Off'}</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Recovery codes</span><span className="acc-kv-val mono">{tfa.enabled ? `${tfa.codesRemaining} left` : '—'}</span></div>
          <div className="acc-card-foot"><Button variant="text" icon={tfa.enabled ? 'tune' : 'add_moderator'} onClick={() => onGo('twofa')}>{tfa.enabled ? 'Manage security' : 'Set up two-factor'}</Button></div>
        </CardBody></Card>

        <Card outlined><CardBody>
          <div className="acc-card-head"><MIcon name="key" /><span className="acc-card-ttl">Access</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Role</span><span className="acc-kv-val">{window.RolePill ? <RolePill role={user.role} /> : user.role}</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Permissions</span><span className="acc-kv-val mono">{permCount} granted</span></div>
          <div className="acc-kv"><span className="acc-kv-label">Member since</span><span className="acc-kv-val">{user.createdAt}</span></div>
          <div className="acc-card-foot"><Button variant="text" icon="visibility" onClick={() => onGo('permissions')}>View permissions</Button></div>
        </CardBody></Card>
      </div>
    </div>
  );
};

/* =============================================================
   PROFILE (identity) — the user's own name & personal details.
   The self-service edit surface for the UserProfile entity (spec §3):
   First/Last (required), Date of birth + Sex (required), Middle name,
   Title, Display name (optional). Owner-only — no admin read/write.
   ============================================================= */
const AccIdentity = ({ profile, onSave }) => {
  const { useState } = React;
  const [draft, setDraft] = useState(profile);
  const [saved, setSaved] = useState(false);
  const onField = (name) => (v) => { setDraft((p) => ({ ...p, [name]: v })); setSaved(false); };

  const { errors, isComplete } = validateProfile(draft);
  const errorCount = Object.keys(errors).length;
  const dirty = JSON.stringify(draft) !== JSON.stringify(profile);
  const canSave = dirty && errorCount === 0 && isComplete;

  const resolved = resolveProfileName(draft);

  const save = () => {
    if (!canSave) return;
    onSave(draft);
    setSaved(true);
    setTimeout(() => setSaved(false), 4000);
  };

  return (
    <div className="acc-section">
      {saved && (
        <Alert severity="success"><b>Profile saved.</b> Your name updates everywhere you appear — no need to sign out.</Alert>
      )}

      <Card outlined><CardBody>
        <AccCardHead icon="badge" title="Your profile"
          sub="Your name and personal details. This is how you appear to other people in this workspace." />

        {/* Live preview of the resolved label + avatar the rest of the app shows */}
        <div className="acc-profile-preview">
          <span className="acc-avatar-xl">{profileInitials(draft)}</span>
          <div className="acc-profile-preview-id">
            <div className="acc-profile-preview-name">{resolved || 'Your name'}</div>
            <div className="acc-profile-preview-meta">
              <Chip tone="info" icon="visibility">Shown to others</Chip>
              <span className="acc-profile-preview-hint">
                {draft.displayName && draft.displayName.trim()
                  ? 'Using your display name'
                  : 'Using your first name — set a display name to override'}
              </span>
            </div>
          </div>
        </div>

        <ProfileFields profile={draft} onField={onField} errors={errors} idPrefix="acc" />

        <ProfileTransparencyNote />

        <div className="acc-form-actions">
          <Button variant="filled" color="primary" icon="save" disabled={!canSave} onClick={save}>Save profile</Button>
          {saved && <span className="acc-saved"><MIcon name="check" />Saved</span>}
        </div>
      </CardBody></Card>
    </div>
  );
};

/* =============================================================
   EMAIL — change email address + resend confirmation
   ============================================================= */
const AccProfile = ({ user }) => {
  const { useState } = React;
  const [newEmail, setNewEmail] = useState('');
  const [pw, setPw] = useState('');
  const [saved, setSaved] = useState(false);
  const [resent, setResent] = useState(false);

  const emailValid = /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(newEmail);
  const same = newEmail.trim().toLowerCase() === user.email.toLowerCase();
  const canSave = emailValid && !same && pw.length > 0;

  const save = () => { setSaved(true); setNewEmail(''); setPw(''); setTimeout(() => setSaved(false), 4000); };

  return (
    <div className="acc-section">
      <Card outlined><CardBody>
        <AccCardHead icon="alternate_email" title="Email address"
          sub="Your email is your sign-in identity and where security notifications are sent." />

        <div className="row gap-3" style={{ marginBottom: 16, flexWrap: 'wrap' }}>
          <span className="acc-kv-label">Current email</span>
          <code className="mono" style={{ fontSize: 14, color: 'var(--mud-palette-text-primary)' }}>{user.email}</code>
          <Chip tone="info" icon="mark_email_read">Email confirmed</Chip>
        </div>

        {saved && (
          <Alert severity="success">
            <b>Confirmation sent.</b> We've emailed a confirmation link to your new address. Your sign-in email stays the same until you confirm it.
          </Alert>
        )}

        <div className="acc-form" style={{ marginTop: saved ? 16 : 0 }}>
          <Field label="New email address" type="email" value={newEmail} onChange={setNewEmail}
            placeholder="you@example.com"
            error={newEmail && !emailValid ? 'Enter a valid email address.' : (same ? 'This is already your email address.' : '')} />
          <Field label="Current password" type="password" value={pw} onChange={setPw}
            placeholder="••••••••" helper="Confirm it's you before we change your email." />
          <div className="acc-form-actions">
            <Button variant="filled" color="primary" icon="send" disabled={!canSave} onClick={save}>Update email</Button>
          </div>
        </div>
      </CardBody></Card>

      <Card outlined><CardBody>
        <div className="row" style={{ justifyContent: 'space-between', gap: 16, flexWrap: 'wrap' }}>
          <div className="row" style={{ gap: 14, alignItems: 'center', minWidth: 0 }}>
            <span className="acc-ic"><MIcon name="mark_email_unread" /></span>
            <div className="acc-sec-titles">
              <div className="acc-sec-title" style={{ fontSize: 14 }}>Resend confirmation email</div>
              <div className="acc-sec-sub">Didn't get the confirmation message? Send it again to your current address.</div>
            </div>
          </div>
          {resent
            ? <span className="acc-saved"><MIcon name="check" />Sent — check your inbox</span>
            : <Button variant="outlined" icon="forward_to_inbox" onClick={() => { setResent(true); setTimeout(() => setResent(false), 4000); }}>Resend</Button>}
        </div>
      </CardBody></Card>
    </div>
  );
};

/* =============================================================
   PASSWORD — change password with live checklist
   ============================================================= */
const AccPassword = () => {
  const { useState } = React;
  const [saved, setSaved] = useState(false);
  const [formKey, setFormKey] = useState(0);

  // A save clears the fields by remounting the shared form (changed key).
  const save = () => { setSaved(true); setFormKey((k) => k + 1); setTimeout(() => setSaved(false), 4000); };

  return (
    <div className="acc-section">
      {saved && (
        <Alert severity="success"><b>Password updated.</b> Your new password is in effect. Other signed-in sessions may be asked to sign in again.</Alert>
      )}

      <Card outlined><CardBody>
        <AccCardHead icon="password" title="Change password"
          sub="Choose a strong password you don't use anywhere else." />
        {/* One shared triad + live checklist + error banner — the SAME
            PasswordChangeForm the admin forced-reset gate renders, so the two
            can never drift, and the autocomplete + role="alert" a11y fixes land
            once. */}
        <PasswordChangeForm key={formKey} onSubmit={save} columns={2} />
      </CardBody></Card>
    </div>
  );
};

/* =============================================================
   PERMISSIONS — read-only claims & permissions
   ============================================================= */
const AccPermissions = ({ user, name }) => {
  const perms = accOwnerPerms();
  const cats = window.UA_CATEGORIES || [];
  const meta = (window.UA_ROLE_META || {})[user.role] || {};
  const byCat = cats
    .map(c => ({ ...c, granted: perms.filter(p => p.startsWith(c.cat + '.')) }))
    .filter(c => c.granted.length > 0);

  return (
    <div className="acc-section">
      {/* Identity claims */}
      <Card outlined><CardBody>
        <AccCardHead icon="key" title="Claims & permissions"
          sub="What your account can do in Odyssey. Access is granted by your role and is read-only here." />
        <div className="meta-grid">
          <MetaTile label="Name" value={name || '—'} />
          <MetaTile label="Email" value={user.email} mono />
          <MetaTile label="Username" value={user.username} mono />
          <MetaTile label="Role" value={window.RolePill ? <RolePill role={user.role} /> : user.role} />
          <MetaTile label="User ID" value={user.userId} mono />
          <MetaTile label="Permissions granted" value={`${perms.length} granted`} />
        </div>
      </CardBody></Card>

      {/* Permission catalog grouped by domain */}
      <Card outlined><CardBody>
        <AccCardHead icon="lock_open" title="Permissions by area"
          sub="Granted actions grouped by the part of Odyssey they apply to." />
        <div className="ua-cat-grid">
          {byCat.map(c => (
            <div key={c.cat} className="ua-cat-card">
              <div className="ua-cat-name"><MIcon name={c.icon} size={15} />{c.cat}</div>
              <div className="ua-actions-row">
                {c.granted.map(p => <span key={p} className="ua-action-chip">{p.split('.').pop()}</span>)}
              </div>
            </div>
          ))}
        </div>
      </CardBody></Card>
    </div>
  );
};

/* =============================================================
   PAGE SHELL — header + tab bar + active section
   ============================================================= */
/* Searchable section index — drives the in-header "Search" region so a user can
   jump to the card they want by name or synonym (e.g. "2fa" → Two-factor). */
const ACC_SEARCH = [
  { key: 'profile',     label: 'Your profile',
    terms: ['profile', 'name', 'display name', 'first name', 'last name', 'middle name', 'title', 'date of birth', 'birthday', 'dob', 'sex', 'identity', 'who i am'] },
  { key: 'email',       label: 'Email address',
    terms: ['email', 'email address', 'change email', 'username', 'confirmation', 'sign-in', 'sign in'] },
  { key: 'password',    label: 'Change password',
    terms: ['password', 'change password', 'reset password', 'credentials', 'passphrase'] },
  { key: 'twofa',       label: 'Two-factor authentication',
    terms: ['two-factor', 'two factor', '2fa', 'mfa', 'authenticator', 'recovery codes', 'otp', 'verification', 'security key'] },
  { key: 'permissions', label: 'Claims & permissions',
    terms: ['permissions', 'claims', 'access', 'role', 'owner', 'authorization', 'what i can do'] },
];

function AccountPage({ onLogout }) {
  const { useState } = React;
  // 2FA state lifted here so the Overview + the Two-factor section share one truth.
  const [tfa, setTfa] = useState({ enabled: false, recoveryCodes: [], codesRemaining: 0, enabledAt: null });
  // The user's own UserProfile. Lifted here so the header + Overview + the
  // Profile section all read one truth; a save re-renders the resolved name
  // everywhere with no reload (spec §3 “without a reload”).
  const [profile, setProfile] = useState(DEFAULT_PROFILE);
  const resolvedName = resolveProfileName(profile) || `@${ACC_USER.username}`;
  const resolvedInitials = profileInitials(profile);

  // In-page search across the stacked section cards. Empty query = show all.
  const [query, setQuery] = useState('');
  const q = query.trim().toLowerCase();
  const matches = (key) => {
    if (!q) return true;
    const sec = ACC_SEARCH.find(s => s.key === key);
    if (!sec) return false;
    return sec.label.toLowerCase().includes(q) || sec.terms.some(t => t.includes(q) || q.includes(t));
  };
  const visibleKeys = ACC_SEARCH.filter(s => matches(s.key)).map(s => s.key);

  // Jump from an Overview "manage" link to the matching section in the merged
  // list. Sections are stacked in one scroll, so we scroll .main to the anchor
  // (no tab switch). Uses rect math rather than scrollIntoView.
  const goTo = (key) => {
    const el = document.getElementById('acc-sec-' + (key === 'twofa' ? 'twofa' : key));
    const main = document.querySelector('.main');
    if (!el || !main) return;
    const r = el.getBoundingClientRect();
    const mr = main.getBoundingClientRect();
    main.scrollBy({ top: r.top - mr.top - 20, behavior: 'smooth' });
  };

  // Page-level problems rollup (the design-system header "signal"): each entry
  // is one notice/warning/error about this account. Right now the only signal
  // is the info-level recommendation to turn on 2FA.
  const problems = [];
  if (!tfa.enabled) problems.push({
    severity: 'info',
    title: 'Add two-factor authentication',
    body: 'Your account is currently protected by its password alone. A second step at sign-in keeps it safe even if your password is exposed.',
    fixLabel: 'Set up 2FA', fixIcon: 'add_moderator', onFix: () => goTo('twofa'),
  });
  // Highest severity present drives the toggle tint (error > warning > info);
  // the label stays the fixed "Problems" noun per the design-system page-header spec.
  const sevRank = { error: 3, warning: 2, info: 1 };
  const topSev = problems.reduce((s, p) => (sevRank[p.severity] > sevRank[s] ? p.severity : s), 'info');

  const signal = problems.length ? {
    severity: topSev,
    count: problems.length,
    label: 'Problems',
    defaultOpen: true,
    region: (
      <div className="col gap-3">
        {problems.map((p, i) => (
          <Alert key={i} severity={p.severity}>
            <div className="row" style={{ justifyContent: 'space-between', gap: 16, flexWrap: 'wrap', alignItems: 'center' }}>
              <div style={{ flex: 1, minWidth: 220 }}><b>{p.title}.</b> {p.body}</div>
              {p.fixLabel && <Button variant="filled" color="primary" icon={p.fixIcon} onClick={p.onFix}>{p.fixLabel}</Button>}
            </div>
          </Alert>
        ))}
      </div>
    ),
  } : undefined;

  return (
    <div className="col gap-6">
      <PageHeader
        title={resolvedName}
        icon={<span className="ph-avatar" aria-hidden="true">{resolvedInitials}</span>}
        sub={`${ACC_USER.email} · @${ACC_USER.username}`}
        chips={[
          window.RolePill ? <RolePill key="role" role={ACC_USER.role} /> : { label: ACC_USER.role, tone: 'outline' },
          ACC_USER.emailConfirmed
            ? { label: 'Email confirmed', tone: 'info', icon: 'mark_email_read' }
            : { label: 'Email unconfirmed', tone: 'pending', icon: 'mark_email_unread' },
          tfa.enabled
            ? { label: '2FA on', tone: 'income', icon: 'verified_user' }
            : { label: '2FA off', tone: 'outline', icon: 'gpp_maybe' },
          { label: `Member since ${ACC_USER.createdAt}`, tone: 'outline', dot: true },
        ]}
        signal={signal}
        primary={{ label: 'Sign out', icon: 'logout', onClick: onLogout }}
        overview={<AccOverview user={ACC_USER} profile={profile} tfa={tfa} onGo={goTo} />}
        overviewDefaultOpen
        search={
          <div className="col gap-3">
            {/* Search is the lone control here, so it spends the full width to
                the right (flex:1 inside a full-bleed row). */}
            <div className="row gap-3" style={{ flexWrap: 'wrap' }}>
              <div style={{ minWidth: 280, flex: 1 }}>
                <Field placeholder="Search account settings — e.g. 2fa, email, password…"
                  value={query} onChange={setQuery} autoFocus clearable />
              </div>
            </div>
            {q && (
              <div className="acc-search-meta">
                {visibleKeys.length
                  ? <span>{visibleKeys.length} {visibleKeys.length === 1 ? 'section matches' : 'sections match'} “{query.trim()}”</span>
                  : <span>No sections match “{query.trim()}”</span>}
              </div>
            )}
          </div>
        }
      />

      {/* All settings on one page — the former tab sections stacked into a
          single scrolling list of cards (mirrors the Preferences page).
          Filtered live by the header Search region. */}
      <div className="acc-list">
        {matches('profile')     && <div id="acc-sec-profile"     className="acc-list-group"><AccIdentity profile={profile} onSave={setProfile} /></div>}
        {matches('email')       && <div id="acc-sec-email"       className="acc-list-group"><AccProfile user={ACC_USER} /></div>}
        {matches('password')    && <div id="acc-sec-password"    className="acc-list-group"><AccPassword /></div>}
        {matches('twofa')       && <div id="acc-sec-twofa"       className="acc-list-group"><AccountTwoFactor tfa={tfa} setTfa={setTfa} /></div>}
        {matches('permissions') && <div id="acc-sec-permissions" className="acc-list-group"><AccPermissions user={ACC_USER} name={resolveProfileName(profile)} /></div>}
        {q && visibleKeys.length === 0 && (
          <EmptyState icon="search_off" title="No settings match your search"
            desc={`Nothing here matches “${query.trim()}”. Try a different term like "email", "password", or "2fa".`} />
        )}
      </div>
    </div>
  );
}

Object.assign(window, { AccountPage });
