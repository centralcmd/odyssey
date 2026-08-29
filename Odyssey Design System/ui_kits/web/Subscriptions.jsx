/* Subscriptions — /subscriptions
   ----------------------------------------------------------------------------
   A pure record-keeping list of recurring subscriptions. Sibling of
   Contacts: the same PageHeader + shared RecordTable scaffold (search /
   interval + status + paused filters / curated SortSelect / expandable detail /
   edit via the create dialog reused in edit mode), driven by the Subscription DTOs.

   A subscription records what it is, an optional external id, an optional linked
   company (contact), a validity window (start / end), a price
   (amount + currency), a billing interval, and a first-billing-date anchor. The
   per-cycle billing position ("day 15", "15 Jan", "Wed") is DERIVED at render
   time — never stored. Paused and Archived are two INDEPENDENT flags: Paused =
   still visible, flagged as not currently billing; Archived = hidden from the
   default list. No transactions, no scheduling, no spend rollup (all Non-Goals).

   Seed + helpers from subscriptions-data.js; atoms from the DS bundle via
   Components.jsx (BillingIntervalSelect / MultiSelect / Chip, SubscriptionStatusChip). */

const SUB_H = window.OdysseyHelpers;
const SUB_D = window.OdysseyData;

const subTone = (interval) => {
  const m = SUB_H.subIntervalInfo(interval);
  return { bg: m.soft, fg: m.color };
};

// The figure caption under the price in the collapsed row header. For a plain
// cadence (count 1) it's "per month"; the "every N" case is handled inline.
const SUB_PER_WORD = { Daily: 'per day', Weekly: 'per week', Monthly: 'per month', Yearly: 'per year' };
const SUB_UNIT_NOUN = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };
// The price caption: "per month" (count 1) or "every 2 months" (count > 1).
const subFigureCaption = (s) => {
  const n = SUB_H.subIntervalCount(s);
  return n > 1 ? `every ${n} ${SUB_UNIT_NOUN[s.interval] || 'cycle'}s` : (SUB_PER_WORD[s.interval] || 'recurring');
};

/* ---------- Expanded DETAIL ---------- */
const SubDetail = ({ s }) => {
  const cp = SUB_H.subContact(s);
  const cpMeta = cp && (SUB_D.contactTypeByKey[cp.type] || {});
  const nextBilling = SUB_H.subNextBilling(s);
  const ended = SUB_H.subEnded(s);
  const nextBillingValue = s.archived ? 'Archived'
    : ended ? 'Ended'
    : s.paused ? 'Paused'
    : nextBilling
      ? `${SUB_H.dateLong(nextBilling)} · ${SUB_H.subRelDays(SUB_H.subDaysUntil(nextBilling))}`
      : 'No further billing';
  return (
    <div className="acct-detail">
      <div className="meta-grid">
        <MetaTile label="Name" value={s.name} />
        <MetaTile label="External id" value={s.externalId || '—'} mono />
        <MetaTile label="Company" value={cp
          ? <Chip tone="outline" icon={cpMeta.icon}>{cp.name}</Chip>
          : '—'} />
        <MetaTile label="Price" value={SUB_H.subMoney(s.amount, s.currencyCode)} mono valueClass="sub-price-val" />
        <MetaTile label="Billing interval" value={<BillingIntervalChip interval={s.interval} count={SUB_H.subIntervalCount(s)} firstBillingDate={s.firstBillingDate} />} />
        <MetaTile label="First billing date" value={SUB_H.dateLong(s.firstBillingDate)} mono />
        <MetaTile label="Next billing" value={nextBillingValue} mono={!s.archived && !s.paused && !ended} />
        <MetaTile label="Start date" value={SUB_H.dateLong(s.startDate)} mono />
        <MetaTile label={ended ? 'Ended on' : 'End date'} value={s.endDate ? SUB_H.dateLong(s.endDate) : '—'} mono />
        <MetaTile label="Status" value={<SubscriptionStatusChip paused={s.paused} ended={ended} archived={s.archived} showActive />} />
        {s.notes ? <MetaTile label="Notes" value={s.notes} /> : null}
        {s.paused ? <MetaTile label="Paused since" value={SUB_H.dateTime(s.paused)} mono /> : null}
        {s.archived ? <MetaTile label="Archived" value={SUB_H.dateTime(s.archived)} mono /> : null}
      </div>
    </div>
  );
};

