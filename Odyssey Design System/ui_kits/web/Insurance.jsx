/* Insurance Policies — /insurance
   ----------------------------------------------------------------------------
   Sibling of Accounts / Budgets / Tax Statements: the same page-header +
   expandable-record scaffold (.acct-list / .acct-item), with an insurance-
   specific expanded detail. Each policy holds an ordered history of RENEWAL
   PERIODS (premium · coverage · validity window) plus documents at the policy
   AND renewal level. Coverage status (Active / ExpiringSoon / Lapsed / Upcoming
   / NoCoverage) and the "current renewal" are DERIVED, never stored — computed
   per the spec §5 ordered rules against one request "today".

   The renewal history follows the Terms pattern (Accounts → account row →
   Terms): a Current-period summary over a status'd, inline-editable history
   TABLE. The portfolio summary
   (counts by status · total current premium · total current coverage, multi-
   currency) rides in the header Overview region.

   Helpers + seed come from insurance-data.js; atoms from the DS bundle via
   Components.jsx. FilesTable comes from the Accounts.jsx bridge. */

const INS_H = window.OdysseyHelpers;
const INS_D = window.OdysseyData;

// Insurance carries the indigo (oklch hue 282) feature hue — distinct from brand
// tide and from Tax's magenta.
const INSURANCE_TONE = { bg: 'oklch(0.72 0.16 282 / 0.16)', fg: 'oklch(0.72 0.16 282)' };
const INS_SEV_RANK = { info: 0, warning: 1, error: 2 };

const INS_CURRENCY_OPTIONS = INS_D.currencies
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: `${c.code} · ${c.name}` }));

/* date 'YYYY-MM-DD' → "Jan 01, 2026" */
const insDate = (iso) => INS_H.dateLong(iso);
const insRange = (a, b) => `${insDate(a)} → ${insDate(b)}`;

/* The headline figure shown on the collapsed card: coverage end + days word. */
const insHeadline = (policy, today) => {
  const st = INS_H.insCoverageStatus(policy, today);
  const current = INS_H.insCurrentRenewal(policy, today);
  const renewals = policy.renewals || [];
  if (st.key === 'NoCoverage') return { value: null, word: 'No coverage yet', cls: '' };
  if (st.key === 'Archived') {
    const latest = renewals.slice().sort((a, b) => (a.toDate < b.toDate ? 1 : -1))[0];
    return { value: latest ? insDate(latest.toDate) : null, word: 'archived', cls: 'archived' };
  }
  if (current) {
    const days = INS_H.insDaysUntil(current.toDate, today);
    const word = days <= 0 ? 'expires today' : `expires in ${days} day${days === 1 ? '' : 's'}`;
    return { value: insDate(current.toDate), word, cls: st.key === 'ExpiringSoon' ? 'soon' : '' };
  }
  if (st.key === 'Upcoming') {
    const earliest = renewals.slice().sort((a, b) => (a.fromDate < b.fromDate ? -1 : 1))[0];
    const days = INS_H.insDaysUntil(earliest.fromDate, today);
    return { value: insDate(earliest.fromDate), word: `starts in ${days} day${days === 1 ? '' : 's'}`, cls: '' };
  }
  // Lapsed
  const latest = renewals.slice().sort((a, b) => (a.toDate < b.toDate ? 1 : -1))[0];
  const days = -INS_H.insDaysUntil(latest.toDate, today);
  return { value: insDate(latest.toDate), word: `expired ${days} day${days === 1 ? '' : 's'} ago`, cls: 'lapsed' };
};

/* ====================== Insurer / insured-account read chips ====================== */
const InsLinkChip = ({ icon, name, type, empty }) => {
  if (!name) return <span className="ins-link-chip empty"><MIcon name={icon} size={16} />{empty}</span>;
  return (
    <span className="ins-link-chip">
      <MIcon name={icon} size={16} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
      {type && <span className="ins-link-type">{type}</span>}
    </span>
  );
};

