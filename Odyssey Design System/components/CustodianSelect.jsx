/**
 * Odyssey DS — CustodianSelect
 * The optional **custodian picker** for the account-editing surfaces — the
 * control that links an account to the contact that holds it. It is the
 * single picker shared by BOTH surfaces the product actually has: the
 * Create-account dialog and the inline edit grid on the account card.
 *
 * Per spec FE-1 this is **not a new widget** — it reuses/extends the DS
 * `Combobox` (a searchable single-select, the DS equivalent of `OdsCombobox`
 * over `MudAutocomplete`), wrapped in the standard field label / help / error
 * chrome. It deliberately does NOT take an `onCreate` (Non-Goal 1: no inline
 * contact creation) and does NOT restrict by ContactType (Non-Goal 4:
 * any contact is eligible).
 *
 * Options are the **active** contacts only — archived ones are filtered
 * out client-side (FE-6 / §9 archived-on-set) so an archived target can't be
 * picked. Each row carries its ContactType icon (decorative). The control
 * is clearable (clears the link) and optional.
 *
 * Accessibility (the picker half of the WCAG 2.2 AA contract):
 *   • A11Y-1 — a persistent, associated "Custodian" label; optional stated in text.
 *   • A11Y-2/A11Y-3 — combobox semantics + keyboard come from the base Combobox;
 *     the clear (×) is a real keyboard-operable button (not a pointer-only ×).
 *   • A11Y-4 — `loading` shows an announced loading row; the empty-list hint
 *     ("create a contact first") is wired to the field via aria-describedby.
 *   • A11Y-9 — on a rejected save pass `error`; it links to the input via
 *     aria-describedby and flips aria-invalid.
 *
 * Props: `value` (contact id | '' / null), `onChange(id)` — fires '' on
 * clear; `contacts` ([{ id|contactId, name, type, archived }]);
 * `label` (default "Custodian"), `optional` (default true), `loading`, `error`,
 * `help`, `disabled`. Reuses the `.odc-field` chrome.
 */

function custodianTypeIcon(typeKey) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const reg = NS.CONTACT_TYPES;
  const FALLBACK = {
    Merchant: { icon: 'storefront', color: 'oklch(0.79 0.115 188)' },
    Person: { icon: 'person', color: 'oklch(0.80 0.15 150)' },
    Organization: { icon: 'corporate_fare', color: 'oklch(0.72 0.16 295)' },
    Company: { icon: 'business', color: 'oklch(0.76 0.13 225)' },
    Institution: { icon: 'account_balance', color: 'oklch(0.75 0.16 330)' },
    Other: { icon: 'category', color: 'oklch(0.74 0.02 250)' },
  };
  if (reg) {
    const hit = reg.find((t) => t.key === typeKey) || reg.find((t) => t.key === 'Other');
    if (hit) return { icon: hit.icon, color: hit.color };
  }
  return FALLBACK[typeKey] || FALLBACK.Other;
}

export function CustodianSelect({
  value,
  onChange,
  contacts = [],
  label = 'Custodian',
  optional = true,
  placeholder = 'Search contacts…',
  help,
  error,
  loading = false,
  disabled = false,
  className = '',
  id,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const Combobox = NS.Combobox;

  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;

  // Active (non-archived) contacts only — an archived custodian must not
  // be selectable (FE-6). The currently-linked value is always kept resolvable
  // so a value that points at a now-archived contact still shows its name.
  const active = contacts.filter((c) => !c.archived);
  const idOf = (c) => c.id || c.contactId;
  const byId = {};
  contacts.forEach((c) => { byId[idOf(c)] = c; });

  const options = active.map((c) => {
    const meta = custodianTypeIcon(c.type);
    return { value: idOf(c), label: c.name, icon: meta.icon, iconColor: meta.color };
  });
  // If the linked value isn't in the active set (edge: linked then archived),
  // surface it as a (single) selectable option so the trigger shows the name.
  if (value && !options.some((o) => o.value === value) && byId[value]) {
    const c = byId[value];
    const meta = custodianTypeIcon(c.type);
    options.unshift({ value, label: c.name, icon: meta.icon, iconColor: meta.color });
  }

  const isEmpty = active.length === 0;
  // The empty-list hint is the help text (A11Y-4) when there's nothing to pick.
  const emptyHint = 'No contacts yet — create one first to link a custodian.';
  const msg = error || help || (isEmpty && !loading ? emptyHint : null);

  if (!Combobox) return null;

  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      <label className="odc-field-label" htmlFor={fieldId}>
        {label}
        {optional ? <span className="odc-field-opt">Optional</span> : null}
      </label>
      <Combobox
        id={fieldId}
        value={value || ''}
        onChange={(v) => onChange && onChange(v || '')}
        options={options}
        placeholder={placeholder}
        clearable
        loading={loading}
        disabled={disabled || (isEmpty && !value)}
        emptyText="No contacts match"
        ariaDescribedBy={msg ? helpId : undefined}
        invalid={!!error}
      />
      {msg ? (
        <div className={`odc-field-help${error ? ' error' : ''}`} id={helpId} role={error ? 'alert' : undefined}>
          {msg}
        </div>
      ) : null}
    </div>
  );
}
