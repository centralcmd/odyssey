/**
 * Odyssey DS — ContactAliases
 * The **Aliases** section of a contact record: the ordered list of alternative
 * names a contact is actually known by (a nickname, a maiden name, a former
 * company name, an abbreviation), each carrying an optional **free-text label**
 * saying what kind of alias it is.
 *
 * Aliases are *names*, not contact methods, so this is its own section — placed
 * ABOVE `Contact information` under its own `SectionDivider`, which the record
 * card emits (the card owns the headings; this component renders bare).
 *
 * It introduces **no new widget type**. Every alias is a tile in this
 * component's own grid, reusing the record-tile shape and the `ActionMenu` slot
 * the address / email / phone tiles use — deliberately NOT a `Chip` (a ~23px
 * nowrap pill cannot host a ⋯ menu at a 24×24 target) and NOT the contact-method
 * tile grid (whose model is positional, carries Primary, and counts only the
 * three method collections). The add / edit dialog is the DS `Modal` + two
 * `Field`s.
 *
 * Ordering is **the order the caller passes**, i.e. the order the API returned:
 * a .NET/JS comparer cannot reproduce a MariaDB `_ci` ordering on accented
 * input, so no client re-sort happens here.
 *
 * Write contract — `onAdd(value, label)` / `onEdit(id, value, label)` return
 * either nothing (accepted) or an **error string**, which the dialog renders
 * inline on the value field, sets `aria-invalid` on it and moves focus there,
 * with no toast. That is how a duplicate (409), a cap breach (422) and a
 * validation failure (400) surface. Duplicates are pre-checked here the same
 * way the service does — case- **and accent**-insensitively, on the value
 * alone, so two labels cannot smuggle in a second "Hansen".
 *
 * Menu gates are distinct on purpose: the copy items are unconditional (so the
 * trigger is always rendered and the menu is never empty), Edit needs
 * `canUpdate` **and** a non-archived contact, Delete needs `canDelete` — a
 * principal holding `contacts.update` without `contacts.delete` must not be
 * offered a Delete that 403s. Deleting an alias is immediate: no confirm
 * dialog, matching the sibling tiles, since an alias is cheap to re-add.
 *
 * a11y: outcomes announce through one polite region owned here (messages carry
 * an invisible nonce so an identical string re-announces); in the Blazor host
 * this component's announcements route to the card's single `LiveAnnouncer`
 * instead — pass `onAnnounce` and the local region stays silent. A failed
 * delete and the 404 that closes the dialog are announced explicitly: they have
 * neither a dialog to hold open nor a field to focus.
 */

export const CONTACT_ALIAS_CAP = 32;
export const CONTACT_ALIAS_MAX = 128;
export const CONTACT_ALIAS_LABEL_MAX = 64;

/** Case- and accent-insensitive equality — what the service, the unique index and this pre-check agree on. */
export function aliasEquals(a, b) {
  const x = String(a == null ? '' : a).trim();
  const y = String(b == null ? '' : b).trim();
  try { return x.localeCompare(y, undefined, { sensitivity: 'base' }) === 0; }
  catch (e) { return x.toLowerCase() === y.toLowerCase(); }
}

/** Trim + collapse runs of whitespace — the canonical form stored on write. */
export function canonicalAlias(v) {
  return String(v == null ? '' : v).replace(/\s+/g, ' ').trim();
}

const ALIAS_CONTROL_CHARS = /[\u0000-\u001F\u007F]/;

