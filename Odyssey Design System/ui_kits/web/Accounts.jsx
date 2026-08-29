/* Accounts — vertical list of rich cards, each expandable into a detail view.
   Driven by the Account entity (name, description, opened, accountNumber,
   accountType, closed, archived, currencyCode) + its AccountFiles & Transactions. */

/* Account-type metadata is sourced from window.OdysseyData.accountTypes (data.js),
   the single source of truth for label / group / icon / fixed color. */
const ACCOUNT_TYPES = (window.OdysseyData || {}).accountTypes || [];
const ACCOUNT_TYPE_BY_KEY = (window.OdysseyData || {}).accountTypeById || {};
const ACCOUNT_TYPE_LABEL = Object.fromEntries(ACCOUNT_TYPES.map(t => [t.key, t.label]));
// Fallback for any legacy/unknown key so the UI never renders blank.
const typeInfo = (key) => ACCOUNT_TYPE_BY_KEY[key]
  || { key, label: key || 'Unknown', icon: 'help_outline', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };

/* ---- Account problem signals (exchange-rate feature) ----
   A "problem" pins a warning or error to a specific account; the Accounts
   header rolls them all up into one severity toggle. Severity follows the
   design-system convention — warning = amber, error = coral, info = sea.
   Keyed by account id. In the real app this is derived from the exchange-rate
   service + sync status; here it's a fixed demo set.
     chip     — short label on the card, beside the status chip
     currency — overrides the display currency (keeps the rate story coherent)
     summary  — one-line copy for the header rollup (triage)
     detail   — the fuller explanation shown inside the expanded record
     fix      — the contextual resolution, surfaced in the expanded record:
                { label, kind: 'navigate', target }
                navigate → routes to the page where it's fixed (e.g. Exchange rates) */
const ACCOUNT_PROBLEMS = {
  '4': { severity: 'warning', currency: 'EUR', chip: 'No rate',
         title: 'Missing exchange rate',
         summary: 'No EUR → USD rate for today, so this account is left out of the combined total.',
         detail: 'Odyssey converts every account into your base currency (USD) to show a combined value. There’s no EUR → USD rate stored for today, so this balance is temporarily left out of the total. Add today’s rate on the Currencies page to include it.',
         fix: { label: 'Set exchange rate', kind: 'navigate', target: 'currencies' } },
  '3': { severity: 'warning', currency: 'GBP', chip: 'No rate',
         title: 'Missing exchange rate',
         summary: 'No GBP → USD rate for today, so this account is left out of the combined total.',
         detail: 'Odyssey needs a GBP → USD rate for today to fold this account into your combined value. Add today’s rate on the Currencies page to include it.',
         fix: { label: 'Set exchange rate', kind: 'navigate', target: 'currencies' } },
};
const SEV_RANK = { info: 0, warning: 1, error: 2 };
const problemFor = (a) => ACCOUNT_PROBLEMS[a.id] || null;

/* File-kind icon + color registry. Canonical source is the design system
   (window.OdysseyData.fileTypeByKey, seeded from data.js); this reads it so a
   file's icon reads the same in the account-detail list as in the upload picker
   and the Files table. The inline fallback covers a missing/unknown kind. */
const FILE_ICON_FALLBACK = { icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' };
const FILE_ICON = (window.OdysseyData && window.OdysseyData.fileTypeByKey) || {
  Statement: { icon: 'description',       color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)' },
  Other:     { icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)' },
};

/* ---- Dropdown action menu (the more_vert), the labelled MetaTile detail
   well, and SortHeader now live in /components (window.ActionMenu / MetaTile /
   SortHeader, bridged by Components.jsx). They used to be defined inline here. */

/* ---- Multi-select dropdown (checkbox list, fixed-position pop like ActionMenu) ---- */
const MultiSelect = ({ allLabel, options, value, onChange }) => {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(false);
  const popId = React.useId();
  const popRef = useRef(null);
  const [pos, setPos] = useState(null);
  const ref = useRef(null);
  const btnRef = useRef(null);

  const toggle = () => {
    if (!open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 4, left: r.left, width: r.width });
    }
    setOpen(o => !o);
  };

  useEffect(() => {
    if (!open) return;
    const onDoc = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    const onScroll = (e) => { if (ref.current && ref.current.contains(e.target)) return; setOpen(false); };
    const onResize = () => setOpen(false);
    document.addEventListener('mousedown', onDoc);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
    };
  }, [open]);

  const toggleVal = (v) => onChange(value.includes(v) ? value.filter(x => x !== v) : [...value, v]);

  // Move focus into the listbox when it opens (keyboard users land on an
  // option, not stranded on the trigger).
  useEffect(() => {
    if (!open) return;
    const t = setTimeout(() => {
      if (!popRef.current) return;
      const opts = popRef.current.querySelectorAll('[role=option]');
      const sel = popRef.current.querySelector('[role=option][aria-selected=true]');
      (sel || opts[0]) && (sel || opts[0]).focus();
    }, 0);
    return () => clearTimeout(t);
  }, [open]);

  // Roving arrow-key navigation among the options (listbox keyboard contract).
  const onPopKey = (e) => {
    const opts = popRef.current ? [...popRef.current.querySelectorAll('[role=option]')] : [];
    const i = opts.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); (opts[i + 1] || opts[0]) && (opts[i + 1] || opts[0]).focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); (opts[i - 1] || opts[opts.length - 1]) && (opts[i - 1] || opts[opts.length - 1]).focus(); }
    else if (e.key === 'Home') { e.preventDefault(); opts[0] && opts[0].focus(); }
    else if (e.key === 'End') { e.preventDefault(); opts[opts.length - 1] && opts[opts.length - 1].focus(); }
    else if (e.key === 'Escape') { e.preventDefault(); setOpen(false); btnRef.current && btnRef.current.focus(); }
  };

  const summary = value.length === 0
    ? allLabel
    : value.length === 1
      ? (options.find(o => o.value === value[0]) || {}).label
      : `${value.length} selected`;

  return (
    <div className="multiselect" ref={ref}>
      <button type="button" ref={btnRef} className={`multiselect-trigger ${value.length ? 'active' : ''}`}
        aria-haspopup="listbox" aria-expanded={open} aria-controls={open ? popId : undefined} onClick={toggle}>
        <span className="multiselect-summary">{summary}</span>
        <MIcon name="expand_more" size={20} className={`chev ${open ? 'open' : ''}`} />
      </button>
      {open && pos && (
        <div id={popId} className="acct-menu-pop multiselect-pop" role="listbox" aria-multiselectable="true" aria-label={allLabel}
          ref={popRef} onKeyDown={onPopKey} style={{ top: pos.top, left: pos.left, minWidth: pos.width }}>
          {options.map(o => {
            const checked = value.includes(o.value);
            return (
              <button key={o.value} type="button" role="option" aria-selected={checked}
                className="multiselect-item" onClick={() => toggleVal(o.value)}>
                <span className={`ms-check ${checked ? 'on' : ''}`}>{checked && <MIcon name="check" size={14} />}</span>
                <span>{o.label}</span>
              </button>
            );
          })}
          {value.length > 0 && (
            <React.Fragment>
              <div className="acct-menu-sep" />
              <button type="button" className="multiselect-item ms-clear" onClick={() => onChange([])}>
                <span className="ms-check" /><span>Clear selection</span>
              </button>
            </React.Fragment>
          )}
        </div>
      )}
    </div>
  );
};

