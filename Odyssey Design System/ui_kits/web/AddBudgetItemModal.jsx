/* AddBudgetItemModal — dialog opened from a budget card's action menu ("Add item")
   and the "Add item" button on the Budget items section. Mirrors the codebase's
   CreateBudgetItemDialog.razor.

   Fields map to the NewBudgetItem creation DTO:
     • name           (required)  — BudgetItem.Name
     • categoryType   (required)  — BudgetItem.CategoryType (Income | Expense)
     • transactionTag (optional)  — BudgetItem.TransactionTagId (links actuals)
     • plannedAmount  (required)  — BudgetItem.PlannedAmount
     • description    (optional)  — BudgetItem.Description
   Only tags not already used by an item in this budget are offered, exactly like
   CreateBudgetItemDialog's AvailableTransactionTags. */

const AddBudgetItemModal = ({ budget, item, onClose, onCreate }) => {
  const { useState, useEffect } = React;
  const D = window.OdysseyData;
  const isEdit = !!item;

  // Tags already used by OTHER items are unavailable; the item's own tag stays selectable.
  const usedTags = new Set(budget.items.filter(i => i.tagId && i.id !== (item && item.id)).map(i => i.tagId));
  const tagOptions = [{ value: '', label: 'None' }].concat(
    D.tags.filter(t => !usedTags.has(t.id)).map(t => ({ value: t.id, label: t.name })));

  const [draft, setDraft] = useState({
    name: item ? item.name : '',
    description: item ? (item.description || '') : '',
    categoryType: item ? item.categoryType : 'Expense',
    tagId: item ? (item.tagId || '') : '',
    planned: item ? String(item.planned) : '',
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const submit = () => {
    const next = {};
    const planned = parseFloat(draft.planned);
    if (!draft.name.trim()) next.name = 'Give the item a name.';
    if (draft.planned === '' || isNaN(planned)) next.planned = 'Enter a planned amount.';
    if (Object.keys(next).length) { setErrors(next); return; }
    onCreate && onCreate({
      id: isEdit ? item.id : `bi-${Date.now()}`,
      name: draft.name.trim(),
      description: draft.description.trim(),
      categoryType: draft.categoryType,
      tagId: draft.tagId || null,
      planned,
    });
  };

  return (
    <Modal
      title={isEdit ? 'Edit budget item' : 'New budget item'}
      subtitle={<React.Fragment>{isEdit ? 'Update this line for ' : 'Plan an income or expense line for '}<strong>{budget.name}</strong>. Link a tag to track its actuals automatically.</React.Fragment>}
      icon={isEdit ? 'edit' : 'playlist_add'}
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={isEdit ? 'check' : 'add'} onClick={submit}>{isEdit ? 'Save changes' : 'Create budget item'}</Button>
        </React.Fragment>
      }>
      <Field
        label="Item name"
        value={draft.name}
        onChange={set('name')}
        placeholder="e.g. Childcare"
        error={errors.name}
        autoFocus
      />

      <FormRow>
        <BudgetCategoryTypeSelect label="Category" value={draft.categoryType} onChange={set('categoryType')} />
        <MoneyField
          label="Planned amount"
          value={draft.planned}
          onChange={set('planned')}
          currency={budget.currency}
          currencyEditable={false}
          allowNegative={false}
          placeholder="0.00"
          error={errors.planned}
          helper={errors.planned ? undefined : 'Budget currency'}
        />
      </FormRow>

      <Select
        label="Transaction tag"
        value={draft.tagId}
        onChange={set('tagId')}
        options={tagOptions}
        helper="Optional — matched transactions become this item's actual."
      />

      <Field
        label="Description"
        value={draft.description}
        onChange={set('description')}
        placeholder="Optional"
      />
    </Modal>
  );
};

Object.assign(window, { AddBudgetItemModal });
