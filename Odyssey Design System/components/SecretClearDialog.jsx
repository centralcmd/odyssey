/**
 * Odyssey DS — SecretClearDialog
 * The confirmation in front of clearing a stored secret. One dialog, two copy
 * variants, chosen by `kind` — because the two kinds carry different losses and
 * a single wording would either overstate one or understate the other.
 *
 *   `credential`  the value is gone from the store and the feature stops until a
 *                 new one is entered. Recoverable: re-issue it at the provider
 *                 and paste it back.
 *   `derivation`  the same, plus the part that cannot be walked back — everything
 *                 already derived with this key stops being re-derivable, for
 *                 good. There is no provider to re-issue it from.
 *
 * The irreversible clause is a coral callout rather than the amber advisory band
 * used elsewhere: an advisory is about cost, and this is the loss itself.
 *
 * One confirm button, not a typed confirmation. The action is already two clicks
 * behind Replace/Clear, the dialog states the consequence in its own words, and
 * a value that cannot be read back cannot be re-typed to prove intent —
 * transcription would only be theatre.
 *
 * Reach for it from `SecretSettingField`'s `onClear`; the row deliberately does
 * not confirm on its own, so the copy stays with the caller that knows the key.
 */
export function SecretClearDialog({
  open = true,
  label,
  secretKey,
  kind = 'credential',
  affects,
  unreadable = false,
  confirmLabel = 'Clear value',
  busy = false,
  onCancel,
  onConfirm,
  children,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Modal, Button } = NS;
  if (!Modal || !Button) return null;
  const derivation = kind === 'derivation';
  return (
    <Modal
      open={open}
      title={`Clear ${label}?`}
      icon={derivation ? 'key_off' : 'delete_outline'}
      iconTone="error"
      onClose={onCancel}
      footer={
        <>
          <Button variant="text" onClick={onCancel}>Cancel</Button>
          <Button variant="danger" loading={busy} onClick={onConfirm}>{confirmLabel}</Button>
        </>
      }
    >
      {secretKey ? <div className="odc-secret-dialog-key">{secretKey}</div> : null}
      <p className="odc-secret-dialog-p">
        The stored value is deleted from the settings store. Nothing recovers it from a database
        backup — the ciphertext is unreadable without the encryption key ring.
      </p>
      {unreadable ? (
        <p className="odc-secret-dialog-p">
          This row is already <b>unreadable</b>, so clearing it changes nothing that is currently
          working. It removes the failing row so a new value can be entered.
        </p>
      ) : (
        <p className="odc-secret-dialog-p">
          {affects ? <>{affects} </> : null}
          The row returns to <b>not set</b>, and behaves as it did before any value was configured.
        </p>
      )}
      {children}
      {derivation ? (
        <div className="odc-secret-irrev">
          <span className="material-icons" aria-hidden="true">report</span>
          <div>
            <b>This key cannot be re-issued.</b> Anything already derived with it can never be
            re-derived — clearing it is permanent, and entering a new value does not restore the old
            relationships. Export the current value first if you have any way to.
          </div>
        </div>
      ) : null}
    </Modal>
  );
}
