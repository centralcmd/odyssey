/* AddContractPartyModal — add OR edit one party on a contract (§5:
   POST …/parties and PUT …/parties/{partyId}).

   The accessible picker the spec mandates, now three decisions deep:
     1. the party KIND — Account / Contact / Property (the one-of-three target)
     2. the ROLE the record plays in the agreement — a FIXED PER-TYPE LIST read
        off the shared contract-type × role matrix, suggested roles first. The
        picker offers no role the server would refuse, so the 422 is a backstop
        rather than a thing the user is walked into.
     3. the specific record, from a type-to-filter `Combobox` whose options are
        PRE-LOADED for the chosen kind (no in-widget async fetch).
   …plus the optional TERM — FromDate / ToDate — with the insurance-party
   semantics: both null is the DEFAULT term, the contract's own extent, not an
   unset value.

   There is NO DEFAULT ROLE. `Unspecified` was retired with the matrix and a
   role is required on every write, so the control starts empty and Save stays
   disabled until one is picked — the client half of the server's `[Required]`.

   The save carries SCALAR IDS ONLY (exactly one of accountId / contactId /
   propertyId) — never a nested Account, Contact or Property object (§7.4
   write-path invariant), so a party write can never create or rename the
   linked record.

   PROPERTY kind: any role the contract's type allows — the kind implies no
   role and no role implies the kind (Non-Goal 1). Archived and disposed
   properties stay linkable so history can still be recorded; the helper says
   so when one is picked. The property's detail fields (address, VIN…) never
   appear here — the picker shows name + kind only.

   Duplicate guard: uniqueness is (contract, target, ROLE), so the picker only
   hides records already linked IN THE SELECTED ROLE, and the party being edited
   is excluded from its own check. The same contact can therefore be Insurer on
   one role and Broker on another — one contract, two parties.

   In edit mode the PUT is a FULL REPLACEMENT of the link, not a patch: a
   cleared date clears. The dialog states that where the decision is made
   rather than after the fact. */

const CONTRACT_PARTY_KINDS = [
  { kind: 'account', label: 'Account', icon: 'account_balance_wallet', field: 'accountId' },
  { kind: 'contact', label: 'Contact', icon: 'groups',                 field: 'contactId' },
  { kind: 'property', label: 'Property', icon: 'home_work',            field: 'propertyId' },
];
const CP_ARTICLE = { account: 'an account', contact: 'a contact', property: 'a property' };