/* ---- Collapsible (Files / Transactions / Terms sections) is now the
   single DS component (components/Collapsible.jsx), bridged in Components.jsx as
   window.Collapsible with the `icon` + `action` slots these sections use. The
   local copy that used to live here has been removed — there is no second impl. */

const H = window.OdysseyHelpers;

/* The full account lifecycle as one line — opened, then closed and/or archived
   when present. Consolidates the formerly-separate Opened / Closed tiles under
   the Status tile, covering every state (open · closed · archived). */
const accountLifecycle = (a) => {
  const parts = [];
  if (a.opened) parts.push(`Opened ${H.dateLong(a.opened)}`);
  if (a.closed) parts.push(`Closed ${H.dateLong(a.closed)}`);
  if (a.archived) parts.push(`Archived ${H.dateLong(String(a.archived).slice(0, 10))}`);
  return parts.join(' · ');
};

/* ---- Account-tone → chart color (kept for reference) ---- */
const TONE_COLOR = {
  tide: 'var(--tide-400)', sea: 'var(--sea-400)', mint: 'var(--mint-500)',
  violet: 'var(--violet-500)', coral: 'var(--coral-500)', amber: 'var(--amber-500)',
};
/* High-contrast categorical sequences for donut slices (distinct hues) */
const ASSET_COLORS = ['var(--violet-500)', 'var(--tide-400)', 'var(--amber-500)', 'var(--sea-400)', 'var(--mint-500)'];
const LIAB_COLORS = ['var(--coral-500)', 'var(--amber-500)', 'var(--violet-500)', 'var(--sea-400)'];

