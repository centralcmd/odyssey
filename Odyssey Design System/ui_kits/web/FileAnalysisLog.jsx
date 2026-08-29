/* =============================================================
   File analysis log — admin-only audit trail of every external AI
   file-analysis transfer (the "Analyze statement" feature sends the
   whole document to Anthropic's Claude API). Exists for ISO 27001
   accountability + breach traceability: who sent which file, when,
   under what lawful basis, and what came back.

   Sits in the admin nav beside Users / Roles / Settings. One record
   row per transfer, built on the same expandable .acct-item scaffold
   as the Roles + Accounts lists; the expanded detail carries the full
   audit fields and the exact consent text the user affirmed — including
   the conditions the transfer ran under, which are now all runtime
   settings: the destination host, and the processor + region the
   deployment disclosed at that instant (issue #439). Jobs recorded
   before those columns existed render "Not recorded", never back-filled
   with current values — a fabricated region would be a fabricated answer
   to "was this a third-country transfer?".

   Reads window.OdysseyData.analysisAuditLog (newest first). Atoms:
   PageHeader, Card, Avatar, SearchField, Select, MIcon, EmptyState.
   ============================================================= */

// Status → leading-media tone + pill styling, reusing the finance tone vocab.
const FAL_STATUS = {
  Completed: { tone: { bg: 'var(--finance-income-soft)', fg: 'var(--finance-income)' }, icon: 'auto_fix_high', pill: 'ok',      label: 'Completed' },
  Running:   { tone: { bg: 'var(--finance-pending-soft)', fg: 'var(--finance-pending)' }, icon: 'auto_fix_high', pill: 'pending', label: 'In progress' },
  Failed:    { tone: { bg: 'var(--finance-expense-soft)', fg: 'var(--finance-expense)' }, icon: 'auto_fix_high', pill: 'err',     label: 'Failed' },
};

// "Jun 30, 2026 · 08:14 UTC" — the log is an audit record, so always UTC.
const falWhen = (iso) => {
  const d = new Date(iso);
  const p = (n) => String(n).padStart(2, '0');
  const month = d.toLocaleDateString('en-US', { month: 'short', day: '2-digit', year: 'numeric', timeZone: 'UTC' });
  return `${month} · ${p(d.getUTCHours())}:${p(d.getUTCMinutes())} UTC`;
};
const falDur = (ms) => (ms == null ? '—' : `${(ms / 1000).toFixed(1)} s`);

// Match-step status → human label (the AI merchant/category matching step, run
// after extraction). Distinct from the analysis `status`; surfaced in the audit
// grid alongside the count of names the transfer carried.
const FAL_MATCH_LABEL = {
  Completed: 'Completed', Skipped: 'Skipped — over cap', Failed: 'Failed', NotRun: 'Not run', Running: 'In progress',
};

// One label/value pair in the expanded audit detail.
const FalFact = ({ label, children, mono, full }) => (
  <div className={`fal-fact ${full ? 'full' : ''}`}>
    <div className="fal-fact-label">{label}</div>
    <div className={`fal-fact-value ${mono ? 'mono' : ''}`}>{children}</div>
  </div>
);

