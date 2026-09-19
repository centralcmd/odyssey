/* Contracts — /contracts
   ----------------------------------------------------------------------------
   Sibling of Accounts / Insurance / Tax Statements: the same page-header +
   expandable-record scaffold (.acct-list / .acct-item), with a contract-
   specific expanded detail. Each contract is a single agreement with a name,
   a type, an active period, a description, the PARTIES it relates to (an
   account or a contact),
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

/* ====================== One party tile ====================== */
/* Same shape as a policy's Parties section (Insurance.jsx `InsLinkTiles`): one
   InfoTile per linked record, drawn in that record's own type icon and colour.
   What changes with the role feature is the OVERLINE: it now names what the
   record DOES in the agreement (the ContractPartyRole), with the kind and the
   record's own type demoted to the caption — "who is the employer and who is
   the employee" is the question this section answers, and the kind was never
   the answer to it. Under the role sits the TERM, when the party's term is not
   simply the contract's own extent (both dates null, which needs no caption).
   The tile's ⋯ menu carries Edit (the new PUT) and Detach. */
const PartyTile = ({ party, today, onEdit, onDetach }) => {
  const r = CON_H.conResolveParty(party);
  const role = CON_H.conPartyRoleInfo(party.role);
  const term = CON_H.conPartyTermText(party);
  // A closed term in the past: still a party of record, drawn quieter.
  const past = CON_H.conPartyPast(party, today);
  // Unspecified is "nobody has said", not a category — so it is drawn as an
  // absence (muted, no colour), and never as the deliberate "Other".
  const plain = role.key === 'Unspecified' || role.unknown;
  return (
    <div className="con-party-tile">
      <InfoTile icon={r.icon} iconColor={r.color} iconSoft={r.soft}
        label={(
          <React.Fragment>
            <span className={`con-role${plain ? ' unset' : ''}`}>
              <span>{role.key === 'Unspecified' ? 'No role set' : role.label}</span>
            </span>
            {term ? <span className={`con-term${past ? ' past' : ''}`}>{term}</span> : null}
            <span className="con-tile-menu">
              <ActionMenu items={[
                { icon: 'edit', label: 'Edit party', onClick: () => onEdit && onEdit(party) },
                { icon: 'content_copy', label: 'Copy name', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(r.name); } },
                { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(party.id); } },
                { divider: true },
                { icon: 'link_off', label: 'Detach party', danger: true, onClick: () => onDetach(party) },
              ]} />
            </span>
          </React.Fragment>
        )}
        value={r.name} valueVariant="text" className={`wrapvalue${past ? ' tone-muted' : ''}`}
        /* The caption is the record's own TYPE only — the same one-word caption a
           policy party carries. The kind is already said by the tile's icon. */
        foot={r.typeLabel || r.kindLabel || undefined} />
    </div>
  );
};