/* ====================== Files table (policy / renewal scoped) ====================== */
const InsFilesTable = ({ files, onDelete, empty }) => {
  const DSFilesTable = (window.OdysseyDesignSystem_d5aa51 || {}).FilesTable;
  const { useState } = React;
  const [edits, setEdits] = useState({});
  const rows = files.map(f => (edits[f.id] ? { ...f, ...edits[f.id] } : f));
  if (!DSFilesTable) return empty || null;
  return (
    <InlinePager items={rows}>
      {(pageRows) => (
        <DSFilesTable
          files={pageRows}
          typeFor={(f) => INS_H.policyFileTypeInfo(f.kind)}
          kinds={INS_D.policyFileTypes}
          formatDate={INS_H.dateLong}
          empty={empty}
          onSave={(id, patch) => setEdits(prev => ({ ...prev, [id]: { ...(prev[id] || {}), ...patch } }))}
          onDelete={onDelete}
          actions={(f) => [
            { icon: 'download', label: 'Download', onClick: () => INS_H.downloadFile(f) },
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(f.id); } },
          ]}
        />
      )}
    </InlinePager>
  );
};

/* ---- Renewal status vocabulary (mirrors the Terms In force / Scheduled /
   Superseded language): current = "In force", future = "Upcoming", else "Past". ---- */
const renewalKind = (r, currentId, today) => {
  if (r.id === currentId) return 'current';
  if (+new Date(r.fromDate) > +new Date(today)) return 'upcoming';
  return 'past';
};
const RenewalStatus = ({ kind }) => {
  if (kind === 'current') return <span className="ins-status current"><MIcon name="check_circle" size={12} />In force</span>;
  if (kind === 'upcoming') return <span className="ins-status upcoming">Upcoming</span>;
  return <span className="ins-status past">Past</span>;
};
const RenewalRowActions = ({ onEdit, onDelete }) => (
  <span className="ins-rowbtns">
    <button type="button" className="ins-iconbtn" aria-label="Edit renewal period" onClick={onEdit}><MIcon name="edit" size={17} /></button>
    <button type="button" className="ins-iconbtn danger" aria-label="Delete renewal period" onClick={onDelete}><MIcon name="delete" size={17} /></button>
  </span>
);

/* ====================== Current-period summary tiles ======================
   (DS InfoTile, mirrors the Terms "Current terms" tiles). */
const CurrentRenewalSummary = ({ r, today }) => {
  const now = today || INS_H.insToday();
  // Tense and tone follow the period's end against today.
  const lapsed = r.toDate < now;
  return (
    <div className="ins-tiles">
      <InfoTile icon="payments" label="Premium" className="tone-expense" value={INS_H.insMoney(r.premium, r.premiumCurrencyCode)} foot="for this period" />
      <InfoTile icon="shield" label="Coverage" value={INS_H.insMoney(r.coverageAmount, r.coverageCurrencyCode)} foot="insured sum" />
      {/* We know only when this period ends — whether it renews depends on terms
          we do not record, so the label never promises a renewal. */}
      <InfoTile icon="event" label={lapsed ? 'Period ended' : 'Period ends'} className={lapsed ? 'tone-expense' : undefined}
        value={insDate(r.toDate)} valueVariant="sm" foot={`${r.fromDate > now ? 'starts' : 'since'} ${insDate(r.fromDate)}`} />
    </div>
  );
};

