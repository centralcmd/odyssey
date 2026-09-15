/* Contracts — /contracts
   ----------------------------------------------------------------------------
   Sibling of Accounts / Insurance / Tax Statements: the same page-header +
   expandable-record scaffold (.acct-list / .acct-item), with a contract-
   specific expanded detail. Each contract is a single agreement with a name,
   a type, an active period, a description, the PARTIES it relates to (an
   account or a contact),
   and the DOCUMENTS that evidence it (references to existing library files).

   Status (Draft / Ready / Upcoming / Active / Paused / Expired / Archived) is
   DERIVED, never stored — computed per request from StartDate / EndDate /
   Ready / Signed / Paused / Archived, at the precedence
   Archived > Draft/Ready > Upcoming > Expired > Paused > Active.

   THE SIGNATURE STAMPS. Ready and Signed are two nullable timestamps in the
   same shape as Paused and Archived. A contract with no Signed stamp is
   unsigned, and reads Draft or Ready WHATEVER its dates say — it is recorded
   on file without pretending to be in force, and it contributes nothing to
   the run rate or the upcoming charges. They are written two ways, both
   landing on the same PUT: the row menu's one-click Mark ready / Mark signed
   / Unsign for the common path, and real date fields in the create + edit
   dialog for backdating a paper contract signed last month.
   Navigation
   is expand-in-place (no /{id} deep-link, per frontend B1). Archive is a
   reversible field on the edit form (a normal update — there is no dedicated
   archive action), distinct from the irreversible hard delete.

   PAUSE is the temporary counterpart of archive, and reads as its opposite in
   every way that matters: a paused contract stays at full brightness, stays
   fully editable, keeps its terms and its price history, and is only absent
   from the money — the run rate, the by-type cost split and the next-charges
   list. It is offered as a one-click toggle in the row menu (the Subscriptions
   pattern), only where it is permitted: pause is enterable from Active alone,
   while Resume is offered wherever a stamp exists.

   Helpers + seed come from contracts-data.js; atoms from the DS bundle via
   Components.jsx. FilesTable comes from the DS bundle. */

const CON_H = window.OdysseyHelpers;
const CON_D = window.OdysseyData;
const CON_SEV_RANK = { info: 0, warning: 1, error: 2 };

/* ====================== Derived-status chip ======================
   The DS ContractStatusChip owns the vocabulary (and the neutral fallback an
   unknown server member gets). Resolved lazily so it appears as soon as
   _ds_bundle.js carries it; the local Chip is the bundle-lag fallback. */
const ContractStatusChip = ({ status }) => {
  const C = (window.OdysseyDesignSystem_d5aa51 || {}).ContractStatusChip;
  if (C) return <C status={status} size="sm" />;
  const meta = CON_H.conStatusMeta(status);
  return <Chip tone={meta.tone} dot={meta.dot}>{meta.label}</Chip>;
};

/* ====================== Files table (contract-scoped) ======================
   The DS FilesTable, in its contract configuration. Three things differ from
   the account one:
     • `renameable={false}` — a contract document's name lives on the
       FileMetadata it references; PUT …/files/{fileId} carries the document
       type and the four validity fields only, so the dialog offers no rename.
     • `requireType` — the update is a full replacement and
       ContractFileType.Signed is the zero member, so an unsent type must be
       rejected rather than defaulted into "this is the signed agreement".
     • `readOnly` — offered for surfaces that genuinely refuse writes. Archiving
       is NOT one of them: an archived contract stays fully writable, it is
       simply hidden from the default list.
   `onSave` stands in for PUT …/files/{fileId} → 204, after which the real
   client re-reads GET …/files (this contract's documents alone) instead of
   refetching the whole contract. */
