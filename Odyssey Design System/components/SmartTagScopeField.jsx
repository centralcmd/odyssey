/**
 * Odyssey DS — SmartTagScopeField (+ SMART_TAG_SCOPE_RULES)
 * The editor for a contract's SMART-TAG SCOPE: the date range the smart-tag
 * match uses instead of the contract's term (*Contract Smart-Tag Scope —
 * Backend, Draft v5*). Two nullable calendar days; an empty side is OPEN.
 *
 * The scope is independent of the term on purpose — a final paycheck, next
 * June's holiday pay or a deposit refund all arrive after the term ends — so
 * the field never validates against the term. It only SHOWS the relation: a
 * two-row strip (term above, scope below, open ends drawn dashed to the edge)
 * and one sentence in words, so the comparison never rests on the drawing.
 *
 * SMART_TAG_SCOPE_RULES is the client half of the server's SmartTagScopeRules:
 *   normalize   rule 2 — a client value → calendar day (offset converted to UTC
 *               first; a bare day or offset-less time is taken as written)
 *   fromTerm    rule 4 — the scope a contract is created / seeded / backfilled
 *               with (one-off → open/open; inverted legacy term → open end)
 *   validate    rules 1 + 3 — 1900-01-01…9999-12-31, start ≤ end, same day ok
 *   toWire      a day → "YYYY-MM-DDT00:00:00Z" (UTC midnight, never an offset)
 */
const STS_MIN = '1900-01-01';
const STS_MAX = '9999-12-31';
const STS_MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const stsDay = (v) => (v ? String(v).slice(0, 10) : null);
const stsFmt = (d) => {
  if (!d) return '';
  const [y, m, dd] = d.split('-').map(Number);
  return `${dd} ${STS_MONTHS[m - 1]} ${y}`;
};
const stsMs = (d) => Date.parse(d + 'T00:00:00Z');
const stsGap = (a, b) => {
  const days = Math.round(Math.abs(stsMs(b) - stsMs(a)) / 86400000);
  if (days < 62) return `${days} day${days === 1 ? '' : 's'}`;
  const months = Math.round(days / 30.44);
  if (months < 24) return `${months} months`;
  const years = Math.round(months / 12);
  return `${years} years`;
};

export const SMART_TAG_SCOPE_RULES = {
  minDate: STS_MIN,
  maxDate: STS_MAX,
  /** Rule 2. Bare day or offset-less time → that day. Z or ±hh:mm → converted
   *  to UTC, then truncated ("2027-06-30T00:30:00+02:00" → 2027-06-29). */
  normalize(input) {
    if (input == null) return null;
    const s = String(input).trim();
    if (!s) return null;
    if (/^\d{4}-\d{2}-\d{2}$/.test(s)) return s;
    if (/^\d{4}-\d{2}-\d{2}T[\d:.]+$/.test(s)) return s.slice(0, 10);
    const t = Date.parse(s);
    return Number.isNaN(t) ? null : new Date(t).toISOString().slice(0, 10);
  },
  /** Rule 4 — the stored term's calendar days, no time-zone conversion. */
  fromTerm(term = {}) {
    if (term.completionDate) return { start: null, end: null };
    const start = stsDay(term.startDate), end = stsDay(term.endDate);
    return { start, end: start && end && start > end ? null : end };
  },
  validate(scope = {}) {
    const errors = {};
    const range = 'Enter a date between 1 Jan 1900 and 31 Dec 9999.';
    if (scope.start && (scope.start < STS_MIN || scope.start > STS_MAX)) errors.start = range;
    if (scope.end && (scope.end < STS_MIN || scope.end > STS_MAX)) errors.end = range;
    if (!errors.start && !errors.end && scope.start && scope.end && scope.start > scope.end) {
      errors.end = 'The scope end must not be before its start.';
    }
    return errors;
  },
  equals(a = {}, b = {}) { return (a.start || null) === (b.start || null) && (a.end || null) === (b.end || null); },
  toWire(day) { return day ? `${day}T00:00:00Z` : null; },
  formatDay: stsFmt,
  /** "1 Sep 2025 – 31 Aug 2026" · "from 1 Sep 2025, no end" · "until …" · "any date". */
  formatRange(scope = {}) {
    const { start, end } = scope;
    if (start && end) return `${stsFmt(start)} – ${stsFmt(end)}`;
    if (start) return `from ${stsFmt(start)}, no end`;
    if (end) return `until ${stsFmt(end)}`;
    return 'any date';
  },
  /** The scope against the term, in words. Never a validation. */
  describe(scope = {}, term = {}) {
    const { start, end } = scope;
    if (term.completionDate) {
      return start || end
        ? `A one-off contract has no term, so this range is the only date rule.`
        : 'A one-off contract has no term, so transactions of any date match.';
    }
    const ts = stsDay(term.startDate), te = stsDay(term.endDate);
    let a;
    if (!start) a = ts ? 'No start limit — transactions before the term match too' : 'No start limit';
    else if (!ts) a = `Starts ${stsFmt(start)}`;
    else if (start === ts) a = 'Starts with the term';
    else if (start < ts) a = `Starts ${stsGap(start, ts)} before the term`;
    else a = `Starts ${stsGap(ts, start)} after the term starts`;
    let b;
    if (!end) b = te ? `stays open after the term ends (${stsFmt(te)})` : 'stays open, like the term';
    else if (!te) b = `ends ${stsFmt(end)}, while the term has no end`;
    else if (end === te) b = 'ends with it';
    else if (end > te) b = `runs ${stsGap(te, end)} past the term’s end (${stsFmt(te)})`;
    else b = `ends ${stsGap(end, te)} before the term does`;
    return `${a}, and ${b}.`;
  },
};