/* ====================== Renewal history — TABLE (mirrors the Terms table) ====================== */
const RenewalTable = ({ renewals, currentId, today, onEdit, onDelete, onUploadRenewal, onDeleteRenewalFile }) => {
  const { useState } = React;
  const [openId, setOpenId] = useState(null);
  const sorted = renewals.slice().sort((a, b) => (a.fromDate < b.fromDate ? 1 : -1));
  return (
    <table className="ins-tbl">
      <thead>
        <tr>
          <th scope="col">Period</th>
          <th scope="col" className="num">Premium</th>
          <th scope="col" className="num">Coverage</th>
          <th scope="col">Status</th>
          <th scope="col" className="num">Docs</th>
          <th scope="col" className="act" aria-label="Actions"></th>
        </tr>
      </thead>
      <tbody>
        {sorted.map(r => {
          const kind = renewalKind(r, currentId, today);
          const docs = (r.files || []).length;
          const isOpen = openId === r.id;
          return (
            <React.Fragment key={r.id}>
              <tr className={`${kind === 'current' ? 'current' : ''} ${isOpen ? 'docs-open' : ''}`}>
                <td>
                  <div className="ins-row-period">
                    <MIcon name={kind === 'current' ? 'bolt' : 'event'} size={16} className={kind === 'current' ? 'cur' : ''} />
                    <div>
                      <div className="ins-row-range">{insRange(r.fromDate, r.toDate)}</div>
                      {r.notes && <div className="ins-row-note">{r.notes}</div>}
                    </div>
                  </div>
                </td>
                <td className="ins-cell-num">{INS_H.insMoney(r.premium, r.premiumCurrencyCode)}</td>
                <td className="ins-cell-num">{INS_H.insMoney(r.coverageAmount, r.coverageCurrencyCode)}</td>
                <td><RenewalStatus kind={kind} /></td>
                <td className="ins-cell-num">
                  <button type="button"
                    className={`ins-docchip ${isOpen ? 'on' : ''} ${docs ? '' : 'empty'}`}
                    aria-expanded={isOpen}
                    aria-label={`${docs} document${docs === 1 ? '' : 's'} on this period`}
                    onClick={() => setOpenId(isOpen ? null : r.id)}>
                    <MIcon name="description" size={15} />{docs}
                    <MIcon name="expand_more" size={15} className={`ins-docchip-chev ${isOpen ? 'open' : ''}`} />
                  </button>
                </td>
                <td className="ins-cell-act"><RenewalRowActions onEdit={() => onEdit(r)} onDelete={() => onDelete(r)} /></td>
              </tr>
              {isOpen && (
                <tr className="ins-docs-row">
                  <td colSpan={6}>
                    <div className="ins-docs-panel">
                      <div className="ins-docs-head">
                        <span className="ins-docs-title">Documents for {insRange(r.fromDate, r.toDate)}</span>
                        <Button variant="text" icon="upload_file" onClick={() => onUploadRenewal(r.id)}>Attach</Button>
                      </div>
                      <InsFilesTable
                        files={r.files || []}
                        onDelete={(f) => onDeleteRenewalFile(r.id, f)}
                        empty={<div className="empty-line" style={{ padding: 16 }}>No documents on this period yet — attach the invoice, schedule of cover, or a claim document.</div>}
                      />
                    </div>
                  </td>
                </tr>
              )}
            </React.Fragment>
          );
        })}
      </tbody>
    </table>
  );
};

/* ====================== Premium-over-time chart (the section's hero) ======================
   Mirrors the Terms hero (a chart leads the section). Plots each renewal period's
   premium by year, in the latest period's currency (off-currency periods convert
   where a rate exists). Shown only with ≥ 2 periods — a single point isn't a trend. */
const PremiumTrend = ({ renewals }) => {
  const sorted = renewals.slice().sort((a, b) => (a.fromDate < b.fromDate ? -1 : 1));
  if (sorted.length < 2 || !LineChart) return null;
  const cur = sorted[sorted.length - 1].premiumCurrencyCode;
  const series = sorted.map(r => ({
    label: String(new Date(r.fromDate).getFullYear()),
    value: (INS_H.insConvert(r.premium, r.premiumCurrencyCode, cur) ?? r.premium),
  }));
  return (
    <LineChart
      title="Premium"
      sub={`By renewal period · ${cur}`}
      series={series}
      color="var(--rec, var(--chart-4))"
      format={(n) => INS_H.insMoney(n, cur)}
      axisFormat={(n) => INS_H.insMoneyCompact(n, cur)}
      showDelta
      deltaSuffix={`vs ${series[0].label}`}
      ariaLabel="Premium by renewal period"
    />
  );
};

/* ====================== Renewal history wrapper — Terms-shaped ======================
   Mirrors AccountTerms: a Current-period summary, then the History as a status'd,
   inline-editable table with an "Add period" action. */
const RenewalHistory = ({ policy, today, onAddRenewal, onEditRenewal, onDeleteRenewal, onUploadRenewal, onDeleteRenewalFile }) => {
  const renewals = policy.renewals || [];
  const current = INS_H.insCurrentRenewal(policy, today);
  const currentId = current ? current.id : null;

  if (!renewals.length) {
    return (
      <div className="ins-empty-cover">
        <MIcon name="event_busy" size={20} />
        <div style={{ flex: 1 }}>No renewal periods yet — add the first period to record premium, coverage and validity dates.</div>
      </div>
    );
  }

  return (
    <div className="ins-terms">
      <PremiumTrend renewals={renewals} />
      {/* The section's own SectionDivider already labels this and carries the
          period count — no second "History" header inside it. */}
      <div className="ins-tbl-frame">
        <RenewalTable renewals={renewals} currentId={currentId} today={today} onEdit={onEditRenewal} onDelete={onDeleteRenewal}
          onUploadRenewal={onUploadRenewal} onDeleteRenewalFile={onDeleteRenewalFile} />
      </div>
    </div>
  );
};

