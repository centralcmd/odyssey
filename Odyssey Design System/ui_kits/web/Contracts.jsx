/* Contracts — /contracts
   ----------------------------------------------------------------------------
   Sibling of Accounts / Insurance / Tax Statements: the same page-header +
   expandable-record scaffold (.acct-list / .acct-item), with a contract-
   specific expanded detail. Each contract is a single agreement with a name,
   a type, an active period, a description, the PARTIES it relates to (an
   account, an institution/contact, or an insurance policy — one-of-three),
   and the DOCUMENTS that evidence it (references to existing library files).

   Status (Upcoming / Active / Expired / Archived) is DERIVED, never stored —
   computed per request (spec §6) from StartDate / EndDate / Archived. Navigation
   is expand-in-place (no /{id} deep-link, per frontend B1). Archive is a
   reversible field on the edit form (a normal update — there is no dedicated
   archive action), distinct from the irreversible hard delete.

   Helpers + seed come from contracts-data.js; atoms from the DS bundle via
   Components.jsx. FilesTable comes from the DS bundle. */

const CON_H = window.OdysseyHelpers;
const CON_D = window.OdysseyData;
const CON_SEV_RANK = { info: 0, warning: 1, error: 2 };

/* ====================== Derived-status chip ====================== */
const ContractStatusChip = ({ status }) => {
  const meta = CON_H.conStatusMeta(status);
  return <Chip tone={meta.tone} dot={meta.dot}>{meta.label}</Chip>;
};

/* ====================== Files table (contract-scoped) ====================== */
const ContractFilesTable = ({ files, onDelete, empty }) => {
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
          typeFor={(f) => CON_H.contractFileTypeInfo(f.kind)}
          kinds={CON_D.contractFileTypes}
          formatDate={CON_H.conDate}
          empty={empty}
          onSave={(id, patch) => setEdits(prev => ({ ...prev, [id]: { ...(prev[id] || {}), ...patch } }))}
          onDelete={onDelete}
          actions={(f) => [
            { icon: 'download', label: 'Download', onClick: () => CON_H.downloadFile && CON_H.downloadFile(f) },
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(f.id); } },
          ]}
        />
      )}
    </InlinePager>
  );
};

/* ====================== One party row ====================== */
const PartyRow = ({ party, onDetach }) => {
  const r = CON_H.conResolveParty(party);
  return (
    <div className="con-party">
      <span className="con-party-av" style={{ background: r.soft || 'var(--mud-palette-action-default-hover)', color: r.color || 'var(--mud-palette-text-secondary)' }}>
        <MIcon name={r.icon} size={20} />
      </span>
      <div className="con-party-main">
        <span className="con-party-name">{r.name}</span>
        <span className="con-party-meta">
          <span className="con-party-kind"><MIcon name={r.icon} size={13} />{r.kindLabel}</span>
          {r.typeLabel && <React.Fragment><span className="con-party-dot">·</span><span>{r.typeLabel}</span></React.Fragment>}
        </span>
      </div>
      <span className="con-party-detach" title={`Detach ${r.name}`}>
        <IconButton icon="link_off" label={`Detach ${r.name}`} onClick={() => onDetach(party)} />
      </span>
    </div>
  );
};

