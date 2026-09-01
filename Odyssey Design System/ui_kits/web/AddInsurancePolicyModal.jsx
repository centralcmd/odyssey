/* AddInsurancePolicyModal — New / edit policy dialog (Insurance page "New policy"
   button, the dashed add-row, and the row action "Edit").
   ----------------------------------------------------------------------------
   Fields mirror the NewInsurancePolicy / UpdateInsurancePolicy write DTOs:
     • name                (required)
     • policyNumber        (optional)
     • type                (InsurancePolicyType)
     • insurerIds          (optional → Contact[])   ┐
     • insuredAccountIds   (optional → Account[])   │ four link collections,
     • insuredContactIds   (optional → Contact[])   │ each a SET of scalar ids,
     • beneficiaryIds      (optional → Contact[])   ┘ carried through UNCHANGED
     • notes               (optional)
   Scalar ids only, at any depth (the §6/§10 mass-assignment invariant): the
   dialog never sends a nested Contact or Account object, so a policy write can
   never create or rename a linked record.

   The four collections are NOT edited here. A party is added, re-dated and
   removed one at a time from the policy's own "New party" action and its party
   tiles, which carry the member's TERM in the role — a fact this dialog has
   nowhere to put. So it reads the existing sets in and writes the same sets
   back untouched: a policy edit can neither add nor drop a party. (The
   TagMultiSelect pickers this dialog used to carry stay in the design system
   for the surfaces that do edit a whole set at once.) */

const AddInsurancePolicyModal = ({ onClose, onCreate, onSave, policy = null }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const editing = !!policy;

  const idsOf = (refs, key) => refs.map(r => r[key]);

  const [draft, setDraft] = useState({
    name: policy?.name || '', policyNumber: policy?.policyNumber || '', type: policy?.type || '',
    insurerIds: policy ? idsOf(H.insInsurers(policy), 'contactId') : [],
    insuredAccountIds: policy ? idsOf(H.insInsuredAccounts(policy), 'accountId') : [],
    insuredContactIds: policy ? idsOf(H.insInsuredContacts(policy), 'contactId') : [],
    beneficiaryIds: policy ? idsOf(H.insBeneficiaries(policy), 'contactId') : [],
    notes: policy?.notes || '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft(d => ({ ...d, [k]: v })); if (errors[k]) setErrors(e => ({ ...e, [k]: undefined })); };

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the policy a name.';
    if (!draft.type) next.type = 'Pick a policy type.';
    if (Object.keys(next).length) { setErrors(next); return; }
    // The link sets ride along exactly as they were read.
    const links = {
      insurerIds: draft.insurerIds,
      insuredAccountIds: draft.insuredAccountIds,
      insuredContactIds: draft.insuredContactIds,
      beneficiaryIds: draft.beneficiaryIds,
    };
    if (editing) {
      onSave && onSave({
        name: draft.name.trim(),
        policyNumber: draft.policyNumber.trim() || null,
        type: draft.type,
        ...links,
        notes: draft.notes.trim() || null,
      });
    } else {
      onCreate && onCreate({
        id: `ip-new-${Date.now()}`,
        name: draft.name.trim(),
        policyNumber: draft.policyNumber.trim() || null,
        type: draft.type,
        ...links,
        notes: draft.notes.trim() || null,
        archived: null, createdAtUtc: new Date().toISOString(),
        renewals: [],
      });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit insurance policy' : 'New insurance policy'}
      subtitle={editing
        ? 'Update the policy details. Parties, renewal periods and documents are managed from the policy.'
        : 'Record the policy details, then add parties, renewal periods and documents from the policy.'}
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

      <NoteField label="Notes" optional maxLength={1024} value={draft.notes} onChange={set('notes')}
        placeholder="What this policy covers, excess, claims history…" />
    </Modal>
  );
};

Object.assign(window, { AddInsurancePolicyModal });
