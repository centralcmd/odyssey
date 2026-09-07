/**
 * Odyssey DS — ContactSelect
 * The one control for **picking a contact**, wherever a record links to one:
 * a transaction's counterparty, a file's issuer, a subscription's biller, an
 * extracted statement merchant. It replaces the plain `Select`-of-names that
 * each of those surfaces used to hand-roll, so every contact field searches,
 * shows each candidate's ContactType glyph in its registry color, and — where
 * the caller allows it — creates a missing contact inline.
 *
 * It is not a new widget: it wraps the DS `Combobox` (typeahead, full keyboard,
 * accessible name, keyboard-operable clear) in the standard field chrome, the
 * same way `CustodianSelect` does for the account ↔ custodian link.
 *
 * Options come from `contacts` ({ id|contactId, name, type, archived }) — the
 * **active** ones only, since an archived contact must not be linkable; a value
 * already pointing at an archived contact stays resolvable so the trigger keeps
 * showing its name. A caller that already has display options (FilesTable's
 * `issuers`) may pass `options` instead.
 *
 * Inline create: with `allowCreate` (gate it on the caller's `contacts.create`
 * claim), the popover offers ONE create row per contact type — a contact is a
 * person or a company and the two are mutually exclusive — and `onCreate(name,
 * kind)` returns the created option so it can be selected in the same gesture.
 *
 * `bare` drops the label/help chrome for a control that sits in a table cell or
 * inside a caller-supplied FieldShell.
 */

const CS_FALLBACK_TYPES = {
  Merchant: { icon: 'storefront', color: 'oklch(0.79 0.115 188)', label: 'Merchant' },
  Person: { icon: 'person', color: 'oklch(0.80 0.15 150)', label: 'Person' },
  Organization: { icon: 'corporate_fare', color: 'oklch(0.72 0.16 295)', label: 'Organization' },
  Company: { icon: 'business', color: 'oklch(0.76 0.13 225)', label: 'Company' },
  Institution: { icon: 'account_balance', color: 'oklch(0.75 0.16 330)', label: 'Institution' },
  Other: { icon: 'category', color: 'oklch(0.74 0.02 250)', label: 'Other' },
};

function contactTypeMeta(typeKey) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const reg = NS.CONTACT_TYPES;
  if (reg) {
    const hit = reg.find((t) => t.key === typeKey);
    if (hit) return hit;
  }
  return CS_FALLBACK_TYPES[typeKey] || CS_FALLBACK_TYPES.Other;
}

/** The default create rows: an organization first (most linked contacts are), then a person. */
export const CONTACT_CREATE_KINDS = ['Organization', 'Person'].map((key) => {
  const m = CS_FALLBACK_TYPES[key];
  return { key, label: m.label, icon: m.icon };
});

export function ContactSelect({
  value,
  onChange,
  contacts,
  options: optionsProp,
  label = 'Contact',
  optional = false,
  required = false,
  placeholder = 'Search contacts…',
  emptyText,
  help,
  error,
  loading = false,
  disabled = false,
  bare = false,
  allowCreate = false,
  onCreate,
  createLabel = 'Add',
  createKinds,
  ariaLabel,
  className = '',
  id,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Combobox = NS.Combobox;

  // Contacts created from the create rows are kept here as well as handed to
  // the caller: a surface that passes pre-built `options` (or whose list is
  // rebuilt on its own schedule) would otherwise not carry the new contact yet,
  // and the field would clear itself the moment it was created.
  const [created, setCreated] = React.useState([]);
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;

  let options = optionsProp;
  if (!options) {
    const list = contacts || [];
    const idOf = (c) => c.id || c.contactId;
    const byId = {};
    list.forEach((c) => { byId[idOf(c)] = c; });
    options = list.filter((c) => !c.archived).map((c) => {
      const meta = contactTypeMeta(c.type);
      return { value: idOf(c), label: c.name, icon: meta.icon, iconColor: meta.color };
    });
    if (value && !options.some((o) => o.value === value) && byId[value]) {
      const c = byId[value];
      const meta = contactTypeMeta(c.type);
      options = [{ value, label: c.name, icon: meta.icon, iconColor: meta.color }, ...options];
    }
  }

  if (created.length) {
    const known = new Set(options.map((o) => o.value));
    options = [...created.filter((o) => !known.has(o.value)), ...options];
  }

  const kinds = allowCreate ? (createKinds || CONTACT_CREATE_KINDS) : undefined;
  const handleCreate = allowCreate && onCreate
    ? ((text, kind) => {
      const made = onCreate(text, kind || (kinds && kinds[0] ? kinds[0].key : 'Organization'));
      if (made == null) return made;
      const opt = typeof made === 'string' ? { value: made, label: text } : made;
      setCreated((prev) => [...prev, opt]);
      return opt;
    })
    : undefined;
  const msg = error || help || null;

  if (!Combobox) return null;

  const control = (
    <Combobox
      id={fieldId}
      value={value || ''}
      onChange={(v, opt) => onChange && onChange(v || '', opt)}
      options={options}
      placeholder={placeholder}
      clearable
      loading={loading}
      disabled={disabled}
      emptyText={emptyText || (handleCreate ? 'No matches — type to add one' : 'No contacts match')}
      onCreate={handleCreate}
      createLabel={createLabel}
      createKinds={kinds}
      ariaLabel={ariaLabel || (bare ? label : undefined)}
      ariaDescribedBy={msg ? helpId : undefined}
      invalid={!!error}
    />
  );

  if (bare) return control;

  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      <label className="odc-field-label" htmlFor={fieldId}>
        {label}
        {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
        {optional && !required ? <span className="odc-field-opt">Optional</span> : null}
      </label>
      {control}
      {msg ? (
        <div className={`odc-field-help${error ? ' error' : ''}`} id={helpId} role={error ? 'alert' : undefined}>
          {msg}
        </div>
      ) : null}
    </div>
  );
}
