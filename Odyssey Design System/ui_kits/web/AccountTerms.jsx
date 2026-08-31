/* AccountTerms — the "Terms" section inside an expanded account record
   (Accounts → account detail), beside Files & Transactions.

   Backs the AccountTerm feature (interest-rate & fee history): a time-versioned
   list of TERMS per account. The latest entry on/before a date is the value in
   force (implicit supersession — no EffectiveTo). This surface renders three
   things from that history, leading with the interest rate:

     1. HERO     — a step-line chart of the rate over time (rates hold flat and
                   jump on each change). Falls back to expected return; hidden
                   when the account has no chartable rate series.
     2. CURRENT  — the values in force for every kind (the GET …/terms/current
                   view). Three summary styles: tiles · row · chips.
     3. HISTORY  — the full GET …/terms list, grouped Rate then Fees, as a table
                   or a vertical timeline, each row editable / deletable.

   Props:
     account       — the account record (drives currency + eligibility)
     summaryStyle  — 'tiles' (default) | 'row' | 'chips'
     historyStyle  — 'table' (default) | 'timeline'
     headerActionRef — optional: receives the "New term" button to hoist into the
                       collapsible header (so the section owns its own create). */

const H = window.OdysseyHelpers;
const D = window.OdysseyData;

const trmToday = () => new Date().toISOString().slice(0, 10);
const trmKindInfo = (k) => H.termKindInfo(k);

/* ---- per-list resolvers (operate on a live array so edits reflect at once) ---- */
const trmCurrentFromList = (terms, asOf) => {
  const cutoff = asOf || trmToday();
  const byKind = {};
  for (const t of terms) {
    if (t.effectiveFrom > cutoff) continue;
    const cur = byKind[t.kind];
    if (!cur || t.effectiveFrom > cur.effectiveFrom) byKind[t.kind] = t;
  }
  return D.termKinds.map(k => byKind[k.key]).filter(Boolean);
};
const trmSeriesFromList = (terms, kind) => terms
  .filter(t => t.kind === kind)
  .map(t => ({ id: t.id, date: t.effectiveFrom, value: t.value, note: t.note }))
  .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));

/* Short month-year for axis + deltas: "Feb ’24" */
const trmMonY = (iso) => {
  const d = new Date(iso + 'T00:00:00');
  return d.toLocaleDateString('en-US', { month: 'short' }) + ' ’' + String(d.getFullYear()).slice(2);
};

/* =============================================================
   Step-line rate chart
   ============================================================= */
/* Clip an axis-aligned polyline to x ≤ xMax (or x ≥ xMin), interpolating the
   crossing so the solid/dashed split lands exactly on the "today" marker. */
const clipPts = (pts, bound, keepBelow) => {
  const out = [];
  for (let i = 0; i < pts.length; i++) {
    const p = pts[i];
    const inside = keepBelow ? p.x <= bound : p.x >= bound;
    const prev = pts[i - 1];
    if (i > 0) {
      const prevInside = keepBelow ? prev.x <= bound : prev.x >= bound;
      if (inside !== prevInside && prev.x !== p.x) {
        const t = (bound - prev.x) / (p.x - prev.x);
        out.push({ x: bound, y: prev.y + (p.y - prev.y) * t });
      } else if (inside !== prevInside) {
        out.push({ x: bound, y: inside ? prev.y : p.y });
      }
    }
    if (inside) out.push(p);
  }
  return out;
};
const ptsToPath = (pts) => pts.length ? 'M ' + pts.map(p => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' L ') : '';