const FalRow = ({ e }) => {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  const st = FAL_STATUS[e.status] || FAL_STATUS.Completed;
  const consentText = (e.consent && e.consent.text)
    || (window.OdysseyData.analysisTransfer && window.OdysseyData.analysisTransfer.consentText);

  return (
    <Card className={`acct-item ${open ? 'open' : ''}`}>
      <div className="acct-head" onClick={() => setOpen(o => !o)}>
        <Avatar icon={st.icon} tone={st.tone} square size="lg" />

        <div className="acct-id">
          <div className="acct-name-row">
            <span className="acct-name">{e.file ? e.file.name : 'statement.pdf'}</span>
            <span className={`fal-pill ${st.pill}`}>{st.label}</span>
          </div>
          <div className="fal-sub">
            <span><MIcon name="person" size={13} />{e.user ? e.user.name : '—'}</span>
            <span className="fal-dot" />
            <span><MIcon name="account_balance_wallet" size={13} />{e.account ? e.account.name : '—'}</span>
            <span className="fal-dot" />
            <span className="fal-prov"><MIcon name="auto_awesome" size={13} />{e.provider} <span className="mono">{e.model}</span></span>
          </div>
        </div>

        <div className="acct-figures">
          <div className="fal-when mono">{falWhen(e.at)}</div>
          <div className="fal-result">
            {e.status === 'Failed'
              ? 'no transactions'
              : e.status === 'Running'
                ? 'awaiting result'
                : `${e.imported}/${e.candidates} imported`}
          </div>
        </div>

        <div className="acct-controls">
          <button className="acct-expand" aria-label={open ? 'Collapse' : 'Expand'}
            onClick={(ev) => { ev.stopPropagation(); setOpen(o => !o); }}>
            <MIcon name="expand_more" size={22} className={`chev ${open ? 'open' : ''}`} />
          </button>
        </div>
      </div>

      {open && (
        <div className="acct-detail">
          <div className="fal-grid">
            <FalFact label="When (UTC)" mono>{falWhen(e.at)}</FalFact>
            <FalFact label="Initiated by">
              {e.user ? e.user.name : '—'}
              {e.user && e.user.email ? <span className="fal-email mono"> · {e.user.email}</span> : null}
            </FalFact>
            <FalFact label="Account">
              {e.account ? e.account.name : '—'}
              {e.account && e.account.number ? <span className="mono fal-acctno"> {e.account.number}</span> : null}
            </FalFact>
            <FalFact label="Processor in force">
              {e.processorInForce
                ? <React.Fragment>{e.processorInForce} · {e.provider}</React.Fragment>
                : <span className="fal-unrec">Not recorded · {e.provider}</span>}
            </FalFact>
            <FalFact label="Region in force">
              {e.processorRegionInForce || <span className="fal-unrec">Not recorded</span>}
            </FalFact>
            <FalFact label="Destination host" mono>
              {e.analyzerBaseUrlHost || <span className="fal-unrec">Not recorded</span>}
            </FalFact>
            <FalFact label="Model" mono>{e.model}</FalFact>
            <FalFact label="Prompt version" mono>v{e.promptVersion}</FalFact>
            <FalFact label="Sent">{e.pages ? `${e.pages} pages` : 'full document'}{e.size ? ` · ${e.size}` : ''}</FalFact>
            <FalFact label="Names sent">{e.vocabularyCount != null ? `${e.vocabularyCount} contact + tag names` : '—'}</FalFact>
            <FalFact label="Result">
              {e.status === 'Failed'
                ? <span className="fal-fail">{e.failure || 'Analysis failed'}</span>
                : `${e.candidates} found · ${e.imported} imported`}
            </FalFact>
            <FalFact label="Matching">
              {e.matchStatus === 'Failed'
                ? <span className="fal-fail">Failed{e.matchFailure ? ` · ${e.matchFailure}` : ''}</span>
                : (FAL_MATCH_LABEL[e.matchStatus] || e.matchStatus || '—')}
            </FalFact>
            <FalFact label="Duration" mono>{falDur(e.durationMs)}</FalFact>
            <FalFact label="Lawful basis">{e.lawfulBasis}</FalFact>
            <FalFact label="Request ID" mono>{e.requestId}</FalFact>
            <FalFact label="Consent">
              <span className="fal-consent-ok"><MIcon name="check_circle" size={14} />Recorded</span>
              <span className="fal-consent-method"> · {(e.consent && e.consent.method) || 'Per-document checkbox'}</span>
            </FalFact>
          </div>
          <div className="fal-consent-quote">
            <MIcon name="format_quote" size={15} />
            <span>“{consentText}”</span>
          </div>
        </div>
      )}
    </Card>
  );
};

function FileAnalysisLog() {
  const { useState } = React;
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('');
  // Shared sort (§6.13): Date/time newest-first default · Outcome; the toolbar
  // is the sole sort surface (chronological card list, no headers).
  const [sort, setSort] = useState({ key: 'at', dir: 'desc' });
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const FAL_STATUS_ORDER = ['Running', 'Completed', 'Failed'];
  const sortFields = [
    { key: 'at',     label: 'Date / time', type: 'date',   sortValue: (e) => e.at || null },
    { key: 'status', label: 'Outcome',     type: 'status', sortValue: (e) => { const i = FAL_STATUS_ORDER.indexOf(e.status); return i < 0 ? FAL_STATUS_ORDER.length : i; } },
  ];
  const log = (window.OdysseyData && window.OdysseyData.analysisAuditLog) || [];

  const matches = (e) => {
    if (status && e.status !== status) return false;
    const t = q.trim().toLowerCase();
    if (!t) return true;
    return [e.file && e.file.name, e.user && e.user.name, e.account && e.account.name, e.requestId]
      .filter(Boolean).some(s => s.toLowerCase().includes(t));
  };
  const rows = log.filter(matches);
  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (e) => e.id) : rows;

  return (
    <div className="col gap-6">
      <PageHeader
        title="Analysis log"
        icon="policy"
        sub="Every statement sent to Claude for analysis — who, when, and the result"
        searchDefaultOpen
        search={(
          <div className="row gap-3" style={{ flexWrap: 'wrap', alignItems: 'center' }}>
            <div style={{ minWidth: 260, flex: 1 }}>
              <SearchField placeholder="Search by file, user, account or request ID…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 170 }}>
              <Select value={status} onChange={setStatus} options={[
                { value: '', label: 'All outcomes' },
                { value: 'Completed', label: 'Completed' },
                { value: 'Running', label: 'In progress' },
                { value: 'Failed', label: 'Failed' },
              ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Entries per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        )}
      />

      <div className="fal-note">
        <MIcon name="shield" size={17} />
        <div>
          File analysis sends the <b>complete document</b> — plus your contact and tag <b>names</b>, for matching — to the configured processor. Each transfer requires
          per-document consent and is recorded here for accountability, together with the <b>host it was sent to</b> and the <b>processor and region disclosed at that moment</b> — the destination, the processor
          and the region are all admin-editable, so none of them can be reconstructed after the fact. Entries recorded before those fields existed read “Not recorded” rather than showing today’s values.
          Entries are retained per the data-retention policy.
        </div>
      </div>

      {rows.length > 0 ? (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(e) => e.id}
            noun="entries"
            renderItem={(e) => <FalRow e={e} />}
          />
        </div>
      ) : (
        <EmptyState
          icon="search_off"
          mutedIcon
          title="No analysis transfers match"
          desc={q || status ? 'Adjust the search or outcome filter to see more entries.' : 'No statements have been sent for analysis yet.'}
        />
      )}
    </div>
  );
}

Object.assign(window, { FileAnalysisLog });
