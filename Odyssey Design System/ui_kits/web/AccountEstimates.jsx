/* AccountEstimates — the "Estimates" section inside an expanded account record
   (Accounts → account detail), positioned ABOVE the Terms section.

   Backs the AccountEstimate feature: a time-versioned list of a single ESTIMATED
   VALUE per account — a money amount in the account's own currency, effective from
   a date. The latest entry on/before a date is the value in force (implicit
   supersession — no EffectiveTo, step function, identical to AccountTerm). Unlike a
   term it has NO kind / unit / billing dimension, so one value leads. The current
   estimate REPLACES the transaction balance in net worth when present (§9 "replace"
   policy); this surface shows the estimate as the headline value and the transaction
   balance as a quiet secondary.

   Three stacked zones:
     1. HERO     — current estimated value + change vs the prior estimate + a value
                   chart over time (step or smooth, tweakable). Estimates hold flat
                   between appraisals and extend to a dashed Today marker.
     2. CURRENT  — the value tile (headline) + the muted transaction-balance tile.
     3. HISTORY  — the full GET …/estimates list, newest first, as a table or a
                   vertical timeline; each row editable / deletable in place.

   Props:
     account        — the account record (drives currency + glyph + recommendation)
     estimates      — controlled list (live account row); else internal seed state
     txns           — the account's transactions, for the secondary balance (else
                      read from the seed)
     historyStyle   — 'table' (default) | 'timeline'
     chartMode      — 'step' (default) | 'smooth'
     emptyStyle     — 'standard' (default) | 'guided'
     chrome         — wrap in a Collapsible like Files/Terms (default true)
     onNew/onEdit/onDelete — delegate mutations to the parent (controlled mode) */

const EST_H = window.OdysseyHelpers;
const EST_D = window.OdysseyData;

const estToday = () => new Date().toISOString().slice(0, 10);
const estTypeInfo = (a) => EST_D.accountTypeById[a.type]
  || { label: a.type, icon: 'savings', color: 'var(--ink-300)', soft: 'rgba(199,208,224,0.12)' };

/* Short month-year: "Apr ’24" */
const estMonY = (iso) => {
  const d = new Date(iso + 'T00:00:00');
  return d.toLocaleDateString('en-US', { month: 'short' }) + ' ’' + String(d.getFullYear()).slice(2);
};

/* ---- per-list resolvers (operate on a live array so edits reflect at once) ---- */
const estCurrentFromList = (estimates, asOf) => {
  const cutoff = asOf || estToday();
  let cur = null;
  for (const e of estimates) {
    if (e.effectiveFrom > cutoff) continue;
    if (!cur || e.effectiveFrom > cur.effectiveFrom) cur = e;
  }
  return cur;
};
const estSeriesFromList = (estimates) => estimates
  .map(e => ({ id: e.id, date: e.effectiveFrom, value: e.value, note: e.note }))
  .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));

/* =============================================================
   Value chart — step OR smooth, with a dashed hold to Today
   ============================================================= */
