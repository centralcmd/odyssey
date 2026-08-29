/* AddInsurancePolicyModal — New policy dialog (Insurance page "New policy" button
   + the dashed add-row). Fields mirror the NewInsurancePolicy creation DTO:
     • name              (required)
     • policyNumber      (optional)
     • type              (InsurancePolicyType)
     • insurerId         (required → Contact, non-archived) — OdsCombobox
     • insuredAccountId  (optional → Account, non-archived)      — OdsCombobox
     • notes             (optional)
   Scalar ids only (no nested entities — the §6/§10 mass-assignment invariant).
   A new policy starts with no renewals (→ NoCoverage) and no documents. */

const AddInsurancePolicyModal = ({ onClose, onCreate, onSave, policy = null }) => {
  const { useState } = React;
  const D = window.OdysseyData;
  const editing = !!policy;

  const [draft, setDraft] = useState({
    name: policy?.name || '', policyNumber: policy?.policyNumber || '', type: policy?.type || '',
    insurerId: policy?.insurerId || '', insuredAccountId: policy?.insuredAccountId || '', notes: policy?.notes || '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); if (errors[k]) setErrors(e => ({ ...e, [k]: undefined })); };

  const insurerOptions = D.activeContacts().map(c => {
    const m = D.contactTypeByKey[c.type] || {};
    return { value: c.id, label: c.name, icon: m.icon, iconColor: m.color };
  });
  const accountOptions = D.accounts.filter(a => !a.archived).map(a => {
    const m = D.accountTypeById[a.type] || {};
    return { value: a.id, label: a.name, icon: m.icon, iconColor: m.color };
  });

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the policy a name.';
    if (!draft.type) next.type = 'Pick a policy type.';
    if (!draft.insurerId) next.insurerId = 'Choose the insurer.';
    if (Object.keys(next).length) { setErrors(next); return; }
    if (editing) {
      onSave && onSave({
        name: draft.name.trim(),
        policyNumber: draft.policyNumber.trim() || null,
        type: draft.type,
        insurerId: draft.insurerId,
        insuredAccountId: draft.insuredAccountId || null,
        notes: draft.notes.trim() || null,
      });
    } else {
      onCreate && onCreate({
        id: `ip-new-${Date.now()}`,
        name: draft.name.trim(),
        policyNumber: draft.policyNumber.trim() || null,
        type: draft.type,
        insurerId: draft.insurerId,
        insuredAccountId: draft.insuredAccountId || null,
        notes: draft.notes.trim() || null,
        archived: null, createdAtUtc: new Date().toISOString(),
        renewals: [], files: [],
      });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit insurance policy' : 'New insurance policy'}
      subtitle={editing
        ? 'Update the insurer and policy details. Renewal periods and documents are managed from the policy.'
        : 'Record the insurer and policy details, then add renewal periods and documents from the policy.'}
      icon="shield"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create policy'}
          </Button>
        </React.Fragment>
      }>
      <Field label="Policy name" value={draft.name} onChange={set('name')} placeholder="e.g. Home & Contents 2026" error={errors.name} autoFocus />

      <FormRow>
        <Field label="Policy number" value={draft.policyNumber} onChange={set('policyNumber')} placeholder="Optional — insurer's reference" />
        <InsurancePolicyTypeSelect value={draft.type} onChange={set('type')} error={errors.type} placeholder="Choose a type…" />
      </FormRow>

      <FieldShell label="Insurer" htmlFor="aip-insurer" error={errors.insurerId}
        helper={errors.insurerId ? undefined : 'The contact that issues this policy.'}>
        <Combobox id="aip-insurer" value={draft.insurerId} onChange={(v) => set('insurerId')(v)} options={insurerOptions}
          placeholder="Search insurers…" ariaLabel="Insurer" invalid={!!errors.insurerId} />
      </FieldShell>

      <FieldShell label="Insured account" htmlFor="aip-account" optional
        helper="Link the account representing the insured asset, if any.">
        <Combobox id="aip-account" value={draft.insuredAccountId} onChange={(v) => set('insuredAccountId')(v || '')} options={accountOptions}
          placeholder="Search accounts…" ariaLabel="Insured account" clearable />
      </FieldShell>

      <NoteField label="Notes" optional maxLength={1024} value={draft.notes} onChange={set('notes')}
        placeholder="What this policy covers, excess, named parties…" />
    </Modal>
  );
};

Object.assign(window, { AddInsurancePolicyModal });
