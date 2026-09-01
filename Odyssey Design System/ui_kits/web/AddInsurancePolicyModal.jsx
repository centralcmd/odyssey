/* AddInsurancePolicyModal — New / edit policy dialog (Insurance page "New policy"
   button, the dashed add-row, and the row action "Edit").
   ----------------------------------------------------------------------------
   Fields mirror the NewInsurancePolicy / UpdateInsurancePolicy write DTOs:
     • name                (required)
     • policyNumber        (optional)
     • type                (InsurancePolicyType)
     • insurerIds          (optional → Contact[])   ┐
     • insuredAccountIds   (optional → Account[])   │ four link collections,
     • insuredContactIds   (optional → Contact[])   │ each a SET of scalar ids
     • beneficiaryIds      (optional → Contact[])   ┘
     • notes               (optional)
   Scalar ids only, at any depth (the §6/§10 mass-assignment invariant): the
   dialog never sends a nested Contact or Account object, so a policy write can
   never create or rename a linked record.

   All four collections are OPTIONAL — a policy drafted before the insurer is
   known is a valid, healthy record, so nothing here is required but the name and
   the type. The save carries the complete desired set for each collection.

   Chip order follows the picker's own model while editing (parent order, new ids
   appended); the server orders by resolved display name, so the dialog RE-SORTS
   ON LOAD only — the user never sees a reorder mid-edit.

   An UNNAMED member (its contact archived, or no longer resolvable) has no name
   in the read model, so it renders through ContactChip's Archived / Unavailable
   state and carries NO remove control: an ordinary write cannot remove it. The
   field help names the two routes that do work, detach first. */

const AIP_LINK_CEILING = 50; // InsuranceLinkLimits.MaxLinksPerPolicy — a shared
                             // compile-time constant, safe to guard against here.