/* ====================== Expanded detail ====================== */
const ContractDetail = ({ contract, today, focusDocs, setContract, onAddParty, onEditParty, onAttach, termCap, onNewTerm, onEditTerm, onDeleteTerm }) => {
  const typeInfo = CON_H.contractTypeInfo(contract.type);
  const parties = contract.parties || [];
  const files = contract.files || [];
  const fileRows = files.map(CON_H.conFileRow);

  const detachParty = (party) => setContract(prev => ({ ...prev, parties: prev.parties.filter(p => p.id !== party.id) }));
  const removeFile = (row) => setContract(prev => ({ ...prev, files: prev.files.filter(f => f.id !== row.id) }));

  const oneOff = !!contract.completionDate;
  const nowDate = today || CON_H.conToday();
  const startFuture = !!contract.startDate && contract.startDate > nowDate;
  const endPast = !!contract.endDate && contract.endDate < nowDate;
  const completionPast = !!contract.completionDate && contract.completionDate <= nowDate;
  const status = CON_H.conStatus(contract, today);
  const statusMeta = CON_H.conStatusMeta(status);
  const statusFoot = status === 'Archived' ? `since ${CON_H.conDate(contract.archived)}`
    : status === 'Expired' ? (contract.endDate ? `since ${CON_H.conDate(contract.endDate)}` : null)
    : status === 'Upcoming' ? (contract.startDate ? `starts ${CON_H.conDate(contract.startDate)}` : null)
    : oneOff ? `completed ${CON_H.conDate(contract.completionDate)}`
    : (contract.startDate ? `since ${CON_H.conDate(contract.startDate)}` : null);
  const statusToneClass = statusMeta.tone === 'income' ? 'income' : statusMeta.tone === 'expense' ? 'expense'
    : statusMeta.tone === 'info' ? 'info' : 'muted';
  return (
    <React.Fragment>
      {/* DETAILS — the contract's full field set. A one-off has a completion date
          instead of a term, so those tiles are alternatives, not omissions. */}
      <InfoTileGrid>
        <InfoTile icon="handshake" label="Name" value={contract.name} valueVariant="text" className="wrapvalue" />
        <InfoTile icon={typeInfo.icon} label="Type" value={typeInfo.label} valueVariant="text" foot={oneOff ? 'One-off' : 'Term'} />
        {oneOff ? (
          <InfoTile icon="event_available" label={completionPast ? 'Completed on' : 'Completes on'}
            className={completionPast ? undefined : 'tone-info'}
            value={CON_H.conDate(contract.completionDate)} valueVariant="sm"
            foot={completionPast ? 'delivered on this date' : 'upcoming'} />
        ) : (
          <React.Fragment>
            {/* Tense and tone follow the date against today. */}
            {contract.startDate ? (
              <InfoTile icon="play_circle" label={startFuture ? 'Starts on' : 'Started on'}
                className={startFuture ? 'tone-info' : undefined}
                value={CON_H.conDate(contract.startDate)} valueVariant="sm"
                foot={startFuture ? 'upcoming' : null} />
            ) : null}
            <InfoTile icon="event_busy" label={contract.endDate ? (endPast ? 'Ended on' : 'Ends on') : 'End date'}
              className={contract.endDate && endPast ? 'tone-expense' : undefined}
              value={contract.endDate ? CON_H.conDate(contract.endDate) : 'Open-ended'}
              valueVariant={contract.endDate ? 'sm' : 'text'}
              foot={contract.endDate ? (endPast ? 'no longer in force' : 'scheduled') : 'runs until ended'} />
          </React.Fragment>
        )}
        <InfoTile icon={statusMeta.icon} label="Status" valueVariant="text"
          className={`con-status-tile ${statusToneClass}`}
          value={statusMeta.label} foot={statusFoot} />
        {contract.archived ? <InfoTile icon="inventory_2" label="Archived" value={CON_H.conDate(contract.archived)} valueVariant="sm" foot="hidden from the default list" /> : null}
      </InfoTileGrid>

      {contract.description ? (
        <InfoTileGrid><InfoTile icon="sticky_note_2" label="Description" value={contract.description} wide /></InfoTileGrid>
      ) : null}

      {/* PARTIES — a plain section; "New party" lives in the row action menu. */}
      <div className="con-section">
        <SectionDivider label="Parties" meta={`${parties.length} linked`} />
        {parties.length ? (
          <InfoTileGrid>
            {parties.map(p => <PartyTile key={p.id} party={p} today={nowDate} onEdit={onEditParty} onDetach={detachParty} />)}
          </InfoTileGrid>
        ) : (
          <div className="con-empty-line">
            <MIcon name="diversity_3" size={20} />
            <div style={{ flex: 1 }}>No parties yet — link the account or contact this contract relates to, and say what it does in the agreement.</div>
          </div>
        )}
      </div>

      {/* TERMS — what the agreement COSTS, as a dated history: the same Term
          rows an account carries, owned by this contract instead. Sits between
          the parties and the documents: who is in it, what it costs, what
          evidences it. */}
      <ContractTerms
        contract={contract}
        terms={contract.terms || []}
        cap={termCap}
        onNew={onNewTerm}
        onEdit={onEditTerm}
        onDelete={onDeleteTerm}
      />

      {/* DOCUMENTS — last section ("Upload document" is in the row menu too). */}
      <div className="con-section">
        <SectionDivider label="Documents" meta={`${files.length} file${files.length === 1 ? '' : 's'}`} />
        <div className="con-files con-tbl-frame">
          <ContractFilesTable
            files={fileRows}
            onDelete={removeFile}
            empty={<div className="con-empty-line"><MIcon name="folder_open" size={20} /><div style={{ flex: 1 }}>No documents yet — upload the signed agreement, an amendment, or correspondence.</div></div>}
          />
        </div>
      </div>
    </React.Fragment>
  );
};

