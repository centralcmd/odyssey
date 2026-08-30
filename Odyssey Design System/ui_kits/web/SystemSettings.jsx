/* =============================================================
   System settings — admin-only page for workspace-wide configuration.
   Distinct from Preferences (per-user): these controls govern the whole Odyssey
   instance, so the page lives in the admin nav alongside Users and Roles and
   carries an "Admin only" pill.

   The catalogue itself is declared in system-settings-data.js — 67 rows across
   sixteen sections. Six control types: OdsSwitch (booleans), OdsNumberField
   (counts, windows, MB caps via `unit`), OdsNumberField with unit="%" (a stored
   0.0–1.0 fraction entered as a whole percent), OdsCapacityField (a finite
   number OR "No limit"), OdsTextInputField in the row's FOOTER well (free-text
   values whose width is set by their content), and the Data export action.

   Feature-specific UX rendered here so the states can be reviewed:
     • per-field authorization split by AXIS — count caps need
       system-settings.update, SIZE caps + the disclosure strings + sender
       identity + the mail throttle need system-settings.security.update; each
       row is disabled by the write claim it needs, never a page-level flag;
     • a PER-GROUP note when a group mixes editable + claim-locked rows;
     • a SHARED note over the four processor-disclosure rows, whose legal weight
       belongs to the set rather than to any one row;
     • an ADVISORY warning on the processor row when the name does not
       correspond to the host of the configured base URL — non-blocking by
       design, because the check can only ever be a heuristic;
     • the three FILE-ANALYSIS RUNTIME rows at the top of that section — the
       kill switch, the model and the provider base URL (issue #439). The
       switch's ON state, a non-default model and a non-default destination each
       carry their own advisory; the base URL is shape-validated as a blocking
       error (absolute https, no userinfo, query, fragment or path) and
       canonicalised so `https://host/` and `https://host` don't read as a
       change. Only the HOST is ever echoed — never the path, query or
       userinfo, because a gateway URL can carry a credential;
     • a SAVE that publishes the file-analysis runtime + disclosure onto
       OdysseyData, standing in for evicting the settings cache and invalidating
       the client's disclosure cache — so the consent gate and the Analyze
       affordance elsewhere in the kit reflect the switch without a reload;
     • TIGHTEN-ONLY rows, where the description says why the value cannot be
       raised and the field's helper line carries the resulting range;
     • tri-state dirty/validation — "No limit" rows are valid with no number,
       and toggling to No-limit and back RETAINS the entered number (not dirty);
     • a GROUP-LEVEL round-trip alert (focusable) when an export cap exceeds its
       import cap — placed at group level because the offending export row may
       be disabled (unlimited on) and so unfocusable;
     • STICKY section heads, because at sixteen sections the group label is the
       only thing saying where in the catalogue you are, and it scrolls away
       within one row;
     • a save bar that explains a disabled Save: an expandable ErrorSummary
       listing every blocking row (and jumping to it) plus a count badge for the
       unsaved changes waiting to be committed;
     • a PAGE-LEVEL alert for a wholesale 403 — claims are frozen in the auth
       cookie at sign-in, so an admin whose claim was revoked afterwards still
       renders those rows editable; nothing they typed is wrong, so this must
       not read as a row error;
     • unsaved-change dots per row + a per-row "Last changed by …" line;
     • loading gate (skeleton), load-failure retry, read-only note;
     • a polite live region. Announcements fire on a SAVE ATTEMPT only —
       validation recomputes on every keystroke, so a live region tied to it
       would interrupt a screen-reader user on each character of "1000".

   The dashed "Preview state" bar at the top is a design-review aid — it flips
   the caller's claims, the load phase and the save outcome. It is NOT part of
   the shipped page.

   Reuses kit atoms: PageHeader, SettingField, Switch, NumberField, CapacityField,
   TextInputField, ErrorSummary, Button, MIcon, SearchField, EmptyState.
   Section + state styling in admin.css; the row-level warning band, footer
   well, dirty dot and group note are DS .odc-setting-* classes.
   ============================================================= */

const CAP_TYPES = { capacity: true };
const NUM_TYPES = { number: true, size: true };
const TEXT_TYPES = { text: true };

const { Toast: DSToast, ToastStack: DSToastStack } = window.OdysseyDesignSystem_d5aa51 || {};
// Resolved at RENDER time, not at module load: this file is evaluated before the
// design-system bundle in some load orders, and a missing atom must degrade to
// one absent group rather than blanking the whole settings page.
const ssDS = (name) => (window.OdysseyDesignSystem_d5aa51 || {})[name];

const ssUtcStamp = (d) => {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getUTCFullYear()}${p(d.getUTCMonth() + 1)}${p(d.getUTCDate())}-`
       + `${p(d.getUTCHours())}${p(d.getUTCMinutes())}${p(d.getUTCSeconds())}Z`;
};

const ssDownloadExport = (name) => {
  const now = new Date();
  const payload = {
    schemaVersion: 1,
    exportedAt: now.toISOString().replace(/\.\d+Z$/, 'Z'),
    format: 'odyssey.database-export.v1',
    exclusions: { fileContentsExcluded: true, excludedTables: ['FileBlobs'], excludedFields: ['FileBlob.Content'] },
    databases: { finance: { accounts: [], budgets: [], budgetItems: [], contacts: [], currencies: [],
      exchangeRates: [], transactions: [], transactionTags: [], fileMetadata: [], accountFiles: [], transactionFiles: [] } },
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; document.body.appendChild(a); a.click();
  a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
};

// A design-review-only bar: swap the caller's claims, the load phase and the
// save outcome to inspect every state the spec calls for. Not shipped.
const SSReviewBar = ({ role, phase, outcome, onRole, onPhase, onOutcome }) => {
  const roles = [
    { id: 'full', label: 'Admin (all claims)' },
    { id: 'count', label: 'Count caps only' },
    { id: 'security', label: 'Security only' },
    { id: 'readonly', label: 'Read-only' },
  ];
  const phases = [
    { id: 'ready', label: 'Loaded' },
    { id: 'loading', label: 'Loading' },
    { id: 'error', label: 'Load error' },
  ];
  const outcomes = [
    { id: 'ok', label: 'Save OK' },
    { id: 'denied', label: 'Save 403' },
  ];
  const seg = (opts, cur, on) => (
    <div className="ss-seg" role="group">
      {opts.map(o => (
        <button key={o.id} type="button"
          className={`ss-seg-btn${cur === o.id ? ' active' : ''}`}
          aria-pressed={cur === o.id} onClick={() => on(o.id)}>{o.label}</button>
      ))}
    </div>
  );
  return (
    <div className="ss-review">
      <span className="ss-review-lbl"><MIcon name="visibility" size={15} /> Preview state</span>
      {seg(roles, role, onRole)}
      <span className="ss-seg-sep" />
      {seg(phases, phase, onPhase)}
      <span className="ss-seg-sep" />
      {seg(outcomes, outcome, onOutcome)}
    </div>
  );
};

const SSSkeletonRow = () => (
  <div className="odc-sfield ss-sk" aria-hidden="true">
    <div className="odc-sfield-frame">
      <div className="odc-sfield-ctrl"><span className="ss-sk-val" /></div>
    </div>
    <div className="odc-sfield-help"><span className="ss-sk-line" /></div>
  </div>
);

const nfmt = (n) => Number(n).toLocaleString();

/* The provider base URL's shape bound, blocking. Mirrors the server's
   StringSetting.Validator, and is deliberately as strict as the request builder:
   the provider resolves a ROOT-ABSOLUTE `/v1/messages` against this value, so a
   path would be silently discarded — accepting one would mean the value that is
   saved and the value that is used differ, with the audit trail unable to show
   it. Returns the canonical form (scheme + host, no trailing slash) so
   `https://host/` and `https://host` are one value and produce no audit line. */
