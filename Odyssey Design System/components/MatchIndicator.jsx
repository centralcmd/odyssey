/**
 * Odyssey DS — MatchIndicator
 * The per-cell annotation for AI merchant/category matching: it states, AS TEXT,
 * where a cell's value came from and how confident the match was. Never colour or
 * a meter alone — the words carry the meaning; the small leading glyph only tints.
 *
 * Five states (the `state` prop):
 *   • 'ai'         — auto-linked LLM suggestion ≥ the auto-link threshold.
 *                    "Suggested by AI · 91%" (pass `confidence` 0–1).
 *   • 'created'    — the reviewer created this contact inline. "Created here".
 *   • 'manual'     — the reviewer picked / applied it. "You chose".
 *   • 'none'       — nothing matched, or the value was cleared. "No match".
 *                    With `onCreate` + `createName`, a no-match isn't a dead end:
 *                    the extracted-but-unmatched string (e.g. the raw merchant
 *                    "Nopa") is offered as an inline Create "…" action.
 *   • 'suggestion' — a SUB-threshold match: shown but NOT auto-linked. A two-row
 *                    affordance — "Suggested by AI · 42%" over an inline
 *                    "Use ‹name›" action (→ `onApply`, label via `applyLabel`)
 *                    and a dismiss (→ `onDismiss`). Pass `name` (+ `confidence`).
 *
 * Confidence renders as a tabular % (the textual level); the band word ("High
 * match" / "Good match" / "Low match") rides along in the accessible name so the
 * level is available to a screen reader without cluttering the row. All text is
 * AA (≥4.5:1) — muted variants use text-secondary, never the disabled alpha.
 */
export function MatchIndicator({
  state = 'none',
  confidence = null,
  name,
  applyLabel = 'Use',
  onApply,
  onDismiss,
  createName,
  createLabel = 'Create',
  onCreate,
  size,
  className = '',
  id,
}) {
  const pct = confidence == null ? null : Math.round(confidence * 100);
  const band = pct == null ? null : pct >= 85 ? 'High match' : pct >= 60 ? 'Good match' : 'Low match';
  const cls = ['odc-match', state, size === 'sm' ? 'sm' : '', className].filter(Boolean).join(' ');

  // Sub-threshold suggested-but-not-linked. Split into a status row ("Suggested
  // by AI · 42%") over an action row ("+ Use ‹name›" + dismiss) — the same
  // status-over-action shape as No match → Create, so a narrow cell shows the
  // full name in the action instead of one massive line.
  if (state === 'suggestion') {
    return (
      <span className={cls} id={id}>
        <span className="odc-match-sugg-head">
          <span className="material-icons odc-match-ic" aria-hidden="true">auto_awesome</span>
          <span className="odc-match-txt">Suggested by AI</span>
          {pct != null ? <span className="odc-match-pct">· {pct}%</span> : null}
          {band ? <span className="sr-only"> — {band}, below the auto-link threshold</span> : null}
        </span>
        {(onApply || onDismiss) ? (
          <span className="odc-match-sugg-actions">
            {onApply ? (
              <button
                type="button"
                className="odc-match-act"
                onClick={onApply}
                aria-label={`${applyLabel} ${name}`}
              >
                <span className="material-icons" aria-hidden="true">add</span>
                <span className="odc-match-act-txt">{`${applyLabel} ${name}`}</span>
              </button>
            ) : null}
            {onDismiss ? (
              <button
                type="button"
                className="odc-match-x"
                aria-label={name ? `Dismiss suggestion ${name}` : 'Dismiss suggestion'}
                onClick={onDismiss}
              >
                <span className="material-icons" aria-hidden="true">close</span>
              </button>
            ) : null}
          </span>
        ) : null}
      </span>
    );
  }

  const META = {
    ai:      { icon: 'auto_awesome', label: 'Suggested by AI' },
    created: { icon: 'add_circle',   label: 'Created here' },
    manual:  { icon: 'edit',         label: 'You chose' },
    none:    { icon: 'remove',       label: 'No match' },
  };
  const m = META[state] || META.none;

  // 'none' + an extracted-but-unmatched string → offer to create it as a new
  // record inline (Create "Nopa"), so a no-match leads somewhere.
  const offerCreate = state === 'none' && !!onCreate && !!createName;

  return (
    <span className={cls} id={id}>
      <span className="material-icons odc-match-ic" aria-hidden="true">{m.icon}</span>
      <span className="odc-match-txt">{m.label}</span>
      {state === 'ai' && pct != null ? <span className="odc-match-pct">· {pct}%</span> : null}
      {state === 'ai' && band ? <span className="sr-only"> — {band}</span> : null}
      {offerCreate ? (
        <span className="odc-match-createline">
          <button
            type="button"
            className="odc-match-create"
            onClick={onCreate}
            aria-label={`${createLabel} ${createName}`}
          >
            <span className="material-icons" aria-hidden="true">add</span>
            <span className="odc-match-create-txt">{`${createLabel} "${createName}"`}</span>
          </button>
        </span>
      ) : null}
    </span>
  );
}
