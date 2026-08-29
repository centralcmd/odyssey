/* Transactions list — search + filters + the shared DS TxnTable, whose rows
   follow the Users/Accounts pattern: click (or the row-actions menu) to expand
   into a read-only detail panel, then an Edit panel swaps in to change the
   transaction. Status from the TransactionStatus enum. */

const STATUS_TONE = { New: 'info', Approved: 'income', Flagged: 'expense' };
const TXN_STATUS_OPTIONS = [
  { value: 'New', label: 'New' },
  { value: 'Approved', label: 'Approved' },
  { value: 'Flagged', label: 'Flagged' },
];
const TXN_DIR_OPTIONS = [
  { value: 'expense', label: 'Money out · expense' },
  { value: 'income', label: 'Money in · income' },
];

/* Contact is the leading segment of the description (e.g. "Spotify · Monthly"). */
const txnContact = (t) => (t.desc.split(' · ')[0] || '').trim();

/* ---------- Expanded DETAIL (read view) ---------- */
const TxnDetail = ({ t, onNavigate }) => {
  const d = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const acct = d.accountById[t.account];
  const tags = d.txnTags(t);
  const cp = t.contact && d.contacts ? d.contacts.find(c => c.id === t.contact) : null;
  const files = H.filesForTransaction(t);
  return (
    <div className="acct-detail">
      <div className="meta-grid">
        <MetaTile label="Description" value={t.desc} />
        <MetaTile label="Account" value={acct ? `${acct.name} ${acct.number}` : '—'} />
        <MetaTile label="Contact" value={cp ? cp.name : (txnContact(t) || '—')} />
        <MetaTile label={tags.length === 1 ? 'Tag' : 'Tags'} value={<TagChips tags={tags.map(tg => ({ id: tg.id, label: tg.name }))} />} />
        <MetaTile label="Status" value={<Chip tone={STATUS_TONE[t.status]} dot>{t.status}</Chip>} />
        <MetaTile label="Direction" value={t.dir === 'income' ? 'Money in' : 'Money out'} />
        <MetaTile label="Amount" value={H.signedMoney(t.amount)} mono valueClass={t.dir} />
        <MetaTile label="Date" value={H.dateLong(t.date)} mono />
        <MetaTile label="Currency" value={t.currency || 'USD'} mono />
        {t.statusComment && <MetaTile label="Status comment" value={t.statusComment} />}
        {(t.externalId || t.internalId) && (
          <React.Fragment>
            <MetaTile label="External ID" value={t.externalId || '—'} mono />
            <MetaTile label="Internal ID" value={t.internalId || '—'} mono />
          </React.Fragment>
        )}
        {t.extraData && <MetaTile label="Extra data" value={t.extraData} />}
      </div>

      <Collapsible icon="attach_file" title="Files" count={files.length} defaultOpen={files.length > 0}
        action={<Button variant="text" color="primary" iconRight="arrow_forward" onClick={() => onNavigate && onNavigate('files')}>View all</Button>}
      >
        {files.length === 0 ? (
          <div className="empty-line">No files attached to this transaction yet.</div>
        ) : (
          <InlinePager items={files}>
            {(pageRows) => <FilesTable files={pageRows} account={acct}
              kinds={window.OdysseyData.transactionFileTypes} showValidity={false} />}
          </InlinePager>
        )}
      </Collapsible>
    </div>
  );
};

/* ---------- Reusable transactions table ----------
   The sortable, expandable, editable table is now the shared DS TxnTable
   (components/TxnTable.jsx) — there is no second implementation here. This
   bridge joins each txn to its account / tag names (the DS component is
   data-prop driven, no store lookups) and supplies the kit's detail + edit
   panels; the DS component owns sorting, accordion expansion, the "Saved"
   flash and the row menu. No pagination in the MVP — lists render whole. */
