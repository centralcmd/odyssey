/* Tax Statements — /tax-statements
   ----------------------------------------------------------------------------
   A per-fiscal-year record of an official tax assessment, mirroring the backend
   "Yearly Tax Statement" feature. Sister screen to Budgets/Accounts: the same
   page-header + expandable-record scaffold (.acct-list / .acct-item), with a
   tax-specific expanded detail whose centrepiece is the RECONCILIATION REPORT —
   the API's TaxStatementReport contrasting three sources:

     • Declared — the figures stated on the official statement.
     • Derived  — Odyssey's own position: net worth from accounts (by AccountType),
                  advance tax paid + actual income summed from tagged transactions.
     • Variance — the differences the report exposes.

   Reconciliation is shown two ways (the `reportLayout` tweak): a three-column
   compare TABLE, or paired declared/derived TILES. The cross-year settlement —
   the spec's headline modelling principle (advance tax paid within the year vs.
   the settlement paid the following year) is surfaced in the table's Tax
   section. Derived figures degrade gracefully when
   account balances are unavailable (derived.available === false).

   FilesTable / MultiSelect come from the Accounts.jsx bridge; everything else
   from the DS bundle via Components.jsx. */

const TS_H = window.OdysseyHelpers;
const TS_D = window.OdysseyData;

// Tax statements carry the magenta "Tax" hue (matches the Tax file-type avatar).
const TAX_TONE = { bg: 'oklch(0.75 0.16 330 / 0.16)', fg: 'oklch(0.75 0.16 330)' };

const TS_CURRENCY_OPTIONS = TS_D.currencies
  .filter(c => !c.archived)
  .map(c => ({ value: c.code, label: `${c.code} · ${c.name}` }));

const TS_STATUS_META = {
  New:      { icon: 'fiber_new',     label: 'New' },
  Approved: { icon: 'check_circle',  label: 'Approved' },
  Flagged:  { icon: 'flag',          label: 'Flagged' },
};

/* ---- Statement problems (the design system's warning/error/info system) ----
   Mirrors the Accounts page's exchange-rate "problems": a data condition that
   needs the user's attention, surfaced in three places — the page-header signal
   rollup, a severity Chip on the record row, and a fix-it Alert in the expanded
   detail. Severity follows the convention: info = sea, warning = amber, error =
   coral. `fix.target` routes to the page that resolves it. */
const TS_SEV_RANK = { info: 0, warning: 1, error: 2 };

const taxProblems = (s) => {
  if (s.archived) return [];
  const out = [];
  if (!s.derived.available) {
    out.push({
      severity: 'info', chip: 'Balances pending',
      title: 'Account balances not synced',
      summary: 'Derived net worth is unavailable until account balances are computed for this period.',
      detail: 'Odyssey derives net worth from your account balances, which haven’t been computed for this period yet — so the net-worth reconciliation is pending. Advance tax and actual income still derive from tagged transactions.',
      fix: { label: 'Open accounts', target: 'accounts' },
    });
  }
  if (s.excludedTransactionCount > 0) {
    const n = s.excludedTransactionCount;
    const parts = Object.entries(s.excludedCurrencies || {}).map(([c, q]) => `${q} ${c}`).join(' · ');
    out.push({
      severity: 'warning', chip: 'Off-currency',
      title: 'Off-currency transactions excluded',
      summary: `${n} off-currency transaction${n === 1 ? '' : 's'} ${n === 1 ? 'is' : 'are'} left out of the derived sums.`,
      detail: `Derived advance tax and actual income only count ${s.baseCurrency} transactions. ${n} off-currency transaction${n === 1 ? '' : 's'} (${parts}) ${n === 1 ? 'is' : 'are'} excluded — add today’s exchange rates to fold them in.`,
      fix: { label: 'Set exchange rates', target: 'exchange-rates' },
    });
  }
  return out;
};

const taxTopSeverity = (problems) =>
  problems.reduce((sev, p) => (TS_SEV_RANK[p.severity] > TS_SEV_RANK[sev] ? p.severity : sev), 'info');

/* ---- Year-over-year trend chart for the header Overview. Plots one declared
   figure across fiscal years (oldest → newest), reusing the kit's .chart-card /
   .line-svg styling from the Dashboard net-worth chart. `accessor` pulls the
   value from a statement's declared block; `color` is a categorical chart var. */
