/* Exchange rates — a flat, newest-first log of every recorded rate, following
   the Transactions table design (sortable rows; every field is a column, so
   rows don't expand).

   Records are edited through the same dialog that creates them (reused in an
   edit mode that locks the currency pair). The latest entry for each pair is
   flagged Current; older ones are Historical.

   Fields mirror the Odyssey.Finance.Dtos ExchangeRate DTOs:
     ExistingExchangeRate — ExchangeRateId, FromCurrencyCode (3), ToCurrencyCode
       (3), Rate (decimal), AsOf (datetime), CreatedAt (datetime)
     NewExchangeRate      — FromCurrencyCode, ToCurrencyCode, Rate (>0),
       AsOf (datetime?, defaults to server UtcNow) */

const XR_TONE = { bg: 'oklch(0.75 0.16 330 / 0.16)', fg: 'oklch(0.75 0.16 330)' };
const XR_STATUS_OPTIONS = [
  { value: 'current',    label: 'Current' },
  { value: 'historical', label: 'Historical' },
];
const xrFmtRate = (n) => Number(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
const xrInverse = (r) => (r.rate ? 1 / r.rate : 0);

const xrSortVal = (r, key) => {
  switch (key) {
    case 'pair':      return `${r.from}>${r.to}`;
    case 'rate':      return r.rate;
    case 'inverse':   return xrInverse(r);
    case 'asOf':      return r.asOf;
    case 'createdAt': return r.createdAt;
    case 'status':    return r._current ? 0 : 1;
    default:          return 0;
  }
};

/* ---------- Table (shared DS RecordTable — append-only log, edit via modal) ---------- */
const ExchangeRateTable = ({ rates, onDelete, onEdit, sort, onSortChange, empty }) => {
  const H = window.OdysseyHelpers;
  const statusChip = (r) => (r._current
    ? <Chip tone="info" dot>Current</Chip>
    : <Chip tone="outline" dot>Historical</Chip>);
  return (
    <RecordTable
      rows={rates}
      ariaLabel="Exchange rates"
      rowKey={(r) => r.id}
      defaultSort={{ key: 'asOf', dir: 'desc' }}
      sort={sort}
      onSortChange={onSortChange}
      multiOpen
      keepDirOnColumnChange
      tiebreak={(a, b) => (a.asOf < b.asOf ? 1 : -1)}
      leading={() => <Avatar icon="currency_exchange" tone={XR_TONE} />}
      columns={[
        {
          key: 'pair', header: 'Pair', sortable: true, sortType: 'text', sortValue: (r) => xrSortVal(r, 'pair'),
          cell: (r) => (
            <span className="mono" style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              {r.from}<MIcon name="arrow_forward" size={15} style={{ opacity: 0.5 }} />{r.to}
            </span>
          ),
        },
        { key: 'rate', header: 'Rate', sortable: true, sortType: 'number', align: 'right', className: 'mono', sortValue: (r) => xrSortVal(r, 'rate'), cell: (r) => xrFmtRate(r.rate) },
        { key: 'inverse', header: 'Inverse', sortable: true, sortType: 'number', align: 'right', className: 'mono muted', sortValue: (r) => xrSortVal(r, 'inverse'), cell: (r) => xrFmtRate(xrInverse(r)) },
        { key: 'status', header: 'Status', sortable: true, sortType: 'status', sortValue: (r) => xrSortVal(r, 'status'), cell: statusChip },
        { key: 'asOf', header: 'Effective from', sortable: true, sortType: 'date', align: 'right', className: 'muted mono', sortValue: (r) => xrSortVal(r, 'asOf'), cell: (r) => H.dateTime(r.asOf) },
        { key: 'createdAt', header: 'Recorded', sortable: true, sortType: 'date', align: 'right', className: 'muted mono', sortValue: (r) => xrSortVal(r, 'createdAt'), cell: (r) => H.dateTime(r.createdAt) },
      ]}
      actions={(r, ctx) => [
        { icon: 'edit', label: 'Edit', onClick: () => onEdit(r) },
        { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(r.id); } },
        { divider: true },
        { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove },
      ]}
      onDelete={onDelete}
      empty={empty}
    />
  );
};