const estClipPts = (pts, bound, keepBelow) => {
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
const estPath = (pts) => pts.length ? 'M ' + pts.map(p => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' L ') : '';

/* Catmull-Rom sampled into a dense polyline (clamped to the plot box). */
const estSmoothPoly = (P, loY, hiY) => {
  if (P.length <= 1) return P.slice();
  const out = [];
  const clampY = (y) => Math.max(loY, Math.min(hiY, y));
  for (let i = 0; i < P.length - 1; i++) {
    const p0 = P[i - 1] || P[i], p1 = P[i], p2 = P[i + 1], p3 = P[i + 2] || P[i + 1];
    const steps = 20;
    for (let j = (i === 0 ? 0 : 1); j <= steps; j++) {
      const t = j / steps, t2 = t * t, t3 = t2 * t;
      const x = 0.5 * ((2 * p1.x) + (-p0.x + p2.x) * t + (2 * p0.x - 5 * p1.x + 4 * p2.x - p3.x) * t2 + (-p0.x + 3 * p1.x - 3 * p2.x + p3.x) * t3);
      const y = 0.5 * ((2 * p1.y) + (-p0.y + p2.y) * t + (2 * p0.y - 5 * p1.y + 4 * p2.y - p3.y) * t2 + (-p0.y + 3 * p1.y - 3 * p2.y + p3.y) * t3);
      out.push({ x, y: clampY(y) });
    }
  }
  return out;
};

const EstimateValueChart = ({ series, mode, color }) => {
  const W = 680, Hh = 210;
  const padL = 54, padR = 18, padT = 16, padB = 28;
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
  if (lo === hi) { lo *= 0.97; hi *= 1.03; }
  const padV = (hi - lo) * 0.28 || hi * 0.05 || 1;
  lo = Math.max(0, lo - padV); hi = hi + padV;
  const y = (v) => padT + plotH - ((v - lo) / (hi - lo)) * plotH;

  const P = series.map(s => ({ x: x(ms(s.date)), y: y(s.value) }));
  const nowX = x(now), endX = x(tMax);

  // Build the base polyline for the chosen mode, then hold flat to the edge.
  let poly;
  if (mode === 'smooth') {
    poly = estSmoothPoly(P, padT, baseY);
    if (endX > P[P.length - 1].x + 0.5) poly.push({ x: endX, y: P[P.length - 1].y });
  } else {
    poly = [];
    P.forEach((p, i) => {
      if (i === 0) poly.push({ x: p.x, y: p.y });
      else { poly.push({ x: p.x, y: poly[poly.length - 1].y }); poly.push({ x: p.x, y: p.y }); }
    });
    poly.push({ x: endX, y: poly[poly.length - 1].y });
  }

  const solid = estClipPts(poly, nowX, true);
  const dashed = estClipPts(poly, nowX, false);
  const areaPath = solid.length
    ? `${estPath(solid)} L ${solid[solid.length - 1].x.toFixed(1)} ${baseY} L ${solid[0].x.toFixed(1)} ${baseY} Z`
    : '';

  const yticks = [hi, (hi + lo) / 2, lo];
  const xticks = series.map(s => ({ t: ms(s.date), label: estMonY(s.date) }));
  const fillId = `estfill-${series[0].id}`;

  return (
    <svg className="est-chart" viewBox={`0 0 ${W} ${Hh}`} role="img" aria-label="Estimated value over time">
      <defs>
        <linearGradient id={fillId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.20" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>

      {yticks.map((v, i) => (
        <g key={i}>
          <line className="grid" x1={padL} y1={y(v)} x2={W - padR} y2={y(v)} />
          <text className="axis" x={padL - 8} y={y(v) + 3} textAnchor="end">{EST_H.moneyCompact(v)}</text>
        </g>
      ))}

      {areaPath && <path d={areaPath} fill={`url(#${fillId})`} />}
      <path className="line" d={estPath(solid)} stroke={color} />
      {dashed.length > 1 && <path className="line future" d={estPath(dashed)} stroke={color} />}

      <line className="nowline" x1={nowX} y1={padT - 4} x2={nowX} y2={baseY} />
      <text className="nowlabel" x={Math.min(nowX, W - padR)} y={padT - 7} textAnchor="end">Today</text>

      {series.map((s) => (
        <g key={s.id}>
          <circle className="dot-halo" cx={x(ms(s.date))} cy={y(s.value)} r="5" />
          <circle cx={x(ms(s.date))} cy={y(s.value)} r="3.4" fill={color} />
        </g>
      ))}

      {xticks.reduce((acc, tk) => {
        const px = x(tk.t);
        if (acc.last == null || px - acc.last > 46) {
          acc.nodes.push(<text key={tk.t} className="axis" x={px} y={Hh - 8} textAnchor="middle">{tk.label}</text>);
          acc.last = px;
        }
        return acc;
      }, { nodes: [], last: null }).nodes}
    </svg>
  );
};

/* =============================================================
   Hero — current value + change + the value chart
   ============================================================= */
const EstimateHero = ({ estimates, account, chartMode }) => {
  const series = estSeriesFromList(estimates);
  if (!series.length) return null;
  const ti = estTypeInfo(account);
  const color = 'var(--finance-income)';
  const current = series[series.length - 1];
  const prev = series.length > 1 ? series[series.length - 2] : null;
  const diff = prev ? current.value - prev.value : 0;
  const dir = !prev ? 'flat' : diff > 0 ? 'up' : diff < 0 ? 'down' : 'flat';

  return (
    <div className="est-hero">
      <div className="est-hero-head">
        <span className="est-glyph lg" style={{ background: ti.soft, color: ti.color }}>
          <MIcon name={ti.icon} size={22} />
        </span>
        <div className="est-hero-titles">
          <div className="est-hero-kind">Estimated value <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400 }}>· history</span></div>
          <div className="est-hero-sub">
            {series.length} estimate{series.length === 1 ? '' : 's'} since {estMonY(series[0].date)} · in force since {EST_H.dateLong(current.date)}
          </div>
        </div>
        <div className="est-hero-figs">
          <div className="est-hero-value">{EST_H.money(current.value, account.currency)}</div>
          {prev && (
            <span className={`est-delta ${dir}`}>
              <MIcon name={dir === 'up' ? 'arrow_upward' : dir === 'down' ? 'arrow_downward' : 'remove'} size={14} />
              {EST_H.money(Math.abs(diff), account.currency).replace('$ ', '$ ')} vs {estMonY(prev.date)}
            </span>
          )}
        </div>
      </div>
      <div className="est-chart-wrap">
        <EstimateValueChart series={series} mode={chartMode} color={color} />
      </div>
    </div>
  );
};