const TaxTrendChart = ({ title, statements, accessor, color, cur, cumulative, sub }) => {
  const series = statements
    .map(s => ({ y: s.fiscalYear, v: accessor(s) }))
    .filter(p => p.v != null)
    .sort((a, b) => a.y - b.y);

  if (series.length === 0) {
    return (
      <Card className="chart-card">
        <div className="chart-head"><div><div className="chart-ttl">{title}</div></div></div>
        <div className="empty-line" style={{ padding: '28px 4px' }}>No declared figures yet.</div>
      </Card>
    );
  }

  const kLabel = v => {
    const sym = (TS_D.currencyByCode[cur] || {}).symbol || cur;
    if (Math.abs(v) >= 1000000) return `${sym} ${(v / 1000000).toFixed(1)}M`;
    if (Math.abs(v) >= 1000) return `${sym} ${(v / 1000).toFixed(0)}k`;
    return `${sym} ${v}`;
  };

  // Prefer the design-system LineChart once the bundle exposes it (the kit's
  // DS-or-local-fallback convention). The local SVG below is the fallback.
  const DSLineChart = (window.OdysseyDesignSystem_d5aa51 || {}).LineChart;
  if (DSLineChart) {
    return (
      <DSLineChart
        title={title}
        sub={`${sub || 'By fiscal year'} · ${cur}`}
        series={series.map(p => ({ label: `'${String(p.y).slice(2)}`, value: p.v }))}
        color={color}
        cumulative={cumulative}
        format={v => TS_H.taxMoney(v, cur)}
        axisFormat={kLabel}
        showDelta
        deltaSuffix={`vs ${series[0].y}`}
        ariaLabel={`${title} by fiscal year`}
      />
    );
  }

  // ---- Local fallback (until _ds_bundle.js ships LineChart) ----
  if (cumulative) {
    let acc = 0;
    series.forEach(p => { acc += p.v; p.v = acc; });
  }

  const latest = series[series.length - 1];
  const first = series[0];
  const delta = latest.v - first.v;
  const single = series.length === 1;

  const x0 = 64, x1 = 968, yTop = 28, yBot = 212;
  const vals = series.map(p => p.v);
  const lo = Math.min(...vals), hi = Math.max(...vals);
  const span = hi - lo || Math.abs(hi) || 1;
  const yMin = Math.max(0, lo - span * 0.18), yMax = hi + span * 0.18;
  const sx = i => (single ? (x0 + x1) / 2 : x0 + i * (x1 - x0) / (series.length - 1));
  const sy = v => yBot - (v - yMin) / (yMax - yMin || 1) * (yBot - yTop);

  const linePts = series.map((p, i) => `${sx(i).toFixed(1)},${sy(p.v).toFixed(1)}`).join(' ');
  const areaPath = `M ${sx(0).toFixed(1)} ${yBot} `
    + series.map((p, i) => `L ${sx(i).toFixed(1)} ${sy(p.v).toFixed(1)}`).join(' ')
    + ` L ${sx(series.length - 1).toFixed(1)} ${yBot} Z`;
  const gridVals = [yMax, yMin + (yMax - yMin) * 2 / 3, yMin + (yMax - yMin) / 3, yMin];
  const fillId = `taxFill-${title.replace(/\s+/g, '')}`;

  return (
    <Card className="chart-card">
      <div className="chart-head">
        <div>
          <div className="chart-ttl">{title}</div>
          <div className="chart-sub">{sub || 'By fiscal year'} · {cur}</div>
        </div>
        <div className="chart-figure">
          <div className="chart-figure-num mono">{TS_H.taxMoney(latest.v, cur)}</div>
          {!single && (
            <div className={`chart-figure-delta mono ${delta >= 0 ? 'income' : 'expense'}`}>
              {delta >= 0 ? '+' : '−'}{TS_H.taxMoney(Math.abs(delta), cur)} vs {first.y}
            </div>
          )}
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
            <text key={i} x={sx(i).toFixed(1)} y={yBot + 26} textAnchor="middle">{`'${String(p.y).slice(2)}`}</text>
          ))}
        </g>
        <defs>
          <linearGradient id={fillId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={color} stopOpacity="0.26" />
            <stop offset="100%" stopColor={color} stopOpacity="0" />
          </linearGradient>
        </defs>
        {!single && <path d={areaPath} fill={`url(#${fillId})`} />}
        {!single && (
          <polyline points={linePts} fill="none" stroke={color} strokeWidth="2.4"
            strokeLinejoin="round" strokeLinecap="round" />
        )}
        {series.map((p, i) => (
          <circle key={i} cx={sx(i).toFixed(1)} cy={sy(p.v).toFixed(1)}
            r={i === series.length - 1 ? 4 : 2.5} fill={color} />
        ))}
      </svg>
    </Card>
  );
};

/* The header Overview: net worth + assessed tax, year over year. */
const TaxOverview = ({ statements }) => {
  const live = statements.filter(s => !s.archived);
  const cur = (live[0] || statements[0] || {}).baseCurrency || 'NOK';
  return (
    <div className="tx-overview">
      <TaxTrendChart title="Net worth" statements={live}
        accessor={s => s.declared.netWorth} color="var(--chart-1)" cur={cur} />
      <TaxTrendChart title="Assessed tax" statements={live}
        accessor={s => s.declared.assessedTax} color="var(--chart-2)" cur={cur} />
      <TaxTrendChart title="Accumulated" statements={live}
        accessor={s => s.declared.assessedTax} color="var(--chart-4)" cur={cur}
        cumulative sub="Assessed tax · all years" />
    </div>
  );
};

