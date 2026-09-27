/**
 * Odyssey DS — HomeownerAssociationSelect
 * The optional picker that links a real-estate property to the **homeowner
 * association** that administers it (borettslag / sameie / HOA) — an
 * Organization contact. Sibling of `CustodianSelect`: the DS `Combobox` in the
 * standard field chrome, clearable, optional.
 *
 * What differs from the custodian picker, per *Property Homeowner Association —
 * Backend (Draft v1)*:
 *   • **Organization only.** Options are active contacts of type
 *     `Organization` (R3/R4 — the server answers 400 archived, 422 not an
 *     organization). Inline create, when `onCreate` is passed, offers a
 *     single Organization row.
 *   • **Change-only rule (§8.3).** `storedValue` is the id the property holds
 *     today. When the current value equals it and that contact is now
 *     archived or no longer an Organization, the link is a legacy row: it is
 *     kept, shown with its state in text, and saving does not refuse it. The
 *     help line says how to change it (pick another or clear).
 *   • **Full-replace PUT.** Clearing sends null and removes the link; the
 *     caller must round-trip `value` to keep it.
 *
 * Props: `value` (id | '' | null), `onChange(id)` ('' on clear), `contacts`
 * ([{ id|contactId, name, type, archived }]), `storedValue`, `label`
 * (default "Homeowner association"), `help`, `error` (the server's message
 * keyed RealEstateDetails.HomeownerAssociationId), `loading`, `disabled`,
 * `onCreate(name, kind)`, `id`, `className`.
 */

const HOA_ORG_FALLBACK = { key: 'Organization', label: 'Organization', icon: 'corporate_fare', color: 'oklch(0.72 0.16 295)' };

function hoaOrgMeta() {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const reg = NS.CONTACT_TYPES;
  return (reg && reg.find((t) => t.key === 'Organization')) || HOA_ORG_FALLBACK;
}

export function homeownerAssociationState(contact, value, storedValue) {
  if (!value) return 'none';
  if (!contact) return 'missing';
  const legacy = value === storedValue;
  if (contact.archived) return legacy ? 'legacy-archived' : 'archived';
  if (contact.type !== 'Organization') return legacy ? 'legacy-type' : 'not-organization';
  return 'ok';
}

export function HomeownerAssociationSelect({
  value,
  onChange,
  contacts = [],
  storedValue = null,
  label = 'Homeowner association',
  placeholder = 'Search organizations…',
  help = 'The borettslag, sameie or HOA that administers it.',
  error,
  loading = false,
  disabled = false,
  onCreate,
  className = '',
  id,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Combobox = NS.Combobox;
  const org = hoaOrgMeta();
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;

  const idOf = (c) => c.id || c.contactId;
  const byId = {};
  contacts.forEach((c) => { byId[idOf(c)] = c; });
  const eligible = contacts.filter((c) => c.type === 'Organization' && !c.archived);
  const options = eligible.map((c) => ({ value: idOf(c), label: c.name, icon: org.icon, iconColor: org.color }));

  const current = value ? byId[value] : null;
  const state = homeownerAssociationState(current, value, storedValue);
  // The stored link stays resolvable even when it would no longer be eligible.
  if (value && current && !options.some((o) => o.value === value)) {
    const reg = NS.CONTACT_TYPES || [];
    const m = reg.find((t) => t.key === current.type) || org;
    options.unshift({ value, label: `${current.name}${current.archived ? ' (archived)' : ''}`, icon: m.icon, iconColor: m.color });
  }

  const name = current ? current.name : '';
  const legacyMsg = state === 'legacy-archived'
    ? `${name} is archived. Saving keeps this link; pick another association or clear it to change it.`
    : state === 'legacy-type'
      ? `${name} is no longer an organization. Saving keeps this link; pick another association or clear it to change it.`
      : null;
  const emptyMsg = eligible.length === 0 && !loading
    ? (onCreate ? 'No organizations in Contacts yet — type a name to add one.' : 'No organizations in Contacts yet — add the association there first.')
    : null;
  const msg = error || legacyMsg || emptyMsg || help;
  const tone = error ? ' error' : legacyMsg ? ' warn' : '';

  if (!Combobox) return null;

  return (
    <div className={`odc-field odc-hoa-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      <label className="odc-field-label" htmlFor={fieldId}>{label}</label>
      <Combobox
        id={fieldId}
        value={value || ''}
        onChange={(v) => onChange && onChange(v || '')}
        options={options}
        placeholder={placeholder}
        clearable
        loading={loading}
        disabled={disabled || (eligible.length === 0 && !value && !onCreate)}
        emptyText={onCreate ? 'No matches — type to add one' : 'No organizations match'}
        onCreate={onCreate}
        createLabel="Add"
        createKinds={onCreate ? [{ key: 'Organization', label: 'organization', icon: org.icon }] : undefined}
        ariaDescribedBy={msg ? helpId : undefined}
        invalid={!!error}
      />
      {msg ? (
        <div className={`odc-field-help${tone}`} id={helpId} role={error ? 'alert' : undefined}>
          {legacyMsg ? <span className="material-icons odc-hoa-warn-ic" aria-hidden="true">info</span> : null}
          {msg}
        </div>
      ) : null}
    </div>
  );
}
