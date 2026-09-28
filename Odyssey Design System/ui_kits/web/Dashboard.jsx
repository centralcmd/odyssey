/* Dashboard — home view. Standard page header + a net-worth line chart +
   a summary stat band + the shared transactions table showing recent activity. */

/* ---- Net worth over time -------------------------------------------------
   The series comes from the net-worth-history endpoint (net-worth-data.js):
   every point is reconstructed from stored data as of that point's instant, so
   the line can fall, can go negative, and ends on the same figure the accounts
   rollup shows. Nothing is interpolated and no point is scaled by today's
   number.

   Two point kinds are disclosed rather than smoothed. An **understated** point
   (a contributing account had no exchange rate that period) is drawn hollow on
   a dashed segment and withholds the delta; a **revalued** point (an estimate
   took effect) is drawn with a tick and keeps it. Both get a note sentence —
   the marking never rests on colour alone.

   `emptyReason` is the only discriminator for an empty series, so the copy can
   name the cause instead of saying "no data". ------------------------------ */
const NW_RANGE_KEY = 'odyssey.dashboard.netWorthRange.v2';
const NW_RANGES = [
  { value: '6M', label: 'Last 6 months', months: 6 },
  { value: '1Y', label: 'Last 12 months', months: 12 },
  { value: 'All', label: 'All time', months: 0 },
  { value: 'Custom', label: 'Custom range' },
];
const NW_DEFAULT_RANGE = { preset: 'All', from: null, to: null };
const nwLoadRange = () => {
  try {
    const v = JSON.parse(localStorage.getItem(NW_RANGE_KEY) || 'null');
    if (v && NW_RANGES.some(r => r.value === v.preset)) return v;
  } catch (e) {}
  return NW_DEFAULT_RANGE;
};

/* Chart settings dialog — the date range the net-worth history covers. */
const NetWorthRangeDialog = ({ value, minDate, maxDate, onClose, onSave }) => {
  const { useState } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const [preset, setPreset] = useState(value.preset);
  // Either end may be left empty — it then runs as far as All time does.
  const [from, setFrom] = useState(value.from || null);
  const [to, setTo] = useState(value.to || null);
  const [tried, setTried] = useState(false);
  const custom = preset === 'Custom';
  const err = custom && from && to && (new Date(to) - new Date(from)) < 31 * 86400000 ? 'The range must span at least one month.' : null;
  const save = () => { setTried(true); if (err) return; onSave(custom ? { preset, from, to } : { preset, from: null, to: null }); };
  return (
    <Modal title="Net worth chart" subtitle="Choose the period the chart covers." icon="settings" onClose={onClose} requiredLegend={false}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="check" onClick={save}>Apply</Button>
        </React.Fragment>
      }>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
        {DS.RadioGroup ? (
          <DS.RadioGroup label="Date range" value={preset} onChange={(v) => setPreset(v)}
            options={NW_RANGES.map(r => ({ value: r.value, label: r.label }))} />
        ) : null}
        {custom ? (
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 16 }}>
            <DateField label="From" value={from} onChange={setFrom} min={minDate} max={to || maxDate}
              placeholder="Earliest" help="Empty starts at the earliest data" />
            <DateField label="To" value={to} onChange={setTo} min={from || minDate} max={maxDate} align="end"
              placeholder="Today" help="Empty runs to today" error={tried && err ? err : undefined} />
          </div>
        ) : null}
      </div>
    </Modal>
  );
};