/* ---- A reconciliation variance cell (table + tiles share the semantics):
   zero = reconciled (income-green ✓), non-zero = a discrepancy (amber),
   null = unavailable (disabled). ---- */
const VarValue = ({ value, cur, cls = 'tx-var' }) => {
  // Prefer the DS Delta (variance mode) once the bundle exposes it.
  if (Delta) return <Delta mode="variance" value={value} format={(n) => TS_H.taxMoney(n, cur)} className={cls === 'tx-tile-var' ? 'odc-delta-lg' : ''} />;
  if (value == null) return <span className={`${cls} na`}>Unavailable</span>;
  const zero = Math.round(value) === 0;
  return (
    <span className={`${cls} ${zero ? 'zero' : 'diff'}`}>
      {zero && <MIcon name="check_circle" size={15} />}
      {zero ? TS_H.taxMoney(0, cur) : TS_H.taxSignedMoney(value, cur)}
    </span>
  );
};

const perRowVariance = (d, v) => (d == null || v == null ? null : d - v);

/* ====================== Reconciliation — TABLE ====================== */
const ReconTable = ({ s, recon }) => {
  const cur = s.baseCurrency;
  const d = s.declared, v = s.derived;
  const av = v.available;
  // The statement doesn't state advance tax paid directly, but it's implied by
  // the assessment: advance paid = assessed tax − settlement balance. We show
  // that beside Odyssey's tag-derived figure so the two can be reconciled.
  const declaredAdvancePaid = (d.assessedTax == null || d.settlementAmount == null) ? null : d.assessedTax - d.settlementAmount;
  const cell = (n) => (n == null ? <span className="tx-num na">—</span> : <span className="tx-num">{TS_H.taxMoney(n, cur)}</span>);
  const dcell = (n) => (!av || n == null ? <span className="tx-num na">Unavailable</span> : <span className="tx-num">{TS_H.taxMoney(n, cur)}</span>);

  return (
    <div className="tx-recon">
      <div className="tx-recon-head">
        <span>Figure</span>
        <span className="ta-r">Declared</span>
        <span className="ta-r tx-h-derived">Odyssey-derived</span>
        <span className="ta-r">Variance</span>
      </div>

      <div className="tx-recon-group"><MIcon name="account_balance_wallet" size={15} />Net worth</div>
      <div className="tx-recon-row">
        <div className="tx-figure">Total assets</div>
        {cell(d.totalAssets)}{dcell(v.totalAssets)}
        <span className="ta-r"><VarValue value={av ? perRowVariance(d.totalAssets, v.totalAssets) : null} cur={cur} /></span>
      </div>
      <div className="tx-recon-row">
        <div className="tx-figure">Total liabilities</div>
        {cell(d.totalLiabilities)}{dcell(v.totalLiabilities)}
        <span className="ta-r"><VarValue value={av ? perRowVariance(d.totalLiabilities, v.totalLiabilities) : null} cur={cur} /></span>
      </div>
      <div className="tx-recon-row total">
        <div className="tx-figure">Net worth</div>
        {cell(d.netWorth)}{dcell(v.netWorth)}
        <span className="ta-r"><VarValue value={recon.netWorthVariance} cur={cur} /></span>
      </div>

      <div className="tx-recon-group"><MIcon name="payments" size={15} />Income</div>
      <div className="tx-recon-row total">
        <div className="tx-figure">Total income</div>
        {cell(d.totalIncome)}
        <span className="tx-num">{TS_H.taxMoney(v.actualIncome, cur)}</span>
        <span className="ta-r"><VarValue value={recon.incomeVariance} cur={cur} /></span>
      </div>

      <div className="tx-recon-group"><MIcon name="gavel" size={15} />Tax</div>
      <div className="tx-recon-row">
        <div className="tx-figure">Assessed tax</div>
        {cell(d.assessedTax)}
        <span className="tx-num na">—</span>
        <span className="ta-r"><span className="tx-var na">—</span></span>
      </div>
      <div className="tx-recon-row">
        <div className="tx-figure">Advance tax paid</div>
        {cell(declaredAdvancePaid)}
        <span className="tx-num">{TS_H.taxMoney(v.paidTax, cur)}</span>
        <span className="ta-r"><VarValue value={recon.settlementVariance} cur={cur} /></span>
      </div>
      <div className="tx-recon-row total">
        <div className="tx-figure">Settlement</div>
        <span className={`tx-num ${d.settlementAmount == null ? 'na' : ''}`}>{d.settlementAmount == null ? '—' : TS_H.taxSignedMoney(d.settlementAmount, cur)}</span>
        <span className={`tx-num ${recon.outstandingTax == null ? 'na' : ''}`}>{recon.outstandingTax == null ? '—' : TS_H.taxSignedMoney(recon.outstandingTax, cur)}</span>
        <span className="ta-r"><VarValue value={recon.settlementVariance} cur={cur} /></span>
      </div>
    </div>
  );
};

