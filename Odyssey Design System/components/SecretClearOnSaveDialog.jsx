/**
 * Odyssey DS — SecretClearOnSaveDialog
 * The gate in front of a PAGE SAVE that will clear a stored secret as a side
 * effect. Distinct from `SecretClearDialog`, which confirms an immediate,
 * single-field clear the user asked for directly: here the user asked to change
 * a *different* value (the SMTP host, the STARTTLS flag) and the clear is a
 * consequence of that change, committed in the same transaction.
 *
 * Two copy variants, chosen by `reason`, because the two triggers protect
 * against different things:
 *   `host`      a credential entered for one relay must never be presented to
 *               another — the new host would receive it in plaintext.
 *   `starttls`  a credential entered for an encrypted transport must never be
 *               replayed over a cleartext one — passive network position is
 *               then enough to harvest it.
 *
 * Three things the copy must say, and no dialog above it can: what is cleared,
 * that the clear and the save commit together or not at all, and — because this
 * gates a WHOLE-PAGE batch save — that Cancel discards nothing.
 */
export function SecretClearOnSaveDialog({
  open = true,
  reason = 'host',
  fromHost,
  toHost,
  secrets = [],
  reEnterAt = 'Credentials',
  pendingCount,
  confirmLabel,
  busy = false,
  onCancel,
  onConfirm,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Modal, Button } = NS;
  if (!Modal || !Button) return null;
  const starttls = reason === 'starttls';
  const list = secrets.length
    ? secrets.reduce((acc, s, i) => acc + (i === 0 ? '' : i === secrets.length - 1 ? ' and ' : ', ') + s, '')
    : 'the stored SMTP credential';
  return (
    <Modal
      open={open}
      title={starttls ? 'Turning STARTTLS off clears the stored SMTP credential' : 'Changing the SMTP host clears the stored SMTP credential'}
      icon={starttls ? 'lock_open' : 'key_off'}
      iconTone="error"
      onClose={onCancel}
      footer={
        <>
          <Button variant="text" onClick={onCancel}>Cancel</Button>
          <Button variant="danger" loading={busy} onClick={onConfirm}>
            {confirmLabel || (pendingCount ? `Save ${pendingCount} change${pendingCount === 1 ? '' : 's'} and clear` : 'Save and clear')}
          </Button>
        </>
      }
    >
      {starttls ? (
        <p className="odc-secret-dialog-p">
          The connection to {fromHost ? <b>{fromHost}</b> : 'the relay'} will no longer be encrypted.
          Anyone in a position to watch that traffic can read the credential and every message sent
          over it, including password-reset links.
        </p>
      ) : (
        <p className="odc-secret-dialog-p">
          Mail will be relayed through {toHost ? <b>{toHost}</b> : 'the new host'}
          {fromHost ? <> instead of <b>{fromHost}</b></> : null}. The SMTP client connects first and
          authenticates second, so whatever host is set here receives the stored credential.
        </p>
      )}
      <p className="odc-secret-dialog-p">
        <b>{list}</b> {secrets.length === 1 ? 'is' : 'are'} cleared by this save, in the same
        transaction — either both the change and the clear land, or neither does. Re-enter the
        credential at <b>{reEnterAt}</b> afterwards; until you do, transactional mail is sent
        unauthenticated and any relay that requires a login will reject it.
      </p>
      <p className="odc-secret-dialog-p">
        <b>Cancel discards nothing.</b> Every edit on this page stays exactly as you left it and
        nothing is saved, so you can put this one value back by hand and save the rest.
      </p>
    </Modal>
  );
}
