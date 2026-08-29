/**
 * Odyssey DS — FieldShell
 * The labelled-field wrapper that every form control shares: the label row
 * (with the required `*` / muted "Optional" marker, and an optional right-aligned
 * `aside` slot — e.g. a character counter), the control itself (`children`), and
 * the helper / error line below.
 *
 * This is the composition primitive behind `Field`, `AmountField`, `NoteField`
 * and `NumberField` — and the one to reach for when you need to label a control
 * the kit doesn't wrap yet (a `Combobox`, a `MultiSelect`, a segmented control,
 * a locked-value display, an upload dropzone). It replaces the hand-rolled
 * `.field` + `.label` + `.atm-opt` + `.helper`/`aam-err` markup scattered across
 * the dialogs, so the label, optional hint and error line read identically
 * everywhere.
 *
 *   <FieldShell label="Insured account" htmlFor="ins-acct" optional help={err}>
 *     <Combobox id="ins-acct" … />
 *   </FieldShell>
 *
 * Pass `htmlFor` matching your control's `id` so the label is associated and the
 * helper line gets `id="<htmlFor>-help"` for the control to point `aria-describedby` at.
 */
export function FieldShell({
  label,
  htmlFor,
  required = false,
  optional = false,
  help,
  error,
  aside,
  children,
  className = '',
  id,
  ...rest
}) {
  const helpId = htmlFor ? `${htmlFor}-help` : undefined;
  const errId = helpId ? `${helpId}-error` : undefined;
  const labelNode = label ? (
    <label className="odc-field-label" htmlFor={htmlFor}>
      {label}
      {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
      {optional ? <span className="odc-field-opt">Optional</span> : null}
    </label>
  ) : null;
  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`} id={id} {...rest}>
      {(label || aside) ? (
        aside ? (
          <div className="odc-field-head">
            {labelNode || <span />}
            {aside}
          </div>
        ) : labelNode
      ) : null}
      {children}
      {help ? <div className="odc-field-help" id={helpId}>{help}</div> : null}
      {error ? <div className="odc-field-help error" id={help ? errId : helpId} role="alert">{error}</div> : null}
    </div>
  );
}
