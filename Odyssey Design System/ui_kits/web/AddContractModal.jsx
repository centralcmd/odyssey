/* AddContractModal — New contract dialog (Contracts page "New contract" button
   + the dashed add-row). Fields mirror the NewContract creation DTO (§6/§9):
     • name         (required, ≤256)
     • type         (ContractType — Employment / Service / Rental / Other)
     • referenceNumber (optional, ≤64) — the COUNTERPARTY's number off the
                    paperwork. DS ReferenceNumberField: live length and
                    hidden-character checks (the DTO's [StringLength] and
                    Cc/Cf/Co/Cn deny-list), trim on blur, blank → null. Not
                    unique, so there is no "already in use" state to draw.
                    Full-replacement PUT: on edit the loaded value is
                    pre-filled and sent back, so saving an unrelated change
                    never clears it; emptying the field is how it is cleared.
     • description  (optional, ≤1024)
     • term         a contract is either TERM-based or ONE-OFF:
         – Term:    startDate (optional) + endDate (optional; ≥ startDate)
         – One-off: completionDate (required) — a point-in-time agreement
                    (a purchase / closing), no ongoing term
     • ready        (optional) — marked ready for signature on this date
     • signed       (optional) — signed by all parties on this date
   The two SIGNATURE stamps are here as ordinary dates so a paper contract
   signed last month can be entered in one go; the common path is the row
   menu's one-click Mark ready / Mark signed. Both omitted creates a Draft,
   which is the normal case. The three guards below are the client half of the
   server's — a signed date needs a ready date, cannot precede it, and neither
   can be in the future. Nothing constrains them against the TERM dates:
   signing after cover has begun is ordinary, and signing before it starts is
   the normal case.
   A new contract starts with no parties and no documents — both are added from
   the contract's detail, by scalar id only (the §7.6 mass-assignment rule).

   On EDIT the type is the one field that can be refused by something other
   than itself: changing it to a type the existing parties' roles are illegal
   on is a 422 (TypeChangeOrphansParties), and the server writes nothing. The
   dialog computes the same thing from the shared matrix, names each offending
   party, and states the two routes that work — re-role them, or detach them —
   rather than letting the user save into a refusal. */

