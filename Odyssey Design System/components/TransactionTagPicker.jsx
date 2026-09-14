/**
 * Odyssey DS — TransactionTagPicker
 * The one control for **picking the transaction tag a record plans for** — the
 * budget item's tag being the case it was built for, where the tag is not a
 * label on the item but the item's identity: the row's name, its description
 * and the thing its actuals are summed from all come from the tag.
 *
 * It is not a new widget. It wraps the DS `Combobox` in a DS `FieldShell`, the
 * pairing every labelled picker in the kit already uses, and adds only what a
 * *required, identity-bearing* tag field needs:
 *
 *  • **The selected tag's description becomes the help line.** With no
 *    description it falls back to the standard helper, so the slot never
 *    renders empty — which is also what keeps the error line's id stable
 *    (`FieldShell` moves the error to `{htmlFor}-help-error` only while help is
 *    present, and `aria-describedby` names BOTH ids from the first render).
 *  • **Tags already planned for are offered but not selectable** — shown with
 *    the trailing words `in use`, never a colour or an icon alone.
 *  • **An archived tag reads `· Archived` in its option label** so the marker
 *    survives into the collapsed input and the accessible name. An archived tag
 *    is offered only when it is already this record's tag.
 *  • **Inline create, gated twice.** Pass `onCreateTag` only where the caller
 *    holds `transactions.tags.create`; omit it (and no create row renders) on a
 *    surface with no submit to stage a create against — an inline grid that
 *    saves on change must not put an unresolved tag on the wire.
 *  • **Duplicate names are refused before the create is staged.** Tag names are
 *    unique case-insensitively, archived rows included, so the picker checks
 *    the typed name against the list it already holds and renders the conflict
 *    at the field — naming the archived case, which has no inline remedy.
 *
 * States: `resolved` · `archived` · `inUse` · `noneSelectable` (every live tag
 * is already planned for, or none exists) · `unavailable` (the tag list failed
 * to load — distinct from "no tags", and the retry belongs here, not in a
 * create-a-tag empty state) · `error`.
 *
 * `bare` drops nothing structural — the FieldShell stays, because the error and
 * the description line are the point. For a table cell, pass `hideLabel` to keep
 * the real `<label for>` association while removing it visually: a grid whose
 * column header is hidden at narrow widths has no other accessible name.
 */

const TTP_DEFAULT_HELP = "Matched transactions become this item's actual.";
const ttpNorm = (s) => String(s || '').trim().toLowerCase();

export function TransactionTagPicker({
  value,
  onChange,
  tags = [],
  usedTagIds = [],
  label = 'Transaction tag',
  id,
  required = true,
  help,
  error,
  disabled = false,
  hideLabel = false,
  placeholder = 'Search tags…',
  canCreate = false,
  onCreateTag,
  loadFailed = false,
  onRetry,
  manageHref = '#/transaction-tags',
  manageLabel = 'Transaction tags',
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Combobox, FieldShell } = NS;
  const autoId = React.useId();
  const fieldId = id || `tagpick-${autoId}`;
  const [dupError, setDupError] = React.useState(null);
  const [created, setCreated] = React.useState([]);

  const all = [...created, ...tags];
  const byId = {};
  all.forEach((t) => { byId[t.id || t.transactionTagId] = t; });
  const idOf = (t) => t.id || t.transactionTagId;
  const used = new Set(usedTagIds.filter((x) => x && x !== value));
  const selected = value ? byId[value] : null;

  const optionOf = (t) => ({
    value: idOf(t),
    label: t.archived ? `${t.name} · Archived` : t.name,
    note: used.has(idOf(t)) ? 'in use' : undefined,
    disabled: used.has(idOf(t)),
    icon: 'local_offer',
  });

  let options = all.filter((t) => !t.archived).map(optionOf);
  // The record's own tag stays selectable even once archived — keeping a link
  // that already exists removes no capability; making a new one does.
  if (selected && selected.archived) options = [optionOf(selected), ...options];

  const selectable = options.filter((o) => !o.disabled).length;
  const noneSelectable = !loadFailed && selectable === 0 && !selected;
  const shownError = error || dupError;
  const helpText = (selected && selected.description) || help || TTP_DEFAULT_HELP;
  const helpId = `${fieldId}-help`;

  const handleCreate = canCreate && onCreateTag
    ? ((text) => {
      const clean = String(text || '').trim();
      if (!clean) return undefined;
      // Names are unique case-insensitively across ALL tags, archived included.
      const clash = all.find((t) => ttpNorm(t.name) === ttpNorm(clean));
      if (clash) {
        setDupError(clash.archived
          ? `A tag called “${clash.name}” already exists but is archived. Restore or rename it on ${manageLabel}.`
          : `A tag called “${clash.name}” already exists. Pick it from the list.`);
        return undefined;
      }
      setDupError(null);
      const made = onCreateTag(clean);
      if (made == null) return undefined;
      const tag = made.id || made.transactionTagId ? made : { id: made, name: clean };
      setCreated((prev) => [...prev, tag]);
      return optionOf(tag);
    })
    : undefined;

  if (!Combobox || !FieldShell) return null;

  const control = loadFailed ? (
    <div className="odc-tagpick-unavailable">
      <span>Unable to load the tag list.</span>
      {onRetry ? <button type="button" className="odc-tagpick-link" onClick={onRetry}>Retry</button> : null}
    </div>
  ) : noneSelectable ? (
    <div className="odc-tagpick-empty">
      <span>No tags left to plan for.</span>
      <a className="odc-tagpick-link" href={manageHref}>{manageLabel}</a>
    </div>
  ) : (
    <Combobox
      id={fieldId}
      value={value || ''}
      onChange={(v, opt) => { setDupError(null); if (onChange) onChange(v || '', opt); }}
      options={options}
      placeholder={placeholder}
      disabled={disabled}
      required={required}
      invalid={!!shownError}
      emptyText={handleCreate ? 'No matches — type a name to create one' : 'No tags match'}
      onCreate={handleCreate}
      createLabel="Create"
      // Constant from the first render, and naming BOTH the help and the error
      // line: the error renders at a different id while help is present, and a
      // description that only appears on a failed submit would never be read.
      ariaDescribedBy={`${helpId} ${helpId}-error`}
    />
  );

  return (
    <FieldShell
      label={label}
      htmlFor={fieldId}
      required={required}
      help={helpText}
      error={shownError}
      className={`odc-tagpick${hideLabel ? ' hide-label' : ''}${className ? ' ' + className : ''}`}
    >
      {control}
    </FieldShell>
  );
}
