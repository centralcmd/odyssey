/* AddSubscriptionModal — the NewSubscription create dialog.
   Mirrors AddContactModal / the insurance dialog: the DS Modal shell with
   the NewSubscription fields. The contact picker is the optional
   ContactSelect (Combobox over active contacts, scalar id only — no
   nested object, matching the mass-assignment guard). Dates bind ISO strings;
   amount + currency + interval + first-billing-date are the required core. */

const SUB_CURRENCY_OPTIONS = () => (window.OdysseyData.currencies || [])
  .filter((c) => !c.archived)
  .map((c) => ({ value: c.code, label: c.name }));

const SUB_CONTACT_OPTIONS = () => {
  const reg = (window.OdysseyData && window.OdysseyData.contactTypeByKey) || {};
  return window.OdysseyData.activeContacts().map((c) => {
    const m = reg[c.type] || {};
    return { value: c.id, label: c.name, icon: m.icon, iconColor: m.color };
  });
};

const SUB_UNIT_NOUN_M = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };
// Live helper under the "Every" field: "= monthly" / "= every 2 months".
const subEveryHelp = (draft) => {
  if (!draft.interval) return 'Pick a cadence first.';
  const n = Math.round(Number(draft.intervalCount));
  const every = Number.isFinite(n) && n > 0 ? n : 1;
  const noun = SUB_UNIT_NOUN_M[draft.interval] || 'cycle';
  return every > 1 ? `= every ${every} ${noun}s` : `= every ${noun}`;
};

const AddSubscriptionModal = ({ onClose, onCreate, onSave, subscription = null }) => {
  const { useState } = React;
  const SUB_H = window.OdysseyHelpers;
  const editing = !!subscription;
  const [draft, setDraft] = useState({
    name: subscription?.name || '', externalId: subscription?.externalId || '', contactId: subscription?.contactId || '',
    startDate: subscription?.startDate || '', endDate: subscription?.endDate || '',
    amount: subscription ? String(subscription.amount) : '', currencyCode: subscription?.currencyCode || 'USD',
    interval: subscription?.interval || '', intervalCount: subscription ? SUB_H.subIntervalCount(subscription) : 1,
    firstBillingDate: subscription?.firstBillingDate || '', notes: subscription?.notes || '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft((d) => ({ ...d, [k]: v }));
    if (errors[k]) setErrors((e) => ({ ...e, [k]: undefined }));
  };

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the subscription a name.';
    const amt = parseFloat(draft.amount);
    if (draft.amount === '' || isNaN(amt)) next.amount = 'Enter the price.';
    else if (amt < 0) next.amount = 'Price cannot be negative.';
    if (!draft.startDate) next.startDate = 'Choose the start date.';
    if (!draft.interval) next.interval = 'Pick a billing cadence.';
    if (draft.endDate && draft.startDate && draft.endDate < draft.startDate) next.endDate = 'End date must be on or after the start date.';
    if (!draft.firstBillingDate) next.firstBillingDate = 'Choose the first billing date.';
    if (Object.keys(next).length) { setErrors(next); return; }
    const core = {
      name: draft.name.trim(),
      externalId: draft.externalId.trim() || null,
      contactId: draft.contactId || null,
      startDate: draft.startDate,
      endDate: draft.endDate || null,
      amount: amt,
      currencyCode: draft.currencyCode,
      interval: draft.interval,
      intervalCount: Math.max(1, Math.round(Number(draft.intervalCount)) || 1),
      firstBillingDate: draft.firstBillingDate,
      notes: draft.notes.trim() || null,
    };
    if (editing) {
      // Paused / Archived are managed from the row's action menu, not here —
      // the parent merge preserves them.
      onSave && onSave(core);
    } else {
      onCreate && onCreate({ ...core, paused: null, archived: null, createdAtUtc: new Date().toISOString() });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit subscription' : 'New subscription'}
      subtitle={editing
        ? 'Update this subscription — what it is, who bills it, how much, and how often.'
        : 'A recurring subscription to keep on record — what it is, who bills it, how much, and how often.'}
      icon="subscriptions"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create subscription'}
          </Button>
        </React.Fragment>
      }>
      <Field label="Name" value={draft.name} onChange={set('name')} required
        placeholder="e.g. Netflix" error={errors.name} helper="Up to 128 characters" autoFocus />

      <Field label="External id" value={draft.externalId} onChange={set('externalId')}
        placeholder="Optional" optional maxLength={128}
        helper="Membership, account, or subscription number — a reference label, not a key." />

      <FieldShell label="Company" htmlFor="sub-new-cp" optional
        helper="The contact that bills this subscription.">
        <Combobox id="sub-new-cp" value={draft.contactId}
          onChange={(v) => set('contactId')(v || '')} options={SUB_CONTACT_OPTIONS()}
          placeholder="Search contacts…" ariaLabel="Company" clearable />
      </FieldShell>

      <FormRow>
        <DateField label="Start date" value={draft.startDate} onChange={set('startDate')}
          required error={errors.startDate} />
        <DateField label="End date" value={draft.endDate} onChange={set('endDate')}
          optional min={draft.startDate || undefined} error={errors.endDate}
          help="Leave blank if ongoing." />
      </FormRow>

      <FormRow>
        <MoneyField label="Price" value={draft.amount} onChange={set('amount')}
          required allowNegative={false} currency={draft.currencyCode} onCurrencyChange={set('currencyCode')}
          currencyOptions={SUB_CURRENCY_OPTIONS()} currencySearchThreshold={0}
          error={errors.amount} placeholder="0.00" className="sub-amount-expense" />
        <BillingIntervalSelect value={draft.interval} onChange={set('interval')}
          error={errors.interval} placeholder="Choose a cadence…" helper={errors.interval ? undefined : 'How often it bills.'} />
      </FormRow>

      <FormRow>
        <NumberField label="Every" value={draft.intervalCount} onChange={(v) => set('intervalCount')(v)}
          min={1} step={1} placeholder="1"
          helper={subEveryHelp(draft)} />
        <DateField label="First billing date" value={draft.firstBillingDate} onChange={set('firstBillingDate')}
          required error={errors.firstBillingDate}
          help="The billing day is derived from this." />
      </FormRow>

      <NoteField label="Notes" value={draft.notes} onChange={set('notes')}
        optional maxLength={1024} placeholder="Optional — anything worth remembering." />
    </Modal>
  );
};

Object.assign(window, { AddSubscriptionModal });
