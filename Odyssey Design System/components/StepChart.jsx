/**
 * Odyssey DS — StepChart
 * The dated-value chart: a price, a rate, a term. Sibling of `LineChart`, and
 * deliberately the same card — identical head (title · sub, figure · delta),
 * viewBox, gridlines, axis type and typography, so a term chart and the
 * dashboard's net worth read as one chart vocabulary. What differs is the two
 * things a trend chart gets wrong about a held value:
 *
 *   • **A real time axis.** `LineChart` spaces its points evenly, which is
 *     right for twelve monthly readings and wrong for a history whose entries
 *     land whenever someone signed something: a rent held for eight months and
 *     then twice in one quarter must not read as three equal steps.
 *   • **Today.** A value in force continues to the present, and an entry dated
 *     ahead has not happened yet. The line holds solid to the now marker and
 *     dashes past it, so a scheduled increase is visibly not yet true.
 *
 * The line is a STAIRCASE by default — the value holds until the day it
 * changes, then jumps, because a price does not drift. `curve` opts out for
 * data that DOES drift between readings — an estimated value, an appraisal:
 * `"linear"` joins the entries with straight segments, `"smooth"` with a
 * monotone curve (never overshoots an entry, so no invented peak or dip).
 * Either way the last entry holds flat to the edge — nothing is known past it.
 *
 * Data is `series: { date, value, id?, note? }[]` (ISO dates, oldest → newest),
 * or `lines: { id, label, color, points }[]` to compare SEVERAL histories.
 *
 * `lines` mode plots **indexed change** by default — each series as its
 * percentage move from its own first entry — rather than absolute values on a
 * shared axis. A lease's rent is 2,250 and its parking is 95; on one absolute
 * axis the largest series owns the domain and everything else collapses onto
 * the floor, two lines 5px apart reading as one band. Indexing is usually the
 * question being asked of several price histories at once: which is rising
 * fastest. `scale` overrides that either way, for a reader who wants the
 * magnitudes of several or the shape of one — the choice belongs to them, so
 * expose it rather than deciding once. The legend carries each line's real
 * value in force and its move whichever axis is showing, so the money is
 * never lost, and its move is stated in whatever unit the axis is speaking.
 * Callers must still only overlay series sharing a unit and a
 * currency — the component cannot know that, and the legend's figures would
 * mix.
 *
 * The head's single figure is replaced by that legend in `lines` mode, and the
 * area fill is dropped: two translucent washes over each other read as a third
 * value that isn't there. A series that has never changed still plots — flat
 * at 0% beside a rising line is an answer to "which of these is moving" — and
 * because indexed mode starts every line at 0%, all lines are fanned by a hair
 * so shared spans never collapse into one visible stroke. The legend shows in both modes — with one series it adds the move
 * since the first entry, which neither the head figure (today's value) nor the
 * delta (the last change) states.
 *
 * `format` renders the headline figure and, absent `axisFormat`, the y ticks.
 * Every figure the card states — head, delta, legend, text equivalent — is the
 * value **in force**: the latest entry that has already taken effect. A
 * scheduled entry is drawn (hollow dot, dashed line) and belongs in `sub`, but
 * it is not what the thing costs, so it never headlines. The delta is
 * **in-force vs the entry before it**, not vs the first: the question a price
 * history answers is what just changed, and the earliest entry is often years
 * of irrelevance away. A single-entry series is a flat hold, not an error — it
 * is a value that has never changed.
 *
 * `figureColor` carries the figure's own meaning (a term's direction: money
 * out is money out whether it rose or fell), which leaves the delta free to be
 * `deltaTone="neutral"` grey. That pairing is usually right here: on an
 * expense, an increase is not income-green, and colouring both fights over
 * one meaning.
 *
 * Styled by .odc-lc (shared) + .odc-sc-* in components.css.
 */
const SC_DAY = 86400000;

const scMs = (iso) => new Date(iso + 'T00:00:00').getTime();
const scMonY = (iso) => {
  const d = new Date(iso + 'T00:00:00');
  return d.toLocaleDateString('en-US', { month: 'short' }) + ' \u2019' + String(d.getFullYear()).slice(2);
};

/** Clip an axis-aligned polyline to x ≤ bound (or x ≥ bound), interpolating the
 *  crossing so the solid/dashed split lands exactly on the now marker. */