/* ====================== Reconciliation — TILES ====================== */
const ReconTile = ({ label, icon, declared, derived, derivedNa, variance, cur, total, signed }) => {
  const fmt = (n) => (signed ? TS_H.taxSignedMoney(n, cur) : TS_H.taxMoney(n, cur));
  return (
  <div className={`tx-tile ${total ? 'total' : ''}`}>
    <div className="tx-tile-lab"><MIcon name={icon} size={15} />{label}</div>
    <div className="tx-tile-pair">
      <div className="tx-tile-cell">
        <span className="k">Declared</span>
        <span className={`v ${declared == null ? 'na' : ''}`}>{declared == null ? '—' : fmt(declared)}</span>
      </div>
      <div className="tx-tile-cell">
        <span className="k derived">Derived</span>
        <span className={`v ${derivedNa ? 'na' : ''}`}>{derivedNa ? 'Unavailable' : fmt(derived)}</span>
      </div>
    </div>
    <div className="tx-tile-foot">
      <span className="lbl">Variance</span>
      <VarValue value={variance} cur={cur} cls="tx-tile-var" />
    </div>
  </div>
  );
};

const ReconTiles = ({ s, recon }) => {
  const cur = s.baseCurrency;
  const d = s.derived, v = s.derived;
  const dd = s.declared;
  const av = s.derived.available;
  const declaredAdvancePaid = (dd.assessedTax == null || dd.settlementAmount == null) ? null : dd.assessedTax - dd.settlementAmount;
  return (
    <div className="tx-tiles">
      <ReconTile label="Net worth" icon="account_balance_wallet" total
        declared={dd.netWorth} derived={s.derived.netWorth} derivedNa={!av} variance={recon.netWorthVariance} cur={cur} />
      <ReconTile label="Total income" icon="payments" total
        declared={dd.totalIncome} derived={s.derived.actualIncome} derivedNa={false} variance={recon.incomeVariance} cur={cur} />
      <ReconTile label="Advance tax paid" icon="event_repeat" total
        declared={declaredAdvancePaid} derived={s.derived.paidTax} derivedNa={false} variance={recon.settlementVariance} cur={cur} />
      <ReconTile label="Settlement" icon="paid" total signed
        declared={dd.settlementAmount} derived={recon.outstandingTax} derivedNa={false} variance={recon.settlementVariance} cur={cur} />
      <ReconTile label="Total assets" icon="savings"
        declared={dd.totalAssets} derived={s.derived.totalAssets} derivedNa={!av} variance={av ? perRowVariance(dd.totalAssets, s.derived.totalAssets) : null} cur={cur} />
      <ReconTile label="Total liabilities" icon="credit_card"
        declared={dd.totalLiabilities} derived={s.derived.totalLiabilities} derivedNa={!av} variance={av ? perRowVariance(dd.totalLiabilities, s.derived.totalLiabilities) : null} cur={cur} />
    </div>
  );
};

/* ====================== Derivation tags (two roles) ====================== */
const RoleWell = ({ icon, title, sum, cap, tags, emptyHint }) => (
  <InfoTile icon={icon} label={title} value={sum} className="tx-role"
    foot={<React.Fragment>
      <span className="tx-role-cap">{cap}</span>
      <span className="tx-role-tags">
        {tags.length
          ? <span className="tx-chips">{tags.map(t => <Chip key={t} tone="tag">{t}</Chip>)}</span>
          : <span className="tx-role-empty">{emptyHint}</span>}
      </span>
    </React.Fragment>} />
);

