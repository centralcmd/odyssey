/* AddBudgetModal — the dialog opened from the Budgets page "New budget" button
   (and the dashed add-card at the bottom of the list).

   Fields mirror the NewBudget creation DTO (CreateBudgetDialog.razor):
     • name           (required)   — Budget.Name
     • startDate      (required)   — Budget.StartDate (defaults to today)
     • endDate        (required)   — Budget.EndDate   (defaults to +1 month)
     • baseCurrency   (required)   — Budget.BaseCurrencyCode (defaults to USD)
     • description    (optional)   — Budget.Description
   Archived is NOT part of creation — a new budget is always active. Items are
   added afterwards from the budget's detail. */

const ABM_CURRENCIES = ['USD', 'EUR', 'GBP', 'NOK', 'SEK', 'JPY', 'CAD']
  .map(c => ({ value: c, label: c }));

const ABM_ISO = (d) => {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
};
const ABM_TODAY = ABM_ISO(new Date());
const ABM_PLUS_MONTH = (() => {
  const d = new Date(); d.setMonth(d.getMonth() + 1);
  return ABM_ISO(d);
})();

const AddBudgetModal = ({ onClose, onCreate, onSave, budget = null }) => {
  const { useState } = React;
  const editing = !!budget;
  const [draft, setDraft] = useState({
    name: budget?.name || '',
    description: budget?.description || '',
    startDate: budget?.startDate || ABM_TODAY,
    endDate: budget?.endDate || ABM_PLUS_MONTH,
    currency: budget?.currency || 'USD',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const submit = () => {
    const next = {};
    if (!draft.name.trim()) next.name = 'Give the budget a name.';
    if (draft.startDate && draft.endDate && draft.endDate < draft.startDate)
      next.endDate = 'End date can’t be before the start date.';
    if (Object.keys(next).length) { setErrors(next); return; }
    const dto = { ...draft, name: draft.name.trim() };
    if (editing) { onSave && onSave(dto); } else { onCreate && onCreate(dto); }
  };

  return (
    <Modal
      title={editing ? 'Edit budget' : 'New budget'}
      subtitle={editing
        ? 'Update this budget’s name, dates, currency, or description.'
        : 'Plan income and expenses for a period, then track them against real transactions.'}
      icon="pie_chart"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create budget'}
          </Button>
        </React.Fragment>
      }>
      <Field
        label="Budget name"
        value={draft.name}
        onChange={set('name')}
        placeholder="e.g. January 2025"
        error={errors.name}
        autoFocus
      />

      <FormRow>
        <DateField label="Start date" value={draft.startDate} onChange={set('startDate')} help="Defaults to today" />
        <DateField label="End date" value={draft.endDate} onChange={set('endDate')} help="Defaults to +1 month" error={errors.endDate} />
      </FormRow>

      <Select label="Base currency" value={draft.currency} onChange={set('currency')} options={ABM_CURRENCIES} helper="All planned amounts use this currency." />

      <Field
        label="Description"
        value={draft.description}
        onChange={set('description')}
        placeholder="Optional — what's this budget for?"
      />
    </Modal>
  );
};

Object.assign(window, { AddBudgetModal });