function scClip(pts, bound, keepBelow) {
  const out = [];
  for (let i = 0; i < pts.length; i++) {
    const p = pts[i];
    const inside = keepBelow ? p.x <= bound : p.x >= bound;
    if (i > 0) {
      const prev = pts[i - 1];
      const prevInside = keepBelow ? prev.x <= bound : prev.x >= bound;
      if (inside !== prevInside) {
        out.push(prev.x === p.x
          ? { x: bound, y: inside ? prev.y : p.y }
          : { x: bound, y: prev.y + (p.y - prev.y) * ((bound - prev.x) / (p.x - prev.x)) });
      }
    }
    if (inside) out.push(p);
  }
  return out;
}
const scPath = (pts) => (pts.length ? 'M ' + pts.map((p) => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' L ') : '');
/** Monotone cubic (Fritsch–Carlson) through pts, sampled into a polyline so
 *  the now-marker clip and the area fill work unchanged. Never overshoots. */
function scMonotone(pts, n = 16) {
  if (pts.length < 3) return pts.slice();
  const k = pts.length, d = [], m = new Array(k);
  for (let i = 0; i < k - 1; i++) d.push((pts[i + 1].y - pts[i].y) / ((pts[i + 1].x - pts[i].x) || 1e-9));
  m[0] = d[0]; m[k - 1] = d[k - 2];
  for (let i = 1; i < k - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
  for (let i = 0; i < k - 1; i++) {
    if (d[i] === 0) { m[i] = 0; m[i + 1] = 0; continue; }
    const a = m[i] / d[i], b = m[i + 1] / d[i], h = a * a + b * b;
    if (h > 9) { const t = 3 / Math.sqrt(h); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
  }
  const out = [pts[0]];
  for (let i = 0; i < k - 1; i++) {
    const p0 = pts[i], p1 = pts[i + 1], dx = p1.x - p0.x;
    for (let j = 1; j <= n; j++) {
      const t = j / n, t2 = t * t, t3 = t2 * t;
      out.push({ x: p0.x + dx * t,
        y: (2 * t3 - 3 * t2 + 1) * p0.y + (t3 - 2 * t2 + t) * dx * m[i] + (-2 * t3 + 3 * t2) * p1.y + (t3 - t2) * dx * m[i + 1] });
    }
  }
  return out;
}
const scSort = (pts) => (pts || [])
  .filter((p) => p && p.value != null && p.date)
  .slice()
  .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));