/* ====================== Expanded detail ====================== */
const PolicyDetail = ({ policy, today, focusDocs, onNavigate, setPolicy, onAddRenewal, onEditRenewal, onDeleteRenewal, onUpload, onUploadRenewal }) => {
  const insurer = INS_H.insInsurer(policy);
  const insured = INS_H.insInsuredAccount(policy);
  const typeInfo = INS_H.insurancePolicyTypeInfo(policy.type);
  const current = INS_H.insCurrentRenewal(policy, today);
  const removeFile = (f) => setPolicy(prev => ({ ...prev, files: prev.files.filter(x => x.id !== f.id) }));
  const removeRenewalFile = (rid, f) => setPolicy(prev => ({ ...prev, renewals: prev.renewals.map(r => (r.id === rid ? { ...r, files: (r.files || []).filter(x => x.id !== f.id) } : r)) }));

  // Referenced records keep their own type mark (icon + colour) — they point
  // elsewhere, so they read as that record rather than as part of this one.
  const insuredMeta = (insured && INS_D.accountTypeById[insured.type]) || {};
  const insurerMeta = (insurer && INS_D.contactTypeByKey[insurer.type]) || {};
  const insuredTypeLabel = insuredMeta.label;
  // Coverage status is derived (archived → upcoming → lapsed → expiring → active
  // → no coverage), so its tile carries the state and the date it began, tinted
  // like the header chip — the Subscriptions status tile pattern.
  const st = INS_H.insCoverageStatus(policy, today);
  const stMeta = INS_H.insCoverageStatusMeta(st.key);
  const stTone = stMeta.tone === 'income' ? 'tone-income' : stMeta.tone === 'expense' ? 'tone-expense'
    : stMeta.tone === 'pending' ? 'tone-pending' : stMeta.tone === 'info' ? 'tone-info' : 'tone-muted';
  // The foot names the period the STATUS refers to — which is only `current`
  // while cover is in force. A lapsed policy points at its most recent period,
  // an upcoming one at its earliest future period.
  const allPeriods = policy.renewals || [];
  const lastEnded = allPeriods.filter(r => r.toDate < today).sort((a, b) => (a.toDate < b.toDate ? 1 : -1))[0];
  const nextStart = allPeriods.filter(r => r.fromDate > today).sort((a, b) => (a.fromDate < b.fromDate ? -1 : 1))[0];
  const stFoot = st.key === 'Archived' ? `since ${INS_H.dateTime(policy.archived)}`
    : (st.key === 'Active' || st.key === 'ExpiringSoon') && current ? `this period ends ${insDate(current.toDate)}`
    : st.key === 'Upcoming' && nextStart ? `starts ${insDate(nextStart.fromDate)}`
    : st.key === 'Lapsed' && lastEnded ? `ended ${insDate(lastEnded.toDate)}`
    : 'no renewal period on record';
  // Total premium accrued through the current period (current + all past), in the
  // current period's currency — a policy-level fact, shown beside the other tiles.
  const accrued = current ? (policy.renewals || []).filter(x => INS_H.insDateOnly(x.fromDate) <= INS_H.insDateOnly(current.toDate)) : [];
  const accruedCur = current && current.premiumCurrencyCode;
  const accruedTotal = accrued.reduce((s, x) => s + (INS_H.insConvert(x.premium, x.premiumCurrencyCode, accruedCur) ?? x.premium), 0);

  // The card's DETAILS slot: the policy's full field set (rule 3), each tile
  // rendering on its own field (rule 5). Empty fields drop out — except the
  // insurer, where the absence is the fact: a policy without one is broken.
  const details = (
    <InfoTileGrid>
      <InfoTile icon="shield" label="Name" value={policy.name} valueVariant="text" className="wrapvalue" />
      {policy.policyNumber ? <InfoTile icon="tag" label="Policy number" value={policy.policyNumber} valueVariant="mono" foot="Insurer reference" /> : null}
      <InfoTile icon={typeInfo.icon} label="Type" value={typeInfo.label} valueVariant="text" />
      <InfoTile icon={insurer ? (insurerMeta.icon || 'groups') : 'apartment'}
        iconColor={insurer ? insurerMeta.color : undefined} iconSoft={insurer ? insurerMeta.soft : undefined}
        label="Insurer" valueVariant="text"
        value={insurer ? insurer.name : 'Not set'} foot={insurer ? (insurerMeta.label || insurer.type) : 'Required'} />
      {insured ? <InfoTile icon={insuredMeta.icon || 'account_balance_wallet'}
        iconColor={insuredMeta.color} iconSoft={insuredMeta.soft}
        label="Insured account" valueVariant="text"
        value={insured.name} foot={insuredTypeLabel} /> : null}
      <InfoTile icon={stMeta.icon} label="Status" valueVariant="text" className={stTone}
        value={stMeta.label} foot={stFoot} />
      {current ? <InfoTile icon="savings" label="Total premium" value={INS_H.insMoney(accruedTotal, accruedCur)} valueVariant="mono" foot={`${accrued.length} period${accrued.length === 1 ? '' : 's'} to date`} /> : null}
    </InfoTileGrid>
  );
  const content = policy.notes ? (
    <InfoTileGrid><InfoTile icon="sticky_note_2" label="Notes" value={policy.notes} wide /></InfoTileGrid>
  ) : null;

  return (
    <React.Fragment>
      {details}
      {content}

      {/* CURRENT PERIOD — the current-state section, first among the sections */}
      {current && (
        <div className="ins-current">
          <SectionDivider label="Current period" meta={`in force · ${insDate(today)}`} />
          <CurrentRenewalSummary r={current} today={today} />
        </div>
      )}

      {/* RENEWAL HISTORY — a plain section: its "New period" action lives in the
          row action menu, so the section header carries the label and count only. */}
      <div className="ins-section">
        <SectionDivider label="Renewal history" meta={`${(policy.renewals || []).length} period${(policy.renewals || []).length === 1 ? '' : 's'}`} />
        <RenewalHistory policy={policy} today={today}
          onAddRenewal={onAddRenewal} onEditRenewal={onEditRenewal} onDeleteRenewal={onDeleteRenewal}
          onUploadRenewal={onUploadRenewal} onDeleteRenewalFile={removeRenewalFile} />
      </div>

      {/* POLICY DOCUMENTS — last section, same treatment ("Attach document" is
          likewise in the row action menu). */}
      <div className="ins-section">
        <SectionDivider label="Policy documents" meta={`${(policy.files || []).length} file${(policy.files || []).length === 1 ? '' : 's'}`} />
        <div className="ins-tbl-frame">
          <InsFilesTable
            files={policy.files || []}
            onDelete={removeFile}
            empty={<div className="empty-line" style={{ padding: 20 }}>No policy-level documents yet — attach the certificate, policy wording, or schedule.</div>}
          />
        </div>
      </div>
    </React.Fragment>
  );
};

