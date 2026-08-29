/**
 * Odyssey DS — SettingField
 * One setting as a self-contained field block, in the MudBlazor
 * `Variant.Outlined` shape: the label sits **on** the field's outline, the
 * control sits inside it, and the helper line below carries the description and
 * the "last changed" stamp as plain text.
 *
 * This is the scaffold for the System settings and Preferences catalogues, where
 * `SettingRow` — icon tile + label + description on the left, control on the
 * right — spends a full card's width on one value. `SettingField` folds the
 * label, control, description and provenance into a single ~half-width block, so
 * a section card can hold a two-column grid of related settings instead of a
 * stack of one-setting cards.
 *
 *   <SettingField label="Session timeout" htmlFor="s-timeout"
 *     help="Idle sessions are signed out after this long."
 *     meta="Last changed by A. Holm, 12 Mar 2026.">
 *     <NumberField id="s-timeout" value={v} unit="min" align="right" onChange={set} />
 *   </SettingField>
 *
 * ## The outline is a real `fieldset`/`legend`
 * Not a floating `<span>` over a border: the browser cuts the notch itself, so
 * the gap tracks the label's own text metrics at any font size or zoom, and
 * nothing has to be painted to match the card behind it. The child control's own
 * border, background and padding are flattened by the sheet — inside the frame
 * the control is just its value.
 *
 * ## One helper line, always visible
 * `help` (what the setting does) and `meta` (who last changed it, and when) render
 * as one sentence-flowing line, `meta` slightly dimmer. Neither is behind a
 * disclosure: at a full page of settings, a `?` per row is a page of buttons
 * nobody presses, and provenance is what an admin actually scans for.
 *
 * `error` renders above the helper line and turns the outline coral; it does not
 * displace the description, so the reader keeps the definition while fixing the
 * value. `dirty` shows the unsaved-change dot on the helper line.
 *
 * ## `advisory` — informational, never blocking
 * An amber band below the helper line. For a value that will save but carries a
 * cost or looks wrong: a raise that spends memory, payload or third-party
 * budget; a check the server can only make heuristically. It carries
 * `role="status"`, not `role="alert"` — it must not interrupt, and it must never
 * gate the primary action. If the value cannot be accepted, that is an `error`,
 * not an advisory.
 *
 * The band opens with the literal word **"Advisory"**, so its meaning does not
 * depend on the tint or on the glyph (which is `aria-hidden`) — the same reason
 * a chip's meaning has to live in its text. The word is set in text-primary
 * rather than amber: amber text on an amber tint clears 4.5:1 in neither theme,
 * so the amber carries the icon and the border only. `aria-invalid` is NOT set:
 * the row is valid. The band's `id` is appended to the control's
 * `aria-describedby` via `describedBy`, so a screen-reader user hears it as part
 * of the field rather than only on walking past it.
 *
 * ## `bound` — a setting that moves one way only
 * `"lower-only"` / `"raise-only"` renders a small marker in the outline beside
 * the label, because that is where the bound lives. Use it where the opposite
 * direction is not merely discouraged but refused: a cap whose cost survives
 * being lowered back, or a control that fails open when its table fills, so a
 * smaller number weakens it. The reason belongs in `help`; the marker only says
 * which way.
 *
 * Pass `htmlFor` matching the control's `id` (the label is then a real `<label>`
 * for it). For a control with no single focusable input, pass `labelId` instead
 * and point the control's `aria-labelledby` at it.
 */
export function SettingField({
  label,
  htmlFor,
  labelId,
  help,
  meta,
  error,
  advisory,
  bound,
  dirty = false,
  wide = false,
  className = '',
  id,
  children,
  ...rest
}) {
  const autoId = React.useId();
  const lid = labelId || (htmlFor ? `${htmlFor}-label` : `${autoId}-label`);
  const helpId = htmlFor ? `${htmlFor}-help` : undefined;
  const advId = `${htmlFor || autoId}-advisory`;
  const BOUND = { 'lower-only': 'lower only', 'raise-only': 'raise only' };
  const labelText = htmlFor ? (
    <label className="odc-sfield-label" id={lid} htmlFor={htmlFor}>{label}</label>
  ) : (
    <span className="odc-sfield-label" id={lid}>{label}</span>
  );
  return (
    <div className={`odc-sfield${wide ? ' wide' : ''}${className ? ' ' + className : ''}`} id={id} {...rest}>
      <fieldset className={`odc-sfield-frame${error ? ' error' : ''}${advisory ? ' advised' : ''}`}>
        <legend className="odc-sfield-legend">
          {labelText}
          {BOUND[bound] ? <span className="odc-sfield-bound">{BOUND[bound]}</span> : null}
        </legend>
        <div className="odc-sfield-ctrl">{children}</div>
      </fieldset>
      {error ? <div className="odc-sfield-err" role="alert">{error}</div> : null}
      {(help || meta || dirty) ? (
        <div className="odc-sfield-help" id={helpId}>
          {help ? <span>{help} </span> : null}
          {meta ? <span className="odc-sfield-stamp">{meta}</span> : null}
          {dirty ? <span className="odc-setting-dot" title="Unsaved change" aria-hidden="true" /> : null}
          {dirty ? <span className="odc-sr-only"> (unsaved change)</span> : null}
        </div>
      ) : null}
      {advisory ? (
        <div className="odc-sfield-advisory" id={advId} role="status">
          <span className="material-icons" aria-hidden="true">info</span>
          <div><b className="odc-sfield-advisory-t">Advisory</b> {advisory}</div>
        </div>
      ) : null}
    </div>
  );
}