/* The relation strip. Bars on one shared axis; an open side runs to the track
   edge and ends dashed. Decorative — the sentence below carries the meaning. */
function ScopeStrip({ scope, term }) {
  const oneOff = !!term.completionDate;
  const ts = oneOff ? null : stsDay(term.startDate), te = oneOff ? null : stsDay(term.endDate);
  const pt = oneOff ? stsDay(term.completionDate) : null;
  const pts = [ts, te, pt, scope.start, scope.end].filter((d) => d && d >= STS_MIN && d <= STS_MAX);
  if (!pts.length) return null;
  let lo = Math.min(...pts.map(stsMs)), hi = Math.max(...pts.map(stsMs));
  if (hi - lo < 86400000 * 30) { lo -= 86400000 * 60; hi += 86400000 * 60; }
  const pad = (hi - lo) * 0.14;
  lo -= pad; hi += pad;
  const x = (d) => ((stsMs(d) - lo) / (hi - lo)) * 100;
  const bar = (s, e) => {
    const l = s ? x(s) : 0, r = e ? x(e) : 100;
    return { left: `${l}%`, width: `${Math.max(r - l, 0.8)}%` };
  };
  return (
    <div className="odc-stscope-strip" aria-hidden="true">
      <span className="odc-stscope-strip-lab">Term</span>
      <div className="odc-stscope-track">
        {oneOff ? (
          <span className="odc-stscope-point" style={{ left: `${x(pt)}%` }} />
        ) : (
          <span className={`odc-stscope-bar term${ts ? '' : ' open-l'}${te ? '' : ' open-r'}`} style={bar(ts, te)} />
        )}
      </div>
      <span className="odc-stscope-strip-lab">Scope</span>
      <div className="odc-stscope-track">
        <span className={`odc-stscope-bar scope${scope.start ? '' : ' open-l'}${scope.end ? '' : ' open-r'}`} style={bar(scope.start, scope.end)} />
      </div>
    </div>
  );
}

export function SmartTagScopeField({
  value = { start: null, end: null },
  onChange,
  term = {},
  errors = {},
  disabled = false,
  showStrip = true,
  showActions = true,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const DateField = NS.DateField;
  const Button = NS.Button;
  const R = SMART_TAG_SCOPE_RULES;
  const scope = { start: value.start || null, end: value.end || null };
  const fromTerm = R.fromTerm(term);
  const same = R.equals(scope, fromTerm);
  const set = (k) => (iso) => onChange && onChange({ ...scope, [k]: R.normalize(iso) });

  const dateField = (k, label, placeholder, help) => DateField ? (
    <DateField label={label} value={scope[k]} onChange={set(k)} placeholder={placeholder}
      min={STS_MIN} max={STS_MAX} help={help} error={errors[k]} disabled={disabled} align={k === 'end' ? 'end' : 'start'} />
  ) : (
    <label className="odc-field">
      <span className="odc-field-label">{label}</span>
      <input className="odc-input" type="date" value={scope[k] || ''} disabled={disabled}
        onChange={(e) => set(k)(e.target.value || null)} />
    </label>
  );

  return (
    <div className={`odc-stscope${className ? ' ' + className : ''}`}>
      <div className="odc-stscope-grid">
        {dateField('start', 'Match from', 'No start limit', 'Leave empty for no start limit.')}
        {dateField('end', 'Match until', 'No end limit', 'Leave empty to keep matching after the term ends.')}
      </div>
      {showStrip ? <ScopeStrip scope={scope} term={term} /> : null}
      <p className="odc-stscope-summary" aria-live="polite">
        <span className="material-icons" aria-hidden="true">compare_arrows</span>
        <span>{R.describe(scope, term)}</span>
      </p>
      {showActions && !disabled ? (
        <div className="odc-stscope-actions">
          {Button ? (
            <React.Fragment>
              <Button variant="text" size="sm" icon="event_repeat" disabled={same} onClick={() => onChange && onChange(fromTerm)}>
                Copy the contract term
              </Button>
              <Button variant="text" size="sm" icon="all_inclusive" disabled={!scope.start && !scope.end} onClick={() => onChange && onChange({ start: null, end: null })}>
                Remove both limits
              </Button>
            </React.Fragment>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