const DerivationTags = ({ s, editing, onChangeTax, onChangeIncome }) => {
  const cur = s.baseCurrency;
  const taxOpts = TS_D.taxTagCatalog.filter(t => t.role === 'TaxPayment').map(t => ({ value: t.name, label: t.name }));
  const incOpts = TS_D.taxTagCatalog.filter(t => t.role === 'Income').map(t => ({ value: t.name, label: t.name }));

  if (editing) {
    return (
      <div className="edit-grid">
        <div className="field">
          <div className="label">Tax-payment tags</div>
          <MultiSelect allLabel="Select tags…" value={s.taxTags} onChange={onChangeTax} options={taxOpts} />
          <div className="helper">Sum into derived advance tax paid (within the year).</div>
        </div>
        <div className="field">
          <div className="label">Income tags</div>
          <MultiSelect allLabel="Select tags…" value={s.incomeTags} onChange={onChangeIncome} options={incOpts} />
          <div className="helper">Sum into derived actual income.</div>
        </div>
      </div>
    );
  }

  return (
    <React.Fragment>
      <div className="tx-roles">
        <RoleWell icon="request_quote" title="Tax-payment tags"
          sum={TS_H.taxMoney(s.derived.paidTax, cur)} cap="Advance tax paid"
          tags={s.taxTags}
          emptyHint="No tax-payment tags selected — derived advance tax is kr 0." />
        <RoleWell icon="payments" title="Income tags"
          sum={TS_H.taxMoney(s.derived.actualIncome, cur)} cap="Actual income"
          tags={s.incomeTags}
          emptyHint="No income tags selected — derived income is kr 0." />
      </div>
    </React.Fragment>
  );
};

/* ====================== Expanded detail ====================== */
const TaxDetail = ({ s, layout, focusDocs, onNavigate, setStatement }) => {
  const recon = TS_H.taxReconciliation(s);
  const cur = s.baseCurrency;
  const status = TS_H.taxStatementStatus(s);
  const problems = taxProblems(s);

  const removeFile = (f) => setStatement(prev => ({ ...prev, files: prev.files.filter(x => x.id !== f.id) }));

  const statusTone = status.tone === 'income' ? 'tone-income' : status.tone === 'expense' ? 'tone-expense'
    : status.tone === 'pending' || status.tone === 'warning' ? 'tone-pending'
    : status.tone === 'info' ? 'tone-info' : 'tone-muted';
  const settled = !!s.declared.settledAtUtc;
  return (
    <React.Fragment>
      {/* PROBLEMS — the design system's fix-it alert (same as the Accounts page) */}
      {problems.map((p, i) => (
        ProblemAlert
          ? <ProblemAlert key={i} severity={p.severity} title={p.title} detail={p.detail}
              actionLabel={p.fix && p.fix.label} onAction={() => p.fix && onNavigate && onNavigate(p.fix.target)} />
          : (
            <div key={i} className={`alert ${p.severity} acct-problem`} role={p.severity === 'error' ? 'alert' : 'status'}>
              <div className="acct-problem-head">
                <SeverityIcon severity={p.severity} size={20} className="alert-icon" />
                <div className="acct-problem-title">{p.title}</div>
                {p.fix && (
                  <button type="button" className="alert-cta" onClick={() => onNavigate && onNavigate(p.fix.target)}>
                    {p.fix.label}<MIcon name="arrow_forward" size={16} />
                  </button>
                )}
              </div>
              <p className="acct-problem-detail">{p.detail}</p>
            </div>
          )
      ))}
      {/* review-status comment (Flagged / commented) — an alert, so it sits with
          the problems above the details rather than between them. */}
      {s.statusComment && (
        <Alert severity={s.status === 'Flagged' ? 'warning' : 'info'}>
          <b>{status.label}.</b> {s.statusComment}
        </Alert>
      )}

      {/* DETAILS — the statement's full field set. Dates that have not happened
          yet show no tile; the settlement is kept because its absence is the fact. */}
      <InfoTileGrid>
        <InfoTile icon="request_quote" label="Name" value={s.name} valueVariant="text" className="wrapvalue" />
        <InfoTile icon="event_note" label="Fiscal year" value={String(s.fiscalYear)} foot={s.country || null} />
        <InfoTile icon="payments" label="Base currency" value={cur} foot="reporting currency" />
        <InfoTile icon="date_range" label="Period" className="wrapvalue" value={`${TS_H.dateLong(s.startDate)} → ${TS_H.dateLong(s.endDate)}`} valueVariant="sm" />
        <InfoTile icon={status.icon || 'rule'} label="Status" value={status.label} valueVariant="text"
          className={statusTone}
          foot={[s.statusChangedAt ? TS_H.dateLong(s.statusChangedAt) : null, s.statusComment ? 'see note above' : null].filter(Boolean).join(' · ') || null} />
        {s.filedAtUtc ? <InfoTile icon="outbox" label="Filed to authority" value={TS_H.dateLong(s.filedAtUtc)} valueVariant="sm" /> : null}
        {s.taxOfficeApprovedAtUtc ? <InfoTile icon="verified" label="Authority approved" value={TS_H.dateLong(s.taxOfficeApprovedAtUtc)} valueVariant="sm" /> : null}
        <InfoTile icon={settled ? 'task_alt' : 'schedule'} label="Settlement paid"
          className={settled ? 'tone-income' : 'tone-pending'}
          value={settled ? TS_H.dateLong(s.declared.settledAtUtc) : 'Not yet'}
          valueVariant={settled ? 'sm' : 'text'} foot={settled ? null : 'outstanding'} />
      </InfoTileGrid>

      {s.notes ? (
        <InfoTileGrid><InfoTile icon="sticky_note_2" label="Notes" value={s.notes} wide /></InfoTileGrid>
      ) : null}

      {/* DERIVATION TAGS */}
      <div className="tax-section">
        <SectionDivider label="Derivation tags" meta={`${s.taxTags.length + s.incomeTags.length} tag${(s.taxTags.length + s.incomeTags.length) === 1 ? '' : 's'}`} />
        <DerivationTags s={s} editing={false} />
      </div>

      {/* RECONCILIATION REPORT */}
      <div className="tax-section">
        <SectionDivider label="Reconciliation" meta={`declared vs. recorded · ${cur}`} />
        {layout === 'tiles' ? <ReconTiles s={s} recon={recon} /> : <ReconTable s={s} recon={recon} />}
      </div>

      {/* DOCUMENTS — last section. */}
      <div className="tax-section">
        <SectionDivider label="Statement documents" meta={`${s.files.length} file${s.files.length === 1 ? '' : 's'}`} />
        <div className="tax-tbl-frame">
          {s.files.length === 0 ? (
            <div className="empty-line">No documents attached yet — upload the tax return / assessment PDFs.</div>
          ) : (
            <InlinePager items={s.files}>
              {(pageRows) => <FilesTable files={pageRows} onDelete={removeFile} />}
            </InlinePager>
          )}
        </div>
      </div>
    </React.Fragment>
  );
};

