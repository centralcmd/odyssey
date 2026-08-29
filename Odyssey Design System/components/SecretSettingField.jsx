/**
 * Odyssey DS — SecretSettingField
 * One encrypted, write-only credential in the SettingField shape: label on the
 * outline, value area inside, description + last-changed stamp on the helper
 * line below. The counterpart to `SettingField` for values the API stores but
 * never returns — the settings store's secret rows.
 *
 * ## Three states, one frame
 * The store answers with one of three results, and each is a different thing to
 * tell an administrator:
 *
 *   `found`       a fixed-length dot mask + Replace / Clear. The mask is always
 *                 the same length, because the real length is itself a
 *                 disclosure. Screen readers get "Value stored, hidden" — a run
 *                 of bullets says nothing.
 *   `not-set`     the entry input, inline and immediately typeable. This is the
 *                 one state with nothing to protect and something to do, so it
 *                 costs no click. `consequence` says what is not working
 *                 meanwhile, in the amber advisory band.
 *   `unreadable`  coral outline, "Cannot be decrypted", Replace / Clear. The row
 *                 the whole encrypted store exists to make legible instead of
 *                 silent: the value is present but this instance's key ring
 *                 cannot open it, so the consumer is failing closed right now.
 *                 Name the consequence — `affects` — because the administrator
 *                 cannot infer it from the key's name.
 *
 * `unreadable` must never read as `not-set`. An absent row is a healthy
 * configuration; an undecryptable one is a fault, and the two states share no
 * colour, no glyph and no copy here.
 *
 * ## Replacing is an explicit act
 * From `found` or `unreadable` the input appears only on **Replace**, so a
 * stored credential cannot be overwritten by a stray keystroke, and the mask
 * stays on screen as the thing being replaced. The old value remains in force
 * until Save — cancelling costs nothing.
 *
 * ## Reveal
 * The entry input is `type="password"` with a reveal toggle. Revealing is not a
 * concession here: a mistyped relay password or API key fails silently and the
 * value can never be read back to check, so the one moment it is legible is
 * while it is being typed.
 *
 * ## The printable-ASCII rule
 * The store accepts `0x20`–`0x7E` only. That is checked here, as you type, and
 * named in the error — a human-chosen password at a third-party relay may well
 * contain a character outside the range, and a bare `400` from the API is not an
 * answer to an administrator. `allowNonAscii` opts a descriptor out where the
 * relaxation has been taken; the rule is then the server's alone.
 *
 * ## `kind`
 * `derivation` marks the value in the outline. A rotatable credential can be
 * re-issued at its provider; a derivation key cannot, and everything already
 * derived with it stops being re-derivable the moment it is lost. Nothing else
 * in the row differs — the distinction is carried by the marker and by the
 * confirmation copy in `SecretClearDialog`.
 */