const TermStepChart = ({ series, color }) => {
  const W = 680, Hh = 210;
  const padL = 48, padR = 18, padT = 16, padB = 28;
  const plotW = W - padL - padR;
  const plotH = Hh - padT - padB;
  const baseY = padT + plotH;

  const now = Date.now();
  const ms = (iso) => new Date(iso + 'T00:00:00').getTime();
  const t0 = ms(series[0].date);
  const tLast = ms(series[series.length - 1].date);
  const tMax = Math.max(now, tLast);
  const span = Math.max(tMax - t0, 1);
  const x = (t) => padL + ((t - t0) / span) * plotW;

  const vals = series.map(s => s.value);
  let lo = Math.min(...vals), hi = Math.max(...vals);
  if (lo === hi) { lo -= 0.005; hi += 0.005; }      // single value → centered band
  const padV = (hi - lo) * 0.35;
  lo = lo - padV; hi = hi + padV;   // no 0-clamp: a loan's rate range is negative
  const y = (v) => padT + plotH - ((v - lo) / (hi - lo)) * plotH;

  // Build the staircase: horizontal hold to each change, then vertical jump.
  const step = [];
  series.forEach((s, i) => {
    const sx = x(ms(s.date)), sy = y(s.value);
    if (i === 0) step.push({ x: sx, y: sy });
    else { step.push({ x: sx, y: step[step.length - 1].y }); step.push({ x: sx, y: sy }); }
  });
  const nowX = x(now);
  const endX = x(tMax);
  step.push({ x: endX, y: step[step.length - 1].y });   // hold current value to the edge

  const solid = clipPts(step, nowX, true);
  const dashed = clipPts(step, nowX, false);
  const areaPath = solid.length
    ? `${ptsToPath(solid)} L ${solid[solid.length - 1].x.toFixed(1)} ${baseY} L ${solid[0].x.toFixed(1)} ${baseY} Z`
    : '';

  // Y ticks (3 lines)
  const yticks = [hi, (hi + lo) / 2, lo];
  // X ticks: each change point (deduped if crowded) + Today
  const xticks = series.map(s => ({ t: ms(s.date), label: trmMonY(s.date) }));
  const fillId = `trmfill-${series[0].id}`;

  return (
    <svg className="trm-chart" viewBox={`0 0 ${W} ${Hh}`} role="img" aria-label="Rate history">
      <defs>
        <linearGradient id={fillId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.20" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>

      {/* gridlines + y labels */}
      {yticks.map((v, i) => (
        <g key={i}>
          <line className="grid" x1={padL} y1={y(v)} x2={W - padR} y2={y(v)} />
          <text className="axis" x={padL - 8} y={y(v) + 3} textAnchor="end">{(v < 0 ? '−' : '') + H.pctStr(Math.abs(v))}</text>
        </g>
      ))}

      {/* area + step line */}
      {areaPath && <path d={areaPath} fill={`url(#${fillId})`} />}
      <path className="step" d={ptsToPath(solid)} stroke={color} />
      {dashed.length > 1 && <path className="step future" d={ptsToPath(dashed)} stroke={color} />}

      {/* today marker */}
      <line className="nowline" x1={nowX} y1={padT - 4} x2={nowX} y2={baseY} />
      <text className="nowlabel" x={Math.min(nowX, W - padR)} y={padT - 7} textAnchor="end">Today</text>

      {/* change-point dots */}
      {series.map((s, i) => (
        <g key={s.id}>
          <circle className="dot-halo" cx={x(ms(s.date))} cy={y(s.value)} r="5" />
          <circle cx={x(ms(s.date))} cy={y(s.value)} r="3.4" fill={color} />
        </g>
      ))}

      {/* x labels (skip if two are within 42px to avoid overlap) */}
      {xticks.reduce((acc, tk) => {
        const px = x(tk.t);
        if (acc.last == null || px - acc.last > 44) { acc.nodes.push(
          <text key={tk.t} className="axis" x={px} y={Hh - 8} textAnchor="middle">{tk.label}</text>
        ); acc.last = px; }
        return acc;
      }, { nodes: [], last: null }).nodes}
    </svg>
  );
};

/* =============================================================
   Hero card — current rate + delta + the step chart
   ============================================================= */
const TermHero = ({ terms, account }) => {
  // Prefer interest rate; else expected return. Need ≥1 entry to show.
  const kind = trmSeriesFromList(terms, 'InterestRate').length ? 'InterestRate'
    : trmSeriesFromList(terms, 'ExpectedReturn').length ? 'ExpectedReturn' : null;
  if (!kind) return null;
  const info = trmKindInfo(kind);
  // Interest charged on a liability is a cost — negative + expense-colored.
  const cost = kind === 'InterestRate' && H.accountIsLiability(account);
  const color = cost ? 'var(--finance-expense)' : info.color;
  const fmt = (v) => (v < 0 ? '−' : '') + H.pctStr(Math.abs(v));
  const raw = trmSeriesFromList(terms, kind);
  const series = cost ? raw.map(s => ({ ...s, value: -Math.abs(s.value) })) : raw;
  const current = series[series.length - 1];
  const prev = series.length > 1 ? series[series.length - 2] : null;
  const diff = prev ? (current.value - prev.value) : 0;
  const dir = !prev ? 'flat' : diff > 0 ? 'up' : diff < 0 ? 'down' : 'flat';

  return (
    <div className="trm-hero">
      <div className="trm-hero-head">
        <span className="trm-kind-ic lg" style={{ background: info.soft, color: info.color }}>
          <MIcon name={info.icon} size={22} />
        </span>
        <div className="trm-hero-titles">
          <div className="trm-hero-kind">{info.label} <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400 }}>· history</span></div>
          <div className="trm-hero-sub">
            {series.length} change{series.length === 1 ? '' : 's'} since {trmMonY(series[0].date)} · in force since {H.dateLong(current.date)}
          </div>
        </div>
        <div className="trm-hero-figs">
          <div className="trm-hero-value" style={{ color }}>{fmt(current.value)}</div>
          {prev && (
            <span className="trm-delta flat">
              <MIcon name={dir === 'up' ? 'arrow_upward' : dir === 'down' ? 'arrow_downward' : 'remove'} size={14} />
              {H.pctStr(Math.abs(diff))} vs {trmMonY(prev.date)}
            </span>
          )}
        </div>
      </div>
      <div className="trm-chart-wrap">
        <TermStepChart series={series} color={color} />
      </div>
    </div>
  );
};