const ContractFilesTable = ({ files, onDelete, onSave, empty, readOnly }) => {
  const DSFilesTable = (window.OdysseyDesignSystem_d5aa51 || {}).FilesTable;
  const { useState } = React;
  const [edits, setEdits] = useState({});
  const rows = files.map(f => (edits[f.id] ? { ...f, ...edits[f.id] } : f));
  if (!DSFilesTable) return empty || null;
  const issuers = (CON_D.contacts || []).filter(c => !c.archived).map(c => {
    const t = (CON_D.contactTypeByKey || {})[c.type] || {};
    return { value: c.id, label: c.name, icon: t.icon, iconColor: t.color };
  });
  return (
    <InlinePager items={rows}>
      {(pageRows) => (
        <DSFilesTable
          files={pageRows}
          typeFor={(f) => CON_H.contractFileTypeInfo(f.kind)}
          kinds={CON_D.contractFileTypes}
          formatDate={CON_H.conDate}
          empty={empty}
          validityColumns
          issuers={readOnly ? undefined : issuers}
          issuerFor={(f) => {
            const c = f.issuedBy && (CON_D.contactById || {})[f.issuedBy];
            return c ? c.name : null;
          }}
          onCreateContact={(name, kind) => CON_D.contactOption(CON_D.createContact(name, kind))}
          renameable={false}
          requireType
          onSave={readOnly ? undefined : (id, patch) => {
            setEdits(prev => ({ ...prev, [id]: { ...(prev[id] || {}), ...patch } }));
            onSave && onSave(id, patch);
          }}
          onDelete={readOnly ? undefined : onDelete}
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
  // A role is required on every write now, so an unset role means one thing:
  // a row written before the matrix. It is drawn as an absence (muted, no
  // colour), and never as the deliberate "Other". Same for a member this
  // client is too old to name.
  const plain = !!role.unset || !!role.unknown;
  return (
    <div className="con-party-tile">
      <InfoTile icon={r.icon} iconColor={r.color} iconSoft={r.soft}
        label={(
          <React.Fragment>
            <span className={`con-role${plain ? ' unset' : ''}`}>
              <span>{role.label}</span>
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
const ContractDetail = ({ contract, today, focusDocs, setContract, onAddParty, onEditParty, onAttach, termCap, onNewTerm, onEditTerm, onDeleteTerm, events, onEditEvent, onDeleteEvent }) => {
  const typeInfo = CON_H.contractTypeInfo(contract.type);
  const parties = contract.parties || [];
  const files = contract.files || [];
  const fileRows = files.map(CON_H.conFileRow);

  const detachParty = (party) => setContract(prev => ({ ...prev, parties: prev.parties.filter(p => p.id !== party.id) }));
  const removeFile = (row) => setContract(prev => ({ ...prev, files: prev.files.filter(f => f.id !== row.id) }));
  /* PUT …/files/{fileId} — a full replacement of the link row's type and
     validity, so an omitted date CLEARS the stored value. The patch is merged
     onto the ContractFile, never onto the FileMetadata it references. */
  const saveFile = (id, patch) => setContract(prev => ({
    ...prev,
    files: (prev.files || []).map(f => (f.id === id ? { ...f, ...patch } : f)),
  }));

  const oneOff = !!contract.completionDate;
  const nowDate = today || CON_H.conToday();
  const startFuture = !!contract.startDate && contract.startDate > nowDate;
  const endPast = !!contract.endDate && contract.endDate < nowDate;
  const completionPast = !!contract.completionDate && contract.completionDate <= nowDate;
  const status = CON_H.conStatus(contract, today);
  const statusMeta = CON_H.conStatusMeta(status);
  const statusFoot = status === 'Archived' ? `since ${CON_H.conDate(contract.archived)}`
    : status === 'Draft' ? 'not yet marked ready for signature'
    : status === 'Ready' ? `ready since ${CON_H.conDate(contract.ready)} — waiting on a signature`
    : status === 'Paused' ? `since ${CON_H.conDate(contract.paused)}`
    : status === 'Expired' ? (contract.endDate ? `since ${CON_H.conDate(contract.endDate)}` : null)
    : status === 'Upcoming' ? (contract.startDate ? `starts ${CON_H.conDate(contract.startDate)}` : null)
    : oneOff ? `completed ${CON_H.conDate(contract.completionDate)}`
    : (contract.startDate ? `since ${CON_H.conDate(contract.startDate)}` : null);
  const statusToneClass = statusMeta.tone === 'income' ? 'income' : statusMeta.tone === 'expense' ? 'expense'
    : statusMeta.tone === 'info' ? 'info' : statusMeta.tone === 'pending' ? 'pending' : 'muted';
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
        {/* One tile per STORED stamp, so nothing is lost when the derived
            status shows only one of them: an archived contract that is also
            paused still says when each began. */}
        {/* The two signature stamps, in lifecycle order and ahead of the
            suspension stamps: they are the earliest facts about the contract.
            A tile appears only once its stamp exists, so a plain Draft adds
            nothing to the grid — the Status tile has already said so. */}
        {contract.ready ? <InfoTile icon="draw" label="Ready for signature" value={CON_H.conDate(contract.ready)} valueVariant="sm" className={contract.signed ? undefined : 'con-status-tile pending'} foot={contract.signed ? 'sent out on this date' : 'waiting on a signature'} /> : null}
        {contract.signed ? <InfoTile icon="history_edu" label="Signed" value={CON_H.conDate(contract.signed)} valueVariant="sm" className="con-status-tile income" foot="signed by all parties" /> : null}
        {contract.paused ? <InfoTile icon="pause_circle" label="Paused" value={CON_H.conDate(contract.paused)} valueVariant="sm" className="con-status-tile pending" foot="still listed and editable, not costing" /> : null}
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
          <EmptyLine>No parties yet — link the account or contact this contract relates to, and say what it does in the agreement.</EmptyLine>
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

      {/* DOCUMENTS — last section ("Upload document" is in the row menu too).
          With no files there is no table to head: the empty line stands on its
          own, as it does in Parties, Terms and Events. */}
      <div className="con-section">
        <SectionDivider label="Documents" meta={`${files.length} file${files.length === 1 ? '' : 's'}`} />
        {fileRows.length === 0 ? (
          <EmptyLine>No documents yet — upload the signed agreement, an amendment, or correspondence.</EmptyLine>
        ) : (
          <div className="con-files con-tbl-frame">
            <ContractFilesTable files={fileRows} onDelete={removeFile} onSave={saveFile} />
          </div>
        )}
      </div>

      {/* EVENTS — what has HAPPENED to the agreement, as a log. Last, and
          deliberately so: the sections above describe what the contract IS
          (details, who is in it, what it costs, what evidences it), and this
          one is its history. Like every other section it stays writable when
          the contract is archived — archival hides a contract, it does not
          lock it. */}
      <ContractEvents
        contract={contract}
        events={events || []}
        onEdit={onEditEvent}
        onDelete={onDeleteEvent}
      />
    </React.Fragment>
  );
};

/* ====================== One contract list item ====================== */
const ContractListItem = ({ row, today, endingWindow, termCap, open: openProp, onToggle, highlight, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  // Terms hang off the record like parties and files do — seeded from the
  // contract-scoped history (GET /api/contracts/{id}/terms).
  const [c, setC] = useState(() => ({ ...row, terms: row.terms || CON_H.conTermsFor(row.id) }));
  /* Events are NOT nested on the contract record: GET /api/contracts/{id}
     deliberately does not inline them (an event log grows without bound), so
     they are their own paged read and their own piece of state. */
  const [events, setEvents] = useState(() => CON_H.cevFor(row.id));
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
  // The event being edited (PUT …/events/{eventId}); same dialog serves create.
  const [editEvent, setEditEvent] = useState(null);
  const cardRef = useRef(null);

  const typeInfo = CON_H.contractTypeInfo(c.type);
  const status = CON_H.conStatus(c, today);
  const headline = CON_H.conHeadline(c, today, endingWindow);
  const parties = c.parties || [];
  const files = c.files || [];
  const terms = c.terms || [];
  const termBlock = CON_H.conTermWriteBlock(c, terms.length, termCap);
  /* An income-bearing contract is marked on the collapsed row, because "this
     file is money in" changes how the whole row reads and is otherwise only
     visible once expanded. It is derived from the in-force entries of this
     record's own terms — the backend's list projection carries no direction
     (it has no term join), so a production list needs either the detail
     payload it already has open or a list-level field the backend deferred. */
  const inForceTerms = window.trmCurrentFromList ? window.trmCurrentFromList(terms) : [];
  const hasIncoming = inForceTerms.some(t => CON_H.termIsIncoming(t));
  const contact = parties.map(CON_H.conResolveParty).find(r => r.kind === 'contact');
  const dimmed = !!c.archived;
  /* An unsigned contract stays at FULL brightness — it is the row most likely
     to need attention — and is marked instead by a dashed card edge: nothing
     about it is settled yet. Dimming is reserved for archived. */
  const unsigned = CON_H.conIsUnsigned(status);
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
      /* The dialog is a FULL REPLACEMENT, and that includes the two signature
         stamps: a value sets them, a cleared field clears them. Carried
         explicitly here — an omitted stamp on this write would flip a signed
         contract back to Draft and drop it out of the run rate. */
      ready: draft.ready || null,
      signed: draft.signed || null,
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
  /* Every write below rebuilds the whole record, so each one carries BOTH
     signature stamps forward explicitly. Omitting them here is the regression
     the backend spec calls out: clicking Archive or Pause on a signed
     contract would clear them, flip it to Draft and empty it out of the run
     rate. Keep them on every write that touches this record. */
  const toggleArchive = () => setC(prev => ({ ...prev, ready: prev.ready, signed: prev.signed, archived: prev.archived ? null : new Date().toISOString() }));
  /* Pause rides the same PUT as archive, and is idempotent the same way: a
     repeated pause keeps the ORIGINAL stamp, so "paused since" never resets. */
  const togglePause = () => setC(prev => ({ ...prev, ready: prev.ready, signed: prev.signed, paused: prev.paused ? null : (prev.paused || new Date().toISOString()) }));

  /* The one-click signature path. Marking ready is idempotent the same way a
     pause is — a repeated mark keeps the ORIGINAL stamp, so "ready since"
     never resets. Signing stamps Ready too when it is missing, because the
     server refuses a Signed without one (contract_signed_requires_ready) and
     a one-click action must not be able to compose an invalid write.
     Unsign clears BOTH, which is never refused in any state. */
  const markReady = () => setC(prev => ({ ...prev, ready: prev.ready || new Date().toISOString() }));
  const markSigned = () => setC(prev => {
    const now = new Date().toISOString();
    return { ...prev, ready: prev.ready || now, signed: prev.signed || now };
  });
  const unsign = () => setC(prev => ({ ...prev, ready: null, signed: null }));

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
  /* A PUT is a full replacement of the event, so the saved DTO REPLACES the
     row rather than merging into it — an omitted description or note is
     cleared, exactly as the endpoint does. */
  const saveEvent = (dto) => {
    setEvents(prev => prev.some(e => e.id === dto.id) ? prev.map(e => (e.id === dto.id ? dto : e)) : [dto, ...prev]);
    setModal(null); setEditEvent(null);
  };
  const deleteEvent = (ev) => setEvents(prev => prev.filter(x => x.id !== ev.id));

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
    <div ref={cardRef} className={unsigned ? 'con-unsigned' : undefined}>
      <RecordCard
        icon={typeInfo.icon}
        accent={typeInfo.color}
        accentSoft={typeInfo.soft}
        name={c.name}
        chips={<ContractStatusChip status={status} />}
        meta={[
          typeInfo.label,
          <span className="con-sub-inst"><MIcon name="groups" size={14} /><span>{contact ? contact.name : 'No contact'}</span></span>,
          ...(hasIncoming ? [<span className="trm-dir in">Money in</span>] : []),
        ]}
        counts={[
          { icon: 'diversity_3', value: parties.length, label: 'Parties' },
          { icon: 'sell', value: terms.length, label: 'Terms' },
          { icon: 'description', value: files.length, label: 'Documents' },
          { icon: 'history', value: events.length, label: 'Events' },
        ]}
        figure={{
          value: headline.value,
          caption: headline.word,
          tone: headline.cls === 'lapsed' || headline.cls === 'expired' ? 'expense' : (headline.cls === 'soon' || headline.cls === 'paused') ? 'pending' : undefined,
        }}
        dimmed={dimmed}
        highlight={highlight}
        open={open}
        onToggle={setOpen}
        actions={<ActionMenu items={[
          { icon: 'edit', label: 'Edit contract', onClick: () => setShowEdit(true) },
          /* Pause is enterable only from Active, so the action is simply absent
             elsewhere — unlike Archive, whose precondition (the contract has to
             end) is a step the user can act on, this one has no instruction to
             give. Resume is offered whenever a stamp exists, in any state:
             clearing a pause is never refused. */
          /* The signature path, first in the menu while it is the thing the
             contract is waiting on. Offered in lifecycle order, one step at a
             time: there is no "Mark ready" on a contract already signed. */
          ...(!c.signed && !c.ready ? [{ icon: 'draw', label: 'Mark ready for signature', onClick: markReady }] : []),
          ...(!c.signed && c.ready ? [{ icon: 'history_edu', label: 'Mark signed', onClick: markSigned }] : []),
          ...(c.signed || c.ready ? [{ icon: 'undo', label: c.signed ? 'Unsign' : 'Clear ready date', onClick: unsign }] : []),
          ...(status === 'Active' ? [{ icon: 'pause_circle', label: 'Pause', onClick: togglePause }]
            /* Pausing is refused for anything that is not Active, the unsigned
               two included — a stamp with nothing to suspend. */
            : CON_H.conIsUnsigned(status) ? [{ icon: 'pause_circle', label: 'Pause', disabled: true, note: 'Only a signed contract in force can be paused.' }]
            : c.paused ? [{ icon: 'play_circle', label: 'Resume', onClick: togglePause }] : []),
          { icon: 'group_add', label: 'New party', onClick: () => { setOpen(true); setModal('party'); } },
          // Refused writes are offered with their reason rather than hidden —
          // the cap is the only thing that refuses one.
          termBlock
            ? { icon: 'sell', label: 'New term', disabled: true }
            : { icon: 'sell', label: 'New term', onClick: () => { setOpen(true); setModal('term'); } },
          { icon: 'attach_file', label: 'Upload document', onClick: () => { setOpen(true); setModal('file'); } },
          /* Creating an event lives HERE rather than in the section: it is one
             of the things you do to a contract, and the log below stays a
             read surface. Offered on an archived contract too — §8.6. */
          { icon: 'history', label: 'New event', onClick: () => { setOpen(true); setModal('event'); } },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(c.id); } },
          { divider: true },
          /* An ENDED or an UNSIGNED contract can be archived — the lifecycle is
             ordered, so the action is offered with its reason rather than
             hidden. Unsigned is in the rule because abandoning a negotiation
             is the likeliest reason to archive a draft, and a draft usually
             has no end date to wait for. */
          (hasEnded || c.archived || !c.signed)
            ? { icon: c.archived ? 'unarchive' : 'inventory_2', label: c.archived ? 'Restore' : 'Archive', onClick: toggleArchive }
            : { icon: 'inventory_2', label: 'Archive', disabled: true, note: 'The contract has to end first.' },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(c.id) },
        ]} />}
      >
        <ContractDetail contract={c} today={today} focusDocs={focusDocs} setContract={setC}
          onAddParty={() => setModal('party')} onEditParty={(p) => setEditParty(p)} onAttach={() => setModal('file')}
          termCap={termCap}
          onNewTerm={() => setModal('term')} onEditTerm={(t) => setEditTerm(t)} onDeleteTerm={deleteTerm}
          events={events} onEditEvent={(ev) => setEditEvent(ev)} onDeleteEvent={deleteEvent} />
      </RecordCard>
      {showEdit && <AddContractModal contract={c} onClose={() => setShowEdit(false)} onSave={saveEdit} />}

      {modal === 'party' && <AddContractPartyModal contract={c} onClose={() => setModal(null)} onAdd={addParty} />}
      {editParty && <AddContractPartyModal contract={c} party={editParty} onClose={() => setEditParty(null)} onSave={saveParty} />}
      {modal === 'file' && <AddContractFileModal contract={c} onClose={() => setModal(null)} onAttach={attachFile} />}
      {(modal === 'term' || editTerm) && (
        <AddContractTermModal contract={c} term={editTerm} existing={terms}
          onClose={() => { setModal(null); setEditTerm(null); }} onSave={upsertTerm} />
      )}
      {(modal === 'event' || editEvent) && (
        <AddContractEventModal contract={c} event={editEvent}
          onClose={() => { setModal(null); setEditEvent(null); }} onSave={saveEvent} />
      )}
    </div>
  );
};

/* ====================== Summary (header Overview) ====================== */
const ContractsSummary = ({ contracts, today, endingWindow }) => {
  const s = CON_H.conSummary(contracts, today);
  // Lifecycle reading order — the shared rank, not the enum ordinal.
  const order = CON_H.CON_STATUS_RANK;
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
  statusRows.splice(order.indexOf('Active') + 1, 0, { key: 'EndingSoon', icon: 'hourglass_bottom', iconColor: TONE_COLOR.pending,
    label: `Ending soon · ${endingWindow}d`, count: endingSoon });

  /* What the file costs to run — and what it brings in. Only the Active
     contracts' in-force periodic fees carry a rate, so the totals and the
     per-type rows are the same read twice: once summed, once split.

     THE HEADER NOW REPORTS TWO SIDES AND A NET. The old tile named "Monthly
     run rate" was one figure that silently mixed a landlord's rent income
     into the same number as a streaming subscription; it is renamed rather
     than reused, because "the run rate" now has to say which way the money
     moves. Each gross counts ONE direction — the only figure that crosses the
     two is the net. */
  const rr = CON_H.conRunRate(contracts, today);
  const rrMoney = (v) => (v == null ? '—' : CON_H.money(v, rr.baseCurrency));
  // The net is the only signed figure on the page, so it is the only one that
  // ever carries a leading +. money() already writes '−' for a negative.
  // signedMoney fills the same sign slot with a real '+', so a net lines up
  // with the grosses above it rather than being a string with one glued on.
  const netMoney = (v) => (v == null ? '—' : CON_H.signedMoney(v, rr.baseCurrency));
  const netClass = (v) => (v == null || v === 0 ? 'net-flat' : v > 0 ? 'net-pos' : 'net-neg');
  /* A paused contract keeps its price on file and contributes nothing here;
     so does an unsigned one, which may be fully priced and is still only a
     quote. The tiles name both, because a run rate that quietly dropped would
     otherwise read as a pricing error. */
  const pausedCount = s.countsByStatus.Paused || 0;
  const unsignedCount = (s.countsByStatus.Draft || 0) + (s.countsByStatus.Ready || 0);
  const excluded = [
    pausedCount ? `${pausedCount} paused` : null,
    unsignedCount ? `${unsignedCount} unsigned` : null,
  ].filter(Boolean);
  /* Every figure now carries its own ISO code, so the foot no longer repeats
     the base currency — it is left to say only what is NOT in the figure: the
     currencies with no rate to base, and the contracts the status gate keeps
     out. A total that quietly dropped would otherwise read as a pricing error. */
  const rrFoot = [
    rr.unconvertedCurrencies.length ? `${rr.unconvertedCurrencies.join(', ')} excluded — no rate to ${rr.baseCurrency}` : null,
    excluded.length ? `${excluded.join(', ')} excluded` : null,
  ].filter(Boolean).join(' · ') || 'every contract in force counted';
  const netFoot = null;
  /* ONE by-type tile per period, carrying the NET per type — signed and
     colored. Two figures per row said the same thing twice at half the
     density; what a reader wants per type is which way that type leaves them,
     and the grosses are already the three tiles above. */
  const netOf = (key, getter) => {
    const o = rr.typeRows.find(r => r.key === key);
    const i = rr.incomingTypeRows.find(r => r.key === key);
    if (!o && !i) return null;
    return Math.round(((i ? getter(i) : 0) - (o ? getter(o) : 0)) * 100) / 100;
  };
  const netCell = (v) => <span className={`con-bd-net ${netClass(v)}`}>{netMoney(v)}</span>;
  const byTypeMoneyRows = (getter) => {
    const keys = CON_D.contractTypes.map(t => t.key)
      .filter(k => rr.typeRows.some(r => r.key === k) || rr.incomingTypeRows.some(r => r.key === k));
    if (!keys.length) return [];
    const rows = keys.map(k => {
      const ty = CON_H.contractTypeInfo(k);
      return { key: k, icon: ty.icon, iconColor: ty.color, label: ty.label, count: netCell(netOf(k, getter)) };
    });
    return rows;
  };
  const rrMonthlyRows = byTypeMoneyRows(r => r.monthly);
  const rrYearlyRows = byTypeMoneyRows(r => r.yearly);
  return (
    <div className="con-summary">
      {/* Four tiles: each period's two grosses. The NET is not a tile — it is
          the ruled last row of each by-type breakdown below, where it reads
          against the types it came from instead of restating a subtraction. */}
      <div className="con-run-tiles">
        {/* The icon names the PERIOD, not the direction — the label and the
            finance hue say which side, and a directional arrow would read as a
            rise or a fall in the figure beside it. */}
        <InfoTile className="dir-out" icon="calendar_month" label="Monthly out" value={rrMoney(rr.monthly)} foot={rrFoot} />
        <InfoTile className="dir-in" icon="calendar_month" label="Monthly in" value={rrMoney(rr.incomingMonthly)} foot={rr.incomingMonthly == null ? 'no incoming terms in force' : rrFoot} />
        <InfoTile className="dir-out" icon="event_repeat" label="Yearly out" value={rrMoney(rr.yearly)} foot={rrFoot} />
        <InfoTile className="dir-in" icon="event_repeat" label="Yearly in" value={rrMoney(rr.incomingYearly)} foot={rr.incomingYearly == null ? 'no incoming terms in force' : rrFoot} />
      </div>
      <div className="con-stats">
        {/* The tile owns its total: By type sums the records on file (archived
            excluded, as its rows are), and By status is given the total
            explicitly — its rows carry an "Ending soon" slice of Active, which
            an arithmetic sum would double-count. */}
        <BreakdownTile label="By type" rows={typeRows} empty="No active contracts." />
        <BreakdownTile label="By status" rows={statusRows} total={s.total} empty="No contracts." />
        {/* Money rows carry nodes, so each tile is handed its own net. */}
        <BreakdownTile className="con-bd-money" label="Monthly net by type" rows={rrMonthlyRows}
          total={netCell(rr.netMonthly)} totalLabel="Net" empty="No recurring terms in force." />
        <BreakdownTile className="con-bd-money" label="Yearly net by type" rows={rrYearlyRows}
          total={netCell(rr.netYearly)} totalLabel="Net" empty="No recurring terms in force." />
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
  /* The shared LIFECYCLE rank, not the wire ordinal: Draft = 5 and Ready = 6
     are appended enum members, so sorting on the ordinal would put the two
     earliest states last, behind Archived. */
  const CON_STATUS_ORDER = CON_H.CON_STATUS_RANK;
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
  /* The receipts beside them. Same row shape, same window, its OWN cap — a
     file with many charges must not be able to starve the receipts list. The
     list names the direction, so the row carries no direction field of its
     own: one fact, one place. */
  const upcomingReceipts = CON_H.conUpcomingReceipts(active, today, { windowDays: chargeWindow, limit: 6 });
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
  /* Awaiting signature: sent out, never returned. Not a dated cliff — the
     thing that makes it actionable is that nothing will move it on its own,
     and every day it sits there is a day an agreement everyone believes is in
     force is not. Drafts are deliberately NOT here: a draft is work in
     progress, and the status filter is where you go looking for it. */
  const awaitingSignature = active
    .filter(c => CON_H.conStatus(c, today) === 'Ready')
    .sort((a, b) => (a.ready < b.ready ? -1 : 1))
    .slice(0, 6);
  /* Paused agreements: not a cliff, but the one group here you cannot see by
     looking at a date — a contract that stopped costing money because someone
     froze it, and which nothing will un-freeze on its own. */
  const pausedRows = active
    .filter(c => CON_H.conStatus(c, today) === 'Paused')
    .sort((a, b) => (a.paused < b.paused ? 1 : -1))
    .slice(0, 6);
  const signal = (flagged.length || upcomingCharges.length || upcomingReceipts.length || recentlyExpired.length || startingSoon.length || pausedRows.length || awaitingSignature.length) ? {
    // The panel's worst severity wins the button: an expired term reads error,
    // a term running out reads warning, and next charges alone read info.
    severity: recentlyExpired.length ? 'error' : (flagged.length || awaitingSignature.length) ? 'warning' : 'info',
    count: flagged.length + upcomingCharges.length + upcomingReceipts.length + recentlyExpired.length + startingSoon.length + pausedRows.length + awaitingSignature.length,
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
        {awaitingSignature.length ? <div className="con-signal-group">Awaiting signature</div> : null}
        {awaitingSignature.map((c) => {
          const days = -CON_H.conDaysUntil(c.ready, today);
          return (
            <div key={c.id} className="alert warning compact signal-row" role="button" tabIndex={0}
              onClick={() => jumpTo(c.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(c.id); } }}>
              <SeverityIcon severity="warning" size={18} className="alert-icon" />
              <div className="alert-body"><strong>{c.name}.</strong> Ready for signature {days <= 0 ? 'today' : `${days} day${days === 1 ? '' : 's'} ago`} — not signed, not counted in the run rate.</div>
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
        {pausedRows.length ? <div className="con-signal-group">Paused</div> : null}
        {pausedRows.map((c) => {
          const days = -CON_H.conDaysUntil(c.paused, today);
          return (
            <div key={c.id} className="alert info compact signal-row" role="button" tabIndex={0}
              onClick={() => jumpTo(c.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(c.id); } }}>
              <SeverityIcon severity="info" size={18} className="alert-icon" />
              <div className="alert-body"><strong>{c.name}.</strong> Paused {days <= 0 ? 'today' : `${days} day${days === 1 ? '' : 's'} ago`} — not counted in the run rate.</div>
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
        {upcomingReceipts.length ? <div className="con-signal-group">Next receipts</div> : null}
        {upcomingReceipts.map(({ contract: c, term, date, days }) => {
          const ti = CON_H.contractTypeInfo(c.type);
          return (
            <div key={`in-${c.id}`} className="con-charge-row incoming" role="button" tabIndex={0}
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
              <span className="con-charge-amt mono in">{CON_H.money(term.value, term.currency || 'USD')}</span>
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
                options={CON_H.CON_STATUS_RANK.map(k => ({ value: k, label: CON_H.conStatusMeta(k).label }))} />
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
              <EmptyLine align="center" pad="lg">
                No contracts match your filters.
              </EmptyLine>
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

Object.assign(window, { Contracts, ContractsSummary });