/* ====================== Expanded detail ====================== */
const ContractDetail = ({ contract, today, focusDocs, setContract, onAddParty, onAttach }) => {
  const typeInfo = CON_H.contractTypeInfo(contract.type);
  const parties = contract.parties || [];
  const files = contract.files || [];
  const fileRows = files.map(CON_H.conFileRow);

  const detachParty = (party) => setContract(prev => ({ ...prev, parties: prev.parties.filter(p => p.id !== party.id) }));
  const removeFile = (row) => setContract(prev => ({ ...prev, files: prev.files.filter(f => f.id !== row.id) }));

  return (
    <div className="acct-detail">
      <div className="con-tiles">
        <InfoTile icon={typeInfo.icon} iconColor={typeInfo.color} iconSoft={typeInfo.soft} label="Type" value={typeInfo.label} valueVariant="text" />
        {contract.completionDate ? (
          <InfoTile icon="event_available" label="Completion" value={CON_H.conDate(contract.completionDate)} valueVariant="sm" foot="One-off" />
        ) : (
          <React.Fragment>
            <InfoTile icon="play_circle" label="Starts" value={contract.startDate ? CON_H.conDate(contract.startDate) : '—'} valueVariant="sm" />
            <InfoTile icon="event_busy" label="Ends" value={contract.endDate ? CON_H.conDate(contract.endDate) : 'Open-ended'} valueVariant="sm" foot={contract.endDate ? null : 'No end date'} />
          </React.Fragment>
        )}
        {contract.description ? <InfoTile icon="sticky_note_2" label="Description" value={contract.description} wide /> : null}
      </div>

      {/* PARTIES */}
      <Collapsible icon="diversity_3" title="Parties" count={parties.length} defaultOpen
        action={<Button variant="text" color="primary" icon="add" onClick={(e) => { e.stopPropagation(); onAddParty(); }}>Add party</Button>}>
        {parties.length ? (
          <div className="con-parties">
            {parties.map(p => <PartyRow key={p.id} party={p} onDetach={detachParty} />)}
          </div>
        ) : (
          <div className="con-empty-line">
            <MIcon name="diversity_3" size={20} />
            <div style={{ flex: 1 }}>No parties yet — link the account, institution, or insurance policy this contract relates to.</div>
          </div>
        )}
      </Collapsible>

      {/* DOCUMENTS */}
      <Collapsible key={focusDocs ? 'docs-open' : 'docs'} icon="folder" title="Documents" count={files.length} defaultOpen={!!focusDocs}
        action={<Button variant="text" icon="upload_file" onClick={(e) => { e.stopPropagation(); onAttach(); }}>Upload</Button>}>
        <div className="con-files">
          <ContractFilesTable
            files={fileRows}
            onDelete={removeFile}
            empty={<div className="con-empty-line"><MIcon name="folder_open" size={20} /><div style={{ flex: 1 }}>No documents yet — upload the signed agreement, an amendment, or correspondence.</div></div>}
          />
        </div>
      </Collapsible>
    </div>
  );
};

/* ====================== One contract list item ====================== */
const ContractListItem = ({ row, today, endingWindow, defaultOpen, highlight, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  const [c, setC] = useState(row);
  const [open, setOpen] = useState(!!defaultOpen);
  const [showEdit, setShowEdit] = useState(false);
  const [focusDocs, setFocusDocs] = useState(false);
  const [modal, setModal] = useState(null); // 'party' | 'file'
  const cardRef = useRef(null);

  const typeInfo = CON_H.contractTypeInfo(c.type);
  const status = CON_H.conStatus(c, today);
  const headline = CON_H.conHeadline(c, today, endingWindow);
  const parties = c.parties || [];
  const files = c.files || [];
  const institution = parties.map(CON_H.conResolveParty).find(r => r.kind === 'contact');
  const dimmed = !!c.archived;

  const saveEdit = (draft) => {
    setC(prev => ({
      ...prev,
      name: draft.name.trim() || prev.name,
      type: draft.type,
      description: draft.description.trim() || null,
      startDate: draft.mode === 'oneoff' ? null : (draft.startDate || null),
      endDate: draft.mode === 'oneoff' ? null : (draft.endDate || null),
      completionDate: draft.mode === 'oneoff' ? draft.completionDate : null,
    }));
    setShowEdit(false);
  };
  const addParty = (party) => { setC(prev => ({ ...prev, parties: [...(prev.parties || []), party] })); setModal(null); setOpen(true); };
  const attachFile = (filesToAdd) => { const arr = Array.isArray(filesToAdd) ? filesToAdd : [filesToAdd]; setC(prev => ({ ...prev, files: [...(prev.files || []), ...arr] })); setModal(null); setOpen(true); setFocusDocs(true); };
  const toggleArchive = () => setC(prev => ({ ...prev, archived: prev.archived ? null : new Date().toISOString() }));

  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    setOpen(true);
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
    <Card className={`acct-item ${open ? 'open' : ''} ${dimmed ? 'dimmed' : ''} ${highlight ? 'flash' : ''}`} ref={cardRef}>
      <div className="acct-head" onClick={() => setOpen(o => !o)}>
        <Avatar icon={typeInfo.icon} tone={{ bg: typeInfo.soft, fg: typeInfo.color }} square size="lg" />

        <div className="acct-id">
          <div className="acct-name-row">
            <span className="acct-name">{c.name}</span>
            <ContractStatusChip status={status} />
          </div>
          <div className="acct-tags">
            <span>{typeInfo.label}</span>
            <span className="acct-dot">·</span>
            <span className="con-sub-inst"><MIcon name="account_balance" size={14} />{institution ? institution.name : 'No institution'}</span>
            <span className="acct-dot">·</span>
            <span className="acct-counts">
              <span><MIcon name="diversity_3" size={14} />{parties.length}</span>
              <span><MIcon name="description" size={14} />{files.length}</span>
            </span>
          </div>
        </div>

        <div className="acct-figures">
          <div className={`con-headline ${headline.value === 'Open-ended' ? 'none' : ''}`}>{headline.value}</div>
          <div className={`con-headline-word ${headline.cls}`}>{headline.word}</div>
        </div>

        <div className="acct-controls" onClick={(e) => e.stopPropagation()}>
          <ActionMenu items={[
            { icon: 'edit', label: 'Edit contract', onClick: () => setShowEdit(true) },
            { icon: 'group_add', label: 'Add party', onClick: () => { setOpen(true); setModal('party'); } },
            { icon: 'attach_file', label: 'Upload document', onClick: () => { setOpen(true); setModal('file'); } },
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(c.id); } },
            { divider: true },
            { icon: c.archived ? 'unarchive' : 'inventory_2', label: c.archived ? 'Restore' : 'Archive', onClick: toggleArchive },
            { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(c.id) },
          ]} />
          <button className="acct-expand" onClick={() => setOpen(o => !o)} aria-label="Expand">
            <MIcon name="expand_more" size={22} className={`chev ${open ? 'open' : ''}`} />
          </button>
        </div>
      </div>

      {open && (
        <ContractDetail contract={c} today={today} focusDocs={focusDocs} setContract={setC}
          onAddParty={() => setModal('party')} onAttach={() => setModal('file')} />
      )}
      {showEdit && <AddContractModal contract={c} onClose={() => setShowEdit(false)} onSave={saveEdit} />}

      {modal === 'party' && <AddContractPartyModal contract={c} onClose={() => setModal(null)} onAdd={addParty} />}
      {modal === 'file' && <AddContractFileModal contract={c} onClose={() => setModal(null)} onAttach={attachFile} />}
    </Card>
  );
};