/* ---------- One subscription list item (Contracts-style record card) ---------- */
const SubscriptionListItem = ({ row, defaultOpen, highlight, onSave, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(!!defaultOpen);
  const [showEdit, setShowEdit] = useState(false);
  const cardRef = useRef(null);

  const s = row;
  const info = SUB_H.subIntervalInfo(s.interval);
  const cp = SUB_H.subContact(s);
  const cpMeta = cp && (SUB_D.contactTypeByKey[cp.type] || {});
  const anchor = SUB_H.subBillingAnchor(s);
  const dimmed = !!s.archived;
  const ended = SUB_H.subEnded(s);

  const saveEdit = (patch) => { onSave(s.id, patch); setShowEdit(false); };
  const togglePaused = () => onSave(s.id, { paused: s.paused ? null : new Date().toISOString() });
  const toggleArchive = () => onSave(s.id, { archived: s.archived ? null : new Date().toISOString() });
  const endNow = () => onSave(s.id, { endDate: SUB_H.subToday() });

  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    setOpen(true);
    const el = cardRef.current;
    let scroller = el.parentElement;
    while (scroller && scroller !== document.body) {
      const oy = getComputedStyle(scroller).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && scroller.scrollHeight > scroller.clientHeight) break;
      scroller = scroller.parentElement;
    }
    requestAnimationFrame(() => {
      if (scroller && scroller !== document.body) {
        const top = scroller.scrollTop + (el.getBoundingClientRect().top - scroller.getBoundingClientRect().top) - 24;
        scroller.scrollTo({ top, behavior: 'smooth' });
      }
    });
  }, [highlight]);

  return (
    <Card className={`acct-item ${open ? 'open' : ''} ${dimmed ? 'dimmed' : ''} ${highlight ? 'flash' : ''}`} ref={cardRef}>
      <div className="acct-head" onClick={() => setOpen((o) => !o)}>
        <Avatar icon={info.icon} tone={{ bg: info.soft, fg: info.color }} square size="lg" />

        <div className="acct-id">
          <div className="acct-name-row">
            <span className="acct-name">{s.name}</span>
            <SubscriptionStatusChip paused={s.paused} ended={ended} archived={s.archived} showActive size="sm" />
          </div>
          <div className="acct-tags">
            {s.externalId ? <React.Fragment><span className="mono sub-extid-inline">{s.externalId}</span><span className="acct-dot">·</span></React.Fragment> : null}
            <span className="sub-inst"><MIcon name={cp ? (cpMeta.icon || 'store') : 'store'} size={14} />{cp ? cp.name : 'No company'}</span>
            <span className="acct-dot">·</span>
            <span className="sub-cadence"><MIcon name={info.icon} size={14} />{SUB_H.subIntervalLabel(s)}{anchor ? ` · ${anchor}` : ''}</span>
          </div>
        </div>

        <div className="acct-figures">
          <div className="acct-balance mono sub-price">{SUB_H.subMoney(s.amount, s.currencyCode)}</div>
          <div className="sub-figure-word">{subFigureCaption(s)}</div>
        </div>

        <div className="acct-controls" onClick={(e) => e.stopPropagation()}>
          <ActionMenu items={[
            { icon: 'edit', label: 'Edit subscription', onClick: () => setShowEdit(true) },
            ...(ended ? [] : [{ icon: s.paused ? 'play_circle' : 'pause_circle', label: s.paused ? 'Resume' : 'Pause', onClick: togglePaused }]),
            ...(ended ? [] : [{ icon: 'event_busy', label: 'End subscription', onClick: endNow }]),
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(s.id); } },
            { divider: true },
            { icon: s.archived ? 'unarchive' : 'inventory_2', label: s.archived ? 'Restore' : 'Archive', onClick: toggleArchive },
            { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(s.id) },
          ]} />
          <button className="acct-expand" onClick={() => setOpen((o) => !o)} aria-label="Expand">
            <MIcon name="expand_more" size={22} className={`chev ${open ? 'open' : ''}`} />
          </button>
        </div>
      </div>

      {open && <SubDetail s={s} />}
      {showEdit && <AddSubscriptionModal subscription={s} onClose={() => setShowEdit(false)} onSave={saveEdit} />}
    </Card>
  );
};

