/* Currencies — search + status filter + sortable, expandable, editable table
   following the Transactions pattern. The CurrencyCode is the primary key
   (ISO-4217), so it's immutable once created — the edit panel locks it.

   Fields mirror the Odyssey.Finance.Dtos Currency DTOs:
     ExistingCurrency — CurrencyCode (3), Name (≤64), MinorUnits (0–12),
                        Symbol (≤8), Archived (datetime?, null = active)
     NewCurrency      — CurrencyCode, Name, MinorUnits, Symbol, Archived (bool) */

const CUR_TONE = { bg: 'oklch(0.79 0.115 188 / 0.16)', fg: 'oklch(0.79 0.115 188)' };
const CUR_STATUS_OPTIONS = [
  { value: 'active',   label: 'Active' },
  { value: 'archived', label: 'Archived' },
];
// MinorUnits is a decimal-place count; the DTO allows 0–12 (Range attribute).
const CUR_MINOR_OPTIONS = Array.from({ length: 13 }, (_, i) => ({ value: String(i), label: String(i) }));

const curSortVal = (c, key) => {
  switch (key) {
    case 'code':       return c.code;
    case 'name':       return c.name.toLowerCase();
    case 'symbol':     return (c.symbol || '').toLowerCase();
    case 'minorUnits': return c.minorUnits;
    case 'status':     return c.archived ? 1 : 0;
    default:           return 0;
  }
};

/* ---------- Expanded DETAIL ---------- */
const CurDetail = ({ c }) => {
  const H = window.OdysseyHelpers;
  const status = H.archivedStatus(c);
  return (
    <div className="acct-detail">
      <div className="meta-grid">
        <MetaTile label="Currency code" value={c.code} mono />
        <MetaTile label="Symbol" value={c.symbol || '—'} />
        <MetaTile label="Name" value={c.name} />
        <MetaTile label="Minor units" value={`${c.minorUnits} decimal place${c.minorUnits === 1 ? '' : 's'}`} />
        <MetaTile label="Status" value={<Chip tone={status.tone} dot>{status.label}</Chip>} />
        <MetaTile label="Base currency" value={c.base ? <Chip tone="info" dot>Workspace base</Chip> : 'No'} />
        <MetaTile label="Example amount" value={`${c.symbol || ''}${(1234.5).toLocaleString('en-US', { minimumFractionDigits: c.minorUnits, maximumFractionDigits: c.minorUnits })}`} mono />
        {c.archived && <MetaTile label="Archived" value={H.dateTime(c.archived)} mono />}
      </div>
    </div>
  );
};

/* ---------- Table (shared DS RecordTable) ---------- */
const CurrencyTable = ({ currencies, onSave, onDelete, onEdit, sort, onSortChange, empty }) => {
  const H = window.OdysseyHelpers;
  return (
    <RecordTable
      rows={currencies}
      ariaLabel="Currencies"
      rowKey={(c) => c.code}
      defaultSort={{ key: 'code', dir: 'asc' }}
      sort={sort}
      onSortChange={onSortChange}
      leading={(c) => <Avatar initials={c.symbol} tone={CUR_TONE} />}
      columns={[
        {
          key: 'code', header: 'Code', sortable: true, sortType: 'text', sortValue: (c) => curSortVal(c, 'code'),
          cell: (c, ctx) => (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
              <span className="mono">{c.code}</span>
              {c.base && <Chip tone="info" dot>Base</Chip>}
              {ctx.justSaved && <Chip tone="income" dot>Saved</Chip>}
            </span>
          ),
        },
        { key: 'name', header: 'Name', sortable: true, sortType: 'text', sortValue: (c) => curSortVal(c, 'name'), cell: (c) => c.name },
        { key: 'symbol', header: 'Symbol', sortable: true, sortType: 'text', className: 'muted', sortValue: (c) => curSortVal(c, 'symbol'), cell: (c) => c.symbol || '—' },
        { key: 'minorUnits', header: 'Minor units', sortable: true, sortType: 'number', defaultDir: 'asc', align: 'right', className: 'muted', sortValue: (c) => curSortVal(c, 'minorUnits'), cell: (c) => c.minorUnits },
        {
          key: 'status', header: 'Status', sortable: true, sortType: 'status', sortValue: (c) => curSortVal(c, 'status'),
          cell: (c) => { const s = H.archivedStatus(c); return <Chip tone={s.tone} dot>{s.label}</Chip>; },
        },
      ]}
      actions={(c, ctx) => [
        { icon: ctx.expanded ? 'close' : 'expand_more', label: ctx.expanded ? 'Collapse' : 'View details', onClick: ctx.toggle },
        { icon: 'edit', label: 'Edit', onClick: () => onEdit(c) },
        { icon: c.archived ? 'unarchive' : 'archive', label: c.archived ? 'Restore' : 'Archive', onClick: () => onSave(c.code, { archived: c.archived ? null : new Date().toISOString() }) },
        { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(c.code); } },
        { divider: true },
        { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove },
      ]}
      renderDetail={(c) => <CurDetail c={c} />}
      onSave={onSave}
      onDelete={onDelete}
      empty={empty}
    />
  );
};