/* ====================== Summary (header Overview) ====================== */
const ContractsSummary = ({ contracts, today }) => {
  const s = CON_H.conSummary(contracts, today);
  const order = ['Active', 'Upcoming', 'Expired', 'Archived'];
  // Distribution rows for the two BreakdownTile instances. Status tones map to
  // the same finance accents the pills / chips use — no new hue enters.
  const TONE_COLOR = { income: 'var(--finance-income)', info: 'var(--sea-400)', expense: 'var(--finance-expense)', outline: 'var(--mud-palette-text-secondary)', pending: 'var(--finance-pending)' };
  const typeRows = s.typeRows.map(r => ({ key: r.key, icon: r.icon, iconColor: r.color, label: r.label, count: r.count }));
  const statusRows = order.map(k => {
    const m = CON_H.conStatusMeta(k);
    return { key: k, icon: m.icon, iconColor: TONE_COLOR[m.tone] || TONE_COLOR.outline, label: m.label, count: s.countsByStatus[k] || 0 };
  });
  return (
    <div className="con-summary">
      <div className="con-stats">
        <BreakdownTile label="By type" rows={typeRows} empty="No active contracts." />
        <BreakdownTile label="By status" rows={statusRows} empty="No contracts." />
      </div>
    </div>
  );
};