/* ====================== One policy list item ====================== */
const PolicyListItem = ({ pol, today, open: openProp, onToggle, highlight, onNavigate, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  const [p, setP] = useState(pol);
  // Open state lives in the list — opening a policy closes its siblings.
  const open = !!openProp;
  const setOpen = (next) => onToggle(typeof next === 'function' ? next(open) : next);
  const [showEdit, setShowEdit] = useState(false);
  const [focusDocs, setFocusDocs] = useState(false);
  const [modal, setModal] = useState(null); // { kind:'renewal'|'upload', renewal? }
  const cardRef = useRef(null);

  const typeInfo = INS_H.insurancePolicyTypeInfo(p.type);
  const st = INS_H.insCoverageStatus(p, today);
  const meta = INS_H.insCoverageStatusMeta(st.key);
  const insurer = INS_H.insInsurer(p);
  const headline = insHeadline(p, today);
  const dimmed = !!p.archived;

  const saveEdit = (draft) => {
    setP(prev => ({ ...prev, name: draft.name.trim() || prev.name, policyNumber: draft.policyNumber ? draft.policyNumber.trim() : null,
      type: draft.type, insurerId: draft.insurerId, insuredAccountId: draft.insuredAccountId || null, notes: draft.notes }));
    setShowEdit(false);
  };
  const addRenewal = (dto, id) => {
    setP(prev => {
      const renewals = id
        ? prev.renewals.map(r => (r.id === id ? { ...r, ...dto } : r))
        : [{ id: `rn-new-${Date.now()}`, files: [], createdAtUtc: new Date().toISOString(), ...dto }, ...prev.renewals];
      return { ...prev, renewals };
    });
    setModal(null);
  };
  const deleteRenewal = (r) => setP(prev => ({ ...prev, renewals: prev.renewals.filter(x => x.id !== r.id) }));
  const handleUpload = (newFiles, target) => {
    setP(prev => {
      if (target && target !== 'policy') {
        return { ...prev, renewals: prev.renewals.map(r => (r.id === target ? { ...r, files: [...(r.files || []), ...newFiles] } : r)) };
      }
      return { ...prev, files: [...(prev.files || []), ...newFiles] };
    });
    setModal(null); setOpen(true);
    if (!target || target === 'policy') setFocusDocs(true);
  };

  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    if (!open) setOpen(true);
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
      }
    });
  }, [highlight]);

  return (
    <div ref={cardRef}>
      <RecordCard
        icon={typeInfo.icon}
        accent={typeInfo.color}
        accentSoft={typeInfo.soft}
        name={p.name}
        chips={<CoverageStatusChip status={st.key} />}
        meta={[
          typeInfo.label,
          <span className="ins-sub-insurer"><MIcon name={insurer ? ((INS_D.contactTypeByKey[insurer.type] || {}).icon || 'groups') : 'apartment'} size={14} /><span>{insurer ? insurer.name : 'No insurer'}</span></span>,
          p.policyNumber ? <span className="mono"><MIcon name="tag" size={14} /><span>{p.policyNumber}</span></span> : null,
        ]}
        counts={[
          { icon: 'event_repeat', value: (p.renewals || []).length, label: 'Renewal periods' },
          { icon: 'description', value: INS_H.insFileCount(p), label: 'Documents' },
        ]}
        figure={{
          value: headline.value || 'No coverage',
          caption: headline.word,
          tone: headline.cls === 'lapsed' ? 'expense' : headline.cls === 'soon' ? 'pending' : undefined,
        }}
        dimmed={dimmed}
        highlight={highlight}
        open={open}
        onToggle={setOpen}
        actions={<ActionMenu items={[
          { icon: 'edit', label: 'Edit policy', onClick: () => setShowEdit(true) },
          { icon: 'event_repeat', label: 'New renewal period', onClick: () => { setOpen(true); setModal({ kind: 'renewal' }); } },
          { icon: 'upload_file', label: 'Attach document', onClick: () => { setOpen(true); setModal({ kind: 'upload' }); } },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(p.id); } },
          { divider: true },
          { icon: p.archived ? 'unarchive' : 'archive', label: p.archived ? 'Unarchive' : 'Archive', onClick: () => setP(prev => ({ ...prev, archived: prev.archived ? null : new Date().toISOString() })) },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(p.id) },
        ]} />}
      >
        <PolicyDetail policy={p} today={today} focusDocs={focusDocs}
          onNavigate={onNavigate} setPolicy={setP}
          onAddRenewal={() => setModal({ kind: 'renewal' })}
          onEditRenewal={(r) => setModal({ kind: 'renewal', renewal: r })}
          onDeleteRenewal={deleteRenewal}
          onUpload={() => setModal({ kind: 'upload' })}
          onUploadRenewal={(rid) => setModal({ kind: 'upload', renewalId: rid })} />
      </RecordCard>
      {showEdit && <AddInsurancePolicyModal policy={p} onClose={() => setShowEdit(false)} onSave={saveEdit} />}

      {modal && modal.kind === 'renewal' && (
        <AddRenewalModal policy={p} renewal={modal.renewal} onClose={() => setModal(null)} onSave={addRenewal} />
      )}
      {modal && modal.kind === 'upload' && (
        <InsuranceUploadModal policy={p} initialTarget={modal.renewalId || 'policy'} onClose={() => setModal(null)} onUpload={handleUpload} />
      )}
    </div>
  );
};