/* =============================================================
   Current — value tile + quiet transaction-balance secondary
   ============================================================= */
const CurrentValueBlock = ({ current, account, txns }) => {
  if (!current) return null;
  const txnSum = (txns || []).reduce((s, t) => s + t.amount, 0);
  const txnCount = (txns || []).length;
  return (
    <div className="est-now">
      <div className="est-now-tile est-now-primary">
        <span className="est-now-label">Estimated value</span>
        <span className="est-now-value">{EST_H.money(current.value, account.currency)}</span>
        <div className="est-now-foot">
          <span>in force since {EST_H.dateLong(current.effectiveFrom)}</span>
          <span className="est-networth"><MIcon name="check" size={13} />In net worth</span>
        </div>
      </div>
      <div className="est-now-tile secondary est-now-secondary">
        <span className="est-now-label">Transaction balance</span>
        <span className="est-now-value">{EST_H.money(txnSum, account.currency)}</span>
        <div className="est-now-foot">
          <span>{txnCount === 0 ? 'No transactions' : `${txnCount} transaction${txnCount === 1 ? '' : 's'} · secondary`}</span>
        </div>
      </div>
    </div>
  );
};

/* =============================================================
   History — newest first, table OR timeline
   ============================================================= */
const EstStatus = ({ e, currentId }) => {
  if (e.effectiveFrom > estToday()) return <span className="est-scheduled">Scheduled</span>;
  if (e.id === currentId) return <span className="est-inforce"><MIcon name="check_circle" size={12} />In force</span>;
  return <span className="est-superseded">Superseded</span>;
};

const EstChange = ({ change, account }) => {
  if (change == null) return <span className="est-change flat">— first</span>;
  const dir = change > 0 ? 'up' : change < 0 ? 'down' : 'flat';
  return (
    <span className={`est-change ${dir}`}>
      <MIcon name={dir === 'up' ? 'arrow_upward' : dir === 'down' ? 'arrow_downward' : 'remove'} size={14} />
      {EST_H.money(Math.abs(change), account.currency)}
    </span>
  );
};

const EstRowActions = ({ onEdit, onDelete }) => (
  <span className="est-rowbtns">
    <button type="button" className="est-iconbtn" aria-label="Edit estimate" onClick={onEdit}><MIcon name="edit" size={17} /></button>
    <button type="button" className="est-iconbtn danger" aria-label="Delete estimate" onClick={onDelete}><MIcon name="delete" size={17} /></button>
  </span>
);

/* Map id → change vs the chronologically prior estimate. */
const estChangeById = (estimates) => {
  const asc = estimates.slice().sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? -1 : a.effectiveFrom > b.effectiveFrom ? 1 : 0));
  const map = {};
  asc.forEach((e, i) => { map[e.id] = i === 0 ? null : e.value - asc[i - 1].value; });
  return map;
};

