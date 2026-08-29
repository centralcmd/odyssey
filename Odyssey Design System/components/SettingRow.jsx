/**
 * Odyssey DS — SettingRow
 * One setting as an outlined card row: a tide-tinted icon tile + label +
 * one-line description on the left, and exactly one control on the right
 * (Switch, Select, Field, Button…). The scaffold behind every row on the
 * Preferences (per-user) and System settings (workspace) pages — formerly
 * duplicated there as PrefCard / SettingCard.
 *
 * `danger` coral-tints the icon tile for destructive settings. Styled by the
 * kit sheet's .pref-* classes (in the styles.css closure) on an .odc-card
 * surface. Reads the Card atom off the DS namespace at render time.
 *
 * ## The control column vs the `footer` slot
 * The row is `align-items: center` with a `flex: none` control column and no
 * wrap, so the column suits controls of a fixed, predictable width: `Switch`,
 * `NumberField`, `CapacityField`, `Select`, a `Button`.
 *
 * Put the control in `footer` instead whenever its natural width is set by its
 * *content* rather than by the design — a URL, a name, an address, anything
 * free-text. A ~40-character value in the control column either overflows or
 * shoves the row about the moment its error line appears. The footer renders as
 * a tinted well across the full card, below the description: the tint is the
 * separator, so nothing is indented to the title and no divider is drawn.
 *
 * The slot also takes a cross-field or round-trip error that can't fit the
 * control column. One row, one control — a footer control and a column control
 * are mutually exclusive.
 *
 * ## `warning` — advisory, not blocking
 * An amber band below the row, plus a glyph beside the title. Use it for a
 * value that saved (or will save) but looks wrong: a check the server can only
 * make heuristically, a setting that contradicts a neighbouring one. It carries
 * `role="status"`, not `role="alert"` — it must not interrupt, and it must
 * never gate the primary action. If the value cannot be accepted, that is an
 * `error` on the control, not a warning here.
 *
 * `dirty` is the unsaved-change dot. It sits with the row title rather than in
 * the control column, so it stays attached to the setting whether the control
 * is in the column or in the footer.
 */
export function SettingRow({ icon, title, desc, descId, titleId, danger, dirty, warning, footer, children }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Card } = NS;
  if (!Card) return null;
  return (
    <Card outlined flush>
      <div className="card-body pref-row">
        <div className="pref-main">
          <span className={`pref-ic ${danger ? 'danger' : ''}`.trim()}>
            <span className="material-icons" aria-hidden="true" style={{ fontSize: 20 }}>{icon}</span>
          </span>
          <div className="pref-text">
            <div className="pref-ttl" id={titleId}>
              {title}
              {warning ? <span className="material-icons odc-setting-warn-ic" aria-hidden="true">warning_amber</span> : null}
              {dirty ? <span className="odc-setting-dot" title="Unsaved change" aria-hidden="true" /> : null}
              {dirty ? <span className="odc-sr-only"> (unsaved change)</span> : null}
            </div>
            {desc && <div className="pref-desc" id={descId}>{desc}</div>}
          </div>
        </div>
        {children ? <div className="pref-control">{children}</div> : null}
      </div>
      {footer ? <div className="odc-setting-footer">{footer}</div> : null}
      {warning ? (
        <div className="odc-setting-warn" role="status">
          <span className="material-icons" aria-hidden="true">warning_amber</span>
          <div>{warning}</div>
        </div>
      ) : null}
    </Card>
  );
}
