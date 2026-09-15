/* AddBudgetItemModal — dialog opened from a budget card's action menu ("New item")
   and the "Edit" row action. Mirrors the codebase's CreateBudgetItemDialog.razor.

   Three fields, down from five. A budget item has no name and no description of
   its own: its TransactionTagId is REQUIRED and the linked tag's name and
   description are the item's identity wherever it is displayed.

     • transactionTagId (required) — BudgetItem.TransactionTagId
     • categoryType     (required) — BudgetItem.CategoryType (Income | Expense)
     • plannedAmount    (required) — BudgetItem.PlannedAmount

   Only one item per tag per budget: tags already planned for are offered but
   marked "in use" and unselectable (the item's own tag stays selectable, even
   when archived). Inline tag creation lives here and only here — this dialog has
   a submit, so a staged create can settle and resolve to a real id before the
   item is written; the edit-multiple grid saves on change and is passed no
   create handler. */

const AddBudgetItemModal = ({ budget, item, onClose, onCreate, canCreateTag = true, tagsFailed = false }) => {
  const { useState } = React;
  const D = window.OdysseyData;
  const isEdit = !!item;

  /* The two directions a budget line can take, and the one in force — read
     from the DS registry so the lead, the copy and the stored enum cannot
     disagree. */
  const BUDGET_DIR_OPTIONS = (window.OdysseyDesignSystem_d5aa51 || {}).BUDGET_CATEGORY_DIRECTION_OPTIONS
    || [{ value: 'Expense', label: 'Expense', short: 'out', tone: 'expense' }, { value: 'Income', label: 'Income', short: 'in', tone: 'income' }];
  const BUDGET_CATS = (window.OdysseyDesignSystem_d5aa51 || {}).BUDGET_CATEGORY_TYPES || [];

  const [draft, setDraft] = useState({
    categoryType: item ? item.categoryType : 'Expense',
    tagId: item ? item.tagId : '',
    planned: item ? String(item.planned) : '',
  });
  const [errors, setErrors] = useState({});
  /* Per-FIELD defaults, not an all-or-nothing fallback object: a consumer whose
     compiled bundle predates the registry's `short` / `tone` / `sentence` still
     finds its entry by key, so an object-level guard would never fire and the
     copy would silently lose two phrases. */
  const cat = BUDGET_CATS.find(t => t.key === draft.categoryType) || {};
  const isIncome = draft.categoryType === 'Income';
  const catInfo = {
    key: draft.categoryType,
    label: cat.label || draft.categoryType,
    short: cat.short || (isIncome ? 'in' : 'out'),
    tone: cat.tone || (isIncome ? 'income' : 'expense'),
    sentence: cat.sentence || (isIncome ? 'money into the budget' : 'money out of the budget'),
  };
  // Tags staged by the inline create row. The POST runs behind the gesture; the
  // dialog waits for it to settle on submit and sends the RESOLVED id, never the
  // temporary one — and registers the tag so every other surface sees it.
  const [staged, setStaged] = useState([]);
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  const tags = staged.concat(D.tags);
  const used = budget.items.filter(i => !item || i.id !== item.id).map(i => i.tagId);

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const createTag = (name) => {
    const tag = { id: `tag-${Date.now()}`, name, description: null, archived: null };
    setStaged(prev => [tag, ...prev]);
    // Registering it in the shared reference data is what the real dialog does by
    // invalidating the tag cache once the staged create settles.
    D.tags.unshift(tag);
    D.tagById[tag.id] = tag;
    return tag;
  };

  const submit = () => {
    const next = {};
    const planned = parseFloat(draft.planned);
    if (!draft.tagId) next.tagId = 'Choose a transaction tag.';
    else if (used.includes(draft.tagId)) next.tagId = 'This budget already plans for that tag.';
    if (draft.planned === '' || isNaN(planned)) next.planned = 'Enter a planned amount.';
    if (Object.keys(next).length) { setErrors(next); return; }
    onCreate && onCreate({
      id: isEdit ? item.id : `bi-${Date.now()}`,
      categoryType: draft.categoryType,
      tagId: draft.tagId,
      planned,
    });
  };

  const selectable = D.tags.filter(t => !t.archived && !used.includes(t.id)).length;
  const blocked = tagsFailed || (selectable === 0 && !canCreateTag && !draft.tagId);

  return (
    <Modal
      title={isEdit ? 'Edit budget item' : 'New budget item'}
      subtitle={<React.Fragment>{isEdit ? 'Update this line for ' : 'Plan an income or expense line for '}<strong>{budget.name}</strong>. The tag you pick names the line and collects its actuals.</React.Fragment>}
      icon={isEdit ? 'edit' : 'playlist_add'}
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" disabled={blocked} icon={isEdit ? 'check' : 'add'} onClick={submit}>{isEdit ? 'Save changes' : 'Create budget item'}</Button>
        </React.Fragment>
      }>
      <TransactionTagPicker
        id="bgt-item-tag"
        value={draft.tagId}
        onChange={set('tagId')}
        tags={tags}
        usedTagIds={used}
        error={errors.tagId}
        canCreate={canCreateTag}
        onCreateTag={createTag}
        loadFailed={tagsFailed}
        onRetry={() => {}}
      />

      {/* ONE control for the planned amount, direction included. A budget item
          records a DIRECTION (Expense / Income), not a sign, so the direction
          rides in the slot a sign would occupy — the same lead a contract
          term's amount carries. The separate Category picker beside it asked
          the same question twice, in a second place that could drift. */}
      <MoneyField
        label="Planned amount"
        required
        value={draft.planned}
        onChange={set('planned')}
        direction={draft.categoryType}
        onDirectionChange={set('categoryType')}
        directionOptions={BUDGET_DIR_OPTIONS}
        tone={catInfo.tone}
        currency={budget.currency}
        currencyEditable={false}
        allowNegative={false}
        placeholder="0.00"
        error={errors.planned}
        help={errors.planned ? undefined : (
          <React.Fragment>
            <b>{catInfo.label}</b> — {catInfo.sentence}. Click <b>{catInfo.short}</b> to switch · {budget.currency}
          </React.Fragment>
        )}
      />
    </Modal>
  );
};

Object.assign(window, { AddBudgetItemModal });