/* ====================== Portfolio summary (header Overview) ====================== */
const InsuranceSummary = ({ policies, today, baseCurrency }) => {
  const s = INS_H.insPortfolioSummary(policies, today, baseCurrency);
  const order = ['Active', 'ExpiringSoon', 'Lapsed', 'Upcoming', 'NoCoverage', 'Archived'];
  // By-type + by-status distribution rows for the two BreakdownTile instances.
  // Status tones map to the same finance accents the coverage chips use.
  const TONE_COLOR = { income: 'var(--finance-income)', pending: 'var(--finance-pending)', expense: 'var(--finance-expense)', info: 'var(--sea-400)', outline: 'var(--mud-palette-text-secondary)' };
  const typeRows = s.typeRows.map(r => ({ key: r.key, icon: r.icon, iconColor: r.color, label: r.label, count: r.count }));
  const statusRows = order.map(k => {
    const m = INS_H.insCoverageStatusMeta(k);
    return { key: k, icon: m.icon, iconColor: TONE_COLOR[m.tone] || TONE_COLOR.outline, label: m.label, count: s.countsByStatus[k] || 0 };
  });

  return (
    <div className="ins-summary">
      <div className="ins-stats">
        <div className="ins-stat">
          <span className="ins-stat-ov">Current premium</span>
          <div className="ins-stat-rows">
            {s.premiumByCurrency.length ? s.premiumByCurrency.map(r => (
              <div className="ins-stat-row" key={r.currencyCode}><span>{r.currencyCode}</span><span className="amt">{INS_H.insMoney(r.amount, r.currencyCode)}</span></div>
            )) : <div className="ins-stat-row"><span className="amt">—</span></div>}
          </div>
          {s.convertedTotalPremium != null && (
            <span className="ins-conv">≈ <span className="b">{INS_H.insMoney(s.convertedTotalPremium, s.baseCurrency)}</span> total / year</span>
          )}
        </div>
        <div className="ins-stat">
          <span className="ins-stat-ov">Current coverage</span>
          <div className="ins-stat-rows">
            {s.coverageByCurrency.length ? s.coverageByCurrency.map(r => (
              <div className="ins-stat-row" key={r.currencyCode}><span>{r.currencyCode}</span><span className="amt">{INS_H.insMoneyCompact(r.amount, r.currencyCode)}</span></div>
            )) : <div className="ins-stat-row"><span className="amt">—</span></div>}
          </div>
          {s.convertedTotalCoverage != null && (
            <span className="ins-conv">≈ <span className="b">{INS_H.insMoneyCompact(s.convertedTotalCoverage, s.baseCurrency)}</span> insured</span>
          )}
          {s.unconvertedCurrencies.length > 0 && (
            <span className="ins-conv ins-unconv"><MIcon name="info" size={14} />{s.unconvertedCurrencies.join(', ')} excluded — no rate</span>
          )}
        </div>
        <BreakdownTile label="By type" rows={typeRows} empty="No active policies." />
        <BreakdownTile label="By status" rows={statusRows} empty="No policies." />
      </div>
    </div>
  );
};

