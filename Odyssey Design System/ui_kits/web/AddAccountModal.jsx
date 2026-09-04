/* AddAccountModal — the dialog opened from the Accounts page "Add account"
   button (and the dashed add-card at the bottom of the list).

   Fields mirror the NewAccount creation DTO, which is the Account entity minus
   the lifecycle flags you can't set at creation time:
     • name           (required)   — Account.name
     • accountType    (required)   — Account.accountType  (asset | liability)
     • currencyCode   (required)   — Account.currencyCode (defaults to USD)
     • accountNumber  (optional)   — Account.accountNumber
     • opened         (optional)   — Account.opened (defaults to today)
     • description    (optional)   — Account.description
   closed / archived are NOT part of creation — a new account is always active. */

const AAM_TYPES = (window.OdysseyData || {}).accountTypes || [];
const AAM_CURRENCIES = (window.OdysseyData.currencies || [])
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: c.name }));
const AAM_TODAY = (() => {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
})();

/* ---- Account-type picker -------------------------------------------------
   Now the typed DS AccountTypeSelect (components/AccountTypeSelect.jsx): the
   colored-glyph trigger + popover grouped into Assets and Liabilities, with the
   registry baked in. Falls back to a flat registry-fed Select until the bundle
   carries it (same pattern as ContactTypeSelect). */
const AccountTypePicker = ({ value, onChange, error }) => {
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  if (DS.AccountTypeSelect) {
    return <DS.AccountTypeSelect value={value} onChange={onChange} error={error}
      types={AAM_TYPES.length ? AAM_TYPES : undefined} />;
  }
  const options = AAM_TYPES.map(t => ({ value: t.key, label: `${t.label} · ${t.group === 'asset' ? 'Asset' : 'Liability'}`, icon: t.icon, iconColor: t.color }));
  return <Select label="Account type" value={value} onChange={onChange} options={options} placeholder="Choose a type…" helper={error} />;
};

const AddAccountModal = ({ onClose, onCreate, onSave, account = null }) => {
  const { useState } = React;
  const editing = !!account;
  const [draft, setDraft] = useState({
    name: account?.name || '',
    type: account?.type || '',
    currency: account?.currency || 'USD',
    accountNumber: account?.accountNumber || '',
    custodianId: account?.custodianId || '',
    opened: account?.opened || AAM_TODAY,
    closed: account?.closed || '',
    description: account?.description || '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const selType = AAM_TYPES.find(t => t.key === draft.type);

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the account a name.';
    if (!draft.type) next.type = 'Pick an account type.';
    if (Object.keys(next).length) { setErrors(next); return; }
    const dto = { ...draft, name: draft.name.trim() };
    if (editing) { onSave && onSave(dto); } else { onCreate && onCreate(dto); }
  };

  return (
    <Modal
      title={editing ? 'Edit account' : 'New account'}
      subtitle={editing
        ? 'Update this account’s name, type, custodian, or lifecycle dates.'
        : 'Add an account to track its balance, files, and transactions.'}
      icon="account_balance_wallet"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create account'}
          </Button>
        </React.Fragment>
      }>
      <Field
        label="Account name"
        value={draft.name}
        onChange={set('name')}
        placeholder="e.g. Chase Sapphire"
        error={errors.name}
        autoFocus
      />

      <AccountTypePicker value={draft.type} onChange={set('type')} error={errors.type} />
      {selType && (
        <div className="aam-group-note">
          <span className={`aam-group-pill ${selType.group}`}>
            {selType.group === 'asset' ? 'Asset' : 'Liability'}
          </span>
          <span>
            {selType.group === 'asset'
              ? 'Counts toward your total assets.'
              : 'Counts toward what you owe.'}
          </span>
        </div>
      )}

      <FormRow>
        <CurrencySelect value={draft.currency} onChange={set('currency')} options={AAM_CURRENCIES} searchThreshold={0} />
        <DateField label="Opened" value={draft.opened} onChange={set('opened')} help="Defaults to today" />
      </FormRow>

      {editing && (
        <DateField label="Closed" value={draft.closed} onChange={set('closed')} help="Leave empty while the account is active" />
      )}

      <Field
        label="Account number"
        value={draft.accountNumber}
        onChange={set('accountNumber')}
        placeholder="Optional"
      />

      <CustodianSelect
        value={draft.custodianId}
        onChange={set('custodianId')}
        contacts={(window.OdysseyData.contacts) || []}
        help="The bank, broker, or provider that holds this account."
      />

      <Field
        label="Description"
        value={draft.description}
        onChange={set('description')}
        placeholder="Optional — what's this account for?"
      />
    </Modal>
  );
};

Object.assign(window, { AddAccountModal });