export function SecretSettingField({
  label,
  secretKey,
  kind = 'credential',
  state = 'not-set',
  help,
  meta,
  error,
  consequence,
  affects,
  locked = false,
  lockNote = 'Requires the security settings claim.',
  allowNonAscii = false,
  maskLength = 16,
  placeholder = 'Paste or type the value',
  saveLabel = 'Save',
  busy = false,
  id,
  className = '',
  onSave,
  onClear,
  ...rest
}) {
  const { useState, useMemo, useRef, useEffect } = React;
  const autoId = React.useId();
  const inputId = `${id || autoId}-in`;
  const [replacing, setReplacing] = useState(false);
  const [value, setValue] = useState('');
  const [shown, setShown] = useState(false);
  const inputRef = useRef(null);

  const entering = !locked && (state === 'not-set' || replacing);

  useEffect(() => {
    if (replacing && inputRef.current) inputRef.current.focus();
  }, [replacing]);

  // Checked as typed rather than left to the store's 400: the constraint is
  // arbitrary from the administrator's side, so it has to be named where the
  // value is entered. The offending character is echoed because "somewhere in
  // what you pasted" is not actionable — and it is the administrator's own
  // input, on their own screen, not a stored value read back.
  const asciiError = useMemo(() => {
    if (allowNonAscii || !value) return null;
    const m = /[^\x20-\x7E]/.exec(value);
    if (!m) return null;
    const ch = m[0];
    const shownCh = ch === '\t' ? 'a tab' : ch.charCodeAt(0) < 0x20 ? 'a control character' : `“${ch}”`;
    return `Only printable ASCII — space to ~ — can be stored. This value contains ${shownCh}.`;
  }, [value, allowNonAscii]);

  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Button = NS.Button;
  const shownError = error || asciiError;
  const unreadable = state === 'unreadable';

  const cancel = () => { setReplacing(false); setValue(''); setShown(false); };
  const save = () => {
    if (!value || asciiError) return;
    if (onSave) onSave(value);
    setValue(''); setShown(false); setReplacing(false);
  };

  const valueArea = entering ? (
    <div className="odc-secret-entry">
      <input
        ref={inputRef}
        id={inputId}
        className="odc-input odc-secret-input"
        type={shown ? 'text' : 'password'}
        value={value}
        placeholder={placeholder}
        autoComplete="off"
        spellCheck={false}
        aria-invalid={asciiError ? 'true' : undefined}
        aria-describedby={`${inputId}-help`}
        onChange={(ev) => setValue(ev.target.value)}
        onKeyDown={(ev) => {
          if (ev.key === 'Enter') { ev.preventDefault(); save(); }
          if (ev.key === 'Escape' && replacing) { ev.preventDefault(); cancel(); }
        }}
      />
      <button
        type="button"
        className="odc-secret-eye"
        aria-label={shown ? 'Hide value' : 'Show value'}
        aria-pressed={shown}
        disabled={!value}
        onClick={() => setShown((s) => !s)}
      >
        <span className="material-icons" aria-hidden="true">{shown ? 'visibility_off' : 'visibility'}</span>
      </button>
    </div>
  ) : unreadable ? (
    <span className="odc-secret-bad">
      <span className="material-icons" aria-hidden="true">lock</span>
      Cannot be decrypted
    </span>
  ) : state === 'found' ? (
    <span className="odc-secret-mask" aria-hidden="true">{'\u2022'.repeat(maskLength)}</span>
  ) : (
    <span className="odc-secret-empty">Not set</span>
  );

  const actions = entering ? (
    <div className="odc-secret-actions">
      {replacing ? <Button variant="text" onClick={cancel}>Cancel</Button> : null}
      <Button variant="filled" loading={busy} disabled={!value || !!asciiError} onClick={save}>{saveLabel}</Button>
    </div>
  ) : locked ? null : (
    <div className="odc-secret-actions">
      {state === 'found' || unreadable ? (
        <Button variant={unreadable ? 'filled' : 'outlined'} onClick={() => setReplacing(true)}>Replace</Button>
      ) : null}
      {state === 'found' || unreadable ? (
        <Button variant="text" icon="delete_outline" onClick={onClear}>Clear</Button>
      ) : null}
    </div>
  );

  const helpLine = (
    <>
      {state === 'found' ? <span className="odc-sr-only">Value stored, hidden. </span> : null}
      {help ? <span>{help} </span> : null}
      {meta ? <span className="odc-sfield-stamp">{meta}</span> : null}
      {locked ? (
        <span className="odc-sfield-stamp odc-secret-lock">
          <span className="material-icons" aria-hidden="true">lock</span>
          {lockNote}
        </span>
      ) : null}
    </>
  );

  const unreadableMsg = unreadable && !error
    ? `Stored, but this instance cannot decrypt it — the encryption key ring has changed or been lost.${affects ? ' ' + affects : ''} Clearing and re-entering the value is the only fix.`
    : null;

  return (
    <div className={`odc-sfield odc-secret wide${className ? ' ' + className : ''}`} id={id} {...rest}>
      <fieldset className={`odc-sfield-frame${shownError || unreadable ? ' error' : ''}${unreadable ? ' unreadable' : ''}${consequence && !unreadable ? ' advised' : ''}`}>
        <legend className="odc-sfield-legend">
          {entering ? (
            <label className="odc-sfield-label" htmlFor={inputId}>{label}</label>
          ) : (
            <span className="odc-sfield-label">{label}</span>
          )}
          {kind === 'derivation' ? (
            <span className="odc-secret-kind" title="Derivation key — its loss cannot be undone by re-issuing it">Derivation key</span>
          ) : null}
        </legend>
        <div className="odc-secret-body">
          {valueArea}
          {actions}
        </div>
      </fieldset>
      {shownError ? <div className="odc-sfield-err" role="alert">{shownError}</div> : null}
      {unreadableMsg ? <div className="odc-sfield-err" role="alert">{unreadableMsg}</div> : null}
      <div className="odc-sfield-help" id={`${inputId}-help`}>
        {helpLine}
        {secretKey ? <span className="odc-sr-only"> Settings key {secretKey}.</span> : null}
      </div>
      {consequence && !unreadable ? (
        <div className="odc-sfield-advisory" role="status">
          <span className="material-icons" aria-hidden="true">info</span>
          <div><b className="odc-sfield-advisory-t">Advisory</b> {consequence}</div>
        </div>
      ) : null}
    </div>
  );
}
