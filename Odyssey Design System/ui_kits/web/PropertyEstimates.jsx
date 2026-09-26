/* PropertyEstimates — the property's value history, laid out like a contract's
   Terms: a "Current value" section of InfoTiles, then a "Value history" section
   with the DS TermHistoryChart over the trm-tbl ledger. Same in-force rule as
   everywhere (greatest EffectiveFrom ≤ today, tie → CreatedAtUtc). */

const PropertyEstimates = ({ property: p, estimates, canWrite, onNew, onEdit, onDelete }) => {
  const H = window.OdysseyHelpers;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const ti = H.propTypeInfo(p.type);
  const today = H.propToday();
  const cur = p.currencyCode;
  const monY = (iso) => { const d = new Date(iso + 'T00:00:00'); return d.toLocaleDateString('en-US', { month: 'short' }) + ' ’' + String(d.getFullYear()).slice(2); };

  const current = H.propCurrentEstimate(estimates);
  const past = estimates.filter(e => e.effectiveFrom <= today).sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? -1 : 1));
  const prev = current ? past.filter(e => e.id !== current.id).pop() : null;
  const scheduled = estimates.filter(e => e.effectiveFrom > today).sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? -1 : 1))[0];
  const first = past[0];
  const diff = current && prev ? current.value - prev.value : null;
  const sinceFirst = current && first && first.id !== current.id ? current.value - first.value : null;
  const up = 'var(--finance-income)', down = 'var(--finance-expense)';
  const tone = (d) => (d > 0 ? up : d < 0 ? down : 'var(--mud-palette-text-secondary)');
  const signed = (d) => (d > 0 ? '+' : d < 0 ? '−' : '') + H.money(Math.abs(d), cur);
  const pct = (d, base) => (base ? ` · ${d >= 0 ? '+' : '−'}${Math.abs((d / base) * 100).toFixed(1)}%` : '');

  if (estimates.length === 0) {
    return (
      <div className="con-section">
        <SectionDivider label="Estimated value" meta="0 entries" />
        <EmptyLine>
          No estimate yet — record what {p.name} is worth, effective from a date. Each new estimate supersedes the last from its date on; the history is kept.
        </EmptyLine>
      </div>
    );
  }

  const sorted = estimates.slice().sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : (a.createdAtUtc < b.createdAtUtc ? 1 : -1)));
  const points = estimates.slice().sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? -1 : 1)).map(e => ({ id: e.id, date: e.effectiveFrom, value: e.value }));
  const series = [{
    key: 'value', label: 'Estimated value', value: current ? H.money(current.value, cur) : '—',
    color: ti.color, group: `amt:${cur}`, points,
    format: (v) => H.money(v, cur),
    /* No currency code on the tick (as ContractTerms): compact so 5.14M fits. */
    axisFormat: (v) => { const a = Math.abs(v), s = v < 0 ? '−' : ''; return s + (a >= 1e6 ? (a / 1e6).toFixed(2) + 'M' : a >= 1e4 ? Math.round(a / 1e3) + 'K' : a.toLocaleString('en-US', { maximumFractionDigits: 0 })); },
  }];

  return (
    <React.Fragment>
      <div className="con-section">
        <SectionDivider label="Current value" meta={current ? `in force · ${H.dateLong(today)}` : 'none in force'} />
        {current ? (
          <InfoTileGrid>
            <InfoTile icon="monitor" iconColor={ti.color} iconSoft={ti.soft} label="Estimated value"
              value={<span style={{ color: ti.color }}>{H.money(current.value, cur)}</span>}
              foot={`since ${H.dateLong(current.effectiveFrom)}${current.note ? ` · ${current.note}` : ''}`} />
            {diff != null ? (
              <InfoTile icon={diff > 0 ? 'trending_up' : diff < 0 ? 'trending_down' : 'trending_flat'} iconColor={tone(diff)}
                iconSoft={`color-mix(in srgb, ${tone(diff)} 16%, transparent)`} label="Change vs previous"
                value={<span style={{ color: tone(diff) }}>{signed(diff)}</span>}
                foot={`from ${H.money(prev.value, cur)} · ${monY(prev.effectiveFrom)}${pct(diff, prev.value)}`} />
            ) : null}
            {sinceFirst != null && past.length > 2 ? (
              <InfoTile icon="history" iconColor={tone(sinceFirst)} iconSoft={`color-mix(in srgb, ${tone(sinceFirst)} 16%, transparent)`} label="Since first estimate"
                value={<span style={{ color: tone(sinceFirst) }}>{signed(sinceFirst)}</span>}
                foot={`from ${H.money(first.value, cur)} · ${monY(first.effectiveFrom)}${pct(sinceFirst, first.value)}`} />
            ) : null}
            {scheduled ? (
              <InfoTile icon="schedule" iconColor="oklch(0.80 0.13 85)" label="Scheduled"
                value={H.money(scheduled.value, cur)} foot={`from ${H.dateLong(scheduled.effectiveFrom)}`} />
            ) : null}
          </InfoTileGrid>
        ) : (
          <EmptyLine>Nothing in force today — every estimate on this property is dated later.</EmptyLine>
        )}
      </div>

      <div className="con-section">
        <SectionDivider label="Value history" meta={`${estimates.length} ${estimates.length === 1 ? 'entry' : 'entries'} · ${cur}`} />
        {DS.TermHistoryChart ? <DS.TermHistoryChart className="trm-seriesplot" series={series} pickerLabel="Value" glyph="¤" curve="smooth" /> : null}
        <div className="trm-history">
        <table className="trm-tbl">
          <thead>
            <tr>
              <th scope="col">Estimate</th>
              <th scope="col">Effective from</th>
              <th scope="col" className="num">Value</th>
              <th scope="col">Status</th>
              {canWrite ? <th scope="col" className="act" aria-label="Actions"></th> : null}
            </tr>
          </thead>
          <tbody>
            {sorted.map(e => {
              const isCur = current && e.id === current.id;
              const isSched = e.effectiveFrom > today;
              return (
                <tr key={e.id} className={isCur ? 'current' : ''}>
                  <td>
                    <div className="trm-row-kind">
                      <span className="trm-kind-ic sm" style={{ background: ti.soft, color: ti.color }}><MIcon name="monitor" size={15} /></span>
                      <div>
                        <div className="trm-row-top"><span className="trm-row-kind-name">Estimated value</span></div>
                        {e.note && <div className="trm-row-note">{e.note}</div>}
                      </div>
                    </div>
                  </td>
                  <td className="trm-cell-date">{H.dateLong(e.effectiveFrom)}</td>
                  <td className="trm-cell-value" style={isCur ? { color: ti.color } : undefined}>{H.money(e.value, e.currencyCode || cur)}</td>
                  <td>
                    {isSched ? <span className="trm-superseded" style={{ color: 'oklch(0.80 0.13 85)', opacity: 1 }}>Scheduled</span>
                      : isCur ? <span className="trm-inforce"><MIcon name="check_circle" size={12} />In force</span>
                      : <span className="trm-superseded">Superseded</span>}
                  </td>
                  {canWrite ? (
                    <td className="trm-cell-act">
                      <span className="trm-rowbtns">
                        <button type="button" className="trm-iconbtn" aria-label="Edit estimate" onClick={() => onEdit(e)}><MIcon name="edit" size={17} /></button>
                        <button type="button" className="trm-iconbtn danger" aria-label="Delete estimate" onClick={() => onDelete(e)}><MIcon name="delete" size={17} /></button>
                      </span>
                    </td>
                  ) : null}
                </tr>
              );
            })}
          </tbody>
        </table>
        </div>
      </div>
    </React.Fragment>
  );
};

Object.assign(window, { PropertyEstimates });