export function StepChart({
  series = [],
  lines,
  scale = 'auto',
  curve = 'step',
  color = 'var(--chart-1)',
  title,
  sub,
  format = (n) => n.toLocaleString(),
  axisFormat,
  showDelta = false,
  showFigure = true,
  deltaSuffix,
  deltaTone = 'signed',
  figure,
  figureColor,
  area = true,
  controls,
  controlsEnd,
  nowLabel = 'Today',
  textEquivalent = false,
  textEquivalentLabel = 'Effective from',
  ariaLabel,
  className = '',
  emptyLabel = 'No entries yet.',
}) {
  const uid = React.useId();
  const fmtAxis = axisFormat || format;

  /* `lines` says what to plot; the COUNT says how to read it. One line has a
     headline figure and a real-value axis whether it arrived as `series` or as
     a one-entry `lines`, so a caller can always pass `lines` and let the count
     decide — which is what lets the legend name a lone series without the card
     also carrying a title. */
  const sets = (Array.isArray(lines) && lines.length
    ? lines
    : [{ id: '_', label: title, color, points: series }])
    .map((l, i) => ({ ...l, id: l.id || `l${i}`, color: l.color || color, pts: scSort(l.points) }))
    .filter((l) => l.pts.length > 0);
  const multi = sets.length > 1;
  const now = Date.now();
  /* The one answer to "what does this cost": the latest entry that has already
     taken effect. A scheduled increase is drawn (hollow dot, dashed line) and
     said in `sub`, but it is not the value — it has not happened. The head
     figure, the delta and the legend all resolve through this, so the card
     never gives two answers to the same question. */
  const inForceIdx = (pts) => {
    const i = pts.map((p) => scMs(p.date)).filter((t) => t <= now).length - 1;
    return i < 0 ? 0 : i;    // entirely future: the soonest entry stands in
  };

  const primary = sets[0];
  const pIdx = primary ? inForceIdx(primary.pts) : 0;
  const last = primary && primary.pts[pIdx];
  const prev = primary && pIdx > 0 ? primary.pts[pIdx - 1] : null;

  const head = (
    <div className="odc-lc-head">
      <div>
        {/* Controls take the title's place: a picker that names what is
            plotted IS the title, and two of them would say the same thing
            twice. A caller with no controls still passes one. */}
        {controls ? <div className="odc-lc-controls">{controls}</div> : (title ? <div className="odc-lc-ttl">{title}</div> : null)}
        {sub ? <div className="odc-lc-sub">{sub}</div> : null}
      </div>
      {/* One series gets the figure + delta. Several get neither: there is no
          single current value to headline, and a delta on an arbitrary one of
          them would be read as the chart's. The legend carries each instead —
          which is also why a card whose legend is always on can turn the
          figure off entirely with `showFigure={false}`. */}
      {(controlsEnd || (showFigure && !multi && (figure != null || sets.length > 0))) && (
        <div className="odc-lc-figure">
          {controlsEnd ? <div className="odc-lc-controls end">{controlsEnd}</div> : null}
          {showFigure && !multi && (figure != null || sets.length > 0) && (
            <React.Fragment>
              <div className="odc-lc-num" style={figureColor ? { color: figureColor } : undefined}>{figure != null ? figure : format(last.value)}</div>
              {showDelta && prev && (() => {
                const d = last.value - prev.value;
                const tone = deltaTone === 'neutral' ? 'neutral' : (d >= 0 ? 'income' : 'expense');
                return (
                  <div className={`odc-lc-delta ${tone}`}>
                    {d >= 0 ? '+' : '\u2212'}{format(Math.abs(d))}{deltaSuffix ? ` ${deltaSuffix}` : ''}
                  </div>
                );
              })()}
            </React.Fragment>
          )}
        </div>
      )}
    </div>
  );

  if (sets.length === 0) {
    return (
      <div className={`odc-lc${className ? ' ' + className : ''}`}>
        {head}
        <div className="odc-lc-empty">{emptyLabel}</div>
      </div>
    );
  }

  const x0 = 64, x1 = 968, yTop = 28, yBot = 212;
  const allPts = sets.flatMap((s) => s.pts);

  /* A series' move from its own first entry. It is what `lines` mode PLOTS
     (see below) and what every legend row states, in either mode. */
  const moveOf = (s, v) => {
    const base = s.pts[0].value;
    return base ? (v - base) / Math.abs(base) : 0;
  };
  /* `auto` is the useful default rather than a fixed one: a comparison needs
     indexing to stay legible, a lone series has an axis that can carry real
     figures. Either can be forced — a reader may want the shape of a single
     charge's rise, or the true magnitudes of several. */
  const indexed = scale === 'auto' ? multi : scale === 'indexed';
  /* What each line is PLOTTED as. One series, or an absolute comparison: the
     value itself. An indexed comparison: the move, so unlike magnitudes stay
     legible and the axis answers "which is rising fastest" rather than "which
     is biggest". A zero first entry has no percentage change, so it is plotted
     absolutely — rare for a price, and better than dividing by zero. */
  const plotted = (s, v) => {
    if (!indexed) return v;
    return s.pts[0].value ? moveOf(s, v) : v;
  };
  const fmtIndexed = (v) => {
    // Sign from the ROUNDED magnitude, not the raw value: a tick a thousandth
    // above zero must not print "+0%".
    const n = Math.round(Math.abs(v) * 100);
    return `${n === 0 ? '' : v > 0 ? '+' : '\u2212'}${n}%`;
  };
  const t0 = Math.min(...allPts.map((p) => scMs(p.date)));
  const tLast = Math.max(...allPts.map((p) => scMs(p.date)));
  // The axis runs to whichever is later: the present, or the furthest entry a
  // scheduled change reaches. One day of span is the floor, so a history whose
  // only entry is dated today still has a plot to draw.
  const tMax = Math.max(now, tLast, t0 + SC_DAY);
  const sx = (t) => x0 + ((t - t0) / (tMax - t0)) * (x1 - x0);

  const vals = sets.flatMap((s) => s.pts.map((p) => plotted(s, p.value)));
  let lo = Math.min(...vals), hi = Math.max(...vals);
  if (lo === hi) {
    /* A set that has never changed has no range, so the band is invented — in
       whatever unit is being PLOTTED. Indexed mode plots fractions, where a
       0.005 band is half a percent and every tick rounds to zero; absolute
       mode needs it proportional, since a fixed step is half a point around a
       rate (stored as a fraction) and fake precision around a rent. */
    const band = indexed ? 0.02 : (Math.abs(lo) >= 1 ? Math.abs(lo) * 0.05 : 0.005);
    lo -= band; hi += band;
  }
  const span = hi - lo;
  const pad = span * 0.18;
  // No zero clamp for data that goes negative — a rate can legitimately be below
  // zero, and so can a credit. But padding an all-positive set down past zero
  // invents a negative region the value could never occupy, so that floor sits
  // at zero instead.
  const yMin = lo >= 0 ? Math.max(0, lo - pad) : lo - pad;
  const yMax = hi + pad;
  const sy = (v) => yBot - ((v - yMin) / (yMax - yMin || 1)) * (yBot - yTop);

  const nowX = sx(now);
  const plots = sets.map((s) => {
    /* Every series is drawn, including one that has never changed: flat at 0%
       beside a rising line is an answer to "which of these is moving", not an
       absence of one. Coincidence is handled below, where it happens. */
    let walk = [];
    s.pts.forEach((p, i) => {
      const px = sx(scMs(p.date)), py = sy(plotted(s, p.value));
      if (i === 0 || curve !== 'step') walk.push({ x: px, y: py });
      else { walk.push({ x: px, y: walk[walk.length - 1].y }); walk.push({ x: px, y: py }); }
    });
    if (curve === 'smooth') walk = scMonotone(walk);
    walk.push({ x: sx(tMax), y: walk[walk.length - 1].y });
    const solid = scClip(walk, nowX, true);
    const dashed = scClip(walk, nowX, false);
    return {
      ...s, solid, dashed,
      areaPath: solid.length > 1
        ? `${scPath(solid)} L ${solid[solid.length - 1].x.toFixed(1)} ${yBot} L ${solid[0].x.toFixed(1)} ${yBot} Z`
        : '',
    };
  });

  /* Coincident strokes: indexed mode starts EVERY series at 0%, so shared
     spans are the normal case there (the run before the first change, a flat
     never-changed line, two charges revised on the same date) — and absolute
     mode has its own version whenever two charges simply cost the same. Left
     alone, the last line painted stands for all of them and a legend swatch
     points at nothing. So every line is fanned by a constant hair — about a
     third of a percent of plot height, under the stroke width — which keeps
     each colour findable across a shared span while moving no value the axis
     could read. */
  if (plots.length > 1) {
    plots.forEach((s, i) => { s.dodge = (i - (plots.length - 1) / 2) * 2.6; });
  }

  const gridVals = [yMax, yMin + (yMax - yMin) * 2 / 3, yMin + (yMax - yMin) / 3, yMin];
  const uidSafe = uid.replace(/[^a-zA-Z0-9_-]/g, '');
  const axisRound = (v) => (Math.abs(yMax) < 10 ? v : Math.round(v));
  const tickFmt = indexed ? fmtIndexed : (v) => fmtAxis(axisRound(v));

  // One label per change, dropped where two would collide, and never within
  // reach of the now label — the marker's own date is what it says.
  const xLabels = [];
  let lastX = -Infinity;
  Array.from(new Set(allPts.map((p) => p.date))).sort().forEach((date) => {
    const px = sx(scMs(date));
    if (px - lastX < 64 || Math.abs(px - nowX) < 48) return;
    xLabels.push({ x: px, label: scMonY(date) });
    lastX = px;
  });

  const autoAria = ariaLabel
    || (multi ? `${sets.map((s) => s.label).filter(Boolean).join(', ')} — values over time`
      : (title ? `${title} — value over time` : 'Value over time'));

  return (
    <div className={`odc-lc${className ? ' ' + className : ''}`}>
      {head}

      <svg className="odc-line-svg" viewBox="0 0 1000 252" preserveAspectRatio="xMidYMid meet"
        role="img" aria-label={autoAria}>
        <g stroke="var(--chart-grid)" strokeWidth="1">
          {gridVals.map((v, i) => <line key={i} x1={x0} y1={sy(v).toFixed(1)} x2={x1} y2={sy(v).toFixed(1)} />)}
        </g>
        <g className="odc-lc-axis">
          {gridVals.map((v, i) => (
            <text key={i} x={x0 - 12} y={(sy(v) + 4).toFixed(1)} textAnchor="end">{tickFmt(v)}</text>
          ))}
        </g>
        <g className="odc-lc-axis">
          {xLabels.map((t) => <text key={t.x} x={t.x.toFixed(1)} y={yBot + 26} textAnchor="middle">{t.label}</text>)}
        </g>

        {/* Area only for a lone line — stacked washes read as a third value. */}
        {area && !multi && plots[0].areaPath && (
          <React.Fragment>
            <defs>
              <linearGradient id={`odc-sc-fill-${uidSafe}`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={plots[0].color} stopOpacity="0.26" />
                <stop offset="100%" stopColor={plots[0].color} stopOpacity="0" />
              </linearGradient>
            </defs>
            <path d={plots[0].areaPath} fill={`url(#odc-sc-fill-${uidSafe})`} />
          </React.Fragment>
        )}

        {plots.map((s) => (
          <g key={s.id} transform={s.dodge ? `translate(0 ${s.dodge.toFixed(2)})` : undefined}>
            {s.solid.length > 1 && <path className="odc-sc-line" d={scPath(s.solid)} fill="none" stroke={s.color} />}
            {s.dashed.length > 1 && <path className="odc-sc-line future" d={scPath(s.dashed)} fill="none" stroke={s.color} />}
          </g>
        ))}

        <line className="odc-sc-now" x1={nowX.toFixed(1)} y1={yTop - 14} x2={nowX.toFixed(1)} y2={yBot} />
        <text className="odc-lc-axis odc-sc-nowlabel" x={Math.min(nowX + 6, x1)} y={yTop - 18}
          textAnchor={nowX > (x0 + x1) / 2 ? 'end' : 'start'}>{nowLabel}</text>

        {plots.map((s) => (
          <g key={s.id} transform={s.dodge ? `translate(0 ${s.dodge.toFixed(2)})` : undefined}>
            {s.pts.map((p, i) => {
              const fIdx = inForceIdx(s.pts);
              const cx = sx(scMs(p.date)), cy = sy(plotted(s, p.value));
              return scMs(p.date) > now
                ? <circle key={`${s.id}-${p.id || i}`} cx={cx.toFixed(1)} cy={cy.toFixed(1)} r="4"
                    fill="var(--mud-palette-surface)" stroke={s.color} strokeWidth="1.8" />
                : <circle key={`${s.id}-${p.id || i}`} cx={cx.toFixed(1)} cy={cy.toFixed(1)}
                    r={i === fIdx ? 4 : 3} fill={s.color} />;
            })}
          </g>
        ))}
      </svg>

      {/* The legend IS the readout when several lines share the axis, and it
          still earns its place with one: the swatch ties the line to its name,
          and the move since the first entry is a fact the head figure (today's
          value) and the delta (the last change) both leave out. */}
      {sets.length > 0 && (
        <div className="odc-sc-legend">
          {sets.map((s) => {
            const idx = inForceIdx(s.pts);
            const cur = s.pts[idx];
            /* The move, in the unit the AXIS is speaking: a percentage while
               the chart plots change, real money while it plots value. The
               legend and the plot answer the same question either way. */
            const moved = indexed
              ? fmtIndexed(moveOf(s, cur.value))
              : (() => {
                const d = cur.value - s.pts[0].value;
                return `${d > 0 ? '+' : d < 0 ? '\u2212' : ''}${format(Math.abs(d))}`;
              })();
            return (
              <span className="odc-sc-leg" key={s.id}>
                <span className="odc-sc-swatch" style={{ background: s.color }}></span>
                <span className="odc-sc-leg-name">{s.label}</span>
                <span className="odc-sc-leg-val" style={{ color: s.color }}>{format(cur.value)}</span>
                <span className="odc-sc-leg-idx">{s.pts.length > 1 ? moved : 'no changes yet'}</span>
              </span>
            );
          })}
        </div>
      )}


      {textEquivalent && (
        <table className="odc-sr-only">
          {title ? <caption>{title}</caption> : null}
          <thead>
            <tr>
              {multi && <th scope="col">Series</th>}
              <th scope="col">{textEquivalentLabel}</th>
              <th scope="col">Value</th>
              <th scope="col">State</th>
            </tr>
          </thead>
          <tbody>
            {sets.map((s) => {
              const fIdx = inForceIdx(s.pts);
              return s.pts.map((p, i) => (
                <tr key={`${s.id}-${p.id || i}`}>
                  {multi && <td>{s.label}</td>}
                  <th scope="row">{p.date}</th>
                  <td>{format(p.value)}</td>
                  <td>{scMs(p.date) > now ? 'Scheduled' : i === fIdx ? 'In force' : 'Superseded'}</td>
                </tr>
              ));
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
