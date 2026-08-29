/* Dashboard — home view. Standard page header + a net-worth line chart +
   a summary stat band + the shared transactions table showing recent activity. */

/* ---- Net-worth-over-time line chart (sum of all balances, since inception) ---- */
const NetWorthChart = () => {
  const H = window.OdysseyHelpers;
  const d = window.OdysseyData;
  const current = d.accounts.reduce((s, a) => s + a.balance, 0);
  const startYear = Math.min(...d.accounts.map(a => +a.opened.slice(0, 4)));
  const endYear = 2026;

  // Net-worth growth as a fraction of today's figure, anchored to land exactly on `current`.
  const curve = [0.017, 0.069, 0.137, 0.230, 0.338, 0.446, 0.546, 0.589, 0.748, 0.884, 1.0];
  const years = [];
  for (let y = startYear; y <= endYear; y++) years.push(y);
  const series = years.map((y, i) => ({
    y,
    v: Math.round(current * (curve[i] != null ? curve[i] : (i + 1) / years.length)),
  }));

  const x0 = 64, x1 = 968, yTop = 28, yBot = 212;
  const vals = series.map(p => p.v);
  const lo = Math.min(...vals), hi = Math.max(...vals);
  const yMin = Math.max(0, lo - (hi - lo) * 0.12), yMax = hi + (hi - lo) * 0.12;
  const sx = i => x0 + i * (x1 - x0) / (series.length - 1);
  const sy = v => yBot - (v - yMin) / (yMax - yMin) * (yBot - yTop);

  const linePts = series.map((p, i) => `${sx(i).toFixed(1)},${sy(p.v).toFixed(1)}`).join(' ');
  const areaPath = `M ${sx(0).toFixed(1)} ${yBot} `
    + series.map((p, i) => `L ${sx(i).toFixed(1)} ${sy(p.v).toFixed(1)}`).join(' ')
    + ` L ${sx(series.length - 1).toFixed(1)} ${yBot} Z`;

  const gridVals = [yMax, yMin + (yMax - yMin) * 2 / 3, yMin + (yMax - yMin) / 3, yMin];
  const kLabel = v => v >= 1000 ? `$${(v / 1000).toFixed(0)}k` : `$${v.toFixed(0)}`;
  const delta = current - series[0].v;

  // Prefer the design-system LineChart once the bundle exposes it (the kit's
  // DS-or-local-fallback convention). The local SVG below is the fallback.
  const DSLineChart = (window.OdysseyDesignSystem_d5aa51 || {}).LineChart;
  if (DSLineChart) {
    return (
      <DSLineChart
        title="Net worth"
        sub={`Since ${startYear} · USD`}
        series={series.map(p => ({ label: `'${String(p.y).slice(2)}`, value: p.v }))}
        color="var(--chart-1)"
        format={v => H.money(v)}
        axisFormat={kLabel}
        showDelta
        deltaSuffix="all-time"
        xTickEvery={2}
        ariaLabel="Net worth over time"
      />
    );
  }

  return (
    <Card className="chart-card">
      <div className="chart-head">
        <div>
          <div className="chart-ttl">Net worth</div>
          <div className="chart-sub">Since {startYear} · USD</div>
        </div>
        <div className="chart-figure">
          <div className="chart-figure-num mono">{H.money(current)}</div>
          <div className="chart-figure-delta mono income">+{H.money(delta)} all-time</div>
        </div>
      </div>
      <svg className="line-svg" viewBox="0 0 1000 252" preserveAspectRatio="xMidYMid meet">
        <g stroke="var(--chart-grid)" strokeWidth="1">
          {gridVals.map((v, i) => (
            <line key={i} x1={x0} y1={sy(v).toFixed(1)} x2={x1} y2={sy(v).toFixed(1)} />
          ))}
        </g>
        <g className="line-axis">
          {gridVals.map((v, i) => (
            <text key={i} x={x0 - 12} y={(sy(v) + 4).toFixed(1)} textAnchor="end">{kLabel(v)}</text>
          ))}
        </g>
        <g className="line-axis">
          {series.map((p, i) => (
            (i % 2 === 0 || i === series.length - 1) &&
            <text key={i} x={sx(i).toFixed(1)} y={yBot + 26} textAnchor="middle">{`'${String(p.y).slice(2)}`}</text>
          ))}
        </g>
        <defs>
          <linearGradient id="netFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--chart-1)" stopOpacity="0.26" />
            <stop offset="100%" stopColor="var(--chart-1)" stopOpacity="0" />
          </linearGradient>
        </defs>
        <path d={areaPath} fill="url(#netFill)" />
        <polyline points={linePts} fill="none" stroke="var(--chart-1)" strokeWidth="2.4"
          strokeLinejoin="round" strokeLinecap="round" />
        {series.map((p, i) => (
          <circle key={i} cx={sx(i).toFixed(1)} cy={sy(p.v).toFixed(1)}
            r={i === series.length - 1 ? 4 : 2.5} fill="var(--chart-1)" />
        ))}
      </svg>
    </Card>
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