/* ---- Reusable allocation donut panel (ring + legend stacked below) ---- */
const DonutPanel = ({ title, sub, centerLabel, centerIcon, colors, items }) => {
  const total = items.reduce((s, it) => s + it.value, 0);
  const r = 74, C = 2 * Math.PI * r;
  // Small gap between slices so same-family hues still read as separate pieces.
  const GAP = items.length > 1 ? 7 : 0;
  let acc = 0;
  const slices = items.map((it, i) => {
    const frac = total > 0 ? it.value / total : 0;
    const dash = Math.max(frac * C - GAP, 1);
    const seg = { ...it, color: it.color || colors[i % colors.length], dash, off: -acc * C, pct: frac };
    acc += frac;
    return seg;
  });

  return (
    <div className="odc-donut-panel">
      <div className="odc-chart-head">
        <div>
          <div className="odc-chart-ttl">{title}</div>
          <div className="odc-chart-sub">{sub}</div>
        </div>
      </div>
      <div className="odc-donut-body stack">
        <div className="odc-donut-ring" style={{ width: 200, height: 200 }}>
          <svg viewBox="0 0 200 200" style={{ transform: 'rotate(-90deg)', display: 'block', width: 200, height: 200 }}>
            <circle cx="100" cy="100" r={r} stroke="var(--mud-palette-divider-light)" strokeWidth="26" fill="none" />
            {slices.map((s, i) => (
              <circle key={i} cx="100" cy="100" r={r} fill="none"
                stroke={s.color} strokeWidth="26"
                strokeDasharray={`${s.dash.toFixed(1)} ${(C - s.dash).toFixed(1)}`}
                strokeDashoffset={s.off.toFixed(1)} strokeLinecap="butt" />
            ))}
          </svg>
          {centerIcon && (
            <div className="odc-donut-center" aria-hidden="true">
              <span className="material-icons odc-donut-center-ic">{centerIcon}</span>
            </div>
          )}
        </div>
        <div className="odc-donut-legend">
          {slices.map((s, i) => (
            <div className="odc-legend-row" key={i}>
              <div className="odc-legend-main">
                <span className="odc-legend-swatch" style={{ background: s.color }} />
                <span className="odc-legend-name">{s.name}</span>
              </div>
              <div className="odc-legend-figs">
                <span className="odc-legend-pct">{Math.round(s.pct * 100)}%</span>
                <span className="odc-legend-amt">{H.money(s.value)}</span>
              </div>
            </div>
          ))}
          {/* Total lives in the ledger (out of the ring) so large sums never overflow the donut. */}
          <div className="odc-donut-total">
            <span className="odc-donut-total-lab">{centerLabel}</span>
            <div className="odc-legend-figs">
              <span className="odc-donut-total-pct">100%</span>
              <span className="odc-donut-total-amt">{H.money(total)}</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};

/* ---- Asset & liability donuts, fed from the account balances ---- */
const AllocationDonuts = () => {
  const d = window.OdysseyData;
  const live = d.accounts.filter(a => !a.archived);
  const assets = live.filter(a => a.balance > 0).sort((a, b) => b.balance - a.balance)
    .map(a => ({ name: a.name, value: a.balance, color: typeInfo(a.type).color }));
  const liabilities = live.filter(a => a.balance < 0).sort((a, b) => a.balance - b.balance)
    .map(a => ({ name: a.name, value: Math.abs(a.balance), color: typeInfo(a.type).color }));

  return (
    <div className="acct-donuts-row">
      <div className="acct-donut-card">
        <DonutPanel
          title="Asset allocation"
          sub={`Where your money sits · ${assets.length} accounts`}
          centerLabel="Total assets"
          centerIcon="account_balance_wallet"
          colors={ASSET_COLORS}
          items={assets}
        />
      </div>
      <div className="acct-donut-card">
        <DonutPanel
          title="Liabilities"
          sub={`What you owe · ${liabilities.length} accounts`}
          centerLabel="Total owed"
          centerIcon="credit_card"
          colors={LIAB_COLORS}
          items={liabilities}
        />
      </div>
    </div>
  );
};

/* ---- THE one files surface — the shared DS FilesTable
   (components/FilesTable.jsx, a preset of RecordTable); there is no second
   implementation here. The table itself owns the record-row lifecycle —
   expand-to-detail, the inline Edit panel (name + document type), the Saved
   flash — so this bridge only resolves kind visuals via FILE_ICON, supplies
   the file-specific menu items (Preview / Download / Analyze / Copy ID), and
   hosts the modals those open OUTSIDE the table. "Preview" opens the document
   (FileViewerModal); "View details" expands the record — two different things,
   hence two names. Pass `onDelete` to allow detaching a file (the transaction
   edit panel); pass `accountFor` when rows span accounts (the Files page) — it
   resolves each file's owning account. ---- */
const FilesTable = ({ files, account, accountFor, onNavigate, onDelete, sort, onSortChange, empty, kinds, showValidity = true }) => {
  const { useState } = React;
  const DSFilesTable = (window.OdysseyDesignSystem_d5aa51 || {}).FilesTable;
  const D = window.OdysseyData;

  // Inline edits saved from the table's edit panel, by file id — keeps this
  // surface live without owning the parent's file list.
  const [edits, setEdits] = useState({});
  const [modal, setModal] = useState(null); // { type:'preview'|'analyze', file, account, initialPhase?, resumeSummary? }
  // The HOST loads the account-scoped resumable map once and decides, per file,
  // whether a resumable review exists — driving the row chip, the Resume menu
  // action, and the dialog's initial phase. `resumeNonce` forces a re-read of
  // the map after a review is imported/finished, so the hint clears (no stale).
  const [, bumpResume] = useState(0);
  const refreshResumable = () => bumpResume(n => n + 1);

  const acctOf = (f) => (accountFor ? accountFor(f) : account);
  const rows = files.map(f => {
    const merged = edits[f.id] ? { ...f, ...edits[f.id] } : f;
    const rj = D.resumableSummaryForFile(merged);
    // A resumable review surfaces as an additive "Review pending" chip — meaning
    // carried as text, with a full accessible name (file + count).
    return rj
      ? { ...merged, statusBadge: {
          text: `Review pending · ${rj.pendingCount}`, tone: 'pending',
          ariaLabel: `Resume analysis review for ${merged.name} — ${rj.pendingCount} candidate${rj.pendingCount === 1 ? '' : 's'} pending`,
        } }
      : merged;
  });
  const open = (type, f) => setModal({ type, file: f, account: acctOf(f) });
  // Open the analyze dialog in a host-resolved initial phase (Resume →
  // resumeLoading; Analyze-with-resumable → reanalyzeConfirm; else consent).
  const openAnalyze = (f, initialPhase, resumeSummary) =>
    setModal({ type: 'analyze', file: f, account: acctOf(f), initialPhase, resumeSummary });

  return (
    <React.Fragment>
      <DSFilesTable
        files={rows}
        sort={sort}
        onSortChange={onSortChange}
        typeFor={(f) => FILE_ICON[f.kind] || FILE_ICON_FALLBACK}
        kinds={kinds || window.OdysseyData.accountFileTypes}
        issuerFor={(f) => {
          const c = f.issuedBy && window.OdysseyData.contactById[f.issuedBy];
          return c ? c.name : null;
        }}
        issuers={showValidity ? (window.OdysseyData.contacts || [])
          .filter(c => !c.archived)
          .map(c => ({ value: c.id, label: c.name })) : undefined}
        formatDate={H.dateLong}
        empty={empty}
        onSave={(id, patch) => setEdits(prev => ({ ...prev, [id]: { ...(prev[id] || {}), ...patch } }))}
        onDelete={onDelete}
        actions={(f) => {
          const rj = D.resumableSummaryForFile(f);
          // The kill switch is read live (issue #439), so the affordance reflects
          // it: with analysis off the Analyze action renders DISABLED with a
          // visible reason, and the consent gate is unreachable — a user is never
          // allowed to pick a document and affirm consent only to be answered
          // 503. Meaning is carried in text, never by the dimmed state alone.
          const analysisOn = !D.analysisDisclosure || D.analysisDisclosure().enabled !== false;
          return [
            { icon: 'visibility', label: 'Preview', onClick: () => open('preview', f) },
            { icon: 'download', label: 'Download', onClick: () => H.downloadFile(f) },
            // Resume review — only when this file has an open, resumable job.
            ...(rj && analysisOn ? [{ icon: 'history', label: 'Resume review', onClick: () => openAnalyze(f, 'resumeLoading', rj) }] : []),
            // Analyze distinguishes resume-vs-reanalyze instead of silently
            // creating a duplicate: a resumable job present → the confirm step.
            analysisOn
              ? { icon: 'auto_fix_high', label: 'Analyze', onClick: () => openAnalyze(f, rj ? 'reanalyzeConfirm' : null, rj) }
              : { icon: 'auto_fix_high', label: 'Analyze', disabled: true,
                  note: 'AI document analysis is turned off for this instance.' },
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(f.id); } },
          ];
        }}
      />
      {modal && modal.type === 'preview' && (
        <FileViewerModal file={modal.file} account={modal.account} onClose={() => setModal(null)} />
      )}
      {modal && modal.type === 'analyze' && (
        <AnalyzeFileModal
          file={modal.file}
          account={modal.account}
          initialPhase={modal.initialPhase}
          resumeSummary={modal.resumeSummary}
          onClose={() => setModal(null)}
          onResolved={(f) => { D.clearResumableJob(f.id); refreshResumable(); }}
          onNavigateTransactions={onNavigate ? () => onNavigate('transactions') : null}
        />
      )}
    </React.Fragment>
  );
};
/* ---- Account smart tags (saved per-account tag watchlist) ----
   Seed associations by account id (TransactionTag ids from data.js). In the
   real app these come from GET /api/accounts/{id}/smart-tags; here they're a
   fixed demo set so the section opens populated for a couple of accounts. */
