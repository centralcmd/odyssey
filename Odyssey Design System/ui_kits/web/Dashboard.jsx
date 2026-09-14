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
const NetWorthChart = () => {
  const H = window.OdysseyHelpers;
  const d = window.OdysseyData;
  const current = d.accounts.reduce((s, a) => s + a.balance, 0);
  const history = d.buildNetWorthHistory(current);
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
    <DSLineChart
      title="Net worth"
      sub={history.emptyReason ? `Monthly · ${currency}` : sub}
      series={pts.map(p => ({ label: p.label, value: p.netWorth, kind: p.kind }))}
      color="var(--chart-1)"
      format={money}
      axisFormat={kLabel}
      showDelta
      deltaSuffix={pts.length ? `since ${pts[0].label}` : undefined}
      xTickEvery="auto"
      textEquivalent
      textEquivalentLabel="Month"
      emptyLabel={emptyCopy}
      ariaLabel={`Net worth over time, ${pts.length} monthly points from ${pts.length ? pts[0].label : ''} to ${pts.length ? pts[pts.length - 1].label : ''}`}
    />
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