/* ====================== One contract list item ====================== */
const ContractListItem = ({ row, today, endingWindow, termCap, open: openProp, onToggle, highlight, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  // Terms hang off the record like parties and files do — seeded from the
  // contract-scoped history (GET /api/contracts/{id}/terms).
  const [c, setC] = useState(() => ({ ...row, terms: row.terms || CON_H.conTermsFor(row.id) }));
  // Open state lives in the list — opening a contract closes its siblings.
  const open = !!openProp;
  const setOpen = (next) => onToggle(typeof next === 'function' ? next(open) : next);
  const [showEdit, setShowEdit] = useState(false);
  const [focusDocs, setFocusDocs] = useState(false);
  const [modal, setModal] = useState(null); // 'party' | 'file'
  // The party being edited through the new PUT …/parties/{partyId}. The same
  // dialog serves add and edit; the row id is what keeps it one party.
  const [editParty, setEditParty] = useState(null);
  // The term being edited (PUT …/terms/{termId}); the same dialog serves create.
  const [editTerm, setEditTerm] = useState(null);
  const cardRef = useRef(null);

  const typeInfo = CON_H.contractTypeInfo(c.type);
  const status = CON_H.conStatus(c, today);
  const headline = CON_H.conHeadline(c, today, endingWindow);
  const parties = c.parties || [];
  const files = c.files || [];
  const terms = c.terms || [];
  const termBlock = CON_H.conTermWriteBlock(c, terms.length, termCap);
  const contact = parties.map(CON_H.conResolveParty).find(r => r.kind === 'contact');
  const dimmed = !!c.archived;
  // "Ended" is not the same as status 'Expired': a delivered one-off stays Active
  // in the status derivation, so completion counts too.
  const hasEnded = status === 'Expired' || (!!c.completionDate && c.completionDate <= today);

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
  /* The edit is a FULL REPLACEMENT of the row — role, target and both dates —
     written in place, so `id` survives a role or target change and the party
     stays one party (spec §5: the response's contractPartyId equals the one in
     the route). */
  const saveParty = (next) => {
    setC(prev => ({ ...prev, parties: (prev.parties || []).map(p => (p.id === next.id ? { ...next } : p)) }));
    setEditParty(null);
  };
  const attachFile = (filesToAdd) => { const arr = Array.isArray(filesToAdd) ? filesToAdd : [filesToAdd]; setC(prev => ({ ...prev, files: [...(prev.files || []), ...arr] })); setModal(null); setOpen(true); setFocusDocs(true); };
  const toggleArchive = () => setC(prev => ({ ...prev, archived: prev.archived ? null : new Date().toISOString() }));

  /* Create and edit are one write path, as they are on the server: the dialog
     posts a NewTerm and the route (this contract) is the only thing that names
     the owner — nothing in the body can move a term to another contract or to
     an account. */
  const upsertTerm = (dto, id) => {
    setC(prev => ({
      ...prev,
      terms: id
        ? (prev.terms || []).map(t => (t.id === id ? { ...t, ...dto } : t))
        : [{ id: `ctm-new-${Date.now()}`, createdAtUtc: new Date().toISOString(), ...dto }, ...(prev.terms || [])],
    }));
    setModal(null); setEditTerm(null);
  };
  const deleteTerm = (t) => setC(prev => ({ ...prev, terms: (prev.terms || []).filter(x => x.id !== t.id) }));

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
        name={c.name}
        chips={<ContractStatusChip status={status} />}
        meta={[
          typeInfo.label,
          <span className="con-sub-inst"><MIcon name="groups" size={14} /><span>{contact ? contact.name : 'No contact'}</span></span>,
        ]}
        counts={[
          { icon: 'diversity_3', value: parties.length, label: 'Parties' },
          { icon: 'sell', value: terms.length, label: 'Terms' },
          { icon: 'description', value: files.length, label: 'Documents' },
        ]}
        figure={{
          value: headline.value,
          caption: headline.word,
          tone: headline.cls === 'lapsed' || headline.cls === 'expired' ? 'expense' : headline.cls === 'soon' ? 'pending' : undefined,
        }}
        dimmed={dimmed}
        highlight={highlight}
        open={open}
        onToggle={setOpen}
        actions={<ActionMenu items={[
          { icon: 'edit', label: 'Edit contract', onClick: () => setShowEdit(true) },
          { icon: 'group_add', label: 'New party', onClick: () => { setOpen(true); setModal('party'); } },
          // Refused writes are offered with their reason rather than hidden —
          // the same guard the section notice and the endpoint state.
          termBlock
            ? { icon: 'sell', label: 'New term', disabled: true, note: termBlock.reason === 'archived' ? 'The contract has to be restored first.' : termBlock.text }
            : { icon: 'sell', label: 'New term', onClick: () => { setOpen(true); setModal('term'); } },
          { icon: 'attach_file', label: 'Upload document', onClick: () => { setOpen(true); setModal('file'); } },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(c.id); } },
          { divider: true },
          // Only an ended contract can be archived — the lifecycle is ordered, so
          // the action is offered with its reason rather than hidden.
          (hasEnded || c.archived)
            ? { icon: c.archived ? 'unarchive' : 'inventory_2', label: c.archived ? 'Restore' : 'Archive', onClick: toggleArchive }
            : { icon: 'inventory_2', label: 'Archive', disabled: true, note: 'The contract has to end first.' },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(c.id) },
        ]} />}
      >
        <ContractDetail contract={c} today={today} focusDocs={focusDocs} setContract={setC}
          onAddParty={() => setModal('party')} onEditParty={(p) => setEditParty(p)} onAttach={() => setModal('file')}
          termCap={termCap}
          onNewTerm={() => setModal('term')} onEditTerm={(t) => setEditTerm(t)} onDeleteTerm={deleteTerm} />
      </RecordCard>
      {showEdit && <AddContractModal contract={c} onClose={() => setShowEdit(false)} onSave={saveEdit} />}

      {modal === 'party' && <AddContractPartyModal contract={c} onClose={() => setModal(null)} onAdd={addParty} />}
      {editParty && <AddContractPartyModal contract={c} party={editParty} onClose={() => setEditParty(null)} onSave={saveParty} />}
      {modal === 'file' && <AddContractFileModal contract={c} onClose={() => setModal(null)} onAttach={attachFile} />}
      {(modal === 'term' || editTerm) && (
        <AddContractTermModal contract={c} term={editTerm} existing={terms}
          onClose={() => { setModal(null); setEditTerm(null); }} onSave={upsertTerm} />
      )}
    </div>
  );
};