const SMART_TAG_SEED = {
  '1': ['t1', 't7'],   // Everyday Checking — Groceries, Utilities
  '3': ['t2'],         // Travel Card — Subscriptions
};

/* Kit wrapper around the DS AccountSmartTagsSection: owns the watched-tag set
   for one account, filters this account's transactions down to the matches,
   and feeds the DS section. Add/remove flips a brief loading state to mirror
   the re-fetch the live section does on every change. */
const AccountSmartTags = ({ a, txns, onNavigate, tagIds, setTagIds }) => {
  const { useState, useEffect, useRef } = React;
  const DSSection = (window.OdysseyDesignSystem_d5aa51 || {}).AccountSmartTagsSection;
  const allTags = window.OdysseyData.tags.filter(t => !t.archived);
  const tagById = window.OdysseyData.tagById;
  const txnTagIds = window.OdysseyData.txnTagIds;

  const [loading, setLoading] = useState(false);
  const timer = useRef(null);

  // Brief re-fetch flash whenever the watched set changes (after first render).
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    setLoading(true);
    timer.current = setTimeout(() => setLoading(false), 420);
    return () => clearTimeout(timer.current);
  }, [tagIds.join(',')]);

  if (!DSSection) return null;

  const matches = txns.filter(t => {
    const ids = txnTagIds(t);
    return ids.some(id => tagIds.includes(id));
  });

  const configured = tagIds.map(id => tagById[id]).filter(Boolean).map(t => ({ id: t.id, label: t.name }));
  const options = allTags.map(t => ({ value: t.id, label: t.name }));

  const add = (id) => setTagIds(prev => (prev.includes(id) ? prev : [...prev, id]));
  const remove = (id) => setTagIds(prev => prev.filter(x => x !== id));

  return (
    <DSSection
      tags={configured}
      tagOptions={options}
      transactions={matches}
      onAddTag={add}
      onRemoveTag={remove}
      loading={loading}
      maxTags={20}
      formatAmount={(n) => window.OdysseyHelpers.signedMoney(n, a.currency)}
      renderTable={(rows) => (
        <div className="acct-txn-table">
          <InlinePager items={rows}>
            {(pageRows) => <TxnTable txns={pageRows} hideAccount onNavigate={onNavigate} />}
          </InlinePager>
        </div>
      )}
    />
  );
};