/* ---------- New / Edit currency dialog (New/ExistingCurrency DTO) ----------
   One dialog serves both create and edit: pass an existing `currency` to
   prefill and switch into edit mode. The CurrencyCode is the primary key
   (ISO-4217) so it's immutable once created — edit mode locks it. */
const AddCurrencyModal = ({ onClose, onCreate, onSave, currency = null, existingCodes }) => {
  const { useState } = React;
  const editing = !!currency;
  const [draft, setDraft] = useState({
    code: currency?.code || '',
    name: currency?.name || '',
    symbol: currency?.symbol || '',
    minorUnits: String(currency?.minorUnits ?? 2),
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(d => ({ ...d, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const submit = () => {
    const next = {};
    const code = editing ? currency.code : draft.code.trim().toUpperCase();
    if (!editing) {
      if (code.length !== 3) next.code = 'Use a 3-letter ISO-4217 code.';
      else if (existingCodes && existingCodes.includes(code)) next.code = 'That currency already exists.';
    }
    if (!draft.name.trim()) next.name = 'Give the currency a name.';
    if (Object.keys(next).length) { setErrors(next); return; }
    if (editing) {
      // Code, base flag and archive state stay as-is — archive is a row action.
      onSave && onSave(code, {
        name: draft.name.trim(),
        symbol: draft.symbol.trim() || code,
        minorUnits: parseInt(draft.minorUnits, 10),
      });
    } else {
      onCreate && onCreate({
        code,
        name: draft.name.trim(),
        symbol: draft.symbol.trim() || code,
        minorUnits: parseInt(draft.minorUnits, 10),
        base: false,
        archived: null,
      });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit currency' : 'New currency'}
      subtitle={editing ? 'Update this currency’s name, symbol, or precision.' : 'Enable a currency your accounts and budgets can be denominated in.'}
      icon="attach_money"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create currency'}
          </Button>
        </React.Fragment>
      }>
      <FormRow>
        <Field label="Currency code" value={editing ? currency.code : draft.code}
          onChange={(v) => set('code')(v.toUpperCase().replace(/[^A-Z]/g, '').slice(0, 3))}
          placeholder="e.g. EUR" error={errors.code}
          helper={editing ? 'ISO-4217 code — can’t be changed' : 'ISO-4217 · 3 letters'}
          disabled={editing} autoFocus={!editing} />
        <Field label="Symbol" value={draft.symbol} onChange={(v) => set('symbol')(v.slice(0, 8))}
          placeholder="e.g. €" helper="Up to 8 characters" />
      </FormRow>
      <Field label="Name" value={draft.name} onChange={set('name')}
        placeholder="e.g. Euro" error={errors.name} helper="Up to 64 characters" autoFocus={editing} />
      <Select label="Minor units" value={draft.minorUnits} onChange={set('minorUnits')}
        options={CUR_MINOR_OPTIONS} helper="Decimal places (2 for most currencies, 0 for JPY)." />
    </Modal>
  );
};

/* ---------- Page ---------- */
const Currencies = () => {
  const { useState, useEffect, useMemo } = React;
  const d = window.OdysseyData;

  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [statusFilter, setStatusFilter] = useState([]);
  const [adding, setAdding] = useState(false);
  const [editingCur, setEditingCur] = useState(null);
  const [rows, setRows] = useState(d.currencies);
  // Shared sort (§6.5): Code + Name curated; one {key,dir} synced with headers.
  const [sort, setSort] = useState({ key: 'code', dir: 'asc' });
  // Server pagination: page (1-based) + rows-per-page (footer Pager owns it,
  // toolbar PageSizeSelect mirrors it).
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  const createCur = (dto) => {
    setRows(prev => [dto, ...prev]);
    setAdding(false);
  };
  const onSave = (code, patch) => { setRows(prev => prev.map(c => c.code === code ? { ...c, ...patch } : c)); setEditingCur(null); };
  const onDelete = (code) => setRows(prev => prev.filter(c => c.code !== code));

  const filtered = useMemo(() => rows.filter(c => {
    const st = c.archived ? 'archived' : 'active';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (debouncedQ) {
      const hay = `${c.code} ${c.name} ${c.symbol || ''}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [rows, statusFilter, debouncedQ]);

  // Any search / filter / sort / size change returns to page 1 (server contract).
  useEffect(() => { setPage(1); }, [debouncedQ, statusFilter, sort, pageSize]);
  const totalCount = filtered.length;
  const paged = useMemo(() => {
    if (pageSize === 'all') return filtered;
    const start = (page - 1) * pageSize;
    return filtered.slice(start, start + pageSize);
  }, [filtered, page, pageSize]);

  const activeCount = rows.filter(c => !c.archived).length;
  const archivedCount = rows.length - activeCount;
  const hasFilters = !!(debouncedQ || statusFilter.length);
  const clearFilters = () => { setQ(''); setStatusFilter([]); };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Currencies"
        icon="payments"
        sub={`${activeCount} active · ${archivedCount} archived · USD base`}
        overview={(
          <div className="odc-summary-grid">
            <BreakdownTile label="By status" empty="No currencies."
              rows={odcStatusRows(rows, [
                { key: 'active', label: 'Active', tone: 'income', icon: 'task_alt' },
                { key: 'archived', label: 'Archived', tone: 'outline', icon: 'inventory_2' },
              ], (c) => (c.archived ? 'archived' : 'active'))} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <Field placeholder="Search code, name, or symbol…" value={q} onChange={setQ} clearable />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter} options={CUR_STATUS_OPTIONS} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[
                { key: 'code', label: 'Code', type: 'text' },
                { key: 'name', label: 'Name', type: 'text' },
              ]} />
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
          </div>
        )}
        primary={{ label: 'New currency', icon: 'add', onClick: () => setAdding(true) }}
      />

      {adding && <AddCurrencyModal onClose={() => setAdding(false)} onCreate={createCur} existingCodes={rows.map(c => c.code)} />}
      {editingCur && <AddCurrencyModal currency={editingCur} onClose={() => setEditingCur(null)} onSave={onSave} />}

      <Card>
        <CardBody style={{ padding: 0 }}>
          <CurrencyTable
            currencies={paged}
            sort={sort}
            onSortChange={setSort}
            onSave={onSave}
            onDelete={onDelete}
            onEdit={setEditingCur}
            empty={(
              <EmptyState icon="payments" mutedIcon
                title="No currencies match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everything.' : 'Enable the currencies your accounts and budgets use.'}
                action={hasFilters
                  ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button>
                  : <Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>New currency</Button>} />
            )}
          />
          {totalCount > 0 && (
            <Pager page={page} pageSize={pageSize} totalCount={totalCount}
              onPageChange={setPage} onPageSizeChange={setPageSize} />
          )}
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { Currencies, CurrencyTable, AddCurrencyModal });
