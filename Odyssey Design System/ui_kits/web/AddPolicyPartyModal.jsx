/* AddPolicyPartyModal — add ONE party to an existing policy, the insurance
   sibling of AddContractPartyModal (Insurance row action "New party").
   The same accessible two-step picker:
     1. choose the party ROLE — Insurer / Insured account / Insured contact /
        Beneficiary (the policy's four link collections)
     2. pick the record from a type-to-filter `Combobox` whose options are
        PRE-LOADED for the chosen role.
   The save carries a SCALAR ID and the collection it belongs to — never a
   nested Contact or Account object (the §6/§10 mass-assignment invariant), so
   adding a party can never create or rename the linked record. Already-linked
   records are filtered out of the picker, so the same record cannot be
   attached twice in the same role. The full set is still editable field by
   field in "Edit policy"; this is the one-off add.

   With `party` ({ field, id, fromDate, toDate }) the same dialog EDITS that
   link — the hover action on a party tile. Role and record stay editable: a
   party moved to another role, or pointed at another record, is the same
   correction as a re-typed date, and the save carries the old link so the
   caller can replace it. */

const POLICY_PARTY_ROLES = [
  { role: 'insurer',   label: 'Insurer',          icon: 'groups',                   field: 'insurerIds',        noun: 'contact', help: 'The contact that carries this cover.' },
  { role: 'account',   label: 'Insured account',  icon: 'account_balance_wallet',   field: 'insuredAccountIds', noun: 'account', help: 'An account representing an insured asset.' },
  { role: 'contact',   label: 'Insured contact',  icon: 'person',                   field: 'insuredContactIds', noun: 'contact', help: 'A person or organisation insured under this policy.' },
  { role: 'beneficiary', label: 'Beneficiary',    icon: 'volunteer_activism',       field: 'beneficiaryIds',    noun: 'contact', help: 'Who receives on this policy.' },
];

const AddPolicyPartyModal = ({ policy, party = null, optionsLoading = false, onClose, onAdd, onSave, onRemove }) => {
  const { useState } = React;
  const D = window.OdysseyData;
  const editing = !!party;

  /* The party's TERM in the role — independent of the policy's own periods,
     with one tie: it cannot begin before cover ever did. Both dates default to
     NULL, and null is read as the policy's own extent — from the first
     period's start, open-ended — so a party added with the defaults follows
     the policy for its whole lifetime and a later renewal never re-dates it. */
  const firstStart = (policy.renewals || []).map(r => r.fromDate).sort()[0] || null;

  const [role, setRole] = useState(editing
    ? (POLICY_PARTY_ROLES.find(r => r.field === party.field) || POLICY_PARTY_ROLES[0]).role
    : 'insurer');
  const [value, setValue] = useState(editing ? party.id : '');
  const [fromDate, setFromDate] = useState(editing ? (party.fromDate || null) : null);
  const [toDate, setToDate] = useState(editing ? (party.toDate || null) : null);
  const [error, setError] = useState(null);
  const [dateError, setDateError] = useState({});

  const def = POLICY_PARTY_ROLES.find(r => r.role === role);
  const linked = new Set(policy[def.field] || []);
  const allOptions = def.noun === 'account'
    ? D.accounts.filter(a => !a.archived).map(a => {
      const m = D.accountTypeById[a.type] || {};
      return { value: a.id, label: a.name, icon: m.icon, iconColor: m.color, sub: m.label };
    })
    : D.activeContacts().map(c => {
      const m = D.contactTypeByKey[c.type] || {};
      return { value: c.id, label: c.name, icon: m.icon, iconColor: m.color, sub: m.label };
    });
  // The party being edited is not "already linked" as far as its own picker is
  // concerned — only its siblings are.
  const options = allOptions.filter(o => !linked.has(o.value) || (editing && o.value === party.id));

  const pickRole = (r) => { setRole(r); setValue(''); setError(null); };

  const submit = () => {
    if (!value) { setError(`Select a ${def.noun} to link.`); return; }
    const de = {};
    if (fromDate && firstStart && fromDate < firstStart) de.fromDate = `Cover began ${firstStart} — a party can’t be in the role before that.`;
    if (fromDate && toDate && toDate < fromDate) de.toDate = 'End date can’t be before the start date.';
    if (Object.keys(de).length) { setDateError(de); return; }
    const dto = { field: def.field, id: value, fromDate: fromDate || null, toDate: toDate || null };
    if (editing) onSave && onSave(dto, { field: party.field, id: party.id });
    else onAdd && onAdd(dto);
  };

  return (
    <Modal
      title={editing ? 'Edit party' : 'New party'}
      subtitle={editing
        ? 'Change the role, the linked record, or the dates this party is in the role.'
        : 'Link a contact or account to this policy in one of its four roles.'}
      icon="group_add"
      onClose={onClose}
      footer={
        <React.Fragment>
          {/* Removing the party detaches the record from the policy — the
              record itself is untouched, so this is not a delete. */}
          {editing && onRemove ? (
            <Button variant="danger" icon="link_off" className="ins-remove-party"
              onClick={() => onRemove({ field: party.field, id: party.id })}>Remove party</Button>
          ) : null}
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create party'}
          </Button>
        </React.Fragment>
      }>
      <FieldShell label="Role">
        <div className="ins-kind-seg" role="radiogroup" aria-label="Role">
          {POLICY_PARTY_ROLES.map(r => (
            <button type="button" key={r.role} role="radio" aria-checked={role === r.role}
              className={`ins-kind-opt ${role === r.role ? 'on' : ''}`} onClick={() => pickRole(r.role)}>
              <span className="material-icons" aria-hidden="true">{r.icon}</span>
              <span className="ins-kind-lab">{r.label}</span>
            </button>
          ))}
        </div>
      </FieldShell>

      <FieldShell label={def.label} htmlFor="app-target" error={error}
        helper={error ? undefined : (options.length
          ? `${def.help} ${options.length} ${def.noun}${options.length === 1 ? '' : 's'} available to link.`
          : `Every ${def.noun} is already linked to this policy in this role.`)}>
        <Combobox id="app-target" value={value} onChange={(v) => { setValue(v || ''); if (error) setError(null); }}
          options={options} loading={optionsLoading}
          placeholder={`Search ${def.noun}s…`}
          ariaLabel={def.label} invalid={!!error} />
      </FieldShell>

      {/* The term is the party's own fact, not the policy's: left as it lands,
          the party is on the policy for its whole life. */}
      <SectionDivider label="In the role" meta="optional" />
      <FormRow>
        <DateField label="From" value={fromDate}
          onChange={(v) => { setFromDate(v || null); setDateError(e => ({ ...e, fromDate: undefined })); }}
          help={firstStart ? `Leave empty to start with the policy (${firstStart}).` : 'Leave empty to start with the policy.'}
          error={dateError.fromDate} />
        <DateField label="To" value={toDate}
          onChange={(v) => { setToDate(v || null); setDateError(e => ({ ...e, toDate: undefined })); }}
          help="Leave empty while the party is still in the role."
          error={dateError.toDate} />
      </FormRow>
    </Modal>
  );
};

Object.assign(window, { AddPolicyPartyModal });