/* ====================== Upload-files modal ======================
   Uses the DS FileUpload (drag/drop + browse + per-file rename and kind
   picker), exactly like the Accounts page — but scoped to a tax statement via a
   tax `guessKind`, so there is no account selector. */
const TaxUploadModal = ({ onClose, onUpload }) => {
  const { useState } = React;
  const [files, setFiles] = useState([]);
  const [error, setError] = useState(null);

  const guessKind = (name) => {
    const isPdf = /\.pdf$/i.test(name);
    const looksAssessment = /assess|notice|vedtak|skatteoppgj/i.test(name);
    return looksAssessment ? 'TaxAssessment' : (isPdf ? 'TaxReturn' : 'SupportingDocument');
  };

  const submit = () => {
    if (!files.length) { setError('Add at least one file.'); return; }
    if (files.some(f => !f.name.trim())) { setError('Every file needs a name.'); return; }
    const uploaded = afmToday();
    onUpload(files.map((f, i) => ({
      id: `tsf-${Date.now()}-${i}`,
      name: f.name.trim(),
      kind: f.kind,
      size: afmFmtSize(f.sizeBytes),
      uploaded,
    })));
  };

  return (
    <Modal
      title="Upload files"
      subtitle="Attach the tax return, assessment, or supporting documents to this statement."
      icon="cloud_upload"
      className="afm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="upload_file" onClick={submit}>
            {files.length > 1 ? `Upload ${files.length} files` : 'Upload'}
          </Button>
        </React.Fragment>
      }>
      <FileUpload files={files} onChange={(next) => { setFiles(next); if (error) setError(null); }} error={error}
        kinds={TS_D.taxStatementFileTypes} guessKind={guessKind} />
    </Modal>
  );
};

