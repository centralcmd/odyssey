/* =============================================================
   Roles — admin-only reference page for the workspace's roles and
   the underlying permission claims. Split out of the Users page so
   the two read-only reference tables (Roles & permissions, and
   Permission claims) live on their own destination under Users.

   Pulls its data + atoms from the Users module (UA_ROLES,
   UA_ROLE_META, UA_CATEGORIES, UA_PERMISSIONS, RolePill), which are
   exported to window there. Loaded after Users.jsx in index.html.
   ============================================================= */

/* Role → leading-media tone, mirroring uaRoleTone() on the Users page so a
   role reads with the same accent everywhere. */
const ROLE_TONE = {
  Admin: { bg: 'rgba(139,92,246,0.16)', fg: 'var(--violet-text)' },
  Owner: { bg: 'rgba(79,215,203,0.14)', fg: 'var(--tide-400)' },
  User:  { bg: 'rgba(26,165,224,0.14)', fg: 'var(--sea-400)' },
  Guest: { bg: 'rgba(152,164,188,0.14)', fg: 'var(--ink-300)' },
};

/* ---- One role as a List-row item (collapsed header + expandable detail) ----
   Built from the same scaffold as an Accounts row: leading media → body →
   value → controls, with the claim chips living in the expanded detail. The
   row is read-only reference, so the controls track carries the disclosure
   only — no overflow menu. */
const RoleRow = ({ r }) => {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  const m = UA_ROLE_META[r.name] || {};
  const tone = ROLE_TONE[r.name] || ROLE_TONE.Guest;
  const n = r.permissions.length;

  return (
    <Card className={`acct-item ${open ? 'open' : ''}`}>
      <div className="acct-head" onClick={() => setOpen(o => !o)}>
        <Avatar icon={m.icon || 'person'} tone={tone} square size="lg" />

        <div className="acct-id">
          <div className="acct-name-row">
            <span className="acct-name">{r.name}</span>
          </div>
          <div className="role-row-desc">{m.desc}</div>
        </div>

        <div className="acct-figures">
          <div className="role-claim-figure mono">{n}</div>
          <div className="role-claim-label">claims</div>
        </div>

        <div className="acct-controls">
          <button className="acct-expand" aria-label={open ? 'Collapse' : 'Expand'}
            onClick={(e) => { e.stopPropagation(); setOpen(o => !o); }}>
            <MIcon name="expand_more" size={22} className={`chev ${open ? 'open' : ''}`} />
          </button>
        </div>
      </div>

      {open && (
        <div className="acct-detail">
          <div className="role-detail-note">
            Permission claims granted by the <b>{r.name}</b> role. Read-only reference —
            change a person's access by assigning them this role on the Users page.
          </div>
          <div className="ua-claims">
            {r.permissions.map(p => (
              <span key={p} className={`ua-claim ${p.startsWith('users.') ? 'granted' : ''}`}>{p}</span>
            ))}
          </div>
        </div>
      )}
    </Card>
  );
};

function Roles() {
  const roleCount = UA_ROLES.length;
  const claimCount = UA_PERMISSIONS.length;

  // The permission-claims catalog is reference data, not the page's primary
  // content — so it rides in a header `info` region (toggled like Overview)
  // rather than sitting as an always-open block below the roles list. Adding
  // the region makes PageHeader self-wrap in a Card, so no manual wrapper here.
  const claimsCatalog = (
    <div className="ua-cat">
      <div className="ua-cat-note">
        Every permission claim defined in the system, grouped by category. These are reference data only —
        individual claims can't be assigned from this page; they're granted through roles.
      </div>
      <div className="ua-cat-grid">
        {UA_CATEGORIES.map(c => (
          <div key={c.cat} className="ua-cat-card">
            <div className="ua-cat-name"><MIcon name={c.icon} size={15} />{c.cat}</div>
            <div className="ua-actions-row">
              {c.actions.map(a => <span key={a} className="ua-action-chip">{a}</span>)}
            </div>
          </div>
        ))}
      </div>
    </div>
  );

  return (
    <div className="col gap-6">
      <PageHeader
        title="Roles"
        icon="badge"
        sub={`${roleCount} roles · ${claimCount} permission claims`}
        info={claimsCatalog}
        infoLabel="All claims"
        infoIcon="key"
      />

      {/* Roles & permissions reference — listed directly under the header,
          like the account list on the Accounts page (no surrounding frame). */}
      <div className="acct-list role-list">
        {UA_ROLES.map(r => <RoleRow key={r.name} r={r} />)}
      </div>
    </div>
  );
}

Object.assign(window, { Roles });