const AccountDetail = ({ a, problem, onFix, onNavigate, txns, onSaveTxn, onDeleteTxn, terms, onNewTerm, onEditTerm, onDeleteTerm,
  estimates, onNewEstimate, onEditEstimate, onDeleteEstimate, smartTagIds, setSmartTagIds }) => {
  const files = H.filesForAccount(a.id);
  const status = H.accountStatus(a);
  const lifecycle = accountLifecycle(a);
  return (
    <div className="acct-detail">
      {problem && (
        <div className={`alert ${problem.severity} acct-problem`} role={problem.severity === 'error' ? 'alert' : 'status'}>
          <div className="acct-problem-head">
            <SeverityIcon severity={problem.severity} size={20} className="alert-icon" />
            <div className="acct-problem-title">{problem.title}</div>
            <button type="button" className="alert-cta" onClick={onFix}>
              {problem.fix.label}
              <MIcon name={problem.fix.kind === 'navigate' ? 'arrow_forward' : 'open_in_new'} size={16} />
            </button>
          </div>
          <p className="acct-problem-detail">{problem.detail}</p>
        </div>
      )}

      <div className="meta-grid">
        <MetaTile label="Account type" value={<AccountTypeChip type={a.type} />} />
        <MetaTile label="Account number" value={a.accountNumber || '—'} mono />
        <MetaTile label="Custodian" value={<CustodianChip custodian={window.OdysseyData.custodianForAccount(a)} />} />
        <MetaTile label="Currency" value={a.currency} mono />
        <MetaTile label="Status" value={(
          <span style={{ display: 'inline-flex', flexDirection: 'column', gap: 6, alignItems: 'flex-start' }}>
            <Chip tone={status.tone} dot>{status.label}</Chip>
            {lifecycle && (
              <span style={{ color: 'var(--mud-palette-text-secondary)', font: 'var(--fw-regular) var(--fs-caption)/1.35 var(--font-sans)' }}>{lifecycle}</span>
            )}
          </span>
        )} />
        <MetaTile label="Description" value={a.description} />
      </div>

      <AccountEstimates account={a} estimates={estimates} txns={txns} onNew={onNewEstimate} onEdit={onEditEstimate} onDelete={onDeleteEstimate} />

      <AccountTerms account={a} terms={terms} onNew={onNewTerm} onEdit={onEditTerm} onDelete={onDeleteTerm} />

      <Collapsible
        icon="attach_file"
        title="Files"
        count={files.length}
        action={<Button variant="text" color="primary" iconRight="arrow_forward" onClick={() => onNavigate && onNavigate('files')}>View all</Button>}
      >
        {files.length === 0 ? (
          <div className="empty-line">No files attached to this account yet.</div>
        ) : (
          <InlinePager items={files}>
            {(pageRows) => <FilesTable files={pageRows} account={a} onNavigate={onNavigate} />}
          </InlinePager>
        )}
      </Collapsible>

      <Collapsible
        icon="receipt_long"
        title="Transactions"
        count={txns.length}
        action={<Button variant="text" color="primary" iconRight="arrow_forward" onClick={() => onNavigate && onNavigate('transactions')}>View all</Button>}
      >
        <div className="acct-txn-table">
          <InlinePager items={txns}>
            {(pageRows) => (
              <TxnTable
                txns={pageRows}
                hideAccount
                onSave={onSaveTxn}
                onDelete={onDeleteTxn}
                empty={<div className="empty-line" style={{ padding: 20 }}>No transactions recorded for this account.</div>}
              />
            )}
          </InlinePager>
        </div>
      </Collapsible>

      <AccountSmartTags a={a} txns={txns} onNavigate={onNavigate} tagIds={smartTagIds} setTagIds={setSmartTagIds} />
    </div>
  );
};

/* ---- Account edit reuses the create dialog (AddAccountModal) in edit mode ---- */
const TYPE_OPTIONS = ACCOUNT_TYPES.map(({ key, label }) => ({ value: key, label }));

