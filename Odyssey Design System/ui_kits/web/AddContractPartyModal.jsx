/* AddContractPartyModal — add a party to a contract (§3 step 3, §7 POST
   …/parties). The accessible two-step picker the spec mandates (frontend B2):
     1. choose the party KIND — Account / Contact / Insurance policy
     2. pick the specific record from a type-to-filter `Combobox` whose options
        are PRE-LOADED for the chosen kind (no in-widget async fetch — the
        candidate lists are handed in up front, keeping it inside Combobox's
        accessible contract). The inaccessible CreateTransactionDialog popover
        is deliberately NOT used.
   On save the party is created from a SCALAR ID only (accountId xor
   contactId xor insurancePolicyId — the XOR + anti-over-posting invariant,
   §6/§10). Already-linked targets are filtered out so the same record can't be
   attached twice (the §9 duplicate guard, surfaced before submit). */

const CONTRACT_PARTY_KINDS = [
  { kind: 'account',         label: 'Account',          icon: 'account_balance_wallet', field: 'accountId' },
  { kind: 'contact',    label: 'Contact',          icon: 'groups',                 field: 'contactId' },
  { kind: 'insurancePolicy', label: 'Insurance policy', icon: 'shield',                 field: 'insurancePolicyId' },
];

const AddContractPartyModal = ({ contract, onClose, onAdd }) => {
  const { useState, useMemo } = React;
  const H = window.OdysseyHelpers;

  const [kind, setKind] = useState('account');
  const [value, setValue] = useState('');
  const [error, setError] = useState(null);

  // Ids already linked to this contract, by field — so the picker offers only
  // not-yet-linked records (the duplicate guard, pre-empted in the UI).
  const linked = useMemo(() => {
    const s = { accountId: new Set(), contactId: new Set(), insurancePolicyId: new Set() };
    (contract.parties || []).forEach(p => {
      if (p.accountId) s.accountId.add(p.accountId);
      if (p.contactId) s.contactId.add(p.contactId);
      if (p.insurancePolicyId) s.insurancePolicyId.add(p.insurancePolicyId);
    });
    return s;
  }, [contract.parties]);

  const def = CONTRACT_PARTY_KINDS.find(k => k.kind === kind);
  const allOptions = kind === 'account' ? H.conAccountOptions()
    : kind === 'contact' ? H.conInstitutionOptions()
    : H.conPolicyOptions();
  const options = allOptions.filter(o => !linked[def.field].has(o.value));

  const pickKind = (k) => { setKind(k); setValue(''); setError(null); };

  const submit = () => {
    if (!value) { setError(`Select an ${def.label.toLowerCase()} to link.`); return; }
    onAdd && onAdd({ id: `cp-new-${Date.now()}`, [def.field]: value });
  };

  return (
    <Modal
      title="Add party"
      subtitle="Link the account, contact, or insurance policy this contract relates to."
      icon="group_add"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="add" onClick={submit}>Add party</Button>
        </React.Fragment>
      }>
      <FieldShell label="Party kind">
        <div className="con-kind-seg" role="radiogroup" aria-label="Party kind">
          {CONTRACT_PARTY_KINDS.map(k => (
            <button type="button" key={k.kind} role="radio" aria-checked={kind === k.kind}
              className={`con-kind-opt ${kind === k.kind ? 'on' : ''}`} onClick={() => pickKind(k.kind)}>
              <span className="material-icons" aria-hidden="true">{k.icon}</span>
              <span className="con-kind-lab">{k.label}</span>
            </button>
          ))}
        </div>
      </FieldShell>

      <FieldShell label={def.label} htmlFor="acp-target" error={error}
        helper={error ? undefined : (options.length
          ? `${options.length} ${def.label.toLowerCase()}${options.length === 1 ? '' : 's'} available to link.`
          : `Every ${def.label.toLowerCase()} is already linked to this contract.`)}>
        <Combobox id="acp-target" value={value} onChange={(v) => { setValue(v || ''); if (error) setError(null); }}
          options={options}
          placeholder={`Search ${def.label.toLowerCase()}s…`}
          ariaLabel={def.label} invalid={!!error} />
      </FieldShell>
    </Modal>
  );
};

Object.assign(window, { AddContractPartyModal });
