/* AddContractModal — New contract dialog (Contracts page "New contract" button
   + the dashed add-row). Fields mirror the NewContract creation DTO (§6/§9):
     • name         (required, ≤256)
     • type         (ContractType — Employment / Service / Rental / Other)
     • description  (optional, ≤1024)
     • term         a contract is either TERM-based or ONE-OFF:
         – Term:    startDate (optional) + endDate (optional; ≥ startDate)
         – One-off: completionDate (required) — a point-in-time agreement
                    (a purchase / closing), no ongoing term
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
        archived: null, createdAtUtc: new Date().toISOString(),
        parties: [], files: [],
      });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit contract' : 'New contract'}
      subtitle={editing
        ? 'Update the agreement’s name, type and dates — a term or a one-off. Parties and documents are managed from the contract.'
        : 'Record the agreement’s name, type and dates — a term or a one-off — then add the parties and documents from the contract.'}
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
      <Field label="Contract name" value={draft.name} onChange={set('name')} placeholder="e.g. Maple St Residence — Lease" error={errors.name} autoFocus />

      <FormRow>
        <ContractTypeSelect value={draft.type} onChange={set('type')} error={errors.type} placeholder="Choose a type…" />
        <FieldShell label="Term">
          <SegmentedControl full value={mode} onChange={setMode}
            options={[{ value: 'term', label: 'Term' }, { value: 'oneoff', label: 'One-off' }]} />
        </FieldShell>
      </FormRow>

      {mode === 'term' ? (
        <FormRow>
          <div className="field">
            <Field type="date" label="Starts (optional)" value={draft.startDate} onChange={set('startDate')} placeholder="No start date" />
          </div>
          <div className="field">
            <Field type="date" label="Ends (optional)" value={draft.endDate} onChange={set('endDate')} placeholder="Open-ended" />
            {errors.endDate
              ? <div className="helper aam-err">{errors.endDate}</div>
              : <div className="helper">Leave empty for an open-ended agreement.</div>}
          </div>
        </FormRow>
      ) : (
        <FormRow>
          <div className="field">
            <Field type="date" label="Completion" value={draft.completionDate} onChange={set('completionDate')} placeholder="Completion date" />
            {errors.completionDate
              ? <div className="helper aam-err">{errors.completionDate}</div>
              : <div className="helper">The one-off closing / delivery date — no ongoing term.</div>}
          </div>
          <div />
        </FormRow>
      )}

      <NoteField label="Description" optional maxLength={1024} value={draft.description} onChange={set('description')}
        placeholder="What this agreement covers, term, notice period, key conditions…" />
    </Modal>
  );
};

Object.assign(window, { AddContractModal });