/* ====================== Summary (header Overview) ====================== */
const ContractsSummary = ({ contracts, today, endingWindow }) => {
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
  /* Ending soon is not a fifth status — it is a slice of Active, and it reads
     here for the same reason the header signal exists: the cliff is the thing
     you act on. Listed straight after Active, never counted as its own status. */
  const endingSoon = (contracts || []).filter(c => !c.archived && c.endDate
    && CON_H.conStatus(c, today) === 'Active'
    && CON_H.conDaysUntil(c.endDate, today) <= endingWindow).length;
  statusRows.splice(1, 0, { key: 'EndingSoon', icon: 'hourglass_bottom', iconColor: TONE_COLOR.pending,
    label: `Ending soon · ${endingWindow}d`, count: endingSoon });

  /* What the file costs to run. Only the Active contracts' in-force periodic
     fees carry a rate, so the totals and the per-type rows are the same read
     twice — once summed, once split. */
  const rr = CON_H.conRunRate(contracts, today);
  const rrMoney = (v) => (v == null ? '—' : CON_H.money(v, rr.baseCurrency));
  const rrFoot = rr.unconvertedCurrencies.length
    ? `in ${rr.baseCurrency} · ${rr.unconvertedCurrencies.join(', ')} excluded`
    : `in ${rr.baseCurrency}`;
  const rrMonthlyRows = rr.typeRows.map(r => ({ key: r.key, icon: r.icon, iconColor: r.color, label: r.label, count: rrMoney(r.monthly) }));
  const rrYearlyRows = rr.typeRows.map(r => ({ key: r.key, icon: r.icon, iconColor: r.color, label: r.label, count: rrMoney(r.yearly) }));
  return (
    <div className="con-summary">
      <div className="con-run-tiles">
        <InfoTile icon="calendar_month" label="Monthly run rate" value={rrMoney(rr.monthly)} foot={rrFoot} />
        <InfoTile icon="event_repeat" label="Yearly run rate" value={rrMoney(rr.yearly)} foot={rrFoot} />
      </div>
      <div className="con-stats">
        <BreakdownTile label="By type" rows={typeRows} empty="No active contracts." />
        <BreakdownTile label="By status" rows={statusRows} empty="No contracts." />
        <BreakdownTile label="Monthly run rate by type" rows={rrMonthlyRows} empty="No recurring fees in force." />
        <BreakdownTile label="Yearly run rate by type" rows={rrYearlyRows} empty="No recurring fees in force." />
      </div>
    </div>
  );
};