const AddInsurancePolicyModal = ({ onClose, onCreate, onSave, policy = null, optionsLoading = false, effectiveCap = null }) => {
  const { useState, useRef } = React;
  const D = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const editing = !!policy;

  // Server order on load (resolved display name ascending); the picker owns
  // order from here.
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

  // Focus APIs — a failed save moves focus to the first offending picker.
  const api = {
    insurerIds: useRef(null), insuredAccountIds: useRef(null),
    insuredContactIds: useRef(null), beneficiaryIds: useRef(null),
  };

  const contactOptions = D.activeContacts().map(c => {
    const m = D.contactTypeByKey[c.type] || {};
    return { value: c.id, label: c.name, icon: m.icon, iconColor: m.color, sub: m.label };
  });
  const accountOptions = D.accounts.filter(a => !a.archived).map(a => {
    const m = D.accountTypeById[a.type] || {};
    return { value: a.id, label: a.name, icon: m.icon, iconColor: m.color, sub: m.label };
  });

  // A member the picker cannot remove: no live, non-archived contact to have
  // been chosen from. Kept by the bulk Clear, and rendered with no remove
  // control — the write path refuses its removal, so the affordance would lie.
  const contactUnnamed = (id) => {
    const c = D.contactById[id];
    return !c || !!c.archived;
  };
  // The chip body for a contact link: the DS ContactChip, which already carries
  // the Archived ("(archived)") and Unavailable (link_off) states in TEXT.
  const contactChip = (id) => {
    // The DS component, read off the namespace — NOT window.ContactChip, which
    // the Journal page overwrites with its own {cp}-shaped adapter.
    const Chip = (window.OdysseyDesignSystem_d5aa51 || {}).ContactChip;
    const c = D.contactById[id];
    if (!Chip) return <span>{c ? c.name : 'Unavailable'}</span>;
    // Name withheld for an unnamed member — the read model never carries it.
    if (!c) return <Chip contact={{ unavailable: true }} size="sm" />;
    if (c.archived) return <Chip contact={{ name: 'Archived contact', type: c.type, archived: c.archived }} size="sm" />;
    return <Chip name={c.name} type={c.type} size="sm" />;
  };

  const LINK_HELP = 'Archived or unresolvable members keep their place without a name and cannot be removed here — detach them from the contact, or unarchive the contact first.';
  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the policy a name.';
    if (!draft.type) next.type = 'Pick a policy type.';
    // The client guards only the shared compile-time ceiling. The EFFECTIVE cap
    // is a server setting — it is never copied into the client, so an over-cap
    // collection is learned from the save's 422, with the cap interpolated.
    const cap = effectiveCap;
    for (const [k, name] of [['insurerIds', 'insurers'], ['insuredAccountIds', 'insured accounts'],
      ['insuredContactIds', 'insured contacts'], ['beneficiaryIds', 'beneficiaries']]) {
      const n = draft[k].length;
      if (n > AIP_LINK_CEILING) next[k] = `A policy takes at most ${AIP_LINK_CEILING} ${name}.`;
      else if (cap != null && n > cap) next[k] = `At most ${cap} ${name} per policy. Remove ${n - cap} to save.`;
    }
    if (Object.keys(next).length) {
      setErrors(next);
      const first = ['name', 'type', 'insurerIds', 'insuredAccountIds', 'insuredContactIds', 'beneficiaryIds'].find(k => next[k]);
      const ref = api[first];
      if (ref && ref.current) ref.current.focus();
      return;
    }
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

  const linkPicker = ({ key, label, addLabel, placeholder, options, help, noun, contacts = true }) => {
    // The unnamed-member rule is stated where it is LIVE — on a field that
    // actually holds one — rather than three times over as standing noise.
    const hasUnnamed = contacts && draft[key].some(contactUnnamed);
    return (
    <TagMultiSelect
      id={`aip-${key}`}
      label={label}
      optional
      value={draft[key]}
      onChange={set(key)}
      options={options}
      loading={optionsLoading}
      loadingText="Loading…"
      placeholder={placeholder}
      addLabel={addLabel}
      noun={noun}
      searchLabel={contacts ? 'Search contacts' : 'Search accounts'}
      searchPlaceholder={contacts ? 'Search contacts…' : 'Search accounts…'}
      emptyText={contacts ? 'No contacts match' : 'No accounts match'}
      unknownLabel="Unavailable"
      chipTemplate={contacts ? contactChip : undefined}
      preserveOnClear={contacts ? contactUnnamed : undefined}
      apiRef={api[key]}
      error={errors[key]}
      help={errors[key] ? undefined : (hasUnnamed ? `${help} ${LINK_HELP}` : help)}
    />
    );
  };

  return (
    <Modal
      title={editing ? 'Edit insurance policy' : 'New insurance policy'}
      subtitle={editing
        ? 'Update the policy details and who is on it. Renewal periods and documents are managed from the policy.'
        : 'Record the policy details and who is on it, then add renewal periods and documents from the policy.'}
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

      {/* The four collections read as four form fields — homogeneous per field,
          each a set of ids, all optional. The divider keeps a now-eight-field
          dialog scannable without hiding anything behind a disclosure. */}
      <SectionDivider label="Parties & insured assets" meta="all optional" />

      {linkPicker({
        key: 'insurerIds', label: 'Insurers', addLabel: 'Add insurer', placeholder: 'No insurers',
        options: contactOptions, noun: 'insurer',
        help: 'The contacts that carry this cover — several where it is placed across co-insurers.',
      })}

      {linkPicker({
        key: 'insuredAccountIds', label: 'Insured accounts', addLabel: 'Add account', placeholder: 'No insured accounts',
        options: accountOptions, noun: 'account', contacts: false,
        help: 'The accounts representing the insured assets — a house and an outbuilding, two vehicles.',
      })}

      {linkPicker({
        key: 'insuredContactIds', label: 'Insured contacts', addLabel: 'Add contact', placeholder: 'No insured contacts',
        options: contactOptions, noun: 'contact',
        help: 'The people and organisations insured under this policy — the policyholder, a spouse, named drivers.',
      })}

      {linkPicker({
        key: 'beneficiaryIds', label: 'Beneficiaries', addLabel: 'Add beneficiary', placeholder: 'No beneficiaries',
        options: contactOptions, noun: 'beneficiary',
        help: 'Who receives on this policy. A person, or an organisation such as a trust or an estate.',
      })}

      <NoteField label="Notes" optional maxLength={1024} value={draft.notes} onChange={set('notes')}
        placeholder="What this policy covers, excess, claims history…" />
    </Modal>
  );
};

Object.assign(window, { AddInsurancePolicyModal });