/* =============================================================
   Current terms summary — three styles
   ============================================================= */
const BillTag = ({ billingPeriod }) => {
  const b = H.billingInfo(billingPeriod);
  if (!b || b.key === 'OneTime') return null;
  return <span className="trm-bill">{b.chip || b.label}</span>;
};

const CurrentTermsSummary = ({ current, style, account }) => {
  if (!current.length) return null;

  if (style === 'row') {
    return (
      <div className="trm-summary row">
        {current.map(t => {
          const info = trmKindInfo(t.kind);
          return (
            <div className="trm-srow" key={t.kind}>
              <span className="trm-kind-ic sm" style={{ background: info.soft, color: info.color }}>
                <MIcon name={info.icon} size={16} />
              </span>
              <span className="trm-srow-kind">{info.label}</span>
              <span className="trm-srow-meta">
                <BillTag billingPeriod={t.billingPeriod} />
                <span className="trm-srow-date">since {trmMonY(t.effectiveFrom)}</span>
                <span className="trm-srow-value" style={{ color: H.costColor(t, account) || undefined }}>{H.fmtTermValueFor(t, account)}</span>
              </span>
            </div>
          );
        })}
      </div>
    );
  }

  if (style === 'chips') {
    return (
      <div className="trm-summary chips">
        {current.map(t => {
          const info = trmKindInfo(t.kind);
          return (
            <div className="trm-cchip" key={t.kind}>
              <span className="trm-kind-ic sm" style={{ width: 26, height: 26, background: info.soft, color: info.color }}>
                <MIcon name={info.icon} size={15} />
              </span>
              <span className="trm-cchip-kind">{info.label}</span>
              <span className="trm-cchip-value" style={{ color: H.costColor(t, account) || undefined }}>{H.fmtTermValueFor(t, account)}</span>
            </div>
          );
        })}
      </div>
    );
  }

  // tiles (default)
  return (
    <div className="trm-summary tiles">
      {current.map(t => {
        const info = trmKindInfo(t.kind);
        return (
          <div className="trm-tile" key={t.kind}>
            <div className="trm-tile-top">
              <span className="trm-kind-ic md" style={{ background: info.soft, color: info.color }}>
                <MIcon name={info.icon} size={18} />
              </span>
              <span className="trm-tile-kind">{info.label}</span>
            </div>
            <div className="trm-tile-value" style={{ color: H.costColor(t, account) || info.color }}>{H.fmtTermValueFor(t, account)}</div>
            <div className="trm-tile-foot">
              <span>since {trmMonY(t.effectiveFrom)}</span>
              <BillTag billingPeriod={t.billingPeriod} />
            </div>
          </div>
        );
      })}
    </div>
  );
};

/* =============================================================
   History — grouped Rate then Fees, as a table or a timeline
   ============================================================= */
const TermStatus = ({ t, currentIds }) => {
  if (t.effectiveFrom > trmToday()) return <span className="trm-superseded" style={{ color: 'oklch(0.80 0.13 85)', opacity: 1 }}>Scheduled</span>;
  if (currentIds.has(t.id)) return <span className="trm-inforce"><MIcon name="check_circle" size={12} />In force</span>;
  return <span className="trm-superseded">Superseded</span>;
};