/* ---- One account list item (collapsed header + expandable detail) ---- */
const AccountListItem = ({ a, problem, highlight, onJump, onNavigate, onDelete }) => {
  const { useState, useEffect, useRef } = React;
  const [acct, setAcct] = useState(problem && problem.currency ? { ...a, currency: problem.currency } : a);
  const [open, setOpen] = useState(false);
  const [showEdit, setShowEdit] = useState(false);
  const [addingFile, setAddingFile] = useState(false);
  const [addingTxn, setAddingTxn] = useState(false);
  const cardRef = useRef(null);
  const status = H.accountStatus(acct);
  const files = H.filesForAccount(acct.id);
  const [txns, setTxns] = useState(() => H.txnsForAccount(acct.id));
  const dimmed = !!(acct.closed || acct.archived);
  const ti = typeInfo(acct.type);
  const cust = window.OdysseyData.custodianForAccount(acct);

  const saveTxn = (id, patch) => setTxns(prev => prev.map(t => t.id === id ? { ...t, ...patch } : t));
  const deleteTxn = (id) => setTxns(prev => prev.filter(t => t.id !== id));

  // Account terms (rate & fee history) — lifted here so the row menu's "New term",
  // the section's own button, and the in-force rate shown in the header all share
  // one source of truth; a term added from either place shows everywhere at once.
  const [terms, setTerms] = useState(() => H.termsForAccount(acct.id));
  const [termModal, setTermModal] = useState(null); // { mode:'new'|'edit', term? }
  const upsertTerm = (dto, id) => {
    setTerms(prev => id
      ? prev.map(t => t.id === id ? { ...t, ...dto } : t)
      : [{ id: `tm-new-${Date.now()}`, accountId: acct.id, createdAtUtc: new Date().toISOString(), ...dto }, ...prev]);
    setTermModal(null);
  };
  const deleteTerm = (t) => setTerms(prev => prev.filter(x => x.id !== t.id));
  const newTerm = () => { setOpen(true); setTermModal({ mode: 'new' }); };

  // Account value estimates — same lifting pattern as terms, so the row menu's
  // "New estimate", the section's own button, and the in-force estimate shown as
  // the header value all share one source of truth.
  const [estimates, setEstimates] = useState(() => H.estimatesForAccount(acct.id));
  const [estModal, setEstModal] = useState(null); // { mode:'new'|'edit', estimate? }
  const upsertEstimate = (dto, id) => {
    setEstimates(prev => id
      ? prev.map(e => e.id === id ? { ...e, ...dto } : e)
      : [{ id: `es-new-${Date.now()}`, accountId: acct.id, createdAtUtc: new Date().toISOString(), ...dto }, ...prev]);
    setEstModal(null);
  };
  const deleteEstimate = (e) => setEstimates(prev => prev.filter(x => x.id !== e.id));
  const newEstimate = () => { setOpen(true); setEstModal({ mode: 'new' }); };

  // Smart tags watched on this account — lifted here so the header count stays
  // live as tags are added/removed inside the expanded Smart tags section.
  const [smartTagIds, setSmartTagIds] = useState(() => SMART_TAG_SEED[acct.id] || []);

  // The estimate in force now — the headline value for an asset account.
  const curEstimate = window.estCurrentFromList ? window.estCurrentFromList(estimates) : null;
  // The interest rate / expected return in force now (never a fee), for the header.
  const rateTerm = (window.trmCurrentFromList ? window.trmCurrentFromList(terms) : [])
    .find(t => window.trmKindInfo(t.kind).group === 'rate');
  // Map a NewTransaction DTO from the modal into a row for this account's list.
  const createTxn = (dto) => {
    const row = {
      id: `new-${Date.now()}`,
      date: dto.TimeStamp || new Date().toISOString().slice(0, 10),
      desc: dto.Description,
      account: dto.AccountId || acct.id,
      tags: dto.TransactionTagIds || [],
      contact: dto.ContactId || undefined,
      currency: dto.CurrencyCode,
      amount: dto.Amount,
      status: dto.Status,
      icon: dto.Amount >= 0 ? 'arrow_downward' : 'shopping_cart',
      dir: dto.dir || (dto.Amount >= 0 ? 'income' : 'expense'),
      files: dto.Files || undefined,
    };
    setTxns(prev => [row, ...prev]);
    setAddingTxn(false);
  };

  // When the header rollup jumps to this account, open it, scroll it into the
  // viewport (via the .main scroll container) and flash a severity ring.
  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    setOpen(true);
    // Find the nearest actually-scrollable ancestor; fall back to the window.
    const el = cardRef.current;
    let scroller = el.parentElement;
    while (scroller && scroller !== document.body) {
      const oy = getComputedStyle(scroller).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && scroller.scrollHeight > scroller.clientHeight) break;
      scroller = scroller.parentElement;
    }
    requestAnimationFrame(() => {
      if (scroller && scroller !== document.body) {
        const top = scroller.scrollTop + (el.getBoundingClientRect().top - scroller.getBoundingClientRect().top) - 24;
        scroller.scrollTo({ top, behavior: 'smooth' });
      } else {
        const top = el.getBoundingClientRect().top + window.scrollY - 24;
        window.scrollTo({ top, behavior: 'smooth' });
      }
    });
  }, [highlight]);

  const startEdit = () => setShowEdit(true);

  // Route a problem's fix: navigate to the page where it's resolved.
  const handleFix = () => {
    if (!problem) return;
    if (problem.fix.kind === 'navigate') onNavigate && onNavigate(problem.fix.target);
  };

  const saveEdit = (draft) => {
    setAcct(prev => ({
      ...prev,
      name: draft.name.trim() || prev.name,
      description: draft.description,
      type: draft.type,
      accountNumber: draft.accountNumber,
      custodianId: draft.custodianId || null,
      opened: draft.opened,
      closed: draft.closed || null,
      currency: draft.currency,
    }));
    setShowEdit(false);
  };

  return (
    <Card className={`acct-item ${open ? 'open' : ''} ${dimmed ? 'dimmed' : ''} ${highlight ? 'flash' : ''}`} ref={cardRef}>
      <div className="acct-head" onClick={() => setOpen(o => !o)}>
        <Avatar icon={ti.icon} tone={{ bg: ti.soft, fg: ti.color }} square size="lg" />

        <div className="acct-id">
          <div className="acct-name-row">
            <span className="acct-name">{acct.name}</span>
            <Chip tone={status.tone} dot>{status.label}</Chip>
            {problem && (
              <Chip tone={problem.severity} className="problem">
                <SeverityIcon severity={problem.severity} size={13} />{problem.chip}
              </Chip>
            )}
          </div>
          <div className="acct-tags">
            {cust && <span className="acct-meta">{cust.name}</span>}
            {cust && <span className="acct-dot">·</span>}
            <span className="acct-meta">{ti.label}</span>
            <span className="mono">{acct.number}</span>
            {rateTerm && (
              <React.Fragment>
                <span className="acct-dot">·</span>
                <span className="acct-rate mono" title={window.trmKindInfo(rateTerm.kind).label}
                  style={{ display: 'inline-flex', alignItems: 'center', gap: 2, color: H.costColor(rateTerm, acct) || window.trmKindInfo(rateTerm.kind).color, fontVariantNumeric: 'tabular-nums', fontWeight: 500 }}>
                  {H.fmtTermValueFor(rateTerm, acct)}
                </span>
              </React.Fragment>
            )}
            <span className="acct-dot">·</span>
            <span className="acct-counts">
              <span title="Transactions"><MIcon name="receipt_long" size={14} />{txns.length}</span>
              <span title="Files"><MIcon name="attach_file" size={14} />{files.length}</span>
              {estimates.length > 0 && <span title="Estimates"><MIcon name="monitor" size={14} />{estimates.length}</span>}
              {terms.length > 0 && <span title="Terms"><span className="acct-count-glyph" aria-hidden="true">§</span>{terms.length}</span>}
              {smartTagIds.length > 0 && <span title="Smart tags"><MIcon name="sell" size={14} />{smartTagIds.length}</span>}
            </span>
          </div>
        </div>

        <div className="acct-figures">
          {curEstimate ? (
            <React.Fragment>
              <div className="acct-balance mono" style={{ color: 'var(--finance-income)' }} title="Estimated value">
                {H.money(curEstimate.value, acct.currency)}
              </div>
              <div className="mono" style={{ fontSize: 10.5, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--mud-palette-text-secondary)', textAlign: 'right', marginTop: 3 }}>
                Est. value
              </div>
            </React.Fragment>
          ) : (
            <div
              className="acct-balance mono"
              style={{ color: acct.balance < 0 ? 'var(--finance-expense)' : 'var(--finance-income)' }}
            >
              {H.money(acct.balance)}
            </div>
          )}
        </div>

        <div className="acct-controls" onClick={(e) => e.stopPropagation()}>
          <ActionMenu items={[
            { icon: 'edit', label: 'Edit account', onClick: startEdit },
            { icon: 'upload_file', label: 'Upload file', onClick: () => setAddingFile(true) },
            { icon: 'receipt_long', label: 'New transaction', onClick: () => setAddingTxn(true) },
            { icon: 'monitor', label: 'New estimate', onClick: newEstimate },
            { icon: '§', label: 'New term', onClick: newTerm },
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(acct.id); } },
            { divider: true },
            { icon: acct.closed ? 'lock_open' : 'lock', label: acct.closed ? 'Reopen' : 'Close',
              onClick: () => setAcct(prev => ({ ...prev, closed: prev.closed ? null : new Date().toISOString().slice(0, 10) })) },
            { icon: acct.archived ? 'unarchive' : 'archive', label: acct.archived ? 'Unarchive' : 'Archive',
              onClick: () => setAcct(prev => ({ ...prev, archived: prev.archived ? null : new Date().toISOString() })) },
            { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(acct.id) },
          ]} />
          <button className="acct-expand" onClick={() => setOpen(o => !o)} aria-label="Expand">
            <MIcon name="expand_more" size={22} className={`chev ${open ? 'open' : ''}`} />
          </button>
        </div>
      </div>

      {open && <AccountDetail a={acct} problem={problem} onFix={handleFix} onNavigate={onNavigate} txns={txns} onSaveTxn={saveTxn} onDeleteTxn={deleteTxn}
        terms={terms} onNewTerm={newTerm} onEditTerm={(t) => setTermModal({ mode: 'edit', term: t })} onDeleteTerm={deleteTerm}
        estimates={estimates} onNewEstimate={newEstimate} onEditEstimate={(e) => setEstModal({ mode: 'edit', estimate: e })} onDeleteEstimate={deleteEstimate}
        smartTagIds={smartTagIds} setSmartTagIds={setSmartTagIds} />}
      {showEdit && <AddAccountModal account={acct} onClose={() => setShowEdit(false)} onSave={saveEdit} />}
      {addingFile && (
        <AddFileModal
          defaultAccount={acct.id}
          onClose={() => setAddingFile(false)}
          onCreate={() => setAddingFile(false)}
        />
      )}
      {addingTxn && (
        <AddTransactionModal
          defaultAccount={acct.id}
          lockAccount
          onClose={() => setAddingTxn(false)}
          onCreate={createTxn}
        />
      )}
      {termModal && (
        <AddTermModal
          account={acct}
          term={termModal.mode === 'edit' ? termModal.term : null}
          existing={terms}
          onClose={() => setTermModal(null)}
          onSave={upsertTerm}
        />
      )}
      {estModal && (
        <AddEstimateModal
          account={acct}
          estimate={estModal.mode === 'edit' ? estModal.estimate : null}
          existing={estimates}
          onClose={() => setEstModal(null)}
          onSave={upsertEstimate}
        />
      )}
    </Card>
  );
};