const TxnTable = ({ txns, onSave, onDelete, onNavigate, hideAccount = false, sort, onSortChange, empty, ariaLabel }) => {
  const { useMemo, useState } = React;
  const d = window.OdysseyData;
  const H = window.OdysseyHelpers;
  const DSTxnTable = (window.OdysseyDesignSystem_d5aa51 || {}).TxnTable;
  const AddTransactionModal = window.AddTransactionModal;
  const [editId, setEditId] = useState(null);
  const editRow = editId ? txns.find(x => x.id === editId) : null;

  const rows = useMemo(() => txns.map(t => {
    const acct = d.accountById[t.account];
    const tags = d.txnTags(t);
    return {
      ...t,
      accountLabel: acct ? acct.name : '',
      accountNumber: acct ? acct.number : '',
      tags: tags.map(tg => ({ id: tg.id, label: tg.name })),
      contact: txnContact(t),
    };
  }), [txns]);

  return (
    <React.Fragment>
    <DSTxnTable
      txns={rows}
      hideAccount={hideAccount}
      ariaLabel={ariaLabel}
      statusTones={STATUS_TONE}
      formatAmount={(t) => H.signedMoney(t.amount)}
      formatDate={H.dateShort}
      renderDetail={(t) => <TxnDetail t={t} onNavigate={onNavigate} />}
      sort={sort}
      onSortChange={onSortChange}
      onSave={onSave}
      onDelete={onDelete}
      empty={empty}
      actions={(t, ctx) => {
        // Status transitions from the TransactionStatus enum (New · Approved ·
        // Flagged), shown only for the states the row isn't already in.
        const setStatus = (status) => onSave && onSave(t.id, { status });
        const statusItems = [
          ...(t.status !== 'Approved' ? [{ icon: 'check_circle', label: 'Approve', onClick: () => setStatus('Approved') }] : []),
          ...(t.status !== 'Flagged' ? [{ icon: 'flag', label: 'Flag', onClick: () => setStatus('Flagged') }] : []),
          ...(t.status !== 'New' ? [{ icon: 'undo', label: 'Reset to New', onClick: () => setStatus('New') }] : []),
        ];
        return [
          { icon: ctx.expanded ? 'close' : 'expand_more', label: ctx.expanded ? 'Collapse' : 'View details', onClick: ctx.toggle },
          { icon: 'edit', label: 'Edit', onClick: () => setEditId(t.id) },
          ...(statusItems.length ? [{ divider: true }, ...statusItems] : []),
          { divider: true },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(t.id); } },
          ...(onDelete ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove }] : []),
        ];
      }}
    />
    {editRow && AddTransactionModal && (
      <AddTransactionModal
        transaction={editRow}
        onClose={() => setEditId(null)}
        onSave={(patch) => { if (onSave) onSave(editRow.id, patch); setEditId(null); }}
      />
    )}
    </React.Fragment>
  );
};