const RowActions = ({ onEdit, onDelete }) => (
  <span className="trm-rowbtns">
    <button type="button" className="trm-iconbtn" aria-label="Edit term" onClick={onEdit}><MIcon name="edit" size={17} /></button>
    <button type="button" className="trm-iconbtn danger" aria-label="Delete term" onClick={onDelete}><MIcon name="delete" size={17} /></button>
  </span>
);

const TermTable = ({ rows, currentIds, onEdit, onDelete, account }) => (
  <table className="trm-tbl">
    <thead>
      <tr>
        <th scope="col">Term</th>
        <th scope="col">Effective from</th>
        <th scope="col" className="num">Value</th>
        <th scope="col">Status</th>
        <th scope="col" className="act" aria-label="Actions"></th>
      </tr>
    </thead>
    <tbody>
      {rows.map(t => {
        const info = trmKindInfo(t.kind);
        const isCurrent = currentIds.has(t.id);
        const b = H.billingInfo(t.billingPeriod);
        return (
          <tr key={t.id} className={isCurrent ? 'current' : ''}>
            <td>
              <div className="trm-row-kind">
                <span className="trm-kind-ic sm" style={{ background: info.soft, color: info.color }}>
                  <MIcon name={info.icon} size={15} />
                </span>
                <div>
                  <div className="trm-row-kind-name">{info.label}</div>
                  {t.note && <div className="trm-row-note">{t.note}</div>}
                </div>
              </div>
            </td>
            <td className="trm-cell-date">{H.dateLong(t.effectiveFrom)}</td>
            <td className="trm-cell-value" style={isCurrent ? { color: H.costColor(t, account) || info.color } : undefined}>
              {H.fmtTermValueFor(t, account)}{b && b.suffix ? <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400 }}> {b.suffix}</span> : null}
            </td>
            <td><TermStatus t={t} currentIds={currentIds} /></td>
            <td className="trm-cell-act"><RowActions onEdit={() => onEdit(t)} onDelete={() => onDelete(t)} /></td>
          </tr>
        );
      })}
    </tbody>
  </table>
);

const TermTimeline = ({ rows, currentIds, onEdit, onDelete, account }) => (
  <div className="trm-timeline">
    {rows.map(t => {
      const info = trmKindInfo(t.kind);
      const b = H.billingInfo(t.billingPeriod);
      return (
        <div className="trm-tl-item" key={t.id}>
          <div className="trm-tl-rail">
            <span className="trm-tl-node" style={{ background: info.color, color: info.color }} />
          </div>
          <div className="trm-tl-body">
            <div className="trm-tl-top">
              <span className="trm-tl-kind">{info.label}</span>
              <span className="trm-tl-date">{H.dateLong(t.effectiveFrom)}</span>
              <TermStatus t={t} currentIds={currentIds} />
            </div>
            {t.note && <div className="trm-tl-note">{t.note}</div>}
          </div>
          <div className="trm-tl-figs">
            <span className="trm-tl-value" style={currentIds.has(t.id) ? { color: H.costColor(t, account) || info.color } : undefined}>
              {H.fmtTermValueFor(t, account)}{b && b.suffix ? <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400, fontSize: 12 }}> {b.suffix}</span> : null}
            </span>
            <span className="trm-rowbtns trm-tl-actions">
              <button type="button" className="trm-iconbtn" aria-label="Edit term" onClick={() => onEdit(t)}><MIcon name="edit" size={16} /></button>
              <button type="button" className="trm-iconbtn danger" aria-label="Delete term" onClick={() => onDelete(t)}><MIcon name="delete" size={16} /></button>
            </span>
          </div>
        </div>
      );
    })}
  </div>
);

const TermHistory = ({ terms, currentIds, historyStyle, onEdit, onDelete, account }) => {
  const sorted = terms.slice().sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : 0));
  const rateRows = sorted.filter(t => trmKindInfo(t.kind).group === 'rate');
  const feeRows = sorted.filter(t => trmKindInfo(t.kind).group === 'fee');
  const View = historyStyle === 'timeline' ? TermTimeline : TermTable;

  const Group = ({ icon, label, rows }) => rows.length === 0 ? null : (
    <div>
      <div className="trm-group-label">
        <MIcon name={icon} size={16} style={{ color: 'var(--mud-palette-text-secondary)' }} />
        {label}
        <span className="trm-group-count">{rows.length}</span>
      </div>
      <View rows={rows} currentIds={currentIds} onEdit={onEdit} onDelete={onDelete} account={account} />
    </div>
  );

  return (
    <div className="trm-history">
      <Group icon="trending_up" label="Rate history" rows={rateRows} />
      <Group icon="sell" label="Fees" rows={feeRows} />
    </div>
  );
};