/* ====================== Page ====================== */
const Contracts = ({ tweaks = {}, onNavigate }) => {
  const { useState } = React;
  // One card open at a time — the list owns it.
  const [openId, setOpenId] = useState('ct-lease');
  const today = CON_H.conToday();
  const endingWindow = tweaks.endingWindowDays != null ? tweaks.endingWindowDays : CON_D.CONTRACTS_ENDING_WINDOW_DAYS;
  const chargeWindow = tweaks.chargeWindowDays != null ? tweaks.chargeWindowDays : CON_D.CONTRACTS_CHARGE_WINDOW_DAYS;
  // ContractMaxTermsPerContract — a system setting, not a per-contract field.
  const termCap = tweaks.contractTermCap != null ? tweaks.contractTermCap : CON_D.CONTRACT_MAX_TERMS_PER_CONTRACT;

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
  /* The other half of the panel: the next recurring charge each contract
     carries, derived from the Fee terms in force. Nothing here is scheduled —
     it is the term history read forward, the way Subscriptions reads its
     billing interval forward into upcoming renewals. */
  const upcomingCharges = CON_H.conUpcomingCharges(active, today, { windowDays: chargeWindow, limit: 6 });
  /* And the other side of the cliff: terms that ran out in the window just
     past and were never archived — the ones still waiting on a decision. */
  const recentlyExpired = active
    .filter(c => c.endDate && CON_H.conStatus(c, today) === 'Expired'
      && -CON_H.conDaysUntil(c.endDate, today) <= endingWindow)
    .sort((a, b) => (a.endDate < b.endDate ? 1 : -1))
    .slice(0, 6);
  /* Signed but not yet begun — the mirror of the ending cliff, and the other
     thing a dated window surfaces: a term about to come into force. */
  const startingSoon = active
    .filter(c => c.startDate && CON_H.conStatus(c, today) === 'Upcoming'
      && CON_H.conDaysUntil(c.startDate, today) <= endingWindow)
    .sort((a, b) => (a.startDate < b.startDate ? -1 : 1))
    .slice(0, 6);
  const signal = (flagged.length || upcomingCharges.length || recentlyExpired.length || startingSoon.length) ? {
    // The panel's worst severity wins the button: an expired term reads error,
    // a term running out reads warning, and next charges alone read info.
    severity: recentlyExpired.length ? 'error' : flagged.length ? 'warning' : 'info',
    count: flagged.length + upcomingCharges.length + recentlyExpired.length + startingSoon.length,
    label: 'Upcoming',
    region: (
      <div className="signal-panel">
        {recentlyExpired.length ? <div className="con-signal-group">Recently expired</div> : null}
        {recentlyExpired.map((c) => {
          const ago = -CON_H.conDaysUntil(c.endDate, today);
          return (
            <div key={c.id} className="alert error compact signal-row" role="button" tabIndex={0}
              onClick={() => jumpTo(c.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(c.id); } }}>
              <SeverityIcon severity="error" size={18} className="alert-icon" />
              <div className="alert-body"><strong>{c.name}.</strong> Term expired {ago <= 0 ? 'today' : `${ago} day${ago === 1 ? '' : 's'} ago`}.</div>
              <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo(c.id); }}>View →</button>
            </div>
          );
        })}
        {flagged.length ? <div className="con-signal-group">Ending soon</div> : null}
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
        {startingSoon.length ? <div className="con-signal-group">Starting soon</div> : null}
        {startingSoon.map((c) => {
          const days = CON_H.conDaysUntil(c.startDate, today);
          return (
            <div key={c.id} className="alert info compact signal-row" role="button" tabIndex={0}
              onClick={() => jumpTo(c.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(c.id); } }}>
              <SeverityIcon severity="info" size={18} className="alert-icon" />
              <div className="alert-body"><strong>{c.name}.</strong> Term starts {days <= 0 ? 'today' : days === 1 ? 'tomorrow' : `in ${days} days`}.</div>
              <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo(c.id); }}>View →</button>
            </div>
          );
        })}
        {upcomingCharges.length ? <div className="con-signal-group">Next charges</div> : null}
        {upcomingCharges.map(({ contract: c, term, date, days }) => {
          const ti = CON_H.contractTypeInfo(c.type);
          return (
            <div key={c.id} className="con-charge-row" role="button" tabIndex={0}
              onClick={() => jumpTo(c.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(c.id); } }}>
              <span className="con-charge-when">
                <span className="con-charge-md mono">{CON_H.conDateMd(date)}</span>
                <span className="con-charge-rel">{CON_H.conRelDays(days)}</span>
              </span>
              <span className="con-charge-name">
                <MIcon name={ti.icon} size={16} style={{ color: ti.color }} />
                <span className="con-charge-title">{c.name}</span>
                <span className="con-charge-term">{CON_H.termDisplayName(term, null)}</span>
              </span>
              <span className="con-charge-amt mono">{CON_H.money(term.value, term.currency || 'USD')}</span>
              <span className="con-charge-go">View →</span>
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
        overview={<ContractsSummary contracts={contracts} today={today} endingWindow={endingWindow} />}
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
              <ContractListItem row={c} today={today} endingWindow={endingWindow} termCap={termCap}
                open={openId === c.id}
                onToggle={(o) => setOpenId(o ? c.id : null)}
                highlight={jumpId === c.id}
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