/* ====================== Page ====================== */
const Contracts = ({ tweaks = {}, onNavigate }) => {
  const { useState } = React;
  const today = CON_H.conToday();
  const endingWindow = tweaks.endingWindowDays != null ? tweaks.endingWindowDays : CON_D.CONTRACTS_ENDING_WINDOW_DAYS;

  const [q, setQ] = useState('');
  const [typeFilter, setTypeFilter] = useState([]);
  const [statusFilter, setStatusFilter] = useState([]);
  const [showAdd, setShowAdd] = useState(false);
  const [contracts, setContracts] = useState(CON_D.contracts);
  const [jumpId, setJumpId] = useState(null);
  // Shared sort (§6.8): Name A→Z default; toolbar is the sole sort surface.
  const [sort, setSort] = useState({ key: 'name', dir: 'asc' });
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  // §6.8 curated fields — one list feeds the SortSelect AND the ordering.
  // Type/Status sort by the registry / lifecycle declared order, not label.
  const CON_STATUS_ORDER = ['Upcoming', 'Active', 'Expired', 'Archived'];
  const sortFields = [
    { key: 'name',      label: 'Name',       type: 'text',   sortValue: (c) => (c.name || '').toLowerCase() },
    { key: 'startDate', label: 'Start date', type: 'date',   sortValue: (c) => c.startDate || null },
    { key: 'endDate',   label: 'End date',   type: 'date',   sortValue: (c) => c.endDate || null },
    { key: 'type',      label: 'Type',       type: 'status', sortValue: (c) => { const i = CON_D.contractTypes.findIndex(t => t.key === c.type); return i < 0 ? CON_D.contractTypes.length : i; } },
    { key: 'status',    label: 'Status',     type: 'status', sortValue: (c) => { const i = CON_STATUS_ORDER.indexOf(CON_H.conStatus(c, today)); return i < 0 ? CON_STATUS_ORDER.length : i; } },
  ];

  const jumpTo = (id) => {
    setJumpId(null);
    requestAnimationFrame(() => setJumpId(id));
    setTimeout(() => setJumpId(curr => (curr === id ? null : curr)), 2200);
  };

  const createContract = (draft) => { setContracts(prev => [draft, ...prev]); setShowAdd(false); };
  const deleteContract = (id) => setContracts(prev => prev.filter(c => c.id !== id));

  const rows = contracts.filter(c => {
    const st = CON_H.conStatus(c, today);
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (typeFilter.length && !typeFilter.includes(c.type)) return false;
    if (q) {
      const needle = q.toLowerCase();
      const partyNames = (c.parties || []).map(p => CON_H.conResolveParty(p).name).join(' ');
      const hay = `${c.name} ${CON_H.contractTypeInfo(c.type).label} ${c.description || ''} ${partyNames}`.toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });

  const active = contracts.filter(c => !c.archived);
  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (c) => c.id) : rows;

  // Header signal: Active contracts ending within the window — the renewal cliff.
  const flagged = active
    .map(c => ({ c, st: CON_H.conStatus(c, today) }))
    .filter(x => x.st === 'Active' && x.c.endDate && CON_H.conDaysUntil(x.c.endDate, today) <= endingWindow)
    .map(x => ({ ...x, sev: 'warning' }));
  const signal = flagged.length ? {
    severity: 'warning',
    count: flagged.length,
    label: 'Ending soon',
    region: (
      <div className="signal-panel">
        {flagged.map(({ c }) => {
          const hl = CON_H.conHeadline(c, today, endingWindow);
          return (
            <div key={c.id} className="alert warning compact signal-row" role="button" tabIndex={0}
              onClick={() => jumpTo(c.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(c.id); } }}>
              <SeverityIcon severity="warning" size={18} className="alert-icon" />
              <div className="alert-body"><strong>{c.name}.</strong> Term {hl.word}.</div>
              <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo(c.id); }}>View →</button>
            </div>
          );
        })}
      </div>
    ),
  } : undefined;

  return (
    <div className="col gap-6">
      <PageHeader
        title="Contracts"
        icon="handshake"
        sub={`${active.length} contract${active.length === 1 ? '' : 's'} on file`}
        signal={signal}
        overview={<ContractsSummary contracts={contracts} today={today} />}
        overviewDefaultOpen
        searchDefaultOpen
        search={
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search name, type, party, description…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any type" value={typeFilter} onChange={setTypeFilter}
                options={CON_D.contractTypes.map(t => ({ value: t.key, label: t.label }))} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={['Active', 'Upcoming', 'Expired', 'Archived'].map(k => ({ value: k, label: CON_H.conStatusMeta(k).label }))} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Contracts per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        }
        primary={{ label: 'New contract', icon: 'add', onClick: () => setShowAdd(true) }}
      />

      {contracts.length === 0 ? (
        <EmptyState
          icon="handshake"
          title="No contracts yet"
          description="Add a contract to record its type and active period, link the parties it relates to, and keep every signed document in one place."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setShowAdd(true)}>New contract</Button>}
        />
      ) : (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(c) => c.id}
            noun="contracts"
            revealKey={jumpId}
            renderItem={(c) => (
              <ContractListItem row={c} today={today} endingWindow={endingWindow}
                defaultOpen={c.id === 'ct-lease'} highlight={jumpId === c.id}
                onDelete={deleteContract} />
            )}
            empty={(
              <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
                No contracts match your filters.
              </div>
            )}
            trailing={(
              <AddRow title="New contract" sub="Record a type and active period, link the parties, and upload the signed documents."
                onClick={() => setShowAdd(true)} />
            )}
          />
        </div>
      )}

      {showAdd && <AddContractModal onClose={() => setShowAdd(false)} onCreate={createContract} />}
    </div>
  );
};

Object.assign(window, { Contracts });