/* =============================================================
   AccountTerms — composes the section + owns create/edit/delete
   ============================================================= */
const AccountTerms = ({ account, summaryStyle = 'tiles', historyStyle = 'table', defaultOpen = false, chrome = true,
  bareAction = true, showCurrent = true, terms: termsProp, onNew, onEdit, onDelete: onDeleteProp }) => {
  const { useState, useMemo } = React;
  const controlled = termsProp != null;
  const [internalTerms, setInternalTerms] = useState(() => H.termsForAccount(account.id));
  const [modal, setModal] = useState(null); // { mode:'new'|'edit', term? } (uncontrolled only)
  const terms = controlled ? termsProp : internalTerms;

  const current = useMemo(() => trmCurrentFromList(terms), [terms]);
  const currentIds = useMemo(() => new Set(current.map(t => t.id)), [current]);

  // Uncontrolled (standalone specimen) keeps its own state + dialog; controlled
  // (live account row) delegates create/edit/delete + the dialog to the parent.
  const upsert = (dto, id) => {
    setInternalTerms(prev => id
      ? prev.map(t => t.id === id ? { ...t, ...dto } : t)
      : [{ id: `tm-new-${Date.now()}`, accountId: account.id, createdAtUtc: new Date().toISOString(), ...dto }, ...prev]);
    setModal(null);
  };
  const openNew = () => (controlled ? (onNew && onNew()) : setModal({ mode: 'new' }));
  const openEdit = (t) => (controlled ? (onEdit && onEdit(t)) : setModal({ mode: 'edit', term: t }));
  const remove = (t) => (controlled ? (onDeleteProp && onDeleteProp(t)) : setInternalTerms(prev => prev.filter(x => x.id !== t.id)));

  const empty = terms.length === 0;
  const newBtn = <Button variant="text" color="primary" icon="add" onClick={openNew}>New term</Button>;

  const body = (
    <div className="trm-section">
      {empty ? (
        <EmptyState
          icon="article"
          mutedIcon
          title="No rates or fees recorded yet"
          desc="Track this account’s interest rate over time and the prices of its services. Add the first term to start the history."
          action={<Button variant="filled" color="primary" icon="add" onClick={openNew}>New term</Button>}
        />
      ) : (
        <React.Fragment>
          <TermHero terms={terms} account={account} />

          {showCurrent ? (
            <div>
              <div className="trm-sub">
                <span className="trm-sub-label">Current terms</span>
                <span className="trm-sub-rule" />
                <span className="trm-sub-meta">in force · {H.dateLong(trmToday())}</span>
              </div>
              <CurrentTermsSummary current={current} style={summaryStyle} account={account} />
            </div>
          ) : null}

          <div>
            {/* Dropped only when the host supplied its own section divider
                (bareAction={false}) — otherwise it would repeat it. */}
            {(chrome || bareAction) ? (
              <div className="trm-sub">
                <span className="trm-sub-label">History</span>
                <span className="trm-sub-rule" />
                <span className="trm-sub-meta">{terms.length} {terms.length === 1 ? 'entry' : 'entries'}</span>
                {!chrome && bareAction && <span style={{ marginLeft: 4 }}>{newBtn}</span>}
              </div>
            ) : null}
            <TermHistory
              terms={terms}
              currentIds={currentIds}
              historyStyle={historyStyle}
              account={account}
              onEdit={openEdit}
              onDelete={remove}
            />
          </div>
        </React.Fragment>
      )}

    </div>
  );

  const dialog = (!controlled && modal) && (
    <AddTermModal
      account={account}
      term={modal.mode === 'edit' ? modal.term : null}
      existing={terms}
      onClose={() => setModal(null)}
      onSave={upsert}
    />
  );

  // Bare (no collapsible chrome) — used by the standalone specimen.
  if (!chrome) {
    return <React.Fragment>{body}{dialog}</React.Fragment>;
  }

  // Default: render as a collapsible, matching Files & Transactions.
  return (
    <React.Fragment>
      <Collapsible icon="§" title="Terms" count={terms.length} defaultOpen={defaultOpen} action={newBtn}>
        {body}
      </Collapsible>
      {dialog}
    </React.Fragment>
  );
};

Object.assign(window, {
  AccountTerms, TermStepChart, TermHero, CurrentTermsSummary, TermHistory,
  trmCurrentFromList, trmSeriesFromList, trmKindInfo, trmToday,
});
