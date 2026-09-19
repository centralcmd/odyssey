/* AddContractPartyModal — add OR edit one party on a contract (§5:
   POST …/parties and the new PUT …/parties/{partyId}).

   The accessible picker the spec mandates (frontend B2), now three decisions
   deep:
     1. the party KIND — Account / Contact (the XOR target)
     2. the ROLE the record plays in the agreement (ContractPartyRole; the
        registry's `Unspecified` is a real, selectable member and the default)
     3. the specific record, from a type-to-filter `Combobox` whose options are
        PRE-LOADED for the chosen kind (no in-widget async fetch).
   …plus the optional TERM — FromDate / ToDate — with the insurance-party
   semantics: both null is the DEFAULT term, the contract's own extent, not an
   unset value.

   The save carries SCALAR IDS ONLY (accountId xor contactId) — never a nested
   Account or Contact object (§4 write-path invariant), so a party write can
   never create or rename the linked record.

   Duplicate guard (§8 rule 6): uniqueness is (contract, target, ROLE), so the
   picker only hides records already linked IN THE SELECTED ROLE, and the party
   being edited is excluded from its own check. The same contact can therefore
   be Seller and Service provider on one contract — one contract, two parties.

   In edit mode the PUT is a FULL REPLACEMENT of the link, not a patch: an
   omitted role resets to Unspecified and a cleared date clears. The dialog
   states that where the decision is made rather than after the fact. */

const CONTRACT_PARTY_KINDS = [
  { kind: 'account', label: 'Account', icon: 'account_balance_wallet', field: 'accountId' },
  { kind: 'contact', label: 'Contact', icon: 'groups',                 field: 'contactId' },
];

const AddContractPartyModal = ({ contract, party = null, onClose, onAdd, onSave }) => {
  const { useState, useMemo } = React;
  const H = window.OdysseyHelpers;
  const editing = !!party;

  const [kind, setKind] = useState(editing && party.contactId ? 'contact' : 'account');
  const [role, setRole] = useState(editing ? (party.role || 'Unspecified') : 'Unspecified');
  const [value, setValue] = useState(editing ? (party.accountId || party.contactId || '') : '');
  const [fromDate, setFromDate] = useState(editing ? (party.fromDate || null) : null);
  const [toDate, setToDate] = useState(editing ? (party.toDate || null) : null);
  const [error, setError] = useState(null);
  const [dateError, setDateError] = useState({});

  const def = CONTRACT_PARTY_KINDS.find(k => k.kind === kind);
  const roleInfo = H.conPartyRoleInfo(role);
  // The contract's own start is the only anchor, and only when it has one: a
  // one-off (completion date) and an open-started term take no lower bound.
  const startDate = H.conDateOnly(contract.startDate);

  // Records already holding THIS role on this contract — the only ones the
  // picker must withhold (uniqueness is per role).
  const taken = useMemo(
    () => H.conPartyTaken(contract.parties, def.field, role, editing ? party.id : null),
    [contract.parties, def.field, role, editing, party]);

  const allOptions = kind === 'account' ? H.conAccountOptions() : H.conInstitutionOptions();
  const options = allOptions.filter(o => !taken.has(o.value));
  const roleNoun = roleInfo.key === 'Unspecified' ? 'no role' : roleInfo.label.toLowerCase();

  const pickKind = (k) => { setKind(k); setValue(''); setError(null); };
  const pickRole = (r) => { setRole(r); setError(null); };

  const submit = () => {
    if (!value) { setError(`Select an ${def.label.toLowerCase()} to link.`); return; }
    const de = {};
    if (fromDate && startDate && fromDate < startDate)
      de.fromDate = `This contract starts ${H.conDate(startDate)} — a party can’t be in the role before that.`;
    if (fromDate && toDate && toDate < fromDate) de.toDate = 'End date can’t be before the start date.';
    if (Object.keys(de).length) { setDateError(de); return; }
    const dto = {
      id: editing ? party.id : `cp-new-${Date.now()}`,
      accountId: kind === 'account' ? value : null,
      contactId: kind === 'contact' ? value : null,
      role, fromDate: fromDate || null, toDate: toDate || null,
    };
    if (editing) onSave && onSave(dto); else onAdd && onAdd(dto);
  };

  return (
    <Modal
      title={editing ? 'Edit party' : 'New party'}
      subtitle={editing
        ? 'Change the role, the linked record, or the dates this party is in the role.'
        : 'Link the account or contact this contract relates to, and say what it does in the agreement.'}
      icon="group_add"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create party'}
          </Button>
        </React.Fragment>
      }>
      <FieldShell label="Party kind" required>
        <CardSelect ariaLabel="Party kind" value={kind} onChange={pickKind}
          accent="var(--con-accent)" accentLine="var(--con-accent-line)" accentSoft="var(--con-accent-soft)"
          columns={2} maxItemWidth={180} center
          options={CONTRACT_PARTY_KINDS.map(k => ({ value: k.kind, label: k.label, icon: k.icon }))} />
      </FieldShell>

      {/* ROLE — a plain registry Select, not a card grid: seven members (and
          growing) is a list, and the role is the one field a reader scans for.
          `Unspecified` is selectable, and means exactly "nobody has said" —
          `Other` is the deliberate none-of-these. */}
      <ContractPartyRoleSelect id="acp-role" label="Role" value={role} onChange={pickRole}
        helper={roleInfo.desc} />

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
          helper={error ? undefined : (options.length
            ? `${options.length} ${def.label.toLowerCase()}${options.length === 1 ? '' : 's'} available in this role.`
            : `Every ${def.label.toLowerCase()} already holds ${roleNoun} on this contract.`)}>
          <Combobox id="acp-target" value={value} onChange={(v) => { setValue(v || ''); if (error) setError(null); }}
            options={options}
            placeholder={`Search ${def.label.toLowerCase()}s…`}
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
