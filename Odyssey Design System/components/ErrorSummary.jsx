/**
 * Odyssey DS — ErrorSummary
 * The compact "n problems · Review" control that sits immediately before a
 * disabled primary action. It answers the one question a greyed-out Save can't:
 * *what* is blocking it, and where.
 *
 * Required on any page long enough that the offending field can be off-screen
 * when the action is in view — the System settings catalogue (42 rows across 11
 * sections) is the reference case. On a single-screen dialog, the field-level
 * error is enough; don't add this.
 *
 * Pass `problems` — `{ label, section, targetId }` per blocking field — and the
 * control becomes a disclosure: the count opens a list, each entry moves focus
 * to that field. With no `problems`, pressing it calls `onReview` and should
 * focus the first blocking field. Either way it is a **button, not a banner**:
 * moving focus is what makes a disabled action recoverable by keyboard.
 *
 * Two rules the announcement depends on. The count is folded into the
 * accessible name, so it reads "2 problems, review" rather than "2 · Review".
 * And this component announces nothing itself — validation that recomputes per
 * keystroke must not be in a live region, so the page announces politely on a
 * *save attempt* instead.
 *
 * Only list problems for rows that are actually rendered. A search-filtered or
 * permission-disabled row has no focus target, so an entry pointing at one is a
 * dead end — filter those out and fall back to a page-level alert.
 */
export function ErrorSummary({
  count = 0,
  problems,
  onReview,
  onJump,
  noun = 'problem',
  action = 'Review',
  className = '',
}) {
  const [open, setOpen] = React.useState(false);
  const list = problems || [];
  const n = count || list.length;
  React.useEffect(() => { if (!n) setOpen(false); }, [n]);
  if (!n) return null;
  const label = `${n} ${noun}${n === 1 ? '' : 's'}`;
  const expandable = list.length > 0;
  const press = () => {
    if (expandable) setOpen(o => !o);
    else if (onReview) onReview();
  };
  const jump = (p) => {
    setOpen(false);
    if (onJump) onJump(p);
    else if (typeof document !== 'undefined' && p.targetId) {
      const el = document.getElementById(p.targetId);
      if (el) el.focus();
    }
  };
  return (
    <div className={`odc-errsum-wrap${className ? ' ' + className : ''}`}>
      <button type="button" className="odc-errsum"
        aria-label={`${label}, ${action.toLowerCase()}`}
        aria-expanded={expandable ? open : undefined}
        onClick={press}>
        <span className="material-icons" aria-hidden="true">error_outline</span>
        <span className="odc-errsum-count" aria-hidden="true">{label}</span>
        <span className="odc-errsum-sep" aria-hidden="true">·</span>
        <span className="odc-errsum-act" aria-hidden="true">{action}</span>
        {expandable ? <span className="material-icons odc-errsum-chev" aria-hidden="true">{open ? 'expand_less' : 'expand_more'}</span> : null}
      </button>
      {expandable && open ? (
        <div className="odc-errsum-panel">
          <div className="odc-errsum-panel-head">Fix these to save</div>
          <ul className="odc-errsum-list">
            {list.map((p, i) => (
              <li key={p.targetId || i}>
                <button type="button" className="odc-errsum-item" onClick={() => jump(p)}>
                  <span className="odc-errsum-item-txt">
                    <span className="odc-errsum-item-lbl">{p.label}</span>
                    {p.section ? <span className="odc-errsum-item-sec">{p.section}</span> : null}
                  </span>
                  <span className="material-icons odc-errsum-item-go" aria-hidden="true">arrow_forward</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}
