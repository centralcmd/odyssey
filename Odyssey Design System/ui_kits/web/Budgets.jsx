/* Budgets — vertical list of rich budget cards, each expandable into a detail view.
   Mirrors the Accounts page pattern, driven by the Budget domain from the codebase:

     • List         = BudgetsCard.razor   (/budgets)        — name, description,
                      start/end, base currency, archived.
     • Detail       = BudgetCard.razor    (/budgets/{id})   — breakdown (income vs
                      expense planned), budget items (category + transaction tag +
                      planned amount), and the matched budget transactions.

   An item's "actual" is derived, not stored: it is the sum of the transactions
   whose tag matches the item's tag within the budget's date range — exactly how
   the server's BudgetReport computes per-tag sums. Helpers live in data.js. */

const H = window.OdysseyHelpers;
const D = window.OdysseyData;

const BUDGET_TONE = window.TONE_MAP;

const CURRENCY_OPTIONS = ['USD', 'EUR', 'GBP', 'NOK', 'SEK', 'JPY', 'CAD'].map(c => ({ value: c, label: c }));

/* Donut palettes — share chroma/lightness with the Accounts allocation rings.
   Used by the per-budget planned-income / planned-expense allocation donuts
   inside each expanded budget's detail view. */
const INCOME_COLORS  = ['var(--mint-500)', 'var(--tide-400)', 'var(--sea-400)', 'var(--violet-500)'];
const EXPENSE_COLORS = ['var(--coral-500)', 'var(--amber-500)', 'var(--violet-500)', 'var(--sea-400)', 'var(--tide-400)', 'var(--mint-500)'];

/* ---- One planned-vs-actual item row ---- */
const BudgetItemRow = ({ item, budget, onEdit, onDelete }) => {
  const actual = H.budgetItemActual(item, budget);
  const tag = item.tagId ? D.tagById[item.tagId] : null;
  const pct = item.planned > 0 ? Math.min(100, (actual / item.planned) * 100) : 0;
  const isIncome = item.categoryType === 'Income';
  const over = !isIncome && actual > item.planned;
  const fillClass = isIncome ? 'income' : over ? 'over' : 'expense';
  return (
    <div className={`bgt-item-row ${isIncome ? 'income' : 'expense'}`}>
      <div className="bgt-item-id">
        <span className="bgt-item-name">{item.name}</span>
        {tag
          ? <Chip tone="tag">{tag.name}</Chip>
          : <Chip tone="outline">Untagged</Chip>}
      </div>
      <div className="bgt-bar-wrap">
        <div className="bar"><div className={`fill ${fillClass}`} style={{ width: `${pct}%` }} /></div>
        {over && <span className="bgt-over">over by {H.money(actual - item.planned)}</span>}
      </div>
      <div className={`bgt-num bgt-actual mono ${item.tagId ? '' : 'muted'}`}>
        {item.tagId ? H.money(actual) : '—'}
      </div>
      <div className="bgt-num bgt-planned mono">{H.money(item.planned)}</div>
      <div className="bgt-item-act">
        <ActionMenu items={[
          ...(onEdit ? [{ icon: 'edit', label: 'Edit', onClick: onEdit }] : []),
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(item.id); } },
          ...(onDelete ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: onDelete }] : []),
        ]} />
      </div>
    </div>
  );
};

/* ---- A group (Income / Expenses) of item rows. Rows are color-coded by kind;
   no header band — the row color marks the split. ---- */
const BudgetItemGroup = ({ items, budget, onEdit, onDelete }) => {
  if (items.length === 0) return null;
  return (
    <div className="bgt-group">
      {items.map(it => (
        <BudgetItemRow key={it.id} item={it} budget={budget}
          onEdit={onEdit ? () => onEdit(it) : null}
          onDelete={onDelete ? () => onDelete(it.id) : null} />
      ))}
    </div>
  );
};