/* ---------- New / Edit exchange-rate dialog (New/ExistingExchangeRate DTO) ----------
   One dialog serves both record and edit: pass an existing `rate` to prefill and
   switch into edit mode. The currency pair (From/To) is the record's identity,
   so edit mode locks it and only Rate + AsOf stay editable. */
const XR_ISO_DATE = (d) => {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
};

const RecordRateModal = ({ onClose, onCreate, onSave, rate: editRate, initial }) => {
  const { useState } = React;
  const d = window.OdysseyData;
  const editing = !!editRate;
  const codeOptions = d.currencies.filter(c => !c.archived).map(c => ({ value: c.code, label: `${c.code} · ${c.name}` }));
  const [draft, setDraft] = useState({
    from: editRate?.from || (initial && initial.from) || 'USD',
    to: editRate?.to || (initial && initial.to) || '',
    rate: editRate ? String(editRate.rate) : '',
    asOf: editRate ? XR_ISO_DATE(new Date(editRate.asOf)) : XR_ISO_DATE(new Date()),
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => {
    setDraft(s => ({ ...s, [k]: v }));
    if (errors[k]) setErrors(e => ({ ...e, [k]: undefined }));
  };

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const rateNum = parseFloat(String(draft.rate).replace(/,/g, ''));
  const submit = () => {
    const next = {};
    if (!draft.to) next.to = 'Pick a target currency.';
    else if (draft.to === draft.from) next.to = 'From and To must differ.';
    if (!(rateNum > 0)) next.rate = 'Enter a rate greater than zero.';
    if (Object.keys(next).length) { setErrors(next); return; }
    if (editing) {
      // Pair identity is locked; only the rate + effective date change.
      onSave && onSave(editRate.id, {
        rate: Number(rateNum),
        asOf: `${draft.asOf}T09:00:00Z`,
      });
    } else {
      onCreate && onCreate({
        from: draft.from,
        to: draft.to,
        rate: Number(rateNum),
        asOf: `${draft.asOf}T09:00:00Z`,
        createdAt: new Date().toISOString(),
      });
    }
  };

  return (
    <Modal
      title={editing ? 'Edit exchange rate' : 'New exchange rate'}
      subtitle={editing
        ? 'Correct this entry’s rate or effective date. The currency pair can’t be changed.'
        : 'Rates are append-only — this adds a new entry and becomes the current rate for the pair.'}
      icon="currency_exchange"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
            {editing ? 'Save changes' : 'Create rate'}
          </Button>
        </React.Fragment>
      }>
      <FormRow>
        <Select label="From currency" value={draft.from} onChange={set('from')} options={codeOptions} disabled={editing} />
        <Select label="To currency" value={draft.to} onChange={set('to')} options={codeOptions} helper={errors.to} placeholder="Select…" disabled={editing} />
      </FormRow>
      <Field label="Rate" value={draft.rate} onChange={set('rate')}
        placeholder="e.g. 0.9218" error={errors.rate}
        helper={draft.to ? `1 ${draft.from} = rate × ${draft.to} · must be greater than 0` : 'Units of the To currency per 1 unit of From'} />
      <DateField label="Effective from (AsOf)" value={draft.asOf} onChange={set('asOf')} helper={editing ? 'When this rate takes effect' : 'Defaults to today'} />
    </Modal>
  );
};

/* ---------- Page ---------- */
const ExchangeRates = () => {
  const { useState, useEffect, useMemo } = React;
  const d = window.OdysseyData;
  const H = window.OdysseyHelpers;

  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [statusFilter, setStatusFilter] = useState([]);
  const [toFilter, setToFilter] = useState([]);
  const [recording, setRecording] = useState(null); // null | {} | { from, to }
  const [editingRate, setEditingRate] = useState(null);
  const [rates, setRates] = useState(d.exchangeRates);
  // Shared sort (§6.6): As-of date (default) · Currency pair · Rate; one
  // {key,dir} synced with the table headers.
  const [sort, setSort] = useState({ key: 'asOf', dir: 'desc' });
  // Server pagination: page (1-based) + rows-per-page (footer Pager owns it,
  // toolbar PageSizeSelect mirrors it).
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  // Tag each rate with whether it's the current (latest AsOf) one for its pair.
  const tagged = useMemo(() => {
    const currentIds = H.currentRateIds(rates);
    return rates.map(r => ({ ...r, _current: currentIds.has(r.id) }));
  }, [rates]);

  const createRate = (dto) => {
    setRates(prev => [{ id: `rate-${Date.now()}`, ...dto }, ...prev]);
    setRecording(null);
  };
  const onDelete = (id) => setRates(prev => prev.filter(r => r.id !== id));
  const onSave = (id, patch) => { setRates(prev => prev.map(r => r.id === id ? { ...r, ...patch } : r)); setEditingRate(null); };

  const toOptions = useMemo(() => {
    const codes = Array.from(new Set(rates.map(r => r.to))).sort();
    return codes.map(c => ({ value: c, label: c }));
  }, [rates]);

  const filtered = useMemo(() => tagged.filter(r => {
    const st = r._current ? 'current' : 'historical';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (toFilter.length && !toFilter.includes(r.to)) return false;
    if (debouncedQ) {
      const hay = `${r.from} ${r.to} ${r.from}/${r.to} ${r.rate}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [tagged, statusFilter, toFilter, debouncedQ]);

  // Any search / filter / sort / size change returns to page 1 (server contract).
  useEffect(() => { setPage(1); }, [debouncedQ, statusFilter, toFilter, sort, pageSize]);
  const totalCount = filtered.length;
  const paged = useMemo(() => {
    if (pageSize === 'all') return filtered;
    const start = (page - 1) * pageSize;
    return filtered.slice(start, start + pageSize);
  }, [filtered, page, pageSize]);

  const pairCount = new Set(rates.map(r => `${r.from}>${r.to}`)).size;
  const latestAsOf = rates.reduce((m, r) => (r.asOf > m ? r.asOf : m), '');
  const hasFilters = !!(debouncedQ || statusFilter.length || toFilter.length);
  const clearFilters = () => { setQ(''); setStatusFilter([]); setToFilter([]); };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Exchange rates"
        icon="currency_exchange"
        sub={`${rates.length} rates · ${pairCount} pairs · latest ${H.dateTime(latestAsOf)}`}
        overview={(
          <div className="odc-summary-grid">
            <BreakdownTile label="By status" empty="No rates."
              rows={odcStatusRows(tagged, [
                { key: 'current', label: 'Current', tone: 'income', icon: 'bolt' },
                { key: 'historical', label: 'Historical', tone: 'outline', icon: 'history' },
              ], (r) => (r._current ? 'current' : 'historical'))} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <div style={{ minWidth: 260, flex: 1 }}>
              <SearchField placeholder="Search pair, e.g. USD/EUR…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="All targets" value={toFilter} onChange={setToFilter} options={toOptions} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Current + history" value={statusFilter} onChange={setStatusFilter} options={XR_STATUS_OPTIONS} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[
                { key: 'asOf', label: 'As-of date', type: 'date' },
                { key: 'pair', label: 'Currency pair', type: 'text' },
                { key: 'rate', label: 'Rate', type: 'number' },
              ]} />
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
          </div>
        )}
        primary={{ label: 'New rate', icon: 'add', onClick: () => setRecording({}) }}
      />

      {recording && <RecordRateModal onClose={() => setRecording(null)} onCreate={createRate} initial={recording} />}
      {editingRate && <RecordRateModal rate={editingRate} onClose={() => setEditingRate(null)} onSave={onSave} />}

      <Card>
        <CardBody style={{ padding: 0 }}>
          <ExchangeRateTable
            rates={paged}
            sort={sort}
            onSortChange={setSort}
            onDelete={onDelete}
            onEdit={setEditingRate}
            empty={(
              <EmptyState icon="currency_exchange" mutedIcon
                title="No rates match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everything.' : 'Record the rates Odyssey uses to convert foreign-currency balances into your base currency.'}
                action={hasFilters
                  ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button>
                  : <Button variant="filled" color="primary" icon="add" onClick={() => setRecording({})}>New rate</Button>} />
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

Object.assign(window, { ExchangeRates, ExchangeRateTable, RecordRateModal });