/* ---------- Page ---------- */
const Subscriptions = () => {
  const { useState, useEffect, useMemo } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};

  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [intervalFilter, setIntervalFilter] = useState([]);
  const [statusFilter, setStatusFilter] = useState([]);
  const [adding, setAdding] = useState(false);
  const [rows, setRows] = useState(SUB_D.subscriptions);
  const [sort, setSort] = useState({ key: 'name', dir: 'asc' });
  const [jumpId, setJumpId] = useState(null);
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  const today = SUB_H.subToday();

  const jumpTo = (id) => {
    setJumpId(null);
    requestAnimationFrame(() => setJumpId(id));
    setTimeout(() => setJumpId((curr) => (curr === id ? null : curr)), 2200);
  };

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  const createSub = (dto) => { setRows((prev) => [{ id: `sub-${Date.now()}`, ...dto }, ...prev]); setAdding(false); };
  const onSave = (id, patch) => setRows((prev) => prev.map((s) => (s.id === id ? { ...s, ...patch } : s)));
  const onDelete = (id) => setRows((prev) => prev.filter((s) => s.id !== id));

  // Curated sort fields — one list feeds the SortSelect AND the ordering
  // (Frequency sorts by the interval's numeric enum order, not label).
  const sortFields = [
    { key: 'name',      label: 'Name',       type: 'text',   sortValue: (s) => (s.name || '').toLowerCase() },
    { key: 'amount',    label: 'Price',      type: 'number', sortValue: (s) => s.amount },
    { key: 'startDate', label: 'Start date', type: 'date',   sortValue: (s) => s.startDate || null },
    { key: 'interval',  label: 'Frequency',  type: 'status', sortValue: (s) => SUB_H.subIntervalInfo(s.interval).enumValue },
  ];

  const filtered = useMemo(() => rows.filter((s) => {
    // Derived single status (archived wins, then ended, then paused) — mirrors the summary.
    const st = s.archived ? 'Archived' : SUB_H.subEnded(s) ? 'Ended' : s.paused ? 'Paused' : 'Active';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    if (intervalFilter.length && !intervalFilter.includes(s.interval)) return false;
    if (debouncedQ) {
      const cp = SUB_H.subContact(s);
      const hay = `${s.name} ${s.externalId || ''} ${cp ? cp.name : ''} ${s.notes || ''}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [rows, statusFilter, intervalFilter, debouncedQ]);

  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(filtered, sortFields, sort, (s) => s.id) : filtered;

  const summary = SUB_H.subSummary(rows);
  const runRate = SUB_H.subRunRate(rows, today);
  // Display values for the run-rate InfoTiles. The tile value is the blended
  // base-currency total (≈ prefixed when >1 currency is in play). The multi-
  // currency detail stays visible in the "By currency" caption below.
  const runRateMulti = runRate.rows.length > 1;
  const runRateMonthly = runRate.convertedMonthly != null
    ? `${runRateMulti ? '≈ ' : ''}${SUB_H.subMoney(runRate.convertedMonthly, runRate.baseCurrency)}`
    : '—';
  const runRateYearly = runRate.convertedYearly != null
    ? `${runRateMulti ? '≈ ' : ''}${SUB_H.subMoney(runRate.convertedYearly, runRate.baseCurrency)}`
    : '—';
  const runRateForCurrencies = runRate.unconvertedCurrencies.length
    ? `in ${runRate.baseCurrency} · ${runRate.unconvertedCurrencies.join(', ')} excluded`
    : `in ${runRate.baseCurrency}`;
  const runRateTopDriver = runRate.topDriver
    ? `Largest: ${runRate.topDriver.name}`
    : `in ${runRate.baseCurrency}`;
  const upcoming = SUB_H.subUpcomingRenewals(rows, today, { windowDays: 45, limit: 6 });
  const active = rows.filter((s) => !s.archived);
  const hasFilters = !!(debouncedQ || intervalFilter.length || statusFilter.length);
  const clearFilters = () => { setQ(''); setIntervalFilter([]); setStatusFilter([]); };

  // Upcoming renewals surface in the header signal panel (info) — each row jumps
  // to and opens its card. Derived next-billing dates; nothing is scheduled.
  const signal = upcoming.length ? {
    severity: 'info',
    count: upcoming.length,
    label: 'Upcoming renewals',
    region: (
      <div className="signal-panel">
        {upcoming.map(({ sub, date, days }) => {
          const info = SUB_H.subIntervalInfo(sub.interval);
          return (
            <div key={sub.id} className="sub-renewal-row" role="button" tabIndex={0}
              onClick={() => jumpTo(sub.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(sub.id); } }}>
              <span className="sub-renewal-when">
                <span className="sub-renewal-md mono">{SUB_H.subDateMd(date)}</span>
                <span className="sub-renewal-rel">{SUB_H.subRelDays(days)}</span>
              </span>
              <span className="sub-renewal-name">
                <span className="material-icons" style={{ color: info.color }} aria-hidden="true">{info.icon}</span>
                {sub.name}
              </span>
              <span className="sub-renewal-amt mono">{SUB_H.subMoney(sub.amount, sub.currencyCode)}</span>
              <span className="sub-renewal-go">View →</span>
            </div>
          );
        })}
      </div>
    ),
  } : undefined;

  return (
    <div className="col gap-6">
      <PageHeader
        title="Subscriptions"
        icon="subscriptions"
        sub={`${summary.countsByStatus.active} active · ${summary.countsByStatus.paused} paused · ${summary.countsByStatus.ended} ended · ${summary.countsByStatus.archived} archived`}
        signal={signal}
        overview={(
          <div className="sub-overview">
            <div className="sub-stat-tiles">
              <InfoTile icon={SUB_H.subIntervalInfo('Monthly').icon} iconColor={SUB_H.subIntervalInfo('Monthly').color} label="Monthly run rate"
                value={runRateMonthly} foot={runRateTopDriver} />
              <InfoTile icon={SUB_H.subIntervalInfo('Yearly').icon} iconColor={SUB_H.subIntervalInfo('Yearly').color} label="Yearly run rate"
                value={runRateYearly} foot={runRateForCurrencies} />
            </div>
            <div className="sub-summary-grid">
              <BreakdownTile label="By interval" empty="No subscriptions."
                rows={summary.intervalRows.map((r) => ({ key: r.key, icon: r.icon, iconColor: r.color, label: r.label, count: r.count }))} />
              <BreakdownTile label="By status" empty="No subscriptions."
                rows={odcStatusRows(rows, [
                  { key: 'active',   label: 'Active',   tone: 'income',  icon: 'autorenew' },
                  { key: 'paused',   label: 'Paused',   tone: 'pending', icon: 'pause_circle' },
                  { key: 'ended',    label: 'Ended',    tone: 'expense', icon: 'event_busy' },
                  { key: 'archived', label: 'Archived', tone: 'outline', icon: 'inventory_2' },
                ], (s) => (s.archived ? 'archived' : SUB_H.subEnded(s) ? 'ended' : s.paused ? 'paused' : 'active'))} />
            </div>
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search name, external id, company…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any interval" value={intervalFilter} onChange={setIntervalFilter}
                options={SUB_D.billingIntervals.map((t) => ({ value: t.key, label: t.label }))} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={[
                  { value: 'Active',   label: 'Active' },
                  { value: 'Paused',   label: 'Paused' },
                  { value: 'Ended',    label: 'Ended' },
                  { value: 'Archived', label: 'Archived' },
                ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Subscriptions per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        )}
        primary={{ label: 'New subscription', icon: 'add', onClick: () => setAdding(true) }}
      />

      {adding && <AddSubscriptionModal onClose={() => setAdding(false)} onCreate={createSub} />}

      {rows.length === 0 ? (
        <EmptyState
          icon="subscriptions"
          title="No subscriptions yet"
          description="Keep a manual list of the recurring subscriptions you want to track — what each one is, who bills it, how much, and how often."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>New subscription</Button>}
        />
      ) : (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(s) => s.id}
            noun="subscriptions"
            revealKey={jumpId}
            renderItem={(s) => (
              <SubscriptionListItem row={s}
                defaultOpen={s.id === 'sub-netflix'} highlight={jumpId === s.id}
                onSave={onSave} onDelete={onDelete} />
            )}
            empty={(
              <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
                {hasFilters
                  ? <React.Fragment>No subscriptions match your filters. <button className="link-btn" onClick={clearFilters}>Clear filters</button></React.Fragment>
                  : 'No subscriptions to show.'}
              </div>
            )}
            trailing={(
              <AddRow title="New subscription" sub="Record what it is, who bills it, the price, and how often it recurs."
                onClick={() => setAdding(true)} />
            )}
          />
        </div>
      )}
    </div>
  );
};

Object.assign(window, { Subscriptions, SubscriptionListItem, AddSubscriptionModal });