/* ---- One inline-editable item row ("Edit multiple" batch mode) ---- */
const EditItemRow = ({ item, budget, onChange, onDelete }) => {
  const used = new Set(budget.items.filter(i => i.tagId && i.id !== item.id).map(i => i.tagId));
  const tagOptions = [{ value: '', label: 'None' }].concat(
    D.tags.filter(t => !used.has(t.id)).map(t => ({ value: t.id, label: t.name })));
  return (
    <div className="bgt-edit-row">
      <input className="bgt-edit-input" defaultValue={item.name} aria-label="Item name"
        onChange={(e) => onChange(item.id, { name: e.target.value })} />
      <BudgetCategoryTypeSelect value={item.categoryType} onChange={(v) => onChange(item.id, { categoryType: v })} />
      <Select value={item.tagId || ''} onChange={(v) => onChange(item.id, { tagId: v || null })} options={tagOptions} />
      <input type="number" className="bgt-edit-input ta-r" defaultValue={item.planned} aria-label="Planned amount"
        onChange={(e) => onChange(item.id, { planned: e.target.value === '' ? 0 : parseFloat(e.target.value) })} />
      <div className="bgt-item-act">
        <button className="bgt-rowbtn del" aria-label="Remove item" onClick={() => onDelete(item.id)}>
          <MIcon name="delete_outline" size={18} />
        </button>
      </div>
    </div>
  );
};

/* ---- Details slot · the budget's FULL field set as tiles ----------------
   The DS RecordCard rollout: the body repeats what the header shows (period,
   currency, status) because at tile scale each value arrives with its own
   label, and adds the four roll-ups the header can only summarise as one
   figure. Derived tiles (Actual*, balances) never replace the planned tiles
   they are computed from. */
const BudgetTiles = ({ budget }) => {
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const { InfoTileGrid, InfoTile } = DS;
  if (!InfoTileGrid || !InfoTile) return null;
  const totals = H.budgetTotals(budget);
  const status = H.budgetStatus(budget);
  const income = budget.items.filter(i => i.categoryType === 'Income');
  const expense = budget.items.filter(i => i.categoryType === 'Expense');
  const ahead = totals.actualDiff >= totals.expectedDiff;
  const lines = (n, w) => `${n} ${w}${n === 1 ? '' : 's'}`;
  return (
    <InfoTileGrid>
      <InfoTile icon={budget.icon || 'pie_chart'} label="Name" value={budget.name} valueVariant="text" className="wrapvalue" />
      <InfoTile icon="event" label="Start date" value={H.dateLong(budget.startDate)} valueVariant="sm" foot="plan period opens" />
      <InfoTile icon="event_busy" label="End date" value={H.dateLong(budget.endDate)} valueVariant="sm" foot="plan period closes" />
      <InfoTile icon="payments" label="Base currency" value={budget.currency} foot="reporting currency" />
      <InfoTile icon={budget.archived ? 'inventory_2' : 'check_circle'} label="Status" valueVariant="text"
        className={budget.archived ? 'tone-muted' : 'tone-income'} value={status.label}
        foot={budget.archived ? `since ${H.dateTime(budget.archived)}` : 'in the default list'} />
      <InfoTile icon="trending_up" label="Planned income" className="tone-income"
        value={H.money(totals.plannedIncome)} foot={lines(income.length, 'income line')} />
      <InfoTile icon="shopping_cart" label="Planned expenses" className="tone-expense"
        value={H.money(totals.plannedExpense)} foot={lines(expense.length, 'expense line')} />
      <InfoTile icon="south_west" label="Actual income" className="tone-income"
        value={H.money(totals.actualIncome)} foot="from matched transactions" />
      <InfoTile icon="north_east" label="Actual expenses" className="tone-expense"
        value={H.money(totals.actualExpense)} foot="from matched transactions" />
      <InfoTile icon="balance" label="Expected balance" value={H.money(totals.expectedDiff)}
        className={totals.expectedDiff < 0 ? 'tone-expense' : 'tone-income'} foot="planned in − planned out" />
      <InfoTile icon="account_balance" label="Actual balance" value={H.money(totals.actualDiff)}
        className={totals.actualDiff < 0 ? 'tone-expense' : 'tone-income'}
        foot={ahead ? 'ahead of plan' : 'behind plan'} />
    </InfoTileGrid>
  );
};