const EstimateTable = ({ rows, currentId, changes, onEdit, onDelete, account }) => (
  <table className="est-tbl">
    <thead>
      <tr>
        <th scope="col">Effective from</th>
        <th scope="col" className="num">Value</th>
        <th scope="col" className="num">Change</th>
        <th scope="col">Status</th>
        <th scope="col" className="act" aria-label="Actions"></th>
      </tr>
    </thead>
    <tbody>
      {rows.map(e => (
        <tr key={e.id} className={e.id === currentId ? 'current' : ''}>
          <td>
            <div className="est-cell-date">{EST_H.dateLong(e.effectiveFrom)}</div>
            {e.note && <div className="est-row-note">{e.note}</div>}
          </td>
          <td className="est-cell-value">{EST_H.money(e.value, account.currency)}</td>
          <td className="num"><EstChange change={changes[e.id]} account={account} /></td>
          <td><EstStatus e={e} currentId={currentId} /></td>
          <td className="est-cell-act"><EstRowActions onEdit={() => onEdit(e)} onDelete={() => onDelete(e)} /></td>
        </tr>
      ))}
    </tbody>
  </table>
);

const EstimateTimeline = ({ rows, currentId, changes, onEdit, onDelete, account }) => (
  <div className="est-timeline">
    {rows.map(e => (
      <div className={`est-tl-item ${e.id === currentId ? 'current' : ''}`} key={e.id}>
        <div className="est-tl-rail">
          <span className="est-tl-node" style={{ color: e.id === currentId ? 'var(--finance-income)' : 'var(--mud-palette-text-secondary)' }} />
        </div>
        <div className="est-tl-body">
          <div className="est-tl-top">
            <span className="est-tl-date">{EST_H.dateLong(e.effectiveFrom)}</span>
            <EstStatus e={e} currentId={currentId} />
          </div>
          {e.note && <div className="est-tl-note">{e.note}</div>}
        </div>
        <div className="est-tl-figs">
          <span className="est-tl-value">{EST_H.money(e.value, account.currency)}</span>
          <EstChange change={changes[e.id]} account={account} />
          <span className="est-rowbtns est-tl-actions">
            <button type="button" className="est-iconbtn" aria-label="Edit estimate" onClick={() => onEdit(e)}><MIcon name="edit" size={16} /></button>
            <button type="button" className="est-iconbtn danger" aria-label="Delete estimate" onClick={() => onDelete(e)}><MIcon name="delete" size={16} /></button>
          </span>
        </div>
      </div>
    ))}
  </div>
);

/* =============================================================
   Empty state — two treatments
   ============================================================= */
const EstimateEmpty = ({ account, txns, emptyStyle, onNew }) => {
  if (emptyStyle === 'guided') {
    const ti = estTypeInfo(account);
    const txnSum = (txns || []).reduce((s, t) => s + t.amount, 0);
    const recommended = EST_H.isEstimateRecommended(account.type);
    return (
      <div className="est-empty-guided">
        <div className="est-eg-head">
          <span className="est-glyph lg" style={{ background: ti.soft, color: ti.color }}>
            <MIcon name={ti.icon} size={22} />
          </span>
          <div>
            <div className="est-eg-title">Track this asset’s worth</div>
            <div className="est-eg-desc">
              An estimate records what {account.name} is worth, even when no transactions capture it. The current
              estimate becomes the account’s value in your net worth.
            </div>
          </div>
        </div>
        <div className="est-eg-balrow">
          <span className="lab">Transaction balance</span>
          <span className="val">{EST_H.money(txnSum, account.currency)}</span>
        </div>
        <div className="est-eg-actions">
          <Button variant="filled" color="primary" icon="add" onClick={onNew}>New estimate</Button>
          {recommended
            ? <span className="est-eg-rec"><MIcon name="recommend" size={15} />Recommended for {window.ACCOUNT_TYPE_LABEL ? (window.ACCOUNT_TYPE_LABEL[account.type] || account.type) : account.type} accounts</span>
            : <span className="est-eg-rec"><MIcon name="info" size={15} />Estimates suit asset accounts, but you can add one here</span>}
        </div>
      </div>
    );
  }
  return (
    <EmptyState
      icon="query_stats"
      mutedIcon
      title="No value estimate yet"
      desc="Record what this account is worth over time. The current estimate stands in for the account’s value in your net worth."
      action={<Button variant="filled" color="primary" icon="add" onClick={onNew}>New estimate</Button>}
    />
  );
};