/* ====================== Page ====================== */
const Insurance = ({ tweaks = {}, onNavigate }) => {
  const { useState } = React;
  // One card open at a time — the list owns it.
  const [openId, setOpenId] = useState('ip-home');
  const baseCurrency = tweaks.summaryBaseCurrency === '' ? null : (tweaks.summaryBaseCurrency || 'USD');
  const today = INS_H.insToday();

  const [q, setQ] = useState('');
  const [typeFilter, setTypeFilter] = useState([]);
  const [statusFilter, setStatusFilter] = useState([]);
  const [showAdd, setShowAdd] = useState(false);
  const [policies, setPolicies] = useState(INS_D.insurancePolicies);
  const [jumpId, setJumpId] = useState(null);
  // Shared sort (§6.9): Name A→Z default; toolbar is the sole sort surface.
  const [sort, setSort] = useState({ key: 'name', dir: 'asc' });
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  // §6.9 curated fields — one list feeds the SortSelect AND the ordering.
  // Renewal end = the CURRENT renewal's end (falls back to the latest end on
  // record); Premium = current renewal premium, raw amount — no FX (§14).
  const insRenewalEnd = (p) => {
    const cur = INS_H.insCurrentRenewal(p, today);
    if (cur) return cur.toDate;
    const ends = (p.renewals || []).map(r => r.toDate).sort();
    return ends.length ? ends[ends.length - 1] : null;
  };
  const sortFields = [
    { key: 'name',       label: 'Name',             type: 'text',   sortValue: (p) => (p.name || '').toLowerCase() },
    { key: 'type',       label: 'Type',             type: 'status', sortValue: (p) => { const i = INS_D.insurancePolicyTypes.findIndex(t => t.key === p.type); return i < 0 ? INS_D.insurancePolicyTypes.length : i; } },
    { key: 'renewalEnd', label: 'Renewal end date', type: 'date',   sortValue: insRenewalEnd },
    { key: 'premium',    label: 'Premium',          type: 'number', sortValue: (p) => { const cur = INS_H.insCurrentRenewal(p, today); return cur ? cur.premium : null; } },
  ];

  const jumpTo = (id) => {
    setJumpId(null);
    requestAnimationFrame(() => setJumpId(id));
    setTimeout(() => setJumpId(curr => (curr === id ? null : curr)), 2200);
  };

  const createPolicy = (draft) => { setPolicies(prev => [draft, ...prev]); setShowAdd(false); };
  const deletePolicy = (id) => setPolicies(prev => prev.filter(p => p.id !== id));

  const rows = policies.filter(p => {
    const st = INS_H.insCoverageStatus(p, today).key;
    if (typeFilter.length && !typeFilter.includes(p.type)) return false;
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (q) {
      const insurer = INS_H.insInsurer(p);
      const needle = q.toLowerCase();
      const hay = `${p.name} ${p.policyNumber || ''} ${INS_H.insurancePolicyTypeInfo(p.type).label} ${insurer ? insurer.name : ''} ${p.notes || ''}`.toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });

  const active = policies.filter(p => !p.archived);
  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (p) => p.id) : rows;

  // Header signal: roll up policies needing attention (ExpiringSoon → warning,
  // Lapsed → error) so the renewal cliff is never missed.
  const flagged = active.map(p => ({ p, st: INS_H.insCoverageStatus(p, today).key }))
    .filter(x => x.st === 'ExpiringSoon' || x.st === 'Lapsed')
    .map(x => ({ ...x, sev: x.st === 'Lapsed' ? 'error' : 'warning' }));
  const topSeverity = flagged.reduce((sev, x) => (INS_SEV_RANK[x.sev] > INS_SEV_RANK[sev] ? x.sev : sev), 'info');
  const signal = flagged.length ? {
    severity: topSeverity,
    count: flagged.length,
    label: 'Renewals',
    region: (
      <div className="signal-panel">
        {flagged.map(({ p, st, sev }) => {
          const hl = insHeadline(p, today);
          return (
            <div key={p.id} className={`alert ${sev} compact signal-row`} role="button" tabIndex={0}
              onClick={() => jumpTo(p.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(p.id); } }}>
              <SeverityIcon severity={sev} size={18} className="alert-icon" />
              <div className="alert-body"><strong>{p.name}.</strong> {st === 'Lapsed' ? 'Coverage has lapsed' : 'Coverage expiring soon'} — {hl.word}.</div>
              <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo(p.id); }}>View →</button>
            </div>
          );
        })}
      </div>
    ),
  } : undefined;

  return (
    <div className="col gap-6">
      <PageHeader
        title="Insurance"
        icon="shield"
        sub={`${active.length} polic${active.length === 1 ? 'y' : 'ies'} on file`}
        signal={signal}
        overview={<InsuranceSummary policies={policies} today={today} baseCurrency={baseCurrency} />}
        overviewDefaultOpen
        searchDefaultOpen
        search={
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search name, number, insurer, type, notes…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any type" value={typeFilter} onChange={setTypeFilter}
                options={INS_D.insurancePolicyTypes.map(t => ({ value: t.key, label: t.label }))} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={['Active', 'ExpiringSoon', 'Lapsed', 'Upcoming', 'NoCoverage', 'Archived'].map(k => ({ value: k, label: INS_H.insCoverageStatusMeta(k).label }))} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Policies per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        }
        primary={{ label: 'New policy', icon: 'add', onClick: () => setShowAdd(true) }}
      />

      {policies.length === 0 ? (
        <EmptyState
          icon="shield"
          title="No insurance policies yet"
          description="Add a policy to track its insurer, renewal periods, premium and coverage, and keep every supporting document in one place."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setShowAdd(true)}>New policy</Button>}
        />
      ) : (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(p) => p.id}
            noun="policies"
            revealKey={jumpId}
            renderItem={(p) => (
              <PolicyListItem pol={p} today={today}
                open={openId === p.id}
                onToggle={(o) => setOpenId(o ? p.id : null)}
                highlight={jumpId === p.id}
                onNavigate={onNavigate} onDelete={deletePolicy} />
            )}
            empty={(
              <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>No policies match your filters.</div>
            )}
            trailing={(
              <AddRow title="New policy" sub="Record an insurer, renewal periods, premium and coverage, and attach the documents."
                onClick={() => setShowAdd(true)} />
            )}
          />
        </div>
      )}

      {showAdd && <AddInsurancePolicyModal onClose={() => setShowAdd(false)} onCreate={createPolicy} />}
    </div>
  );
};

Object.assign(window, { Insurance });