/* ---- Body sections: allocation + items + transactions ---- */
const BudgetDetail = ({ budget, setItems, onNavigate, onAddItem, onEditItem, editMulti, setEditMulti }) => {
  const { useState } = React;
  const [txns, setTxns] = useState(() => H.budgetMatchedTxns(budget));
  const saveTxn = (id, patch) => setTxns(prev => prev.map(t => t.id === id ? { ...t, ...patch } : t));
  const deleteTxn = (id) => setTxns(prev => prev.filter(t => t.id !== id));
  const income  = budget.items.filter(i => i.categoryType === 'Income');
  const expense = budget.items.filter(i => i.categoryType === 'Expense');
  // Per-budget allocation slices — this budget's own planned lines, biggest first.
  const incomeSlices  = income.map(i => ({ name: i.name, value: i.planned })).filter(s => s.value > 0).sort((a, b) => b.value - a.value);
  const expenseSlices = expense.map(i => ({ name: i.name, value: i.planned })).filter(s => s.value > 0).sort((a, b) => b.value - a.value);

  const deleteItem = (id) => setItems(prev => prev.filter(i => i.id !== id));
  const updateItem = (id, patch) => setItems(prev => prev.map(i => (i.id === id ? { ...i, ...patch } : i)));

  return (
    <React.Fragment>
      {budget.items.length > 0 && (
        <div className="acct-section">
          <SectionDivider label="Allocation" meta={`planned · ${budget.currency}`} />
          <div className="bgt-donuts-row">
            <div className="bgt-donuts">
              <DonutPanel title="Planned income" centerLabel="Planned in" centerIcon="trending_up"
                sub={`${income.length} income line${income.length === 1 ? '' : 's'}`}
                colors={INCOME_COLORS} items={incomeSlices} />
            </div>
            <div className="bgt-donuts">
              <DonutPanel title="Planned expenses" centerLabel="Planned out" centerIcon="shopping_cart"
                sub={`${expense.length} expense line${expense.length === 1 ? '' : 's'}`}
                colors={EXPENSE_COLORS} items={expenseSlices} />
            </div>
          </div>
        </div>
      )}

      <div className="acct-section">
        <SectionDivider label="Budget items"
          meta={`${budget.items.length} item${budget.items.length === 1 ? '' : 's'}${editMulti ? ' · editing' : ''}`} />
        {budget.items.length === 0 ? (
          <div className="empty-line">No items yet — add income and expense lines to start planning.</div>
        ) : editMulti ? (
          <div className="bgt-items">
            <div className="bgt-edit-head">
              <span>Item</span>
              <span>Category</span>
              <span>Transaction tag</span>
              <span className="ta-r">Planned</span>
              <span />
            </div>
            {budget.items.map(it => (
              <EditItemRow key={it.id} item={it} budget={budget} onChange={updateItem} onDelete={deleteItem} />
            ))}
            <div className="bgt-edit-foot">
              <span className="muted">Changes apply as you type.</span>
              <Button variant="text" color="primary" icon="check" onClick={() => setEditMulti(false)}>Done</Button>
            </div>
          </div>

        ) : (
          <div className="bgt-items acct-table-frame">
            <div className="bgt-item-head">
              <span>Item</span>
              <span>Actual vs planned</span>
              <span className="ta-r">Actual</span>
              <span className="ta-r">Planned</span>
              <span />
            </div>
            <BudgetItemGroup kind="income"  items={income}  budget={budget} onEdit={onEditItem} onDelete={deleteItem} />
            <BudgetItemGroup kind="expense" items={expense} budget={budget} onEdit={onEditItem} onDelete={deleteItem} />
          </div>
        )}
      </div>

      <div className="acct-section">
        <SectionDivider label="Transactions" meta={`${txns.length} matched · in range and tagged`} />
        <div className="acct-txn-table acct-table-frame">
          <InlinePager items={txns}>
            {(pageRows) => (
              <TxnTable
                txns={pageRows}
                onSave={saveTxn}
                onDelete={deleteTxn}
                empty={<div className="empty-line" style={{ padding: 20 }}>No transactions matched this budget's tags in its date range yet.</div>}
              />
            )}
          </InlinePager>
        </div>
      </div>
    </React.Fragment>
  );
};

/* ---- One budget record (DS RecordCard) ---- */
const BudgetRecordCard = ({ b, open, onToggle, onDelete, onNavigate }) => {
  const { useState } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const { RecordCard, InfoTileGrid, InfoTile } = DS;
  const [budget, setBudget] = useState(b);
  const [showEdit, setShowEdit] = useState(false);
  const [itemModal, setItemModal] = useState(null); // null | { item }  (item null = add)
  const [editMulti, setEditMulti] = useState(false);
  const status = H.budgetStatus(budget);
  const totals = H.budgetTotals(budget);
  const txnCount = H.budgetMatchedTxns(budget).length;
  const dimmed = !!budget.archived;
  const tone = BUDGET_TONE[budget.tone] || BUDGET_TONE.tide;

  const setItems = (updater) =>
    setBudget(prev => ({ ...prev, items: typeof updater === 'function' ? updater(prev.items) : updater }));

  const startEdit = () => setShowEdit(true);
  const saveEdit = (draft) => {
    setBudget(prev => ({
      ...prev,
      name: draft.name.trim() || prev.name,
      description: draft.description,
      startDate: draft.startDate,
      endDate: draft.endDate,
      currency: draft.currency,
    }));
    setShowEdit(false);
  };
  const toggleArchive = () =>
    setBudget(prev => ({ ...prev, archived: prev.archived ? null : new Date().toISOString() }));
  const addItem = (it) => {
    setItems(prev => itemModal && itemModal.item
      ? prev.map(i => (i.id === it.id ? it : i))
      : [...prev, it]);
    setItemModal(null);
  };
  const openAddItem = () => { onToggle(true); setItemModal({ item: null }); };
  const openEditItem = (item) => { onToggle(true); setItemModal({ item }); };

  if (!RecordCard || !InfoTileGrid || !InfoTile) return null;

  return (
    <div>
      <RecordCard
        icon={budget.icon || 'pie_chart'}
        accent={tone.fg}
        accentSoft={tone.bg}
        name={budget.name}
        chips={<Chip tone={status.tone} dot>{status.label}</Chip>}
        meta={[
          <span><MIcon name="date_range" size={14} /><span>{H.dateLong(budget.startDate)} → {H.dateLong(budget.endDate)}</span></span>,
          <span className="mono"><MIcon name="payments" size={14} /><span>{budget.currency}</span></span>,
        ]}
        counts={[
          { icon: 'format_list_bulleted', value: budget.items.length, label: 'Budget items' },
          { icon: 'receipt_long', value: txnCount, label: 'Transactions' },
        ]}
        figure={{ value: H.money(totals.expectedDiff), caption: 'Expected balance', tone: totals.expectedDiff < 0 ? 'expense' : 'income' }}
        dimmed={dimmed}
        open={open}
        onToggle={onToggle}
        actions={<ActionMenu items={[
          { icon: 'edit', label: 'Edit budget', onClick: startEdit },
          { icon: 'playlist_add', label: 'New item', onClick: openAddItem },
          { icon: editMulti ? 'check' : 'edit_note', label: editMulti ? 'Done editing items' : 'Edit multiple',
            onClick: () => { onToggle(true); setEditMulti(m => !m); } },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(budget.id); } },
          { divider: true },
          { icon: budget.archived ? 'unarchive' : 'archive', label: budget.archived ? 'Unarchive' : 'Archive', onClick: toggleArchive },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(budget.id) },
        ]} />}
        details={<BudgetTiles budget={budget} />}
        content={budget.description ? (
          <InfoTileGrid>
            <InfoTile icon="notes" label="Description" wide value={budget.description} />
          </InfoTileGrid>
        ) : null}
      >
        <BudgetDetail budget={budget} setItems={setItems} onNavigate={onNavigate}
          onAddItem={openAddItem} onEditItem={openEditItem}
          editMulti={editMulti} setEditMulti={setEditMulti} />
      </RecordCard>
      {showEdit && <AddBudgetModal budget={budget} onClose={() => setShowEdit(false)} onSave={saveEdit} />}
      {itemModal && <AddBudgetItemModal budget={budget} item={itemModal.item} onClose={() => setItemModal(null)} onCreate={addItem} />}
    </div>
  );
};