const AddContractPartyModal = ({ contract, party = null, onClose, onAdd, onSave }) => {
  const { useState, useMemo } = React;
  const H = window.OdysseyHelpers;
  const editing = !!party;

  const [kind, setKind] = useState(editing && party.contactId ? 'contact' : editing && party.propertyId ? 'property' : 'account');
  // No default: the role is chosen, never inherited. An edited party keeps the
  // role it holds — including a legacy one the matrix no longer accepts, which
  // is exactly the party this dialog exists to correct.
  const [role, setRole] = useState(editing ? (party.role || '') : '');
  const [value, setValue] = useState(editing ? (party.accountId || party.contactId || party.propertyId || '') : '');
  const [fromDate, setFromDate] = useState(editing ? (party.fromDate || null) : null);
  const [toDate, setToDate] = useState(editing ? (party.toDate || null) : null);
  const [error, setError] = useState(null);
  const [roleError, setRoleError] = useState(null);
  const [dateError, setDateError] = useState({});

  const def = CONTRACT_PARTY_KINDS.find(k => k.kind === kind);
  const roleInfo = H.conPartyRoleInfo(role);
  const typeInfo = H.contractTypeInfo(contract.type);
  // A role this contract's type would refuse — only reachable on an edit of a
  // party written before the matrix, or before the contract's type changed.
  const roleRejected = !!role && H.conRoleLegality(contract.type, role) === 'rejected';
  // The contract's own start is the only anchor, and only when it has one: a
  // one-off (completion date) and an open-started term take no lower bound.
  const startDate = H.conDateOnly(contract.startDate);

  // Records already holding THIS role on this contract — the only ones the
  // picker must withhold (uniqueness is per role).
  const taken = useMemo(
    () => H.conPartyTaken(contract.parties, def.field, role, editing ? party.id : null),
    [contract.parties, def.field, role, editing, party]);

  const allOptions = kind === 'account' ? H.conAccountOptions() : kind === 'property' ? H.conPropertyOptions() : H.conInstitutionOptions();
  const options = allOptions.filter(o => !taken.has(o.value));
  const roleNoun = role ? roleInfo.label.toLowerCase() : 'this role';

  const pluralNoun = (n) => (kind === 'property' ? (n === 1 ? 'property' : 'properties') : `${def.label.toLowerCase()}${n === 1 ? '' : 's'}`);
  const picked = kind === 'property' && value ? allOptions.find(o => o.value === value) : null;
  const pickedNote = picked && (picked.archived || picked.status !== 'Owned')
    ? `This property is ${picked.archived ? 'archived' : picked.status.toLowerCase()} — it can still be linked, so the contract’s history stays complete.`
    : null;
  const pickKind = (k) => { setKind(k); setValue(''); setError(null); };
  const pickRole = (r) => { setRole(r); setError(null); setRoleError(null); };

  const submit = () => {
    if (!role) { setError(null); setRoleError('Pick the role this record plays in the agreement.'); return; }
    if (!value) { setError(`Select ${CP_ARTICLE[kind]} to link.`); return; }
    const de = {};
    if (fromDate && startDate && fromDate < startDate)
      de.fromDate = `This contract starts ${H.conDate(startDate)} — a party can’t be in the role before that.`;
    if (fromDate && toDate && toDate < fromDate) de.toDate = 'End date can’t be before the start date.';
    if (Object.keys(de).length) { setDateError(de); return; }
    const dto = {
      id: editing ? party.id : `cp-new-${Date.now()}`,
      accountId: kind === 'account' ? value : null,
      contactId: kind === 'contact' ? value : null,
      propertyId: kind === 'property' ? value : null,
      role, fromDate: fromDate || null, toDate: toDate || null,
    };
    if (editing) onSave && onSave(dto); else onAdd && onAdd(dto);
  };

  return (
    <Modal
      title={editing ? 'Edit party' : 'New party'}
      subtitle={editing
        ? 'Change the role, the linked record, or the dates this party is in the role.'
        : 'Link the account, contact or property this contract relates to, and say what it does in the agreement.'}
      icon="group_add"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}
            disabled={!role || !value}>
            {editing ? 'Save changes' : 'Create party'}
          </Button>
        </React.Fragment>
      }>
      <FieldShell label="Party kind" required>
        <CardSelect ariaLabel="Party kind" value={kind} onChange={pickKind}
          accent="var(--con-accent)" accentLine="var(--con-accent-line)" accentSoft="var(--con-accent-soft)"
          columns={3} maxItemWidth={180} center
          options={CONTRACT_PARTY_KINDS.map(k => ({ value: k.kind, label: k.label, icon: k.icon }))} />
      </FieldShell>

      {/* ROLE — a plain registry Select, not a card grid: the legal list is
          short but the whole vocabulary is fifteen members, and the role is the
          one field a reader scans for. What this type accepts is decided by the
          shared matrix, not by this dialog. */}
      <ContractPartyRoleSelect id="acp-role" label="Role" required value={role} onChange={pickRole}
        contractType={contract.type} error={roleError}
        placeholder={`Choose a role on this ${typeInfo.label.toLowerCase()} contract…`}
        helper={roleError ? undefined : (role ? roleInfo.desc : `A ${typeInfo.label.toLowerCase()} contract takes ${H.conRoleListText(contract.type)}.`)} />

      {roleRejected ? (
        <Alert severity="warning">
          <strong>{roleInfo.label}</strong> is no longer accepted on a {typeInfo.label.toLowerCase()} contract.
          Saving requires one of the roles offered above — the server refuses this one.
        </Alert>
      ) : null}

      {/* The Contact kind uses the canonical ContactSelect, create rows and all;
          the Account kind keeps the plain Combobox over accounts. */}
      {kind === 'contact' ? (
        <ContactSelect id="acp-target" label={def.label} required error={error} allowCreate
          help={error ? undefined : (options.length
            ? `${options.length} contact${options.length === 1 ? '' : 's'} available in this role.`
            : `Every contact already holds ${roleNoun} on this contract — or add a new one below.`)}
          value={value} onChange={(v) => { setValue(v || ''); if (error) setError(null); }}
          options={options} placeholder="Search contacts…" ariaLabel={def.label} />
      ) : (
        <FieldShell label={def.label} htmlFor="acp-target" required error={error}
          helper={error ? undefined : (pickedNote || (options.length
            ? `${options.length} ${pluralNoun(options.length)} available in this role.`
            : `Every ${def.label.toLowerCase()} already holds ${roleNoun} on this contract.`))}>
          <Combobox id="acp-target" value={value} onChange={(v) => { setValue(v || ''); if (error) setError(null); }}
            options={options}
            placeholder={`Search ${pluralNoun(2)}…`}
            ariaLabel={def.label} invalid={!!error} />
        </FieldShell>
      )}

      {/* The term is the party's own fact, not the contract's: left empty, the
          party is on the contract for its whole extent, and editing the
          contract's dates later never re-dates it. */}
      <SectionDivider label="In the role" />
      <FormRow>
        <DateField label="From" value={fromDate}
          onChange={(v) => { setFromDate(v || null); setDateError(e => ({ ...e, fromDate: undefined })); }}
          help={startDate ? `Leave empty to start with the contract (${H.conDate(startDate)}).` : 'Leave empty to start with the contract.'}
          error={dateError.fromDate} />
        <DateField label="To" value={toDate}
          onChange={(v) => { setToDate(v || null); setDateError(e => ({ ...e, toDate: undefined })); }}
          help="Leave empty while the party is still in the role."
          error={dateError.toDate} />
      </FormRow>

      {editing ? (
        <Alert severity="info">
          Saving replaces the whole party — role, record and both dates. Clearing a date clears it on
          the contract; the party keeps its id, so this stays one party.
        </Alert>
      ) : null}
    </Modal>
  );
};

Object.assign(window, { AddContractPartyModal });