const Accounts = ({ onNavigate }) => {
  const d = window.OdysseyData;
  const [q, setQ] = React.useState('');
  const [typeFilter, setTypeFilter] = React.useState([]);
  const [statusFilter, setStatusFilter] = React.useState([]);
  const [showAdd, setShowAdd] = React.useState(false);
  const [accounts, setAccounts] = React.useState(d.accounts);
  const [jumpId, setJumpId] = React.useState(null);
  // Shared sort (§6.1): Name A→Z default; toolbar SortSelect is the SOLE sort
  // surface — this card list has no column headers.
  const [sort, setSort] = React.useState({ key: 'name', dir: 'asc' });
  // Card-list server paging: batch size ("Load N at a time") owned here and fed
  // to InfiniteList, which appends batches as its sentinel scrolls into view.
  const [batch, setBatch] = React.useState(25);
  const DS = window.OdysseyDesignSystem_d5aa51 || {};

  // Clear the one-shot highlight a moment after a jump so the ring can re-fire.
  const jumpTo = (id) => {
    setJumpId(null);
    requestAnimationFrame(() => setJumpId(id));
    setTimeout(() => setJumpId(curr => (curr === id ? null : curr)), 2200);
  };

  // Create a new account from the modal draft (prototype: prepend to the list).
  const createAccount = (draft) => {
    const ti = typeInfo(draft.type);
    const acct = {
      id: `new-${Date.now()}`,
      name: draft.name,
      number: '·NEW',
      accountNumber: draft.accountNumber || '',
      custodianId: draft.custodianId || null,
      description: draft.description || '',
      type: draft.type,
      currency: draft.currency,
      opened: draft.opened || null,
      closed: null,
      archived: null,
      balance: 0,
      deltaLabel: 'Just added',
      deltaDir: 'flat',
      icon: ti.icon,
      tone: 'tide',
    };
    setAccounts(prev => [acct, ...prev]);
    setShowAdd(false);
  };

  const deleteAccount = (id) => setAccounts(prev => prev.filter(a => a.id !== id));

  const rows = accounts.filter(a => {
    if (typeFilter.length && !typeFilter.includes(a.type)) return false;
    if (statusFilter.length && !statusFilter.includes(H.accountStatus(a).label)) return false;
    if (q) {
      const needle = q.toLowerCase();
      const hay = `${a.name} ${a.description} ${a.number} ${a.accountNumber || ''}`.toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });

  // §6.1 curated sort fields — the ONE list feeding both the SortSelect
  // options and the ordering below (SortHelpers.sortRows: stable, nulls last,
  // id tiebreak, applied AFTER search + filters). Balance/txnCount compare raw
  // values — no FX conversion (§14). Account type sorts by the registry's
  // declared order, not the label.
  const sortFields = [
    { key: 'name',     label: 'Name',              type: 'text',   sortValue: (a) => (a.name || '').toLowerCase() },
    { key: 'balance',  label: 'Balance',           type: 'number', sortValue: (a) => a.balance },
    { key: 'type',     label: 'Account type',      type: 'status', sortValue: (a) => { const i = ACCOUNT_TYPES.findIndex(t => t.key === a.type); return i < 0 ? ACCOUNT_TYPES.length : i; } },
    { key: 'opened',   label: 'Date opened',       type: 'date',   sortValue: (a) => a.opened || null },
    { key: 'txnCount', label: 'Transaction count', type: 'number', sortValue: (a) => d.transactions.filter(t => t.account === a.id).length },
  ];
  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (a) => a.id) : rows;

  const active = rows.filter(a => !a.closed && !a.archived);
  const total = active.reduce((s, a) => s + a.balance, 0);

  // Roll up problems across the visible active accounts. Highest severity wins
  // the header toggle's tint; the count is the number of affected accounts.
  const flagged = active.map(a => ({ a, p: problemFor(a) })).filter(x => x.p);
  const topSeverity = flagged.reduce(
    (s, x) => (SEV_RANK[x.p.severity] > SEV_RANK[s] ? x.p.severity : s), 'info');
  const signal = flagged.length ? {
    severity: topSeverity,
    count: flagged.length,
    label: topSeverity === 'error' ? 'Problems' : 'Attention',
    defaultOpen: true,
    region: (
      <div className="signal-panel">
        {flagged.map(({ a, p }) => (
          <div key={a.id} className={`alert ${p.severity} compact signal-row`}
               role="button" tabIndex={0}
               onClick={() => jumpTo(a.id)}
               onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(a.id); } }}>
            <SeverityIcon severity={p.severity} size={18} className="alert-icon" />
            <div className="alert-body"><strong>{a.name}.</strong> {p.summary}</div>
            <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo(a.id); }}>View →</button>
          </div>
        ))}
      </div>
    ),
  } : null;

  return (
    <div className="col gap-6">
      <PageHeader
        title="Accounts"
        icon="account_balance_wallet"
        sub={`${active.length} open · combined ${H.money(total)}`}
        signal={signal}
        overview={(
          <div className="col gap-4">
            <AllocationDonuts />
            <div className="odc-summary-grid">
              <BreakdownTile label="By type" empty="No accounts."
                rows={odcTypeRows(d.accounts.filter(a => !a.archived), ACCOUNT_TYPES, (a) => a.type)} />
              <BreakdownTile label="By status" empty="No accounts."
                rows={odcStatusRows(d.accounts, [
                  { key: 'Open', label: 'Open', tone: 'income', icon: 'lock_open' },
                  { key: 'Closed', label: 'Closed', tone: 'expense', icon: 'lock' },
                  { key: 'Archived', label: 'Archived', tone: 'outline', icon: 'inventory_2' },
                ], (a) => H.accountStatus(a).label)} />
            </div>
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search name, description, account number…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 200 }}>
              <MultiSelect allLabel="Any type" value={typeFilter} onChange={setTypeFilter}
                options={TYPE_OPTIONS} />
            </div>
            <div style={{ minWidth: 160 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={[
                  { value: 'Open',     label: 'Open' },
                  { value: 'Closed',   label: 'Closed' },
                  { value: 'Archived', label: 'Archived' },
                ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Accounts per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        )}
        primary={{ label: 'New account', icon: 'add', onClick: () => setShowAdd(true) }}
      />

      <div className="acct-list">
        <InfiniteList
          items={sortedRows}
          batchSize={batch}
          itemKey={(a) => a.id}
          noun="accounts"
          revealKey={jumpId}
          renderItem={(a) => (
            <AccountListItem a={a}
              problem={problemFor(a)}
              highlight={jumpId === a.id}
              onJump={jumpTo}
              onDelete={deleteAccount}
              onNavigate={onNavigate} />
          )}
          empty={(
            <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
              No accounts match your filters.
            </div>
          )}
          trailing={(
            <AddRow
              title="New account"
              sub="Credit, debit, savings, loan, or investment."
              onClick={() => setShowAdd(true)}
            />
          )}
        />
      </div>

      {showAdd && <AddAccountModal onClose={() => setShowAdd(false)} onCreate={createAccount} />}
    </div>
  );
};

Object.assign(window, { Accounts, ACCOUNT_TYPE_LABEL, MultiSelect, DonutPanel, FilesTable, FILE_ICON, FILE_ICON_FALLBACK });
