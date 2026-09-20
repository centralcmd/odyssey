/* AddContractModal — New contract dialog (Contracts page "New contract" button
   + the dashed add-row). Fields mirror the NewContract creation DTO (§6/§9):
     • name         (required, ≤256)
     • type         (ContractType — Employment / Service / Rental / Other)
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
   the contract's detail, by scalar id only (the §6/§10 mass-assignment rule). */

const AddContractModal = ({ onClose, onCreate, onSave, contract = null }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const editing = !!contract;

  const [mode, setMode] = useState(contract && contract.completionDate ? 'oneoff' : 'term'); // 'term' | 'oneoff'
  const [draft, setDraft] = useState({
    name: contract?.name || '', type: contract?.type || '', description: contract?.description || '',
    startDate: contract ? (H.conDateOnly(contract.startDate) || '') : H.conToday(),
    endDate: contract ? (H.conDateOnly(contract.endDate) || '') : '',
    completionDate: contract ? (H.conDateOnly(contract.completionDate) || '') : '',
    ready: contract ? (H.conDateOnly(contract.ready) || '') : '',
    signed: contract ? (H.conDateOnly(contract.signed) || '') : '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); if (errors[k]) setErrors(e => ({ ...e, [k]: undefined })); };

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the contract a name.';
    if (!draft.type) next.type = 'Pick a contract type.';
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
      onSave && onSave({ ...draft, name: draft.name.trim(), mode });
    } else {
      onCreate && onCreate({
        id: `ct-new-${Date.now()}`,
        name: draft.name.trim(),
        type: draft.type,
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
        ? 'Update the agreement’s name, type, term and signature dates. Parties and documents are managed from the contract.'
        : 'Record the agreement’s name, type and dates — a term or a one-off. It starts as a draft; mark it ready and signed from the contract itself.'}
      icon="handshake"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create contract'}
          </Button>
        </React.Fragment>
      }>
      <Field label="Contract name" required value={draft.name} onChange={set('name')} placeholder="e.g. Maple St Residence — Lease" error={errors.name} autoFocus />

      <FormRow>
        <ContractTypeSelect required value={draft.type} onChange={set('type')} error={errors.type} placeholder="Choose a type…" />
        <FieldShell label="Term">
          <SegmentedControl full value={mode} onChange={setMode}
            options={[{ value: 'term', label: 'Term' }, { value: 'oneoff', label: 'One-off' }]} />
        </FieldShell>
      </FormRow>

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