/* ====================== One statement list item ====================== */
const TaxListItem = ({ st, layout, open: openProp, onToggle, highlight, onNavigate, onDelete }) => {
  const { useState, useRef, useEffect } = React;
  const [s, setS] = useState(st);
  // Open state lives in the list — opening a statement closes its siblings.
  const open = !!openProp;
  const setOpen = (next) => onToggle(typeof next === 'function' ? next(open) : next);
  const [showEdit, setShowEdit] = useState(false);
  const [focusDocs, setFocusDocs] = useState(false);
  const [uploading, setUploading] = useState(false);
  const cardRef = useRef(null);

  const recon = TS_H.taxReconciliation(s);
  const status = TS_H.taxStatementStatus(s);
  const cur = s.baseCurrency;
  const dimmed = !!s.archived;
  const problems = taxProblems(s);
  const topSev = taxTopSeverity(problems);

  // Header figure: declared settlement, or estimated outstanding for an
  // in-progress year that hasn't declared a settlement yet.
  const settle = s.declared.settlementAmount != null ? s.declared.settlementAmount : recon.outstandingTax;
  const word = s.declared.settlementAmount != null
    ? (settle > 0 ? 'additional tax to pay' : settle < 0 ? 'refund' : 'settled')
    : (settle == null ? 'awaiting assessment'
      : settle > 0 ? 'outstanding (est.)' : settle < 0 ? 'refund (est.)' : 'settled (est.)');

  const setStatus = (status, comment) => setS(prev => ({
    ...prev, status, statusComment: comment !== undefined ? comment : prev.statusComment,
    statusChangedAt: new Date().toISOString(),
  }));
  const toggleArchive = () => setS(prev => ({ ...prev, archived: prev.archived ? null : new Date().toISOString() }));
  const handleUpload = (newFiles) => {
    setS(prev => ({ ...prev, files: [...prev.files, ...newFiles] }));
    setUploading(false);
    setOpen(true);
    setFocusDocs(true);
  };
  const saveEdit = (patch) => { setS(prev => ({ ...prev, ...patch })); setShowEdit(false); };

  // When the header rollup jumps to this statement, open it, scroll it into
  // view (via the nearest scroll container) and flash a severity ring.
  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    if (!open) setOpen(true);
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
      } else {
        const top = el.getBoundingClientRect().top + window.scrollY - 24;
        window.scrollTo({ top, behavior: 'smooth' });
      }
    });
  }, [highlight]);

  return (
    <div ref={cardRef}>
      <RecordCard
        icon="request_quote"
        accent={TAX_TONE.fg}
        accentSoft={TAX_TONE.bg}
        name={s.name}
        chips={(
          <React.Fragment>
            <Chip tone={status.tone} dot={status.dot}>{status.label}</Chip>
            {problems.length > 0 && (
              <Chip tone={topSev} className="problem">
                <SeverityIcon severity={topSev} size={13} />{problems.length === 1 ? problems[0].chip : 'Attention'}
              </Chip>
            )}
          </React.Fragment>
        )}
        meta={[
          <span><MIcon name="date_range" size={14} /><span>{TS_H.dateLong(s.startDate)} → {TS_H.dateLong(s.endDate)}</span></span>,
          <span className="mono"><MIcon name="payments" size={14} /><span>{cur}</span></span>,
        ]}
        counts={[
          { icon: 'local_offer', value: s.taxTags.length + s.incomeTags.length, label: 'Derivation tags' },
          { icon: 'description', value: s.files.length, label: 'Documents' },
        ]}
        figure={{
          value: settle == null ? '—' : TS_H.taxMoney(Math.abs(settle), cur),
          caption: word,
          tone: settle == null || settle === 0 ? undefined : settle > 0 ? 'expense' : 'income',
        }}
        dimmed={dimmed}
        highlight={highlight}
        open={open}
        onToggle={setOpen}
        actions={<ActionMenu items={[
            { icon: 'edit', label: 'Edit statement', onClick: () => setShowEdit(true) },
            { icon: 'upload_file', label: 'Upload file', onClick: () => setUploading(true) },
            { icon: 'check_circle', label: 'Mark approved', onClick: () => setStatus('Approved', null) },
            { icon: 'flag', label: 'Flag for review', onClick: () => setStatus('Flagged', s.statusComment || 'Flagged for review.') },
            { icon: 'fiber_new', label: 'Mark as new', onClick: () => setStatus('New', null) },
            { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(s.id); } },
            { divider: true },
            { icon: s.archived ? 'unarchive' : 'archive', label: s.archived ? 'Unarchive' : 'Archive', onClick: toggleArchive },
            { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(s.id) },
        ]} />}
      >
        <TaxDetail s={s} layout={layout} focusDocs={focusDocs} onNavigate={onNavigate} setStatement={setS} />
      </RecordCard>
      {showEdit && <AddTaxStatementModal statement={s} onClose={() => setShowEdit(false)} onSave={saveEdit} />}
      {uploading && <TaxUploadModal onClose={() => setUploading(false)} onUpload={handleUpload} />}
    </div>
  );
};

