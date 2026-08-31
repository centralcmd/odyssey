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

/* ---------- Expanded DETAIL · DS RecordCard direction (tile-based) ----------
   The Subscriptions rollout of the DS pattern. The body carries the
   record's FULL field set — including the fields the collapsed header already
   shows. Repetition is deliberate: at tile scale each value arrives with its own
   label, so "Monthly · day 1" in the header and a labelled Billing interval
   tile read as two different things, and the body stays a complete record
   rather than a remainder of one. Foot captions carry the extra precision where
   there is any; they are not a toll a field has to pay to appear. Tiles never
   condition on each other: each renders on its own field, so no timestamp can
   disappear when a derived state takes precedence. Nothing is invented — spend
   rollups stay a non-goal, so there is no "paid to date". */
const SubRecordCard = ({ row, open, onToggle, highlight, onSave, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const { RecordCard, InfoTileGrid, InfoTile } = DS;
  const [showEdit, setShowEdit] = useState(false);
  const cardRef = useRef(null);

  const s = row;
  const info = SUB_H.subIntervalInfo(s.interval);
  const cp = SUB_H.subContact(s);
  const cpMeta = cp && (SUB_D.contactTypeByKey[cp.type] || {});
  const anchor = SUB_H.subBillingAnchor(s);
  const ended = SUB_H.subEnded(s);
  const nextBilling = SUB_H.subNextBilling(s);
  const paused = !!s.paused;
  const startFuture = !!s.startDate && s.startDate > SUB_H.subToday();
  const archived = !!s.archived;

  const saveEdit = (patch) => { onSave(s.id, patch); setShowEdit(false); };
  const togglePaused = () => onSave(s.id, { paused: s.paused ? null : new Date().toISOString() });
  const toggleArchive = () => onSave(s.id, { archived: s.archived ? null : new Date().toISOString() });
  const endNow = () => onSave(s.id, { endDate: SUB_H.subToday() });

  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    if (!open) onToggle(true);
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

  // Next billing is a DERIVED field, and its derivation is what makes it empty:
  // a paused, ended or archived subscription has no next billing date. The tile
  // renders on its own value only — it never consults the Status tile.
  const nextDue = (!paused && !ended && !archived) ? nextBilling : null;
  const nextFoot = nextDue
    ? `${SUB_H.subMoney(s.amount, s.currencyCode)} · ${SUB_H.subRelDays(SUB_H.subDaysUntil(nextDue))}`
    : null;

  if (!RecordCard || !InfoTileGrid || !InfoTile) return null;

  return (
    <div ref={cardRef}>
      <RecordCard
        icon={info.icon}
        accent={info.color}
        accentSoft={info.soft}
        name={s.name}
        chips={<SubscriptionStatusChip paused={s.paused} ended={ended} archived={s.archived} showActive size="sm" />}
        meta={[
          s.externalId ? <span className="sub-extid-inline"><MIcon name="tag" size={14} /><span className="mono">{s.externalId}</span></span> : null,
          <span className="sub-inst"><MIcon name={cp ? (cpMeta.icon || 'store') : 'store'} size={14} /><span>{cp ? cp.name : 'No company'}</span></span>,
          <span className="sub-cadence"><MIcon name={info.icon} size={14} /><span>{SUB_H.subIntervalLabel(s)}{anchor ? ` · ${anchor}` : ''}</span></span>,
        ]}
        figure={{ value: SUB_H.subMoney(s.amount, s.currencyCode), caption: subFigureCaption(s), tone: 'expense' }}
        dimmed={archived}
        highlight={highlight}
        open={open}
        onToggle={onToggle}
        actions={<ActionMenu items={[
          { icon: 'edit', label: 'Edit subscription', onClick: () => setShowEdit(true) },
          ...(ended ? [] : [{ icon: s.paused ? 'play_circle' : 'pause_circle', label: s.paused ? 'Resume' : 'Pause', onClick: togglePaused }]),
          ...(ended ? [] : [{ icon: 'event_busy', label: 'End subscription', onClick: endNow }]),
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(s.id); } },
          { divider: true },
          // Only an ended subscription can be archived — the lifecycle is ordered,
          // so the action is offered with its reason rather than hidden.
          (ended || s.archived)
            ? { icon: s.archived ? 'unarchive' : 'inventory_2', label: s.archived ? 'Restore' : 'Archive', onClick: toggleArchive }
            : { icon: 'inventory_2', label: 'Archive', disabled: true, note: 'End the subscription first.' },
          { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(s.id) },
        ]} />}
        details={(
          <InfoTileGrid>
            <InfoTile icon="subscriptions" label="Name" value={s.name} valueVariant="text" className="wrapvalue" />
            {s.externalId ? (
              <InfoTile icon="tag" label="External id" value={s.externalId} valueVariant="mono" foot="Provider reference" />
            ) : null}
            {/* The company is a REFERENCE to a contact — its own type icon and
                colour, so it reads as that contact record. */}
            {cp ? (
              <InfoTile icon={cpMeta.icon || 'store'} iconColor={cpMeta.color} iconSoft={cpMeta.soft}
                label="Company" valueVariant="text"
                value={cp.name} foot={cpMeta.label || 'Contact'} />
            ) : null}
            <InfoTile icon="payments" label="Price" value={SUB_H.subMoney(s.amount, s.currencyCode)}
              className="sub-price-tile" foot={subFigureCaption(s)} />
            <InfoTile icon={info.icon} label="Billing interval" value={SUB_H.subIntervalLabel(s)} valueVariant="text"
              foot={anchor || undefined} />
            <InfoTile icon="event_available" label="First billing date" value={SUB_H.dateLong(s.firstBillingDate)}
              valueVariant="sm" foot="cadence anchor" />
            {nextDue ? (
              <InfoTile icon="event_repeat" label="Next billing" value={SUB_H.dateLong(nextDue)}
                className="sub-status-tile income" valueVariant="sm" foot={nextFoot} />
            ) : null}
            {/* Tense follows the date: not yet reached reads "Starts on" in info
                blue; already reached reads "Started on" in neutral ink. */}
            <InfoTile icon="play_arrow" label={startFuture ? 'Starts on' : 'Started on'}
              className={startFuture ? 'tone-info' : undefined}
              value={SUB_H.dateLong(s.startDate)} valueVariant="sm"
              foot={startFuture ? 'upcoming' : null} />
            {/* Renders on its own field, never on what the Status tile happens to
               be showing. The empty case is kept deliberately: whether a
               recurring charge ever stops is material. */}
            <InfoTile icon="flag" label={s.endDate ? (ended ? 'Ended on' : 'Ends on') : 'End date'}
              className={ended ? 'tone-expense' : undefined}
              value={s.endDate ? SUB_H.dateLong(s.endDate) : 'No end date'}
              valueVariant={s.endDate ? 'sm' : 'text'}
              foot={s.endDate ? (ended ? 'no longer billing' : 'scheduled') : 'open-ended'} />
            {/* Status is a DERIVED summary of endDate / paused / archived. It
               carries the state and the date that state began — and the fields it
               derives from still render their own tiles below, so no timestamp can
               go missing when one state takes precedence over another. */}
            <InfoTile icon={archived ? 'inventory_2' : ended ? 'event_busy' : paused ? 'pause_circle' : 'autorenew'}
              label="Status" valueVariant="text"
              className={`sub-status-tile ${archived ? 'muted' : ended ? 'expense' : paused ? 'pending' : 'income'}`}
              value={archived ? 'Archived' : ended ? 'Ended' : paused ? 'Paused' : 'Active'}
              foot={archived ? `since ${SUB_H.dateTime(s.archived)}`
                : ended ? `since ${SUB_H.dateLong(s.endDate)}`
                : paused ? `since ${SUB_H.dateTime(s.paused)}`
                : 'billing on schedule'} />
            {paused ? (
              <InfoTile icon="pause_circle" label="Paused" value={SUB_H.dateTime(s.paused)} valueVariant="sm"
                className="sub-status-tile pending" foot="still listed, not billing" />
            ) : null}
            {archived ? (
              <InfoTile icon="inventory_2" label="Archived" value={SUB_H.dateTime(s.archived)} valueVariant="sm"
                className="sub-status-tile muted" foot="hidden from the default list" />
            ) : null}
          </InfoTileGrid>
        )}
        content={s.notes ? (
          <InfoTileGrid>
            <InfoTile icon="sticky_note_2" label="Notes" wide value={s.notes} />
          </InfoTileGrid>
        ) : null}
      />
      {showEdit && <AddSubscriptionModal subscription={s} onClose={() => setShowEdit(false)} onSave={saveEdit} />}
    </div>
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
  // The list owns ONE openId — opening a record closes its siblings.
  const [openId, setOpenId] = useState('sub-netflix');
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
              <SubRecordCard row={s}
                open={openId === s.id}
                onToggle={(o) => setOpenId(o ? s.id : null)}
                highlight={jumpId === s.id}
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

Object.assign(window, { Subscriptions, SubRecordCard, AddSubscriptionModal });
