/* Shared term visuals — the step chart, current-terms summary, history table /
   timeline, name / cadence / direction tags and the series resolvers.

   This file used to also compose the account "Terms" section. That section is
   gone: MoveAccountTermsToContracts made the contract the only owner of a term,
   removed the /api/accounts/{id}/terms routes and the accounts.terms.* claims,
   and moved every account term onto a contract. The pieces below are what
   ContractTerms.jsx composes; the file keeps its name so every page that loads
   it keeps working. */

const H = window.OdysseyHelpers;
const D = window.OdysseyData;

const trmToday = () => new Date().toISOString().slice(0, 10);
const trmKindInfo = (t) => H.termInfo(t);

/* The series the hero charts: the percentage series with the most history
   (ties → the one that started first). Null when no percentage term exists. */
const trmHeadlineKey = (terms) => {
  const by = {};
  for (const t of terms) {
    if (t.unit !== 'Percentage') continue;
    const k = H.termSeriesKey(t);
    const s = by[k] || (by[k] = { k, n: 0, first: t.effectiveFrom });
    s.n += 1; if (t.effectiveFrom < s.first) s.first = t.effectiveFrom;
  }
  const best = Object.values(by).sort((x, y) => (y.n - x.n) || (x.first < y.first ? -1 : 1))[0];
  return best ? best.k : null;
};

/* ---- per-list resolvers (operate on a live array so edits reflect at once) ----
   The series key is (kind, labelKey), so one kind can hold several concurrently
   in-force terms; within a label, the latest EffectiveFrom still wins. */
const trmKey = (t) => H.termSeriesKey(t);
const trmCurrentFromList = (terms, asOf) => {
  const cutoff = asOf || trmToday();
  const bySeries = {};
  for (const t of terms) {
    if (t.effectiveFrom > cutoff) continue;
    const k = trmKey(t);
    const cur = bySeries[k];
    if (!cur || t.effectiveFrom > cur.effectiveFrom
      || (t.effectiveFrom === cur.effectiveFrom && (t.createdAtUtc || '') > (cur.createdAtUtc || ''))) bySeries[k] = t;
  }
  return H.sortTermsBySeries(Object.values(bySeries));
};
const trmSeriesFromList = (terms, labelKey) => terms
  .filter(t => trmKey(t) === (labelKey || ''))
  .map(t => ({ id: t.id, date: t.effectiveFrom, value: t.value, note: t.note }))
  .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));

/* A term's name (its label, else the kind wording) plus the kind caption that
   sits beneath a labelled one — both plain TEXT, so nothing is carried by the
   glyph or its hue alone. */
const TermName = ({ t, nameClass }) => (
  <span className="trm-name">
    <span className={nameClass}>{H.termDisplayName(t)}</span>
  </span>
);

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

/* `fmtAxis` lets a caller label the value axis in its own unit — a contract's
   terms are usually AMOUNTS, not rates, and an axis reading "2.7%" beside a
   monthly rent is simply wrong. Percentage is the default, so the account hero
   is unchanged. */