/* =============================================================
   AccountEstimates — composes the section + owns create/edit/delete
   ============================================================= */
const AccountEstimates = ({ account, historyStyle = 'table', chartMode = 'step', emptyStyle = 'standard',
  defaultOpen = false, chrome = true, estimates: estProp, txns: txnsProp, onNew, onEdit, onDelete: onDeleteProp }) => {
  const { useState, useMemo } = React;
  const controlled = estProp != null;
  const [internal, setInternal] = useState(() => EST_H.estimatesForAccount(account.id));
  const [modal, setModal] = useState(null);
  const estimates = controlled ? estProp : internal;
  const txns = txnsProp != null ? txnsProp : EST_H.txnsForAccount(account.id);

  const current = useMemo(() => estCurrentFromList(estimates), [estimates]);
  const currentId = current ? current.id : null;
  const changes = useMemo(() => estChangeById(estimates), [estimates]);
  const rows = useMemo(() => estimates.slice().sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : 0)), [estimates]);

  const upsert = (dto, id) => {
    setInternal(prev => id
      ? prev.map(e => e.id === id ? { ...e, ...dto } : e)
      : [{ id: `es-new-${Date.now()}`, accountId: account.id, createdAtUtc: new Date().toISOString(), ...dto }, ...prev]);
    setModal(null);
  };
  const openNew = () => (controlled ? (onNew && onNew()) : setModal({ mode: 'new' }));
  const openEdit = (e) => (controlled ? (onEdit && onEdit(e)) : setModal({ mode: 'edit', estimate: e }));
  const remove = (e) => (controlled ? (onDeleteProp && onDeleteProp(e)) : setInternal(prev => prev.filter(x => x.id !== e.id)));

  const empty = estimates.length === 0;
  const newBtn = <Button variant="text" color="primary" icon="add" onClick={openNew}>New estimate</Button>;
  const View = historyStyle === 'timeline' ? EstimateTimeline : EstimateTable;

  const body = (
    <div className="est-section">
      {empty ? (
        <EstimateEmpty account={account} txns={txns} emptyStyle={emptyStyle} onNew={openNew} />
      ) : (
        <React.Fragment>
          <EstimateHero estimates={estimates} account={account} chartMode={chartMode} />

          <div>
            <div className="est-sub">
              <span className="est-sub-label">Current value</span>
              <span className="est-sub-rule" />
              <span className="est-sub-meta">in force · {EST_H.dateLong(estToday())}</span>
            </div>
            <CurrentValueBlock current={current} account={account} txns={txns} />
          </div>

          <div>
            <div className="est-sub">
              <span className="est-sub-label">History</span>
              <span className="est-sub-rule" />
              <span className="est-sub-meta">{estimates.length} {estimates.length === 1 ? 'estimate' : 'estimates'}</span>
              {!chrome && <span style={{ marginLeft: 4 }}>{newBtn}</span>}
            </div>
            <div className="est-history">
              <View rows={rows} currentId={currentId} changes={changes} account={account} onEdit={openEdit} onDelete={remove} />
            </div>
          </div>
        </React.Fragment>
      )}
    </div>
  );

  const dialog = (!controlled && modal) && (
    <AddEstimateModal
      account={account}
      estimate={modal.mode === 'edit' ? modal.estimate : null}
      existing={estimates}
      onClose={() => setModal(null)}
      onSave={upsert}
    />
  );

  if (!chrome) {
    return <React.Fragment>{body}{dialog}</React.Fragment>;
  }

  return (
    <React.Fragment>
      <Collapsible icon="monitor" title="Estimates" count={estimates.length} defaultOpen={defaultOpen} action={newBtn}>
        {body}
      </Collapsible>
      {dialog}
    </React.Fragment>
  );
};

Object.assign(window, {
  AccountEstimates, EstimateValueChart, EstimateHero, CurrentValueBlock,
  estCurrentFromList, estSeriesFromList, estChangeById,
});