/* ====================== Page ====================== */
const TaxStatements = ({ tweaks = {}, onNavigate }) => {
  const { useState } = React;
  // One card open at a time — the list owns it.
  const [openId, setOpenId] = useState('ts-2024');
  const layout = tweaks.reportLayout || 'table';

  const [q, setQ] = useState('');
  const [statusFilter, setStatusFilter] = useState([]);
  const [showAdd, setShowAdd] = useState(false);
  const [statements, setStatements] = useState(TS_D.taxStatements);
  const [jumpId, setJumpId] = useState(null);
  // Shared sort (§6.10): Fiscal year, most recent first; toolbar is the sole
  // sort surface. Status sorts by the declared TransactionStatus-style order.
  const [sort, setSort] = useState({ key: 'fiscalYear', dir: 'desc' });
  // Card-list server paging: "Load N at a time" batch size, fed to InfiniteList.
  const [batch, setBatch] = useState(25);
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const TS_STATUS_ORDER = ['New', 'Approved', 'Flagged', 'Archived'];
  // §6.10 curated fields — one list feeds the SortSelect AND the ordering.
  const sortFields = [
    { key: 'fiscalYear', label: 'Fiscal year', type: 'number', sortValue: (s) => s.fiscalYear },
    { key: 'name',       label: 'Name',        type: 'text',   sortValue: (s) => (s.name || '').toLowerCase() },
    { key: 'status',     label: 'Status',      type: 'status', sortValue: (s) => { const i = TS_STATUS_ORDER.indexOf(TS_H.taxStatementStatus(s).label); return i < 0 ? TS_STATUS_ORDER.length : i; } },
  ];

  // Clear the one-shot highlight a moment after a jump so the ring can re-fire.
  const jumpTo = (id) => {
    setJumpId(null);
    requestAnimationFrame(() => setJumpId(id));
    setTimeout(() => setJumpId(curr => (curr === id ? null : curr)), 2200);
  };

  const createStatement = (draft) => {
    setStatements(prev => [draft, ...prev]);
    setShowAdd(false);
  };
  const deleteStatement = (id) => setStatements(prev => prev.filter(s => s.id !== id));

  const rows = statements.filter(s => {
    const label = TS_H.taxStatementStatus(s).label;
    if (statusFilter.length && !statusFilter.includes(label)) return false;
    if (q) {
      const needle = q.toLowerCase();
      const hay = `${s.name} ${s.fiscalYear} ${s.baseCurrency} ${s.notes || ''}`.toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });

  const active = statements.filter(s => !s.archived);
  const latest = active.find(s => s.derived.available !== undefined) || active[0];
  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(rows, sortFields, sort, (s) => s.id) : rows;

  // Roll up statements that have problems into the header signal toggle. Highest
  // severity wins the tint; the count is the number of affected statements.
  const flagged = active.map(s => ({ s, p: taxProblems(s) })).filter(x => x.p.length);
  const topSeverity = flagged.reduce(
    (sev, x) => (TS_SEV_RANK[taxTopSeverity(x.p)] > TS_SEV_RANK[sev] ? taxTopSeverity(x.p) : sev), 'info');
  const signal = flagged.length ? {
    severity: topSeverity,
    count: flagged.length,
    label: 'Attention',
    region: (
      <div className="signal-panel">
        {flagged.map(({ s, p }) => {
          const sev = taxTopSeverity(p);
          return (
            <div key={s.id} className={`alert ${sev} compact signal-row`}
              role="button" tabIndex={0}
              onClick={() => jumpTo(s.id)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jumpTo(s.id); } }}>
              <SeverityIcon severity={sev} size={18} className="alert-icon" />
              <div className="alert-body"><strong>{s.name}.</strong> {p[0].summary}{p.length > 1 ? ` (+${p.length - 1} more)` : ''}</div>
              <button className="alert-fix" onClick={(e) => { e.stopPropagation(); jumpTo(s.id); }}>View →</button>
            </div>
          );
        })}
      </div>
    ),
  } : undefined;

  return (
    <div className="col gap-6">
      <PageHeader
        title="Tax Statements"
        icon="request_quote"
        sub={`${active.length} year${active.length === 1 ? '' : 's'} on file${latest ? ` · latest assessment ${latest.fiscalYear}` : ''}`}
        signal={signal}
        overview={<TaxOverview statements={statements} />}
        overviewDefaultOpen
        searchDefaultOpen
        search={
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search year, name, currency, notes…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 180 }}>
              <MultiSelect allLabel="Any status" value={statusFilter} onChange={setStatusFilter}
                options={[
                  { value: 'New', label: 'New' },
                  { value: 'Approved', label: 'Approved' },
                  { value: 'Flagged', label: 'Flagged' },
                  { value: 'Archived', label: 'Archived' },
                ]} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Statements per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        }
        primary={{ label: 'New tax statement', icon: 'add', onClick: () => setShowAdd(true) }}
      />

      {statements.length === 0 ? (
        <EmptyState
          icon="request_quote"
          title="No tax statements yet"
          description="Add a fiscal year to record its declared figures and reconcile them against your Odyssey accounts and tagged transactions."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setShowAdd(true)}>New tax statement</Button>}
        />
      ) : (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(s) => s.id}
            noun="tax statements"
            revealKey={jumpId}
            renderItem={(s) => (
              <TaxListItem st={s} layout={layout}
                open={openId === s.id}
                onToggle={(o) => setOpenId(o ? s.id : null)}
                highlight={jumpId === s.id}
                onNavigate={onNavigate} onDelete={deleteStatement} />
            )}
            empty={(
              <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
                No tax statements match your filters.
              </div>
            )}
            trailing={(
              <AddRow
                title="New tax statement"
                sub="Record a fiscal year's declared figures, then reconcile against your accounts and tags."
                onClick={() => setShowAdd(true)}
              />
            )}
          />
        </div>
      )}

      {showAdd && <AddTaxStatementModal onClose={() => setShowAdd(false)} onCreate={createStatement} />}
    </div>
  );
};

Object.assign(window, { TaxStatements });