const Transactions = ({ onNavigate }) => {
  const { useState, useEffect, useMemo } = React;
  const d = window.OdysseyData;
  const H = window.OdysseyHelpers;

  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [acctFilter, setAcctFilter] = useState([]);
  const [statusFilter, setStatusFilter] = useState([]);
  const [tagFilter, setTagFilter] = useState([]);
  const [dirFilter, setDirFilter] = useState([]);
  const [adding, setAdding] = useState(false);
  const [txns, setTxns] = useState(d.transactions);
  // Shared sort (§6.2 of the sorting spec): one {key,dir} drives BOTH the
  // toolbar SortSelect and the TxnTable header sort. Keys = the table's
  // sortable columns; the curated subset below is what the dropdown offers.
  const [sort, setSort] = useState({ key: 'date', dir: 'desc' });
  // Server pagination: page (1-based) + rows-per-page (footer Pager owns it,
  // toolbar PageSizeSelect mirrors it).
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);

  // Debounce the search ~300ms.
  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  // Map a NewTransaction DTO from the modal into a list row for the prototype.
  const createTransaction = (dto) => {
    const row = {
      id: `new-${Date.now()}`,
      date: dto.TimeStamp || new Date().toISOString().slice(0, 10),
      desc: dto.Description,
      account: dto.AccountId,
      tags: dto.TransactionTagIds || [],
      amount: dto.Amount,
      status: dto.Status,
      icon: dto.Amount >= 0 ? 'arrow_downward' : 'shopping_cart',
      dir: dto.dir || (dto.Amount >= 0 ? 'income' : 'expense'),
    };
    setTxns(prev => [row, ...prev]);
    setAdding(false);
  };

  const onSave = (id, patch) => setTxns(prev => prev.map(t => t.id === id ? { ...t, ...patch } : t));
  const onDelete = (id) => setTxns(prev => prev.filter(t => t.id !== id));

  const filtered = useMemo(() => txns.filter(t => {
    if (acctFilter.length && !acctFilter.includes(t.account)) return false;
    if (statusFilter.length && !statusFilter.includes(t.status)) return false;
    if (tagFilter.length && !d.txnTagIds(t).some(id => tagFilter.includes(id))) return false;
    if (dirFilter.length && !dirFilter.includes(t.dir)) return false;
    if (debouncedQ) {
      const n = debouncedQ.toLowerCase();
      const acct = d.accountById[t.account];
      const tagNames = d.txnTags(t).map(tg => tg.name).join(' ');
      const hay = `${t.desc} ${acct ? acct.name : ''} ${tagNames} ${Math.abs(t.amount)}`.toLowerCase();
      if (!hay.includes(n)) return false;
    }
    return true;
  }), [txns, acctFilter, statusFilter, tagFilter, dirFilter, debouncedQ]);

  const total = filtered.length;
  const totalIn  = filtered.filter(t => t.dir === 'income').reduce((s, t) => s + t.amount, 0);
  const totalOut = filtered.filter(t => t.dir === 'expense').reduce((s, t) => s + t.amount, 0);

  const hasFilters = !!(debouncedQ || acctFilter.length || statusFilter.length || tagFilter.length || dirFilter.length);
  const clearFilters = () => { setQ(''); setAcctFilter([]); setStatusFilter([]); setTagFilter([]); setDirFilter([]); };

  // Any search / filter / sort / size change returns to page 1 (server contract).
  useEffect(() => { setPage(1); }, [debouncedQ, acctFilter, statusFilter, tagFilter, dirFilter, sort, pageSize]);
  const paged = useMemo(() => {
    if (pageSize === 'all') return filtered;
    const start = (page - 1) * pageSize;
    return filtered.slice(start, start + pageSize);
  }, [filtered, page, pageSize]);

  return (
    <div className="col gap-6">
      <PageHeader
        title="Transactions"
        icon="receipt_long"
        sub={`${total} transactions · in ${H.money(totalIn)} · out ${H.money(Math.abs(totalOut))}`}
        overview={(
          <div className="odc-summary-grid">
            <BreakdownTile label="By status" empty="No transactions."
              rows={odcStatusRows(txns, [
                { key: 'New', label: 'New', tone: 'info', icon: 'fiber_new' },
                { key: 'Approved', label: 'Approved', tone: 'income', icon: 'check_circle' },
                { key: 'Flagged', label: 'Flagged', tone: 'expense', icon: 'flag' },
              ], (t) => t.status)} />
            <BreakdownTile label="By type" empty="No transactions."
              rows={odcStatusRows(txns, [
                { key: 'income', label: 'Money in', tone: 'income', icon: 'arrow_downward' },
                { key: 'expense', label: 'Money out', tone: 'expense', icon: 'arrow_upward' },
              ], (t) => t.dir)} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap', alignItems: 'flex-end' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search description, contact, amount…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 190 }}>
              <MultiSelect allLabel="All accounts" value={acctFilter} onChange={setAcctFilter}
                options={d.accounts.map(a => ({ value: a.id, label: `${a.name} ${a.number}` }))} />
            </div>
            <div style={{ minWidth: 160 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={TXN_STATUS_OPTIONS} />
            </div>
            <div style={{ minWidth: 160 }}>
              <MultiSelect allLabel="All tags" value={tagFilter} onChange={setTagFilter}
                options={d.tags.map(t => ({ value: t.id, label: t.name }))} />
            </div>
            <div style={{ minWidth: 150 }}>
              <MultiSelect allLabel="Any direction" value={dirFilter} onChange={setDirFilter}
                options={[
                  { value: 'income',  label: 'Money in' },
                  { value: 'expense', label: 'Money out' },
                ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[
                { key: 'date',         label: 'Date',         type: 'date' },
                { key: 'amount',       label: 'Amount',       type: 'number' },
                { key: 'desc',         label: 'Description',  type: 'text' },
                { key: 'contact', label: 'Contact', type: 'text' },
                { key: 'account',      label: 'Account',      type: 'text' },
                { key: 'status',       label: 'Status',       type: 'status' },
              ]} />
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
          </div>
        )}
        primary={{ label: 'New transaction', icon: 'add', onClick: () => setAdding(true) }}
      />

      {adding && (
        <AddTransactionModal
          defaultAccount={acctFilter.length === 1 ? acctFilter[0] : ''}
          onClose={() => setAdding(false)}
          onCreate={createTransaction}
        />
      )}

      <Card>
        <CardBody style={{ padding: 0 }}>
          <TxnTable
            txns={paged}
            ariaLabel="Transactions"
            onNavigate={onNavigate}
            sort={sort}
            onSortChange={setSort}
            onSave={onSave}
            onDelete={onDelete}
            empty={(
              <EmptyState icon="receipt_long" mutedIcon
                title="No transactions match"
                desc={hasFilters ? 'Try a different search or clear the filters to see everything.' : 'There are no transactions to show.'}
                action={hasFilters ? <Button variant="outlined" icon="close" onClick={clearFilters}>Clear filters</Button> : undefined} />
            )}
          />
          {total > 0 && (
            <Pager page={page} pageSize={pageSize} totalCount={total}
              onPageChange={setPage} onPageSizeChange={setPageSize} />
          )}
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { Transactions, TxnTable });