const AddContractModal = ({ onClose, onCreate, onSave, contract = null, initialType = null }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const editing = !!contract;

  const [mode, setMode] = useState(contract && contract.completionDate ? 'oneoff' : 'term'); // 'term' | 'oneoff'
  const [draft, setDraft] = useState({
    name: contract?.name || '', type: initialType || contract?.type || '', description: contract?.description || '',
    referenceNumber: contract?.referenceNumber || '',
    startDate: contract ? (H.conDateOnly(contract.startDate) || '') : H.conToday(),
    endDate: contract ? (H.conDateOnly(contract.endDate) || '') : '',
    completionDate: contract ? (H.conDateOnly(contract.completionDate) || '') : '',
    ready: contract ? (H.conDateOnly(contract.ready) || '') : '',
    signed: contract ? (H.conDateOnly(contract.signed) || '') : '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); if (errors[k]) setErrors(e => ({ ...e, [k]: undefined })); };

  /* The parties the INCOMING type would orphan — the client half of the 422.
     Only meaningful on edit, and only once the type actually differs. */
  const typeChanged = editing && draft.type && draft.type !== contract.type;
  const orphans = typeChanged ? H.conPartiesRejectedByType(contract.parties, draft.type) : [];
  const blockedByParties = orphans.length > 0;

  const normRef = (v) => { const t = (v || '').trim(); return t === '' ? null : t; };
  const DSNS = window.OdysseyDesignSystem_d5aa51 || {};
  const RefField = DSNS.ReferenceNumberField;

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the contract a name.';
    if (!draft.type) next.type = 'Pick a contract type.';
    const RN = (window.OdysseyDesignSystem_d5aa51 || {}).REFERENCE_NUMBER_RULES;
    const refErr = RN && RN.validate(draft.referenceNumber);
    if (refErr) next.referenceNumber = refErr.message;
    if (blockedByParties) next.type = `This type rejects ${orphans.length} existing part${orphans.length === 1 ? 'y' : 'ies'}.`;
    if (mode === 'oneoff') {
      if (!draft.completionDate) next.completionDate = 'Set a completion date.';
    } else if (draft.endDate && draft.startDate && draft.endDate < draft.startDate) {
      next.endDate = '“Ends” can’t be before “Starts”.';
    }
    /* The shared signature guards, run identically on create and edit — one
       helper, the way the server shares one between POST and PUT. Clearing a
       stamp is never refused. */
    const sig = H.conSignatureError(draft);
    if (sig) next[sig.field] = sig.message.replace(/^Unable to save\. /, '');
    if (Object.keys(next).length) { setErrors(next); return; }
    if (editing) {
      // Parity with the list item's saveEdit patch shape.
      onSave && onSave({ ...draft, name: draft.name.trim(), referenceNumber: normRef(draft.referenceNumber), mode });
    } else {
      onCreate && onCreate({
        id: `ct-new-${Date.now()}`,
        name: draft.name.trim(),
        type: draft.type,
        referenceNumber: normRef(draft.referenceNumber),
        description: draft.description.trim() || null,
        startDate: mode === 'oneoff' ? null : (draft.startDate || null),
        endDate: mode === 'oneoff' ? null : (draft.endDate || null),
        completionDate: mode === 'oneoff' ? draft.completionDate : null,
        // Both omitted is the normal path — a new contract starts as a Draft.
        ready: draft.ready || null, signed: draft.signed || null,
        paused: null, archived: null, createdAtUtc: new Date().toISOString(),
        parties: [], files: [],
      });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit contract' : 'New contract'}
      subtitle={editing
        ? 'Update the agreement’s name, reference, type, term and signature dates. Parties and documents are managed from the contract.'
        : 'Record the agreement’s name, type and dates — a term or a one-off. It starts as a draft; mark it ready and signed from the contract itself.'}
      icon="handshake"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit} disabled={blockedByParties}>
            {editing ? 'Save changes' : 'Create contract'}
          </Button>
        </React.Fragment>
      }>
      <Field label="Contract name" required value={draft.name} onChange={set('name')} placeholder="e.g. Maple St Residence — Lease" error={errors.name} autoFocus />

      {RefField ? (
        <RefField value={draft.referenceNumber} onChange={set('referenceNumber')}
          error={errors.referenceNumber && !DSNS.REFERENCE_NUMBER_RULES ? errors.referenceNumber : undefined} />
      ) : (
        <Field label="Reference number" value={draft.referenceNumber} onChange={set('referenceNumber')}
          placeholder="e.g. AGR-2026/114-B.2" error={errors.referenceNumber} />
      )}

      <FormRow>
        <ContractTypeSelect required value={draft.type} onChange={set('type')} error={errors.type} placeholder="Choose a type…" />
        <FieldShell label="Term">
          <SegmentedControl full value={mode} onChange={setMode}
            options={[{ value: 'term', label: 'Term' }, { value: 'oneoff', label: 'One-off' }]} />
        </FieldShell>
      </FormRow>

      {blockedByParties ? (
        <Alert severity="warning">
          <div><strong>A {H.contractTypeInfo(draft.type).label.toLowerCase()} contract can’t hold {orphans.length === 1 ? 'this party' : 'these parties'}.</strong></div>
          <div>
            {orphans.length === 1 ? 'One party holds' : `${orphans.length} parties hold`} a role that a{' '}
            {H.contractTypeInfo(draft.type).label.toLowerCase()} contract does not accept. Re-role{' '}
            {orphans.length === 1 ? 'it' : 'them'} to {H.conRoleListText(draft.type)}, or detach{' '}
            {orphans.length === 1 ? 'it' : 'them'} — then change the type. Nothing is saved until then.
          </div>
          <div className="con-orphans">
            {orphans.map(o => (
              <div className="con-orphan" key={o.partyId}>
                <span className="con-orphan-role">{o.roleLabel}</span>
                <span className="con-orphan-name">{o.displayName}</span>
                <span className="con-orphan-id">{o.partyId}</span>
              </div>
            ))}
          </div>
        </Alert>
      ) : null}

      {mode === 'term' ? (
        <FormRow>
          <div className="field">
            <Field type="date" label="Starts" value={draft.startDate} onChange={set('startDate')} placeholder="No start date" />
          </div>
          <div className="field">
            <Field type="date" label="Ends" value={draft.endDate} onChange={set('endDate')} placeholder="Open-ended" />
            {errors.endDate
              ? <div className="helper aam-err">{errors.endDate}</div>
              : <div className="helper">Leave empty for an open-ended agreement.</div>}
          </div>
        </FormRow>
      ) : (
        <FormRow>
          <div className="field">
            <Field type="date" label="Completion" required value={draft.completionDate} onChange={set('completionDate')} placeholder="Completion date" />
            {errors.completionDate
              ? <div className="helper aam-err">{errors.completionDate}</div>
              : <div className="helper">The one-off closing / delivery date — no ongoing term.</div>}
          </div>
          <div />
        </FormRow>
      )}

      {/* SIGNATURE — edit only. A brand-new contract always starts as a
          Draft, so the stamps are set later from the contract itself; on edit
          they matter, and neither is required. */}
      {editing ? (
      <FormRow>
        <div className="field">
          <Field type="date" label="Ready for signature" value={draft.ready} onChange={set('ready')} placeholder="Not yet ready" />
          {errors.ready
            ? <div className="helper aam-err">{errors.ready}</div>
            : <div className="helper">Leave blank while it is still being drafted.</div>}
        </div>
        <div className="field">
          <Field type="date" label="Signed" value={draft.signed} onChange={set('signed')} placeholder="Not signed" />
          {errors.signed
            ? <div className="helper aam-err">{errors.signed}</div>
            : <div className="helper">Signed by all parties. Until this is set, the contract stays out of the run rate.</div>}
        </div>
      </FormRow>
      ) : null}

      <NoteField label="Description" optional maxLength={1024} value={draft.description} onChange={set('description')}
        placeholder="What this agreement covers, term, notice period, key conditions…" />
    </Modal>
  );
};

Object.assign(window, { AddContractModal });