const ssParseBaseUrl = (raw) => {
  const v = String(raw || '').trim();
  if (!v) return { error: 'Enter a value' };
  let u = null;
  try { u = new URL(v); } catch (e) { u = null; }
  if (!u || !u.host) return { error: 'Enter an absolute address including https:// — for example https://api.anthropic.com' };
  if (u.protocol !== 'https:') return { error: 'Only https:// is accepted — the configured API key is sent to this host' };
  if (u.username || u.password) return { error: 'Remove the username and password from the address — credentials in the URL are not accepted' };
  if (u.search || u.hash) return { error: 'Remove the query string and fragment — enter the host only' };
  if (u.pathname && u.pathname !== '/') return { error: 'Enter the host only — the provider appends /v1/messages itself' };
  return { host: u.host, canonical: `https://${u.host}` };
};
// Host only, for anything that ECHOES the value (advisory, job stamp, log).
const ssHostOf = (raw) => (ssParseBaseUrl(raw).host || null);

/* The SMTP host's shape bound, blocking. A DNS hostname or an IP literal and
   nothing else: no scheme, no port, no path, no userinfo. CR, LF and NUL are
   rejected outright — not because MailKit would compose a command from them
   (it does not), but because the value is written to log lines and audit
   entries, where a newline forges a record. Empty is legal and means the
   deployment has no mail configured; canonicalisation lowercases and strips a
   single trailing dot so `SMTP.Example.Net.` and `smtp.example.net` are one
   stored value and produce no spurious audit line. */
const ssParseSmtpHost = (raw) => {
  const v = String(raw == null ? '' : raw).trim();
  if (!v) return { empty: true, canonical: '' };
  if (/[\r\n\0]/.test(v)) return { error: 'Remove the line break — a host is a single line' };
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(v)) return { error: 'Enter the host only — no https:// or smtp:// prefix' };
  if (v.includes('@')) return { error: 'Remove the username from the address — credentials are entered below, not in the host' };
  if (v.includes('/')) return { error: 'Enter the host only — a path is not part of an SMTP address' };
  if (/:\d+$/.test(v)) return { error: 'Enter the host only — the port is its own setting below' };
  if (v.length > 255) return { error: 'Must be 255 characters or fewer' };
  const c = v.toLowerCase().replace(/\.$/, '');
  const ipv6 = /^\[[0-9a-f:]+\]$/.test(c);
  if (!ipv6) {
    if (c.split('.').some(l => l.length > 63)) return { error: 'Each label of the host must be 63 characters or fewer' };
    if (!/^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$/.test(c)) {
      return { error: 'Enter a hostname or IP address — letters, digits, hyphens and dots only' };
    }
  }
  return { host: c, canonical: c };
};

/* The public link origin, blocking. https is required EXCEPT for loopback,
   where http keeps the dev and Aspire stacks working without an env var — a
   loopback link resolves on the recipient's own machine, so the exemption
   cannot be used to intercept anything. A path is allowed (a deployment may be
   hosted under a subpath) and normalised without its trailing slash, because
   links are composed as {base}/{clientPath}. */
const SS_LOOPBACK = /^(localhost|127(\.\d+){3}|\[?::1\]?)$/i;
const ssParseClientBaseUrl = (raw) => {
  const v = String(raw == null ? '' : raw).trim();
  if (!v) return { empty: true, canonical: '' };
  let u = null;
  try { u = new URL(v); } catch (e) { u = null; }
  if (!u || !u.host) return { error: 'Enter an absolute address including https:// — for example https://odyssey.example.net' };
  const loopback = SS_LOOPBACK.test(u.hostname);
  if (u.protocol !== 'https:' && !(u.protocol === 'http:' && loopback)) {
    return { error: 'Only https:// is accepted — every password-reset link is composed against this address' };
  }
  if (u.username || u.password) return { error: 'Remove the username and password from the address' };
  if (u.search || u.hash) return { error: 'Remove the query string and fragment — enter the origin only' };
  const path = u.pathname.replace(/\/+$/, '');
  return { host: u.host, origin: u.origin, canonical: `${u.protocol}//${u.host}${path}` };
};

// Which stored secrets a host change or a STARTTLS switch-off clears.
const SS_MAIL_SECRETS = ['secretEmailUsername', 'secretEmailPassword'];