const Budgets = ({ onNavigate }) => {
  const { useState } = React;
  const [q, setQ] = useState('');
  const [statusFilter, setStatusFilter] = useState([]);
  const [showAdd, setShowAdd] = useState(false);
  const [budgets, setBudgets] = useState(D.budgets);
  // Shared sort (§6.3): Start date newest-first default; the toolbar is the
  // sole sort surface (card list, no headers).
  const [sort, setSort] = useState({ key: 'startDate', dir: 'desc' });
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  // The list owns ONE openId — opening a record closes its siblings.
  const [openId, setOpenId] = useState(null);
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  // §6.3 curated fields — one list feeds the SortSelect AND the ordering.
  const sortFields = [
    { key: 'startDate', label: 'Start date', type: 'date', sortValue: (b) => b.startDate || null },
    { key: 'name',      label: 'Name',       type: 'text', sortValue: (b) => (b.name || '').toLowerCase() },
    { key: 'endDate',   label: 'End date',   type: 'date', sortValue: (b) => b.endDate || null },
  ];

  const createBudget = (draft) => {
    const budget = {
      id: `new-${Date.now()}`,
      name: draft.name,
      description: draft.description || '',
      currency: draft.currency,
      startDate: draft.startDate,
      endDate: draft.endDate,
      archived: null,
      icon: 'pie_chart',
      tone: 'mint',
      items: [],
    };
    setBudgets(prev => [budget, ...prev]);
    setShowAdd(false);
  };

  const rows = budgets.filter(b => {
    if (statusFilter.length && !statusFilter.includes(H.budgetStatus(b).label)) return false;
    if (q) {
      const needle = q.toLowerCase();
      const hay = `${b.name} ${b.description} ${b.currency}`.toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });

  const active = budgets.filter(b => !b.archived);
  const plannedNet = active.reduce((s, b) => s + H.budgetTotals(b).expectedDiff, 0);
  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (b) => b.id) : rows;

  const deleteBudget = (id) => setBudgets(prev => prev.filter(b => b.id !== id));

  return (
    <div className="col gap-6">
      <PageHeader
        title="Budgets"
        icon="pie_chart"
        sub={`${active.length} active · planned balance ${H.money(plannedNet)}`}
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search name, description, currency…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 180 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={[
                  { value: 'Active', label: 'Active' },
                  { value: 'Archived', label: 'Archived' },
                ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Budgets per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        )}
        primary={{ label: 'New budget', icon: 'add', onClick: () => setShowAdd(true) }}
      />

      <div className="acct-list">
        <InfiniteList
          items={sortedRows}
          batchSize={batch}
          itemKey={(b) => b.id}
          noun="budgets"
          renderItem={(b) => (
            <BudgetRecordCard b={b} open={openId === b.id} onToggle={(o) => setOpenId(o ? b.id : null)}
              onDelete={deleteBudget} onNavigate={onNavigate} />
          )}
          empty={(
            <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
              No budgets match your filters.
            </div>
          )}
          trailing={(
            <AddRow
              title="New budget"
              sub="Plan a period's income and expenses, then track against transactions."
              onClick={() => setShowAdd(true)}
            />
          )}
        />
      </div>

      {showAdd && <AddBudgetModal onClose={() => setShowAdd(false)} onCreate={createBudget} />}
    </div>
  );
};

Object.assign(window, { Budgets });