export function ContactAliases({
  aliases = [],
  canCreate = false,
  canUpdate = false,
  canDelete = false,
  archived = false,
  cap = CONTACT_ALIAS_CAP,
  addRequest,
  onConsumeAddRequest,
  onAdd,
  onEdit,
  onDelete,
  onAnnounce,
  className = '',
}) {
  const { useState, useRef, useEffect } = React;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Modal, Field, Button, ActionMenu, MIcon } = NS;

  const [dialog, setDialog] = useState(null); // { mode:'add'|'edit', id, value, label }
  const [error, setError] = useState(null);
  const [copiedId, setCopiedId] = useState(null);
  const [live, setLive] = useState('');
  const nonce = useRef(0);
  const copyTimer = useRef(null);
  const valueRef = useRef(null);
  const focusedFor = useRef(null);

  const say = (text) => {
    if (onAnnounce) { onAnnounce(text); return; }
    nonce.current += 1;
    setLive(`${text}${'\u200B'.repeat((nonce.current % 4) + 1)}`);
  };

  // The card's ⋯ menu drives adding, exactly as it does for addresses / emails
  // / phones: `addRequest` is a { nonce } token, consumed once acted on.
  useEffect(() => {
    if (addRequest && canCreate && !archived) {
      setError(null);
      setDialog({ mode: 'add', value: '', label: '' });
      onConsumeAddRequest && onConsumeAddRequest();
    }
  }, [addRequest && addRequest.nonce]);

  useEffect(() => () => clearTimeout(copyTimer.current), []);

  // The dialog takes focus on the value field ONCE, when it opens — guarded by a
  // ref, not by an effect dependency, so typing in the label field never yanks
  // focus back to the value field. (Modal owns the trap and restores focus to
  // the invoking ⋯ trigger on close.)
  useEffect(() => {
    if (!dialog) { focusedFor.current = null; return; }
    const key = `${dialog.mode}:${dialog.id || 'new'}`;
    if (focusedFor.current === key) return;
    focusedFor.current = key;
    setTimeout(focusValue, 0);
  });

  const focusValue = () => { const el = valueRef.current && valueRef.current.querySelector('input'); if (el) el.focus(); };
  const reject = (message) => { setError(message); setTimeout(focusValue, 0); };
  // Stable: `Modal`'s focus/trap effect keys on `onClose`, so a fresh arrow per
  // render would re-run it on every keystroke and yank focus back to the first
  // body input (the value field) while the user types in the label.
  const close = React.useCallback(() => { setDialog(null); setError(null); }, []);

  const copy = (text, id) => {
    const done = () => {
      setCopiedId(id);
      clearTimeout(copyTimer.current);
      copyTimer.current = setTimeout(() => setCopiedId(null), 1400);
    };
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(done, done);
    else done();
  };

  const submit = () => {
    const value = canonicalAlias(dialog.value);
    const label = canonicalAlias(dialog.label) || null;
    if (!value) { reject('Enter an alias.'); return; }
    if (value.length > CONTACT_ALIAS_MAX) { reject(`Keep the alias to ${CONTACT_ALIAS_MAX} characters or fewer.`); return; }
    if (ALIAS_CONTROL_CHARS.test(value) || (label && ALIAS_CONTROL_CHARS.test(label))) { reject('Remove the line breaks and control characters.'); return; }
    if (label && label.length > CONTACT_ALIAS_LABEL_MAX) { reject(`Keep the label to ${CONTACT_ALIAS_LABEL_MAX} characters or fewer.`); return; }
    // Uniqueness is per contact, on the VALUE alone — the label is not part of the key.
    const clash = aliases.some((a) => a.id !== dialog.id && aliasEquals(a.value, value));
    if (clash) { reject('This contact already has that alias.'); return; }
    if (dialog.mode === 'add' && aliases.length >= cap) { reject(`A contact can have at most ${cap} aliases.`); return; }

    const problem = dialog.mode === 'add'
      ? (onAdd && onAdd(value, label))
      : (onEdit && onEdit(dialog.id, value, label));
    if (typeof problem === 'string' && problem) { reject(problem); return; }
    setDialog(null);
    setError(null);
    say(dialog.mode === 'add' ? `Alias ${value} added.` : `Alias ${value} updated.`);
  };

  const remove = (a) => {
    const problem = onDelete && onDelete(a.id);
    // A failed delete has no dialog and no field, so it is announced.
    if (typeof problem === 'string' && problem) { say(problem); return; }
    say(`Alias ${a.value} deleted.`);
  };

  const empty = canCreate && !archived
    ? 'No aliases yet — use the ⋯ menu to add one.'
    : 'No aliases.';

  return (
    <div className={`odc-aliases${className ? ' ' + className : ''}`}>
      {aliases.length === 0 ? (
        <p className="odc-aliases-empty">{empty}</p>
      ) : (
        <div className="odc-alias-grid">
          {aliases.map((a) => {
            const items = [
              { icon: copiedId === a.id ? 'check' : 'content_copy', label: copiedId === a.id ? 'Copied' : 'Copy alias', onClick: () => copy(a.value, a.id) },
              ...(canUpdate && !archived ? [{ icon: 'edit', label: 'Edit', onClick: () => { setError(null); setDialog({ mode: 'edit', id: a.id, value: a.value, label: a.label || '' }); } }] : []),
              { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => copy(a.id, `${a.id}-id`) },
              ...(canDelete && !archived ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: () => remove(a) }] : []),
            ];
            return (
              <div key={a.id} className="odc-alias-tile">
                <span className="odc-alias-menu">
                  {ActionMenu ? <ActionMenu items={items} ariaLabel={`Actions for alias: ${a.value}`} /> : null}
                </span>
                <div className="odc-alias-top">
                  <span className="odc-alias-ic" aria-hidden="true">{MIcon ? <MIcon name="badge" size={15} /> : null}</span>
                  <span className="odc-alias-kind">Alias</span>
                </div>
                <div className="odc-alias-value" title={a.value}>{a.value}</div>
                {a.label ? <div className="odc-alias-foot" title={a.label}>{a.label}</div> : null}
              </div>
            );
          })}
        </div>
      )}

      {dialog && Modal && Field ? (
        <Modal
          title={dialog.mode === 'edit' ? 'Edit alias' : 'New alias'}
          subtitle="Another name this contact is known by."
          icon="badge"
          onClose={close}
          footer={<React.Fragment>
            <Button variant="text" onClick={close}>Cancel</Button>
            <Button variant="filled" color="primary" icon={dialog.mode === 'edit' ? 'check' : 'add'} onClick={submit}>
              {dialog.mode === 'edit' ? 'Save changes' : 'Create alias'}
            </Button>
          </React.Fragment>}>
          <div className="odc-alias-form" ref={valueRef}>
            <Field label="Alias" required maxLength={CONTACT_ALIAS_MAX}
              value={dialog.value}
              onChange={(v) => { setDialog((d) => ({ ...d, value: v })); if (error) setError(null); }}
              error={error || undefined}
              placeholder="e.g. Kari"
              help={error ? undefined : 'The name to also find this contact by'} />
            <Field label="Label" optional maxLength={CONTACT_ALIAS_LABEL_MAX}
              value={dialog.label}
              onChange={(v) => setDialog((d) => ({ ...d, label: v }))}
              placeholder="e.g. maiden name"
              help="Optional — e.g. maiden name, nickname, trading as" />
          </div>
        </Modal>
      ) : null}

      {onAnnounce ? null : <div className="odc-sr-only" role="status" aria-live="polite">{live}</div>}
    </div>
  );
}