const NetWorthChart = () => {
  const H = window.OdysseyHelpers;
  const d = window.OdysseyData;
  const DSNS = window.OdysseyDesignSystem_d5aa51 || {};
  /* The reader's range sticks across visits. */
  const [range, setRangePref] = React.useState(nwLoadRange);
  const [settingsOpen, setSettingsOpen] = React.useState(false);
  const saveRange = (v) => { setRangePref(v); setSettingsOpen(false); try { localStorage.setItem(NW_RANGE_KEY, JSON.stringify(v)); } catch (e) {} };
  const rg = NW_RANGES.find(r => r.value === range.preset) || NW_RANGES[2];
  const current = d.accounts.reduce((s, a) => s + a.balance, 0);
  const full = d.buildNetWorthHistory(current);
  const history = rg.value === 'Custom'
    ? d.buildNetWorthHistory(current, { from: range.from, to: range.to })
    : d.buildNetWorthHistory(current, { months: rg.months });
  const todayIso = new Date().toISOString().slice(0, 10);
  const minDate = full.from || todayIso;
  const currency = history.mainCurrencyCode;

  const DSLineChart = (window.OdysseyDesignSystem_d5aa51 || {}).LineChart;

  const kLabel = v => {
    const k = Math.round(v / 1000);
    return (v < 0 ? '−$' : '$') + Math.abs(k) + 'k';
  };
  const money = v => (v < 0 ? '−' + H.money(Math.abs(v)) : H.money(v));

  const pts = history.points;
  const emptyCopy = history.emptyReason
    ? (d.netWorthEmptyCopy[history.emptyReason] || '').replace('{currency}', currency)
    : null;

  const partial = pts.filter(p => p.unconvertedAccountCount > 0);
  const revalued = pts.filter(p => p.revaluedAccountCount > 0);
  const unconverted = history.unconvertedAccounts;

  // The sub-line is the caption plus one sentence per disclosure. Every
  // condition the markers show is also stated in text.
  const sub = (
    <>
      <span>{pts.length} monthly points · {pts[0] ? pts[0].label : ''} – {pts.length ? pts[pts.length - 1].label : ''} · {currency}</span>
      {partial.length > 0 && (
        <span className="odc-lc-note">
          {partial.length === 1 ? `${partial[0].label} is` : `${partial.length} periods are`} understated
          {unconverted.length === 1 ? ` — ${unconverted[0].name} (${unconverted[0].currencyCode}) had no exchange rate` : ' — an account had no exchange rate'}.
          {' '}The change since the first period is withheld while an endpoint is understated.
        </span>
      )}
      {revalued.length > 0 && (
        <span className="odc-lc-note">
          {revalued.length === 1 ? `${revalued[0].label} steps` : `${revalued.length} periods step`} because an
          account estimate took effect — a real movement, not a correction.
        </span>
      )}
    </>
  );

  if (!DSLineChart) {
    return (
      <Card className="chart-card">
        <div className="chart-head">
          <div>
            <div className="chart-ttl">Net worth</div>
            <div className="chart-sub">{pts.length} monthly points · {currency}</div>
          </div>
          <div className="chart-figure">
            <div className="chart-figure-num mono">{money(current)}</div>
          </div>
        </div>
        <div className="chart-sub" style={{ padding: '24px 0 8px' }}>
          Net-worth history could not be loaded.
        </div>
      </Card>
    );
  }

  return (
    <React.Fragment>
    {settingsOpen && (
      <NetWorthRangeDialog value={range} minDate={minDate} maxDate={todayIso}
        onClose={() => setSettingsOpen(false)} onSave={saveRange} />
    )}
    <DSLineChart
      title="Net worth"
      sub={history.emptyReason ? `Monthly · ${currency}` : sub}
      controlsEnd={<IconButton icon="settings" size="sm" label="Chart settings" onClick={() => setSettingsOpen(true)} />}
      series={pts.map(p => ({ label: p.label, value: p.netWorth, kind: p.kind }))}
      color="var(--chart-1)"
      format={money}
      axisFormat={kLabel}
      showFigure={false}
      legend
      deltaSuffix={pts.length ? `since ${pts[0].label}` : undefined}
      xTickEvery="auto"
      textEquivalent
      textEquivalentLabel="Month"
      emptyLabel={emptyCopy}
      ariaLabel={`Net worth over time, ${pts.length} monthly points from ${pts.length ? pts[0].label : ''} to ${pts.length ? pts[pts.length - 1].label : ''}`}
    />
    </React.Fragment>
  );
};

const Dashboard = ({ onNavigate }) => {
  const { useState, useMemo } = React;
  const d = window.OdysseyData;

  // Local copy so the shared table's edit / delete mutations have somewhere to land.
  const [txns, setTxns] = useState(d.transactions);
  const onSave = (id, patch) => setTxns(prev => prev.map(t => (t.id === id ? { ...t, ...patch } : t)));
  const onDelete = (id) => setTxns(prev => prev.filter(t => t.id !== id));

  // The eight most recent transactions, newest first.
  const recent = useMemo(
    () => [...txns].sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : 0)).slice(0, 8),
    [txns]
  );

  const hour = new Date().getHours();
  const partOfDay = hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
  const first = d.user.name.split(' ')[0];
  const total = d.accounts.reduce((s, a) => s + a.balance, 0);

  return (
    <div className="col gap-6">
      <PageHeader
        title={`Good ${partOfDay}, ${first}`}
        icon="space_dashboard"
        sub={`Net worth ${window.OdysseyHelpers.money(total)} across ${d.accounts.length} accounts`}
        card
      />

      <NetWorthChart />

      {window.AllocationDonuts ? <window.AllocationDonuts includeProperties /> : null}

      <Card>
        <CardHeader title="Recent transactions"
          action={<Button variant="text" onClick={() => onNavigate('transactions')}>View all</Button>} />
        <CardBody style={{ padding: 0 }}>
          <TxnTable
            txns={recent}
            onSave={onSave}
            onDelete={onDelete}
            empty={(
              <EmptyState icon="receipt_long" mutedIcon
                title="No transactions yet"
                desc="New activity will appear here as it lands in your accounts." />
            )}
          />
        </CardBody>
      </Card>
    </div>
  );
};

Object.assign(window, { Dashboard });