const TermStepChart = ({ series, color, fmtAxis, ariaLabel }) => {
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
  /* One entry — a series that has never changed — has no range, so the band is
     invented. It has to be invented PROPORTIONALLY: a fixed ±0.005 is half a
     point around a rate (right) and two hundredths of a cent around a rent
     (fake precision on a flat line). Rates are stored as fractions below 1, so
     the magnitude rule leaves every rate chart exactly as it was. */
  if (lo === hi) {
    const band = Math.abs(lo) >= 1 ? Math.abs(lo) * 0.05 : 0.005;
    lo -= band; hi += band;
  }
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
    <svg className="trm-chart" viewBox={`0 0 ${W} ${Hh}`} role="img" aria-label={ariaLabel || 'Rate history'}>
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
          <text className="axis" x={padL - 8} y={y(v) + 3} textAnchor="end">{fmtAxis ? fmtAxis(v) : (v < 0 ? '−' : '') + H.pctStr(Math.abs(v))}</text>
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
  const key = trmHeadlineKey(terms);
  if (key == null) return null;
  const series = trmSeriesFromList(terms, key);
  const head = terms.find(t => trmKey(t) === key);
  const info = trmKindInfo(head);
  const color = info.color;
  const label = H.termDisplayName(head);
  const fmt = (v) => (v < 0 ? '−' : '') + H.pctStr(Math.abs(v));
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
          <div className="trm-hero-kind">{label} <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400 }}>· history</span></div>
          <div className="trm-hero-sub">
            {series.length} change{series.length === 1 ? '' : 's'} since {trmMonY(series[0].date)} · in force since {H.dateLong(current.date)}
          </div>
        </div>
        <div className="trm-hero-figs">
          <div className="trm-hero-value" style={{ color }}>{fmt(current.value)}</div>
          {prev && (
            <span className="trm-delta flat">
              <MIcon name={dir === 'up' ? 'arrow_upward' : dir === 'down' ? 'arrow_downward' : 'remove'} size={14} />
              {H.pctStr(Math.abs(diff))} {dir === 'up' ? 'higher' : dir === 'down' ? 'lower' : 'same'} vs {trmMonY(prev.date)}
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
/* The cadence tag — interval AND count, in words, from the shared helper. A
   one-time charge and an unset interval carry no cadence and render nothing. */
const CadenceTag = ({ term }) => {
  const text = H.cadenceTextFor(term);
  if (!text) return null;
  return <span className="trm-bill">{text}</span>;
};

/* The direction caption — the WORD, beneath the term's name. No glyph: an arrow
   reads against value (up = gain) rather than against the household, and the
   unambiguous alternatives are emoji. The word is what a reader parses first,
   and the figure's hue is redundant support, never the carrier.

   EVERY contract fee term states its direction, incoming or outgoing. An
   earlier revision stated Outgoing only on records that carried both sides;
   that made the caption's ABSENCE carry meaning, which a reader cannot see —
   and left two records with the same fee reading differently. Stated always,
   the fact is on the row rather than inferred from the set around it.

   Rendered only where direction MEANS something (a fee term on a contract), so
   every account surface and every rate row is unchanged. */
const TermDirectionTag = ({ term, owner }) => {
  if (!H.termDirectionApplies(term, owner)) return null;
  const incoming = H.termIsIncoming(term);
  const d = H.termDirectionInfo(term);
  return <span className={`trm-dir ${incoming ? 'in' : 'out'}`}>{d.label}</span>;
};

/* The colour an in-force figure takes: its DIRECTION's finance hue wherever
   direction is stated — coral out, mint in, on every contract fee term —
   otherwise whatever the surface already gave it (an account term, a rate).
   One helper, so the table, the timeline and the tiles cannot disagree about
   which figure is mint. */
const trmValueColor = (t, account) => {
  if (H.termDirectionApplies(t, account)) return H.termDirectionInfo(t).color;
  return trmKindInfo(t).color;
};

const CurrentTermsSummary = ({ current, style, account }) => {
  if (!current.length) return null;

  if (style === 'row') {
    return (
      <div className="trm-summary row">
        {current.map(t => {
          const info = trmKindInfo(t);
          return (
            <div className="trm-srow" key={trmKey(t)}>
              <span className="trm-kind-ic sm" style={{ background: info.soft, color: info.color }}>
                <MIcon name={info.icon} size={16} />
              </span>
              <TermName t={t} account={account} nameClass="trm-srow-kind" />
              <span className="trm-srow-meta">
                <CadenceTag term={t} />
                <span className="trm-srow-date">since {trmMonY(t.effectiveFrom)}</span>
                <span className="trm-srow-value" >{H.fmtTermValueFor(t, account)}</span>
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
          const info = trmKindInfo(t);
          return (
            <div className="trm-cchip" key={trmKey(t)}>
              <span className="trm-kind-ic sm" style={{ width: 26, height: 26, background: info.soft, color: info.color }}>
                <MIcon name={info.icon} size={15} />
              </span>
              <TermName t={t} account={account} nameClass="trm-cchip-kind" captionClass="trm-kind-caption inline" />
              <span className="trm-cchip-value" >{H.fmtTermValueFor(t, account)}</span>
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
        const info = trmKindInfo(t);
        return (
          <div className="trm-tile" key={trmKey(t)}>
            <div className="trm-tile-top">
              <span className="trm-kind-ic md" style={{ background: info.soft, color: info.color }}>
                <MIcon name={info.icon} size={18} />
              </span>
              <TermName t={t} account={account}
                nameClass="trm-tile-name" />
            </div>
            <div className="trm-tile-value" style={{ color: info.color }}>{H.fmtTermValueFor(t, account)}</div>
            <div className="trm-tile-foot">
              <span>since {trmMonY(t.effectiveFrom)}</span>
              <CadenceTag term={t} />
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
        const info = trmKindInfo(t);
        const isCurrent = currentIds.has(t.id);
        const cadence = H.cadenceTextFor(t);
        return (
          <tr key={t.id} className={isCurrent ? 'current' : ''}>
            <td>
              <div className="trm-row-kind">
                <span className="trm-kind-ic sm" style={H.termDirectionApplies(t, account)
                  ? { background: H.termDirectionInfo(t).soft || `color-mix(in srgb, ${H.termDirectionInfo(t).color} 16%, transparent)`, color: H.termDirectionInfo(t).color }
                  : { background: info.soft, color: info.color }}>
                  <MIcon name={info.icon} size={15} />
                </span>
                <div>
                  <div className="trm-row-top">
                    <TermName t={t} account={account} nameClass="trm-row-kind-name" />
                    {H.termDirectionApplies(t, account) && <span className="trm-row-dot" aria-hidden="true" />}
                    <TermDirectionTag term={t} owner={account} />
                  </div>
                  {t.note && <div className="trm-row-note">{t.note}</div>}
                </div>
              </div>
            </td>
            <td className="trm-cell-date">{H.dateLong(t.effectiveFrom)}</td>
            {/* Every entry is coloured by its own direction, in force or not —
                the direction is a fact of the ENTRY, and a superseded row was
                money out (or in) when it applied. Nothing is dimmed to make the
                point: the in-force row's own ground and weight, and the status
                cell beside it, carry the hierarchy without spending contrast.

                Where direction does NOT apply — an account's rates and fees, a
                contract's interest rate — colour stays the in-force marker it
                has always been on that surface. */}
            <td className="trm-cell-value"
              style={(H.termDirectionApplies(t, account) || isCurrent) ? { color: trmValueColor(t, account) } : undefined}>
              {H.fmtTermValueFor(t, account)}{cadence ? <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400 }}> {cadence}</span> : null}
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
      const info = trmKindInfo(t);
      const cadence = H.cadenceTextFor(t);
      return (
        <div className="trm-tl-item" key={t.id}>
          <div className="trm-tl-rail">
            <span className="trm-tl-node" style={{ background: info.color, color: info.color }} />
          </div>
          <div className="trm-tl-body">
            <div className="trm-tl-top">
              <TermName t={t} account={account} nameClass="trm-tl-kind" captionClass="trm-kind-caption inline" />
              <TermDirectionTag term={t} owner={account} />
              <span className="trm-tl-date">{H.dateLong(t.effectiveFrom)}</span>
              <TermStatus t={t} currentIds={currentIds} />
            </div>
            {t.note && <div className="trm-tl-note">{t.note}</div>}
          </div>
          <div className="trm-tl-figs">
            <span className="trm-tl-value"
              style={(H.termDirectionApplies(t, account) || currentIds.has(t.id)) ? { color: trmValueColor(t, account) } : undefined}>
              {H.fmtTermValueFor(t, account)}{cadence ? <span style={{ color: 'var(--mud-palette-text-secondary)', fontWeight: 400, fontSize: 12 }}> {cadence}</span> : null}
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
  const View = historyStyle === 'timeline' ? TermTimeline : TermTable;

  return (
    <div className="trm-history">
      <View rows={sorted} currentIds={currentIds} onEdit={onEdit} onDelete={onDelete} account={account} />
    </div>
  );
};

Object.assign(window, {
  TermStepChart, TermHero, CurrentTermsSummary, TermHistory, TermName, CadenceTag, TermDirectionTag,
  trmCurrentFromList, trmSeriesFromList, trmKindInfo, trmHeadlineKey, trmToday, trmKey, trmMonY,
});