function SystemSettings() {
  const { useState, useMemo, useRef, useEffect } = React;

  // ---- Review controls (design aid) ----
  const [role, setRole] = useState('full');
  const [phase, setPhase] = useState('ready');
  const [outcome, setOutcome] = useState('ok');

  // ---- Page state ----
  const [q, setQ] = useState('');
  const [vals, setVals] = useState(SS_SAVED);
  const [snapshot, setSnapshot] = useState(SS_SAVED);
  const [saving, setSaving] = useState(false);
  const [justSaved, setJustSaved] = useState(false);
  const [announce, setAnnounce] = useState('');
  const [denied, setDenied] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [exportName, setExportName] = useState(null);
  const [saveGate, setSaveGate] = useState(null);
  const [cleared, setCleared] = useState(null);
  const confirmOpener = useRef(null);
  const alertRefs = useRef({});

  const publishLimits = (v) => {
    window.__odysseyImportLimits = {
      contacts: v.contactVCardMaxImportMegabytes,
      calendar: v.calendarIcsMaxImportMegabytes,
      tasks: v.taskIcsMaxImportMegabytes,
      journal: v.journalIcsMaxImportMegabytes,
      upload: v.fileStorageMaxUploadMegabytes,
    };
  };
  useEffect(() => { publishLimits(snapshot); publishAnalysis(snapshot); }, []);

  // The file-analysis runtime, published for the rest of the kit. Stands in for
  // two server effects a save has: evicting FileAnalysisSettingsLookup's cached
  // snapshot, and invalidating the client's disclosure cache — so the consent
  // gate and the Analyze affordance pick the change up without a reload. The
  // kill switch is published as its own live value, never folded into a cached
  // snapshot, mirroring IsEnabledAsync.
  const publishAnalysis = (v) => {
    const D = window.OdysseyData;
    if (!D) return;
    const parsed = ssParseBaseUrl(v.fileAnalysisBaseUrl);
    D.fileAnalysisRuntime = {
      enabled: !!v.fileAnalysisEnabled,
      // A value that cannot be used is published as null, never substituted with
      // the shipped default: analysis refuses rather than transferring to a
      // processor neither the administrator nor the user chose.
      model: (v.fileAnalysisModel || '').trim() || null,
      baseUrl: parsed.canonical || null,
    };
    D.analysisTransfer = Object.assign({}, D.analysisTransfer, {
      processor: v.aiProcessor,
      processorRegion: v.aiProcessorRegion,
      lawfulBasis: v.aiLawfulBasis,
      privacyNoticeUrl: v.aiPrivacyNoticeUrl,
    });
  };

  // Claim possession — computed once from the loaded auth state, NOT from any
  // control's momentary Disabled flag.
  const hasSecurity = role === 'full' || role === 'security';
  const hasCount = role === 'full' || role === 'count';
  const canSave = hasSecurity || hasCount;
  const holds = (claim) => claim === 'security' ? hasSecurity : claim === 'count' ? hasCount : true;

  // ---- Draft mutations ----
  const touch = () => { setJustSaved(false); setDenied(false); };
  const setScalar = (key, v) => { setVals(s => ({ ...s, [key]: v })); touch(); };
  const setCapValue = (key, v) => { setVals(s => ({ ...s, [key]: { ...s[key], value: v } })); touch(); };
  const setCapUnlimited = (key, on) => { setVals(s => ({ ...s, [key]: { ...s[key], unlimited: on } })); touch(); };

  // The effective bounds for a row. A single-direction row is bounded by the
  // shipped default on the side it may not move: a tighten-only row by its
  // ceiling, a raise-only row by its floor — not by the range the API
  // advertises. Both come from the server, so the control cannot offer a value
  // the API would refuse.
  const floorOf = (row) => (row.floor != null ? row.floor : row.min);
  const capOf = (row) => row.ceiling != null ? row.ceiling : row.max;

  // ---- Validation. Only meaningful while the row is editable — a
  //      permission-disabled row sends null and is never client-validated. ----
  const errorFor = (row) => {
    if (!holds(row.claim)) return null;
    if (CAP_TYPES[row.type]) {
      const c = vals[row.key];
      if (c.unlimited) return null;
      if (c.value == null || c.value === '') return 'Enter a value';
      if (c.value < row.min || c.value > row.max) return `Must be between ${nfmt(row.min)} and ${nfmt(row.max)}`;
      return null;
    }
    if (NUM_TYPES[row.type]) {
      const v = vals[row.key];
      const hi = capOf(row), lo = floorOf(row);
      if (v == null || v === '') return 'Enter a value';
      if (row.floor != null && v < lo) return `Can only be raised — ${nfmt(lo)} is the lowest this may be set to`;
      if (row.ceiling != null && v > hi) return `Can only be lowered — ${nfmt(hi)} is the highest this may be set to`;
      if (v < lo || v > hi) return `Must be between ${nfmt(lo)} and ${nfmt(hi)}`;
      return null;
    }
    if (TEXT_TYPES[row.type]) {
      const v = (vals[row.key] || '').trim();
      // An `allowEmpty` row means something when it is empty — "mail is not
      // configured" — so empty short-circuits before the per-row rule, exactly as
      // the server's StringSetting.AllowEmpty does. Blocking it would make
      // configuring mail a one-way door.
      if (!v) return row.allowEmpty ? null : 'Enter a value';
      if (row.checkSmtpHost) {
        const parsed = ssParseSmtpHost(v);
        if (parsed.error) return parsed.error;
      }
      if (row.checkClientBaseUrl) {
        const parsed = ssParseClientBaseUrl(v);
        if (parsed.error) return parsed.error;
      }
      if (row.checkBaseUrl) {
        const parsed = ssParseBaseUrl(v);
        if (parsed.error) return parsed.error;
      }
      if (row.key === 'aiPrivacyNoticeUrl' && !/^https:\/\/\S+\.\S+/.test(v)) return 'Enter a full https:// URL';
      if (row.key === 'emailFromAddress' && !/^[^\s@,<>]+@[^\s@,<>]+\.[^\s@,<>]+$/.test(v)) return 'Enter one plain mailbox address';
      if (row.maxLength && v.length > row.maxLength) return `Must be ${nfmt(row.maxLength)} characters or fewer`;
      return null;
    }
    if (row.type === 'percent') {
      const v = vals[row.key];
      if (v == null || v === '') return 'Enter a value';
      if (v < 0 || v > 1) return 'Must be between 0% and 100%';
      return null;
    }
    return null;
  };

  // Non-blocking advisories. Four kinds, one channel:
  //   • a cost advisory when a resource-shaped cap is raised above its shipped
  //     default — the value is legal, it just is not free, and the row is the
  //     only place that can say so before it is discovered in production;
  //   • the kill switch's ON state, which says what turning it on lets happen —
  //     naming the processor and region documents are transferred to, and that a
  //     per-document consent is still required for each one;
  //   • a value moved off its shipped default (the model, the destination),
  //     which says what the change does and does not affect: records already
  //     written keep what they ran under, and the destination advisory states
  //     plainly that the configured API key goes to the host that is set;
  //   • the processor-host heuristic, which the server can only make loosely:
  //     a strict match would reject legitimate gateway deployments, a loose one
  //     would pass a lookalike domain.
  // None blocks Save; none marks the row invalid. Only the HOST of the base URL
  // is ever echoed — a gateway URL such as https://key:secret@gateway.internal
  // is the expected shape here, so the host is parsed once at this boundary and
  // the rest is unreachable from the text.
  // The clearing triggers (G4, G7), computed from the SNAPSHOT rather than from
  // a row's dirty flag, because only one direction of each clears: a host that
  // moved to a different non-empty value, and STARTTLS moving true → false.
  // false → false and false → true clear nothing.
  const hostClearing = () => {
    const next = ssParseSmtpHost(vals.emailSmtpHost).canonical;
    const prev = ssParseSmtpHost(snapshot.emailSmtpHost).canonical;
    return !!next && next !== prev;
  };
  const startTlsClearing = () => !!snapshot.emailUseStartTls && !vals.emailUseStartTls;
  // Host first: it is the change the administrator made deliberately, and its
  // wording names the destination the credential would otherwise reach.
  const clearTrigger = () => (hostClearing() ? 'host' : startTlsClearing() ? 'starttls' : null);

  const advisoryFor = (row) => {
    // Stated on the row before Save is pressed, so the consequence is visible
    // while the value is being edited and not only in the dialog that gates the
    // save. Same channel as every other advisory: non-blocking, never an error.
    if (row.clearsCredential === 'host' && hostClearing()) {
      return `Saving this clears the stored SMTP username and password, so a credential entered for ${ssParseSmtpHost(snapshot.emailSmtpHost).canonical || 'the previous relay'} is never presented to ${ssParseSmtpHost(vals.emailSmtpHost).canonical}. Re-enter them below afterwards.`;
    }
    if (row.clearsCredential === 'starttls' && startTlsClearing()) {
      return 'Saving this sends the credential and every link over an unencrypted connection unless the relay uses implicit TLS on its port. The stored SMTP username and password are cleared with the change, so an existing credential is never put on the wire in clear.';
    }
    // Computed in the BROWSER, against the origin you are actually on. The
    // server has no view of the caller's origin on the read path, and an
    // advisory composed there would re-fire on every page load rather than on
    // the value that differs. A hint only: an operator may legitimately set a
    // public URL from an internal hostname, or set it ahead of a DNS cutover.
    if (row.checkClientBaseUrl) {
      const parsed = ssParseClientBaseUrl(vals[row.key]);
      if (!parsed.origin) return null;
      const here = typeof window !== 'undefined' && window.location ? window.location.origin : null;
      if (!here || here === parsed.origin || here.startsWith('about:') || here === 'null') return null;
      return `This differs from the address you are using now (${here}). Confirmation and password-reset links will point at ${parsed.origin} — correct if that is the public address, worth a second look if it is not.`;
    }
    if (row.advise) {
      const v = vals[row.key];
      if (typeof v === 'number' && v > row.advise.above) return row.advise.cost;
    }
    if (row.adviseWhenOn) {
      if (!vals[row.key]) return null;
      return `Documents will be transferred to ${vals.aiProcessor} in ${vals.aiProcessorRegion} on each analysis. Each transfer still requires the user’s per-document consent.`;
    }
    if (row.adviseOffDefault) {
      const v = (vals[row.key] || '').trim();
      if (!v || v === row.adviseOffDefault) return null;
      return `Analyses already recorded keep the model they ran under; only future analyses use this. Per-document cost and extraction quality vary by model. The shipped default is “${row.adviseOffDefault}”.`;
    }
    if (row.checkBaseUrl) {
      const host = ssHostOf(vals[row.key]);
      if (!host || host === ssHostOf(SS_DEFAULT_BASE_URL)) return null;
      return `Analysis requests, including the configured API key, are sent to ${host}. Confirm you control this host and that the disclosed processor and region below still describe it.`;
    }
    if (!row.checkHost) return null;
    const v = (vals[row.key] || '').trim();
    if (!v) return null;
    // The destination comes from the SETTING now, not from configuration.
    const dest = ssHostOf(vals.fileAnalysisBaseUrl);
    if (!dest) return null;
    const first = v.toLowerCase().split(/[\s,.]+/)[0];
    if (first && dest.toLowerCase().includes(first)) return null;
    return `Requests still go to ${dest}, which doesn’t look like “${v}”. Odyssey can’t tell a gateway deployment from a mistake, so this won’t stop you saving — but the consent gate will name ${v} to every user.`;
  };

  const effCount = (key) => { const c = vals[key]; return c.unlimited ? Infinity : c.value; };

  const roundTripError = (group) => {
    const rt = group.roundTrip;
    if (!rt) return null;
    const exp = effCount(rt.exportKey), imp = effCount(rt.importKey);
    if (exp == null || imp == null || Number.isNaN(exp) || Number.isNaN(imp)) return null;
    if (exp > imp) {
      const expLbl = vals[rt.exportKey].unlimited ? 'no limit' : nfmt(vals[rt.exportKey].value);
      const impLbl = vals[rt.importKey].unlimited ? 'no limit' : nfmt(vals[rt.importKey].value);
      return `Export limit (${expLbl}) must not exceed the import limit (${impLbl}), or an exported file could not be imported back.`;
    }
    return null;
  };

  const hasFieldErrors = SS_GROUPS.some(g => g.rows.some(r => errorFor(r)));
  const hasRoundTrip = SS_GROUPS.some(g => roundTripError(g));
  const hasErrors = hasFieldErrors || hasRoundTrip;

  const dirtyRow = (row) => {
    // Secrets are saved per row on their own request, never with the page's
    // Save — so a secret is never dirty, never counted, and never blocks Save.
    if (row.type === 'secret' || row.type === 'export') return false;
    if (CAP_TYPES[row.type]) {
      const a = vals[row.key], b = snapshot[row.key];
      return a.unlimited !== b.unlimited || (!a.unlimited && a.value !== b.value);
    }
    // A canonicalised value is compared canonically: https://host/ and
    // https://host are one stored value, so neither reads as a change and
    // neither produces a spurious audit line.
    if (row.checkBaseUrl) {
      const a = ssParseBaseUrl(vals[row.key]), b = ssParseBaseUrl(snapshot[row.key]);
      if (a.canonical && b.canonical) return a.canonical !== b.canonical;
    }
    if (row.checkSmtpHost || row.checkClientBaseUrl) {
      const p = row.checkSmtpHost ? ssParseSmtpHost : ssParseClientBaseUrl;
      const a = p(vals[row.key]), b = p(snapshot[row.key]);
      if (a.canonical != null && b.canonical != null) return a.canonical !== b.canonical;
    }
    return vals[row.key] !== snapshot[row.key];
  };

  const groupPartial = (group) => {
    if (!canSave) return false;
    const editable = group.rows.some(r => r.claim && holds(r.claim));
    const locked = group.rows.some(r => r.claim && !holds(r.claim));
    return editable && locked;
  };

  const matches = (row, group) => {
    const t = q.trim().toLowerCase();
    if (!t) return true;
    return row.title.toLowerCase().includes(t)
        || row.desc.toLowerCase().includes(t)
        || group.toLowerCase().includes(t);
  };

  const visibleGroups = useMemo(() => SS_GROUPS
    .map(g => ({ ...g, rows: g.rows.filter(r => matches(r, g.group)) }))
    .filter(g => g.rows.length > 0), [q]);

  // Only rows that are actually RENDERED get an entry — a search-filtered row
  // has no focus target, so listing it would be a dead end.
  const problems = useMemo(() => {
    const out = [];
    visibleGroups.forEach(g => {
      const rt = roundTripError(g);
      if (rt) out.push({ label: 'Export limit exceeds the import limit', section: g.group, targetId: `ss-rt-${g.group}` });
      g.rows.forEach(r => {
        const err = errorFor(r);
        if (err) out.push({ label: `${r.title} — ${err.charAt(0).toLowerCase()}${err.slice(1)}`, section: g.group, targetId: `ss-in-${r.key}` });
      });
    });
    return out;
  }, [visibleGroups, vals, role]);

  const dirtyCount = SS_GROUPS.reduce((n, g) => n + g.rows.filter(r => dirtyRow(r)).length, 0);

  // Jump to a blocking field the way every other page's rollup does: scroll its
  // block into the nearest scroller, move focus, flash a one-shot ring.
  const jumpTo = (p) => {
    const el = document.getElementById(p.targetId);
    if (!el) {
      // The row exists in the catalogue but isn't rendered — say so rather than
      // silently doing nothing.
      setAnnounce('That setting is hidden by the current search. Clear the search to reach it.');
      return;
    }
    const block = el.closest('.odc-sfield') || el;
    let scroller = block.parentElement;
    while (scroller && scroller !== document.body) {
      const oy = getComputedStyle(scroller).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && scroller.scrollHeight > scroller.clientHeight) break;
      scroller = scroller.parentElement;
    }
    requestAnimationFrame(() => {
      const r = block.getBoundingClientRect();
      if (scroller && scroller !== document.body) {
        scroller.scrollTo({ top: scroller.scrollTop + (r.top - scroller.getBoundingClientRect().top) - 24, behavior: 'smooth' });
      } else {
        window.scrollTo({ top: window.scrollY + r.top - 96, behavior: 'smooth' });
      }
      el.focus({ preventScroll: true });
      block.classList.remove('ss-flash');
      requestAnimationFrame(() => block.classList.add('ss-flash'));
      setTimeout(() => block.classList.remove('ss-flash'), 2200);
    });
  };

  const runExport = () => {
    if (exporting) return;
    setExporting(true);
    const name = `odyssey-database-export-${ssUtcStamp(new Date())}.json`;
    setTimeout(() => { setExporting(false); ssDownloadExport(name); setExportName(name); }, 1700);
  };

  const save = (confirmed) => {
    if (!canSave || saving || phase !== 'ready') return;
    // Announce on the ATTEMPT, never from validation itself.
    if (hasErrors) {
      setAnnounce(`${problems.length} setting${problems.length === 1 ? '' : 's'} need${problems.length === 1 ? 's' : ''} fixing before this can be saved.`);
      return;
    }
    // The gate on the page's SINGLE batch save. There is no per-field save to
    // hang it on, so Confirm submits the whole batch exactly as an unguarded
    // Save would, and Cancel submits nothing and discards nothing.
    const trigger = confirmed === true ? null : clearTrigger();
    if (trigger) {
      confirmOpener.current = document.activeElement;
      setSaveGate({
        reason: trigger,
        from: ssParseSmtpHost(snapshot.emailSmtpHost).canonical || null,
        to: ssParseSmtpHost(vals.emailSmtpHost).canonical || null,
      });
      setAnnounce(trigger === 'host'
        ? 'Confirm required. Saving clears the stored SMTP username and password because the SMTP host changed.'
        : 'Confirm required. Saving clears the stored SMTP username and password because STARTTLS is being turned off.');
      return;
    }
    const clearing2 = clearTrigger();
    setSaving(true);
    setTimeout(() => {
      setSaving(false);
      if (outcome === 'denied') {
        setDenied(true);
        setAnnounce('Your permission to change these settings has been withdrawn. Nothing was saved.');
        return;
      }
      // Canonicalise on commit, exactly as the server does before it persists.
      const committed = { ...vals };
      const parsed = ssParseBaseUrl(committed.fileAnalysisBaseUrl);
      if (parsed.canonical) committed.fileAnalysisBaseUrl = parsed.canonical;
      const host = ssParseSmtpHost(committed.emailSmtpHost);
      if (host.canonical != null) committed.emailSmtpHost = host.canonical;
      const base = ssParseClientBaseUrl(committed.emailClientBaseUrl);
      if (base.canonical != null) committed.emailClientBaseUrl = base.canonical;
      setVals(committed);
      setSnapshot(committed);
      publishLimits(committed);
      publishAnalysis(committed);
      // The credential clear commits in the SAME transaction as the settings
      // write — so it is applied here, on the same success, and never as a
      // second request that could land on its own.
      if (clearing2) {
        setSecrets(s => {
          const n = { ...s };
          SS_MAIL_SECRETS.forEach(k => { n[k] = { state: 'not-set', meta: null }; });
          return n;
        });
        setCleared(clearing2);
        setAnnounce('System settings saved. The stored SMTP username and password were cleared and must be re-entered in Email.');
      } else {
        setAnnounce('System settings saved.');
      }
      setJustSaved(true);
      setTimeout(() => setJustSaved(false), 2200);
    }, 900);
  };

  // Closing the dialog by any route returns focus to the control that opened it
  // — neither Modal nor the page's other dialogs restore it on their own.
  const closeSaveGate = (proceed) => {
    setSaveGate(null);
    const opener = confirmOpener.current;
    if (opener && opener.focus) setTimeout(() => opener.focus(), 0);
    if (proceed) save(true);
    else setAnnounce('Nothing was saved. Your changes are still on the page.');
  };

  const retry = () => {
    setPhase('loading');
    setTimeout(() => { setPhase('ready'); setAnnounce('Settings loaded.'); }, 1100);
  };

  // ---- Controls ----
  // Inside a SettingField frame the control is just its value: the frame owns the
  // label, the helper line owns the description, the range and the stamp, so
  // nothing is passed for label or help here.
  const controlFor = (row) => {
    const editable = holds(row.claim) && phase === 'ready';
    const err = errorFor(row);
    if (CAP_TYPES[row.type]) {
      const c = vals[row.key];
      return (
        <CapacityField variant="inline"
          value={c.value} unlimited={c.unlimited}
          onValueChange={(v) => setCapValue(row.key, v)}
          onUnlimitedChange={(on) => setCapUnlimited(row.key, on)}
          label={row.title} min={row.min} max={row.max}
          disabled={!editable} error={err || undefined}
          ariaLabelledBy={`ss-ttl-${row.key}`} ariaDescribedBy={`ss-in-${row.key}-help`} />
      );
    }
    if (TEXT_TYPES[row.type]) {
      const urlish = row.key === 'aiPrivacyNoticeUrl' || row.checkBaseUrl || row.checkClientBaseUrl;
      return (
        <TextInputField id={`ss-in-${row.key}`} value={vals[row.key] || ''}
          placeholder={row.placeholder || (row.checkBaseUrl ? 'https://api.anthropic.com' : row.key === 'aiPrivacyNoticeUrl' ? 'https://…' : undefined)}
          inputMode={urlish ? 'url' : row.key === 'emailFromAddress' ? 'email' : row.checkSmtpHost ? 'url' : 'text'}
          maxLength={row.maxLength} disabled={!editable} error={err ? ' ' : undefined}
          onChange={(v) => setScalar(row.key, v)} />
      );
    }
    // A stored 0.0–1.0 fraction, entered as a whole percent. The unit sits
    // inside the input's trailing edge so it survives an error message.
    if (row.type === 'percent') {
      const stored = vals[row.key];
      return (
        <NumberField id={`ss-in-${row.key}`}
          value={stored == null ? null : Math.round(stored * 100)} disabled={!editable}
          min={0} max={100} step={1} align="right" unit="%" error={err ? ' ' : undefined}
          onChange={(v) => setScalar(row.key, v == null ? null : v / 100)} />
      );
    }
    return (
      <NumberField id={`ss-in-${row.key}`} value={vals[row.key]} disabled={!editable}
        min={floorOf(row)} max={capOf(row)} step={1} align="right"
        unit={row.type === 'size' ? 'MB' : row.unit} error={err ? ' ' : undefined}
        onChange={(v) => setScalar(row.key, v)} />
    );
  };

  // One always-visible helper line: what the setting does, the obligation or
  // range that qualifies it, then who last changed it. Nothing behind a
  // disclosure — at a full page of settings a "?" per row is a page of buttons
  // nobody presses, and provenance is what an admin actually scans for.
  const helpFor = (row) => {
    const parts = [row.desc];
    if (row.extra) parts.push(row.extra);
    if (row.ceiling != null) parts.push(`Can be lowered but not raised: ${nfmt(row.min)}–${nfmt(row.ceiling)}.`);
    if (row.floor != null) parts.push(`Can be raised but not lowered: ${nfmt(row.floor)}–${nfmt(row.max)}.`);
    if (!holds(row.claim)) parts.push('Changing this needs an additional permission.');
    return parts.join(' ');
  };
  const metaFor = (row) => (row.meta
    ? `Last changed by ${row.meta.by}, ${row.meta.on}.`
    : 'Never changed — default value.');

  // A switch or an action has no frame to notch a label into: it reads as one
  // tile, label and helper left, control right, spanning the grid.
  const renderTile = (row) => {
    const editable = holds(row.claim) && phase === 'ready';
    const adv = holds(row.claim) ? advisoryFor(row) : null;
    return (
      <div key={row.key} className={`odc-sfield wide${holds(row.claim) ? '' : ' locked'}`} data-ss-row={row.key}>
        <div className="odc-sfield-tile">
          <div className="odc-sfield-tile-main">
            <span className="odc-sfield-label" id={`ss-ttl-${row.key}`}>{row.title}</span>
            <div className="odc-sfield-help">
              <span>{helpFor(row)} </span>
              <span className="odc-sfield-stamp">{metaFor(row)}</span>
              {dirtyRow(row) ? <span className="odc-setting-dot" title="Unsaved change" aria-hidden="true" /> : null}
            </div>
          </div>
          {row.type === 'export'
            ? <Button variant="filled" icon="file_download" loading={exporting} disabled={!editable} onClick={runExport}>Export database JSON</Button>
            : <Switch checked={!!vals[row.key]} disabled={!editable}
                aria-labelledby={`ss-ttl-${row.key}`} onChange={(c) => setScalar(row.key, c)} />}
        </div>
        {adv ? (
          <div className="odc-sfield-advisory" role="status">
            <span className="material-icons" aria-hidden="true">info</span>
            <div><b className="odc-sfield-advisory-t">Advisory</b> {adv}</div>
          </div>
        ) : null}
      </div>
    );
  };

  // ---- Secrets ----
  // Write-only rows, on their own lifecycle: entered, replaced or cleared one
  // request at a time, so nothing here touches `vals`, the dirty count or Save.
  // The page keeps only the store's read result per key and the stamp, because
  // that is all the API ever returns — the value itself is never readable.
  // Secrets live in their SUBJECT cards, not in a Credentials group of their
  // own: the API key beside the destination it is sent to and the switch that
  // decides whether anything is sent, the relay password beside the from address
  // it authenticates. `type: 'secret'` is what marks them, so they are found
  // wherever they sit.
  // Which card a secret now lives in. The rollup has to say it: the rows are
  // scattered across the page by subject, so "SMTP password" alone does not tell
  // a reader where to look, and the jump target may be below the fold.
  const secretGroupOf = (key) => {
    const g = SS_GROUPS.find(gr => gr.rows.some(r => r.key === key));
    return g ? `In ${g.group}.` : '';
  };

  const secretRows = useMemo(
    () => SS_GROUPS.flatMap(g => g.rows).filter(r => r.type === 'secret'), []);
  const [secrets, setSecrets] = useState(() => {
    const m = {};
    secretRows.forEach(r => { m[r.key] = { state: r.state, meta: r.meta }; });
    return m;
  });
  const [clearing, setClearing] = useState(null);

  // A secret commits on its own request, so it announces its own outcome — the
  // page's Save never covers it.
  const saveSecret = (row, value) => {
    setSecrets(s => ({ ...s, [row.key]: { state: 'found', meta: { by: 'You', on: 'just now' } } }));
    setAnnounce(`${row.title} stored.`);
  };
  const confirmClear = () => {
    const row = clearing;
    setClearing(null);
    setSecrets(s => ({ ...s, [row.key]: { state: 'not-set', meta: null } }));
    setAnnounce(`${row.title} cleared. The row is now not set.`);
  };

  // Unreadable is an outage, not a settings problem: the value is present and
  // the consumer is failing closed right now. It surfaces as the page-header
  // SIGNAL rollup — the same gesture Accounts, Insurance and Tax statements use
  // for a data condition needing attention — and deliberately not in the
  // `problems` list, which exists to explain a disabled Save. Neither the cause
  // nor the fix here is a Save, so merging them would make Save look blocked by
  // something Save cannot fix.
  const unreadable = secretRows.filter(r => secrets[r.key] && secrets[r.key].state === 'unreadable');
  // Mail with no host is the other page-level condition worth surfacing here: it
  // is not a fault and not a blocked Save, it is an INCOMPLETE deployment — so
  // it joins the same rollup at `information` severity rather than growing a
  // second mechanism. It is only rendered for someone holding the claim that
  // can fix it, which the rollup's own gate already gives us.
  const mailUnconfigured = hasSecurity && !ssParseSmtpHost(snapshot.emailSmtpHost).canonical;
  const signalCount = unreadable.length + (mailUnconfigured ? 1 : 0);
  const pageSignal = signalCount ? {
    severity: unreadable.length ? 'error' : 'info',
    count: signalCount,
    label: unreadable.length ? 'Credentials' : 'Email',
    defaultOpen: true,
    region: (
      <div className="signal-panel">
        {unreadable.map(r => (
          <div key={r.key} className="alert error compact signal-row"
            role="button" tabIndex={0}
            onClick={() => jumpTo({ targetId: `ss-in-${r.key}` })}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo({ targetId: `ss-in-${r.key}` }); } }}>
            <SeverityIcon severity="error" size={18} className="alert-icon" />
            <div className="alert-body">
              <strong>{r.title} cannot be decrypted.</strong> {r.affects} The value is stored but this
              instance’s encryption key ring cannot open it, and nothing falls back to a configured
              value. Clear the row and enter the credential again.
              <span className="signal-where"> {secretGroupOf(r.key)}</span>
            </div>
            <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo({ targetId: `ss-in-${r.key}` }); }}>Fix →</button>
          </div>
        ))}
        {mailUnconfigured && (
          <div className="alert info compact signal-row"
            role="button" tabIndex={0}
            onClick={() => jumpTo({ targetId: 'ss-in-emailSmtpHost' })}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo({ targetId: 'ss-in-emailSmtpHost' }); } }}>
            <SeverityIcon severity="info" size={18} className="alert-icon" />
            <div className="alert-body">
              <strong>Transactional mail is not configured.</strong> Confirmation and password-reset
              messages are logged and skipped, so no account can be confirmed or recovered until an
              SMTP host is set.
              <span className="signal-where"> In Email.</span>
            </div>
            <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo({ targetId: 'ss-in-emailSmtpHost' }); }}>Fix →</button>
          </div>
        )}
      </div>
    ),
  } : null;

  const renderSecret = (row) => {
    const Field = ssDS('SecretSettingField');
    if (!Field) return null;
    const cur = secrets[row.key] || { state: 'not-set', meta: null };
    return (
      <Field key={row.key} label={row.title} secretKey={row.secretKey}
        id={`ss-in-${row.key}`} tabIndex={-1}
        kind={row.kind} state={cur.state}
        help={helpFor(row)}
        meta={cur.meta ? `Set by ${cur.meta.by}, ${cur.meta.on}.` : 'Never set.'}
        consequence={cur.state === 'not-set' ? row.consequence : undefined}
        affects={row.affects}
        allowNonAscii={row.allowNonAscii}
        locked={!holds(row.claim) || phase !== 'ready'}
        onSave={(v) => saveSecret(row, v)}
        onClear={() => setClearing(row)}
        data-ss-row={row.key} />
    );
  };

  const renderField = (row) => {
    if (row.type === 'secret') return renderSecret(row);
    if (row.type === 'switch' || row.type === 'export') return renderTile(row);
    const wide = TEXT_TYPES[row.type];
    return (
      <SettingField key={row.key} label={row.title} htmlFor={`ss-in-${row.key}`}
        labelId={`ss-ttl-${row.key}`} wide={wide}
        help={helpFor(row)} meta={metaFor(row)}
        error={errorFor(row) || undefined} dirty={dirtyRow(row)}
        advisory={holds(row.claim) ? advisoryFor(row) || undefined : undefined}
        bound={row.ceiling != null ? 'lower-only' : row.floor != null ? 'raise-only' : undefined}
        className={holds(row.claim) ? undefined : 'locked'}
        data-ss-row={row.key}>
        {controlFor(row)}
      </SettingField>
    );
  };

  const savePrimary = canSave ? (
    <div className="ss-savebar">
      {phase === 'ready' && problems.length > 0
        ? <ErrorSummary problems={problems} onJump={jumpTo} />
        : null}
      <Button variant="filled" icon={justSaved ? 'check' : 'save'} loading={saving}
        badge={dirtyCount || undefined} badgeLabel="unsaved changes"
        disabled={phase !== 'ready' || hasErrors} onClick={() => save()}>
        {justSaved ? 'Saved' : 'Save changes'}
      </Button>
    </div>
  ) : null;

  return (
    <div className="col gap-6">
      <SSReviewBar role={role} phase={phase} outcome={outcome}
        onRole={setRole} onPhase={setPhase} onOutcome={setOutcome} />

      <PageHeader
        title="System settings"
        icon="settings"
        sub="Instance-wide configuration for this Odyssey deployment"
        signal={phase === 'ready' ? pageSignal : null}
        searchDefaultOpen
        search={(
          <div className="row gap-3" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search settings…" value={q} onChange={setQ} />
            </div>
          </div>
        )}
        primary={savePrimary}
      />

      {denied && phase === 'ready' && (
        <div className="ss-authalert" role="alert" tabIndex={-1}>
          <span className="material-icons" aria-hidden="true">gpp_bad</span>
          <div>
            <div className="ss-authalert-ttl">Your permission to change these settings has been withdrawn</div>
            <div className="ss-authalert-desc">
              Nothing was saved. Permissions are read when you sign in, so this page still shows the
              controls you had then. Sign out and back in to see what you can change now — your edits
              on this page will be lost.
            </div>
            <div className="ss-authalert-act">
              <Button variant="outlined" icon="logout" onClick={() => setDenied(false)}>Sign out</Button>
            </div>
          </div>
        </div>
      )}

      {!canSave && phase === 'ready' && (
        <div className="ss-note" role="note">
          <span className="material-icons" aria-hidden="true">lock</span>
          <div>
            <div className="ss-note-ttl">You have read-only access to system settings</div>
            <div className="ss-note-desc">These values are visible to you but can only be changed by an administrator with the matching update permission.</div>
          </div>
        </div>
      )}

      {phase === 'loading' && (
        <div className="ss-sect-stack" aria-busy="true" aria-label="Loading settings">
          {SS_GROUPS.map(g => (
            <section key={g.group} className="ss-sect">
              <header className="ss-sect-h">
                <span className="ss-sect-ic"><MIcon name={g.icon} size={17} /></span>
                <span className="ss-sect-t" style={{ opacity: 0.6 }}>{g.group}</span>
              </header>
              <div className="ss-sect-body">
                <div className="odc-sfield-grid">
                  {g.rows.map((r, i) => <SSSkeletonRow key={i} />)}
                </div>
              </div>
            </section>
          ))}
        </div>
      )}

      {phase === 'error' && (
        <div className="ss-retry" role="alert">
          <span className="material-icons" aria-hidden="true">cloud_off</span>
          <div className="ss-retry-ttl">Couldn’t load settings</div>
          <div className="ss-retry-desc">Something went wrong reading the system settings. Your changes weren’t affected.</div>
          <Button variant="outlined" icon="refresh" onClick={retry}>Retry</Button>
        </div>
      )}

      {phase === 'ready' && (
        visibleGroups.length > 0 ? (
          <div className="ss-sect-stack">
            {visibleGroups.map(g => {
              const rtErr = roundTripError(g);
              const dirtyHere = g.rows.filter(r => dirtyRow(r)).length;
              return (
                <section key={g.group} className="ss-sect" aria-labelledby={`ss-grp-${g.group}`}>
                  <header className="ss-sect-h">
                    <span className="ss-sect-ic"><MIcon name={g.icon} size={17} /></span>
                    <h2 className="ss-sect-t" id={`ss-grp-${g.group}`}>{g.group}</h2>
                    {dirtyHere > 0
                      ? <span className="ss-sect-badge">{dirtyHere} unsaved</span>
                      : <span className="ss-sect-count">{g.rows.length} settings</span>}
                  </header>
                  {groupPartial(g) && (
                    <div className="ss-sect-band" role="note">
                      <span className="material-icons" aria-hidden="true">info</span>
                      <div>Some settings in this group require additional permissions.</div>
                    </div>
                  )}
                  {rtErr && (
                    <div className="ss-sect-band error" id={`ss-rt-${g.group}`} role="alert" tabIndex={-1}
                      ref={(el) => { alertRefs.current[g.group] = el; }}>
                      <span className="material-icons" aria-hidden="true">error_outline</span>
                      <div>{rtErr}</div>
                    </div>
                  )}
                  <div className="ss-sect-body">
                    <div className="odc-sfield-grid">
                      {g.rows.map(r => renderField(r))}
                    </div>
                  </div>
                </section>
              );
            })}
          </div>
        ) : (
          <EmptyState icon="search_off" mutedIcon title="No settings match"
            desc={`Nothing here matches "${q.trim()}". Clear the search to see every setting.`} />
        )
      )}

      {/* polite live region — save attempts, save success, load failure */}
      <div className="odc-sr-only" role="status" aria-live="polite">{announce}</div>

      {clearing && ssDS('SecretClearDialog') && React.createElement(ssDS('SecretClearDialog'), {
        label: clearing.title,
        secretKey: clearing.secretKey,
        kind: clearing.kind,
        affects: clearing.consequence,
        unreadable: (secrets[clearing.key] || {}).state === 'unreadable',
        onCancel: () => setClearing(null),
        onConfirm: confirmClear,
      })}

      {saveGate && ssDS('SecretClearOnSaveDialog') && React.createElement(ssDS('SecretClearOnSaveDialog'), {
        reason: saveGate.reason,
        fromHost: saveGate.from,
        toHost: saveGate.to,
        secrets: ['SMTP username', 'SMTP password'],
        reEnterAt: 'Email',
        pendingCount: dirtyCount,
        busy: saving,
        onCancel: () => closeSaveGate(false),
        onConfirm: () => closeSaveGate(true),
      })}

      {cleared && DSToast && (
        <DSToastStack>
          <DSToast key="clr" severity="warning" duration={9000} onClose={() => setCleared(null)}
            message={(
              <div>
                <div>SMTP username and password cleared</div>
                <div style={{ fontSize: 12, color: 'var(--mud-palette-text-secondary)', marginTop: 2 }}>
                  {cleared === 'host'
                    ? 'The credential was entered for the previous host and was cleared with the change.'
                    : 'The credential was entered for an encrypted transport and was cleared with the change.'}
                  {' '}Re-enter it in Email — until then, mail is sent unauthenticated.
                </div>
              </div>
            )} />
        </DSToastStack>
      )}

      {exportName && DSToast && (
        <DSToastStack>
          <DSToast key="exp" severity="success" duration={4200} onClose={() => setExportName(null)}
            message={(
              <div>
                <div>Export ready</div>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: 11.5, color: 'var(--mud-palette-text-secondary)', marginTop: 2, wordBreak: 'break-all' }}>{exportName}</div>
              </div>
            )} />
        </DSToastStack>
      )}
    </div>
  );
}

Object.assign(window, { SystemSettings });
